using Microsoft.Extensions.Logging;
using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>Maps OCR field names to form control jsonIds (at workflow start only).</summary>
public sealed class OcrToFormDataMapper
{
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly ILogger<OcrToFormDataMapper> _logger;

    public OcrToFormDataMapper(
        ITenantConnectionProvider connectionProvider,
        ILogger<OcrToFormDataMapper> logger)
    {
        _connectionProvider = connectionProvider;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>> MapAsync(
        string? formId,
        IReadOnlyList<UploadIndexFieldDto>? ocrFieldList,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ocrFieldList is not { Count: > 0 } || string.IsNullOrWhiteSpace(formId))
            return result;

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
        if (controls.Count == 0)
        {
            _logger.LogWarning(
                "OcrToFormDataMapper: no wFormControl rows for formId {FormId} (tried wFormId candidates: {Candidates})",
                normalizedFormId,
                string.Join(", ", wFormIdCandidates));
            return result;
        }

        var nameToJsonId = BuildNameIndex(controls);
        var skipped = 0;

        foreach (var field in ocrFieldList)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || string.IsNullOrWhiteSpace(field.Value))
                continue;

            if (!TryResolveJsonId(field.Name, nameToJsonId, controls, out var jsonId))
            {
                skipped++;
                _logger.LogDebug(
                    "OcrToFormDataMapper: no form control match for OCR field {Name}",
                    field.Name);
                continue;
            }

            result[jsonId] = field.Value.Trim();
        }

        _logger.LogInformation(
            "OcrToFormDataMapper: formId {FormId}, controls={ControlCount}, ocrFields={OcrCount}, mapped={MappedCount}, skipped={SkippedCount}",
            normalizedFormId,
            controls.Count,
            ocrFieldList.Count,
            result.Count,
            skipped);

        return result;
    }

    private static Dictionary<string, string> BuildNameIndex(IReadOnlyList<FormControlRow> controls)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in controls)
        {
            if (string.IsNullOrWhiteSpace(control.JsonId))
                continue;

            index[control.JsonId] = control.JsonId;

            if (!string.IsNullOrWhiteSpace(control.Name))
            {
                index[control.Name.Trim()] = control.JsonId;
                index[NormalizeFieldName(control.Name)] = control.JsonId;
            }

            if (EzfbColumnNaming.TryToColumnName(control.JsonId, out var ezfbCol)
                && !string.IsNullOrWhiteSpace(ezfbCol))
            {
                index[ezfbCol] = control.JsonId;
            }
        }

        return index;
    }

    private static bool TryResolveJsonId(
        string ocrFieldName,
        IReadOnlyDictionary<string, string> nameToJsonId,
        IReadOnlyList<FormControlRow> controls,
        out string jsonId)
    {
        foreach (var candidate in RepositoryFormFieldAliases.ExpandKeys(ocrFieldName))
        {
            if (nameToJsonId.TryGetValue(candidate, out jsonId!))
                return true;

            foreach (var control in controls)
            {
                if (string.IsNullOrWhiteSpace(control.JsonId))
                    continue;

                if (!string.IsNullOrWhiteSpace(control.Name)
                    && string.Equals(control.Name.Trim(), candidate, StringComparison.OrdinalIgnoreCase))
                {
                    jsonId = control.JsonId;
                    return true;
                }

                if (string.Equals(control.JsonId, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    jsonId = control.JsonId;
                    return true;
                }

                if (string.Equals(NormalizeFieldName(control.Name ?? string.Empty), NormalizeFieldName(candidate), StringComparison.OrdinalIgnoreCase))
                {
                    jsonId = control.JsonId;
                    return true;
                }
            }
        }

        jsonId = string.Empty;
        return false;
    }

    private static string NormalizeFieldName(string name) =>
        name.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();

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
