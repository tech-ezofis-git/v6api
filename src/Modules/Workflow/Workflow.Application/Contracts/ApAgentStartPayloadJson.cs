using System.Text;
using System.Text.Json;

namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>
/// Workflow start payload helpers. Hangfire posts the agents <c>/chat</c> contract
/// (<c>session_id</c> + <c>intent=ap</c> + mapped <c>payload</c>), not the legacy <c>startPayload</c> wrapper.
/// </summary>
public static class ApAgentStartPayloadJson
{
    public static string UnwrapInner(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return json;

        if (TryGetNestedStartPayload(root, out var inner))
            return inner.GetRawText();

        return json;
    }

    public static string WrapForPythonApi(string innerFlatJson)
    {
        var inner = JsonDocument.Parse(innerFlatJson).RootElement;
        if (inner.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Start payload must be a JSON object.", nameof(innerFlatJson));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("startPayload");
            inner.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryGetNestedStartPayload(JsonElement root, out JsonElement startPayload)
    {
        if (root.TryGetProperty("startPayload", out startPayload) && startPayload.ValueKind == JsonValueKind.Object)
            return true;

        if (root.TryGetProperty("StartPayload", out startPayload) && startPayload.ValueKind == JsonValueKind.Object)
            return true;

        startPayload = default;
        return false;
    }

    /// <summary>Adds job tracking fields for Python progress callbacks (ignored if already present).</summary>
    public static string EnrichWithJobTracking(
        string innerFlatJson,
        Guid workflowId,
        Guid instanceId,
        string apAgentJobId,
        string? apiBaseUrl)
    {
        using var doc = JsonDocument.Parse(innerFlatJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return innerFlatJson;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("apAgentJobId")
                    || prop.NameEquals("apAgentJobStatusUrl")
                    || prop.NameEquals("apAgentProgressUrl")
                    || prop.NameEquals("workflowId")
                    || prop.NameEquals("instanceId"))
                {
                    continue;
                }

                prop.WriteTo(writer);
            }

            writer.WriteString("apAgentJobId", apAgentJobId);
            writer.WriteString("workflowId", workflowId.ToString("D"));
            writer.WriteString("instanceId", instanceId.ToString("D"));

            if (!string.IsNullOrWhiteSpace(apiBaseUrl))
            {
                var baseUrl = apiBaseUrl.TrimEnd('/');
                writer.WriteString("apAgentJobStatusUrl", $"{baseUrl}/ap-agent/jobs/{apAgentJobId}");
                writer.WriteString(
                    "apAgentProgressUrl",
                    $"{baseUrl}/{workflowId:D}/instances/{instanceId:D}/ap-agent/progress");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Builds POST body for agents <c>/chat</c> matching start-workflow AP inputs:
    /// <c>{ "session_id", "intent": "ap", "payload": { tenant_id, formid, item_id, filepath, workflowId, skills?, ... } }</c>.
    /// When <paramref name="skills"/> is null/empty, <c>skills</c> is omitted so agents run the full default plan.
    /// </summary>
    public static string BuildChatApRequestJson(
        string innerStartPayloadJson,
        string sessionId,
        IReadOnlyList<string>? skills = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("session_id is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(innerStartPayloadJson))
            throw new ArgumentException("Start payload JSON is empty.", nameof(innerStartPayloadJson));

        using var doc = JsonDocument.Parse(innerStartPayloadJson);
        var inner = doc.RootElement;
        if (inner.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Start payload must be a JSON object.", nameof(innerStartPayloadJson));

        var resolvedSkills = skills != null
            ? NormalizeSkills(skills)
            : ExtractSkillsFromPayload(inner);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("session_id", sessionId);
            writer.WriteString("intent", "ap");
            writer.WritePropertyName("payload");
            writer.WriteStartObject();

            // Same fields agents expect from start-workflow / AP initiate.
            WriteMappedString(writer, "tenant_id", inner, "tenantId", "TenantId", "tenant_id");
            WriteMappedString(writer, "formid", inner, "formid", "formId", "FormId");
            WriteMappedString(writer, "item_id", inner, "itemId", "ItemId", "item_id");
            WriteMappedString(writer, "filepath", inner, "filepath", "blobPath", "BlobPath", "filePath");
            WriteMappedString(writer, "workflowId", inner, "workflowId", "WorkflowId", "workflow_id");
            WriteMappedString(writer, "instanceId", inner, "instanceId", "InstanceId", "instance_id");
            WriteMappedString(writer, "repositoryId", inner, "repositoryId", "RepositoryId");
            WriteMappedString(writer, "repositoryItemId", inner, "repositoryItemId", "RepositoryItemId");
            WriteMappedString(writer, "transactionId", inner, "transactionId", "TransactionId");
            WriteMappedString(writer, "formentryId", inner, "formentryId", "formEntryId", "FormEntryId");

            if (!TryGetPropertyIgnoreCase(inner, "pageno", out _)
                && !TryGetPropertyIgnoreCase(inner, "pageNo", out _))
                writer.WriteString("pageno", "1");
            else
                WriteMappedString(writer, "pageno", inner, "pageno", "pageNo", "Pageno");

            if (resolvedSkills is { Count: > 0 })
            {
                writer.WritePropertyName("skills");
                writer.WriteStartArray();
                foreach (var skill in resolvedSkills)
                    writer.WriteStringValue(skill);
                writer.WriteEndArray();
            }

            // Hangfire callback fields when present.
            WriteMappedString(writer, "apAgentJobId", inner, "apAgentJobId");
            WriteMappedString(writer, "apAgentJobStatusUrl", inner, "apAgentJobStatusUrl");
            WriteMappedString(writer, "apAgentProgressUrl", inner, "apAgentProgressUrl");

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Null/empty/whitespace-only/Swagger placeholders → null (omit skills → agents full plan).
    /// Otherwise distinct non-empty skill names.
    /// </summary>
    public static IReadOnlyList<string>? NormalizeSkills(IEnumerable<string>? skills)
    {
        if (skills == null)
            return null;

        var list = skills
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Where(s => !IsSwaggerPlaceholderSkill(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return list.Count == 0 ? null : list;
    }

    /// <summary>
    /// Swagger UI fills array-of-string examples with the literal <c>"string"</c>.
    /// That must not be forwarded to agents (they reject unknown skill names).
    /// </summary>
    private static bool IsSwaggerPlaceholderSkill(string skill) =>
        string.Equals(skill, "string", StringComparison.OrdinalIgnoreCase)
        || string.Equals(skill, "null", StringComparison.OrdinalIgnoreCase)
        || string.Equals(skill, "undefined", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes <c>skills</c> onto the flat start-payload JSON (Python / response <c>startPayload</c>).
    /// Null skills → <c>"skills": null</c>. Non-empty → string array. Replaces any existing skills property.
    /// </summary>
    public static string MergeSkillsIntoPayloadJson(string innerFlatJson, IReadOnlyList<string>? skills)
    {
        if (string.IsNullOrWhiteSpace(innerFlatJson))
            return innerFlatJson;

        using var doc = JsonDocument.Parse(innerFlatJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return innerFlatJson;

        var normalized = NormalizeSkills(skills);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("skills") || prop.NameEquals("Skills"))
                    continue;
                prop.WriteTo(writer);
            }

            if (normalized is { Count: > 0 })
            {
                writer.WritePropertyName("skills");
                writer.WriteStartArray();
                foreach (var skill in normalized)
                    writer.WriteStringValue(skill);
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteNull("skills");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static IReadOnlyDictionary<string, object?> MergeSkillsIntoStartPayload(
        IReadOnlyDictionary<string, object?> startPayload,
        IReadOnlyList<string>? skills)
    {
        var normalized = NormalizeSkills(skills);
        // Preserve original key casing (payload may contain both formId and formid).
        var copy = new Dictionary<string, object?>(startPayload.Count + 1);
        foreach (var (key, value) in startPayload)
        {
            if (string.Equals(key, "skills", StringComparison.OrdinalIgnoreCase))
                continue;
            copy[key] = value;
        }

        copy["skills"] = normalized is { Count: > 0 } ? normalized.ToList() : null;
        return copy;
    }

    /// <summary>
    /// Reads <c>skills</c> from request root, <c>startPayload</c>, or <c>payload</c>.
    /// Returns false when the property is absent (caller should fall back to DefaultSkills).
    /// </summary>
    public static bool TryGetSkillsFromRequestBody(JsonElement root, out IReadOnlyList<string>? skills)
    {
        skills = null;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetPropertyIgnoreCase(root, "skills", out var top)
            && (top.ValueKind == JsonValueKind.Array || top.ValueKind == JsonValueKind.Null))
        {
            skills = top.ValueKind == JsonValueKind.Null
                ? null
                : NormalizeSkills(ReadStringArray(top));
            return true;
        }

        if (TryGetNestedStartPayload(root, out var startPayload)
            && TryGetPropertyIgnoreCase(startPayload, "skills", out var nested)
            && (nested.ValueKind == JsonValueKind.Array || nested.ValueKind == JsonValueKind.Null))
        {
            skills = nested.ValueKind == JsonValueKind.Null
                ? null
                : NormalizeSkills(ReadStringArray(nested));
            return true;
        }

        if (TryGetPropertyIgnoreCase(root, "payload", out var payload)
            && payload.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(payload, "skills", out var inPayload)
            && (inPayload.ValueKind == JsonValueKind.Array || inPayload.ValueKind == JsonValueKind.Null))
        {
            skills = inPayload.ValueKind == JsonValueKind.Null
                ? null
                : NormalizeSkills(ReadStringArray(inPayload));
            return true;
        }

        return false;
    }

    public static IReadOnlyList<string>? ExtractSkillsFromRequestBody(JsonElement root) =>
        TryGetSkillsFromRequestBody(root, out var skills) ? skills : null;

    private static IReadOnlyList<string>? ExtractSkillsFromPayload(JsonElement inner)
    {
        if (!TryGetPropertyIgnoreCase(inner, "skills", out var skills))
            return null;
        if (skills.ValueKind == JsonValueKind.Null)
            return null;
        if (skills.ValueKind == JsonValueKind.Array)
            return NormalizeSkills(ReadStringArray(skills));
        return null;
    }

    private static IEnumerable<string> ReadStringArray(JsonElement array)
    {
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    yield return s;
            }
        }
    }

    private static void WriteMappedString(
        Utf8JsonWriter writer,
        string outputName,
        JsonElement source,
        params string[] sourceNames)
    {
        foreach (var name in sourceNames)
        {
            if (!TryGetPropertyIgnoreCase(source, name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var s = value.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    writer.WriteString(outputName, s);
                return;
            }

            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                writer.WriteString(outputName, value.ToString());
                return;
            }
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
