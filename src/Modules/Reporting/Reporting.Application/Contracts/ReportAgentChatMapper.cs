using System.Text.Json;

namespace SaaSApp.Reporting.Application.Contracts;

public static class ReportAgentChatMapper
{
    public const string Intent = "report";

    public static string ResolveAgentsBaseUrl(string? chatUrl)
    {
        var url = (chatUrl ?? string.Empty).Trim().TrimEnd('/');
        if (url.EndsWith("/chat", StringComparison.OrdinalIgnoreCase))
            url = url[..^5].TrimEnd('/');
        return url;
    }

    public static ReportAgentProxyResult Parse(int statusCode, string? body)
    {
        var raw = body ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return new ReportAgentProxyResult { StatusCode = statusCode, RawBody = raw };

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var reply = ReadString(root, "reply");
            var result = ReadProperty(root, "report_result", "reportResult");
            return new ReportAgentProxyResult
            {
                StatusCode = statusCode,
                RawBody = raw,
                Reply = reply,
                ReportResult = result.ValueKind is JsonValueKind.Undefined
                    ? default
                    : result.Clone()
            };
        }
        catch (JsonException)
        {
            return new ReportAgentProxyResult { StatusCode = statusCode, RawBody = raw };
        }
    }

    public static bool IsLockFailure(IEnumerable<string>? warnings)
    {
        foreach (var warning in warnings ?? [])
        {
            if (string.IsNullOrWhiteSpace(warning))
                continue;
            if (warning.Contains("Dropped unknown source table", StringComparison.OrdinalIgnoreCase)
                || warning.Contains("Redirected report source", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static IReadOnlyList<string> ReadWarnings(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return [];

        var prop = ReadProperty(result, "warnings");
        if (prop.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    list.Add(value);
            }
        }

        return list;
    }

    public static string? ReadString(JsonElement obj, params string[] names)
    {
        var prop = ReadProperty(obj, names);
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    public static JsonElement ReadProperty(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return default;

        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var exact))
                return exact;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    return prop.Value;
            }
        }

        return default;
    }

    public static JsonElement ParseObjectOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return EmptyObject();

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.Clone()
                : EmptyObject();
        }
        catch (JsonException)
        {
            return EmptyObject();
        }
    }

    public static string SpecJson(JsonElement spec) =>
        spec.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? spec.GetRawText()
            : "{}";

    public static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    public static JsonElement EmptyArray()
    {
        using var doc = JsonDocument.Parse("[]");
        return doc.RootElement.Clone();
    }

    public static object MergePromptResponse(Guid id, string sessionId, JsonElement reportResult)
    {
        var spec = ReadProperty(reportResult, "reportSpec", "report_spec");
        if (spec.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            spec = EmptyObject();

        return new
        {
            id,
            sessionId,
            title = ReadString(reportResult, "title") ?? "Report",
            reportType = ReadString(reportResult, "reportType", "report_type"),
            reportPrompt = ReadString(reportResult, "reportPrompt", "report_prompt") ?? "",
            reportSpec = spec,
            warnings = ReadWarnings(reportResult),
            schemaTables = ReadStringList(reportResult, "schemaTables", "schema_tables"),
            durationMs = ReadNumber(reportResult, "durationMs", "duration_ms")
        };
    }

    public static object MergeRunResponse(Guid id, JsonElement reportResult)
    {
        var columns = ReadProperty(reportResult, "columns");
        var rows = ReadProperty(reportResult, "rows");
        if (columns.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            columns = EmptyArray();
        if (rows.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            rows = EmptyArray();

        return new
        {
            id,
            title = ReadString(reportResult, "title") ?? "Report",
            reportType = ReadString(reportResult, "reportType", "report_type"),
            columns,
            rows,
            totalCount = ReadNumber(reportResult, "totalCount", "total_count") ?? 0,
            page = ReadNumber(reportResult, "page") ?? 1,
            pageSize = ReadNumber(reportResult, "pageSize", "page_size") ?? 25,
            warnings = ReadWarnings(reportResult)
        };
    }

    public static ReportAiDefinitionDto ToDto(ReportAiDefinitionRecord row)
    {
        var spec = ParseObjectOrEmpty(row.ReportSpecJson);
        return new ReportAiDefinitionDto
        {
            Id = row.Id,
            SessionId = row.SessionId,
            Name = row.Name,
            Description = row.Description,
            ReportType = row.ReportType,
            WorkflowName = row.WorkflowName,
            RepositoryName = row.RepositoryName,
            Title = row.Name,
            ReportPrompt = row.ReportPrompt,
            ReportSpec = spec,
            Status = row.Status,
            Runs = row.Runs,
            CreatedAtUtc = row.CreatedAtUtc,
            ModifiedAtUtc = row.ModifiedAtUtc
        };
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement obj, params string[] names)
    {
        var prop = ReadProperty(obj, names);
        if (prop.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    list.Add(value);
            }
        }

        return list;
    }

    private static double? ReadNumber(JsonElement obj, params string[] names)
    {
        var prop = ReadProperty(obj, names);
        return prop.ValueKind is JsonValueKind.Number && prop.TryGetDouble(out var value)
            ? value
            : null;
    }
}
