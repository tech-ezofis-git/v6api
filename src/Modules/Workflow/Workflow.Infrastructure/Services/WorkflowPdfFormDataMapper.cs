using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using SaaSApp.MultiTenancy;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Maps formData jsonId keys to wFormControl.name for the given formId.
/// Does not filter or rename against pdf template dataKeys.
/// </summary>
public sealed class WorkflowPdfFormDataMapper
{
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly ILogger<WorkflowPdfFormDataMapper> _logger;

    public WorkflowPdfFormDataMapper(
        ITenantConnectionProvider connectionProvider,
        ILogger<WorkflowPdfFormDataMapper> logger)
    {
        _connectionProvider = connectionProvider;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>> MapAsync(
        string? formId,
        string? formDataJson,
        IReadOnlyCollection<string>? templateDataKeys = null,
        CancellationToken cancellationToken = default)
    {
        _ = templateDataKeys;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(formDataJson))
            return result;

        Dictionary<string, string> jsonIdToName = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(formId))
        {
            var connectionString = _connectionProvider.ConnectionString
                ?? throw new InvalidOperationException("Tenant connection string not resolved.");

            var normalizedFormId = FormIdNaming.NormalizeFormId(formId);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var wFormIdCandidates = await FormWFormIdResolver.BuildCandidatesAsync(
                connection,
                normalizedFormId,
                cancellationToken);
            var controls = await LoadFormControlsWithFallbackAsync(connection, wFormIdCandidates, cancellationToken);
            jsonIdToName = BuildJsonIdToNameIndex(controls);

            if (controls.Count == 0)
            {
                _logger.LogWarning(
                    "WorkflowPdfFormDataMapper: no wFormControl rows for formId {FormId}",
                    normalizedFormId);
            }
        }

        using var doc = JsonDocument.Parse(formDataJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(prop.Name))
                continue;

            foreach (var (name, value) in ExtractScalarValues(prop.Name, prop.Value, jsonIdToName))
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                    continue;

                result[name] = value;
            }
        }

        _logger.LogInformation(
            "WorkflowPdfFormDataMapper: formId {FormId}, mapped {Count} field(s) by control name (no template key match)",
            formId,
            result.Count);

        return result;
    }

    /// <summary>
    /// Prefer move-next submitted formData (jsonId keys); fill gaps from ezfb.
    /// </summary>
    public async Task<Dictionary<string, string>> MapMergedAsync(
        string? formId,
        string? submittedFormDataJson,
        string? ezfbFormDataJson,
        IReadOnlyCollection<string>? templateDataKeys = null,
        CancellationToken cancellationToken = default)
    {
        _ = templateDataKeys;
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(ezfbFormDataJson))
        {
            foreach (var (k, v) in await MapAsync(formId, ezfbFormDataJson, null, cancellationToken))
                merged[k] = v;
        }

        if (!string.IsNullOrWhiteSpace(submittedFormDataJson))
        {
            foreach (var (k, v) in await MapAsync(formId, submittedFormDataJson, null, cancellationToken))
                merged[k] = v;
        }

        return merged;
    }

    private static IEnumerable<(string Key, string Value)> ExtractScalarValues(
        string propName,
        JsonElement value,
        IReadOnlyDictionary<string, string> jsonIdToName)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var rowIndex = 1;
            foreach (var row in value.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    rowIndex++;
                    continue;
                }

                foreach (var cell in row.EnumerateObject())
                {
                    var cellScalar = FormatScalar(cell.Value);
                    if (string.IsNullOrWhiteSpace(cellScalar))
                        continue;

                    var baseName = ResolveName(cell.Name, jsonIdToName);
                    yield return ($"{baseName} {rowIndex}", cellScalar);
                    yield return (baseName, cellScalar);
                }

                rowIndex++;
            }

            yield break;
        }

        var scalar = FormatScalar(value);
        if (string.IsNullOrWhiteSpace(scalar))
            yield break;

        yield return (ResolveName(propName, jsonIdToName), scalar);
    }

    private static string ResolveName(string key, IReadOnlyDictionary<string, string> jsonIdToName)
    {
        if (jsonIdToName.TryGetValue(key, out var name) && !string.IsNullOrWhiteSpace(name))
            return name.Trim();

        return key;
    }

    private static Dictionary<string, string> BuildJsonIdToNameIndex(IReadOnlyList<FormControlRow> controls)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in controls)
        {
            if (string.IsNullOrWhiteSpace(control.JsonId) || string.IsNullOrWhiteSpace(control.Name))
                continue;

            index[control.JsonId.Trim()] = control.Name.Trim();
        }

        return index;
    }

    private static string? FormatScalar(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };

    private static async Task<List<FormControlRow>> LoadFormControlsWithFallbackAsync(
        NpgsqlConnection connection,
        IReadOnlyList<object> wFormIdCandidates,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in wFormIdCandidates)
        {
            var controls = await LoadFormControlsAsync(connection, candidate, cancellationToken);
            if (controls.Count > 0)
                return controls;
        }

        return [];
    }

    private static async Task<List<FormControlRow>> LoadFormControlsAsync(
        NpgsqlConnection connection,
        object wFormIdValue,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, "jsonId", name, type, COALESCE("parentId", 0)
            FROM dbo."wFormControl"
            WHERE "wFormId" = @FormId AND "isDeleted" = false AND "jsonId" IS NOT NULL AND BTRIM("jsonId") <> ''
            """;

        var list = new List<FormControlRow>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", wFormIdValue);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.IsDBNull(3) ? null : reader.GetString(3);
            if (IsFileControl(type))
                continue;

            list.Add(new FormControlRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                type,
                Convert.ToInt32(reader.GetValue(4))));
        }

        return list;
    }

    private static bool IsFileControl(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;

        var t = type.Trim();
        return t.Contains("FILE", StringComparison.OrdinalIgnoreCase)
            || t.Contains("ATTACHMENT", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record FormControlRow(int Id, string JsonId, string? Name, string? Type, int ParentId);
}
