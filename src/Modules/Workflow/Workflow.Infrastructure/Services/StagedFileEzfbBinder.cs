using Microsoft.Extensions.Logging;
using Npgsql;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>After stage promote, writes archived repository itemId into ezfb FILE control columns.</summary>
public sealed class StagedFileEzfbBinder
{
    private readonly IWorkflowApAgentMoveNextService _apAgentMoveNext;
    private readonly ILogger<StagedFileEzfbBinder> _logger;

    public StagedFileEzfbBinder(
        IWorkflowApAgentMoveNextService apAgentMoveNext,
        ILogger<StagedFileEzfbBinder> logger)
    {
        _apAgentMoveNext = apAgentMoveNext;
        _logger = logger;
    }

    public async Task<int> BindAsync(
        string connectionString,
        string? formId,
        Guid formEntryItemId,
        IReadOnlyList<(StartWorkflowStagedFileRef Staged, Guid ItemId)> archived,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(formId) || formEntryItemId == Guid.Empty || archived.Count == 0)
            return 0;

        var normalizedFormId = FormIdNaming.NormalizeFormId(formId);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var controls = await LoadControlsAsync(connection, normalizedFormId, cancellationToken);
        var fileControlJsonIds = controls
            .Where(c => IsFileControl(c.Type))
            .Select(c => c.JsonId)
            .ToList();

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var explicitIndex = 0;
        var updated = 0;

        foreach (var (staged, itemId) in archived)
        {
            if (itemId == Guid.Empty)
                continue;

            var targetJsonId = staged.ResolveFormJsonId();
            if (string.IsNullOrWhiteSpace(targetJsonId))
            {
                if (archived.Count == 1 && fileControlJsonIds.Count == 1)
                {
                    targetJsonId = fileControlJsonIds[0];
                }
                else if (explicitIndex < fileControlJsonIds.Count)
                {
                    targetJsonId = fileControlJsonIds[explicitIndex];
                    explicitIndex++;
                }
            }

            if (string.IsNullOrWhiteSpace(targetJsonId))
            {
                _logger.LogWarning(
                    "StagedFileEzfbBinder: could not resolve FILE jsonId for staged file {StageId} (form {FormId}).",
                    staged.FileId,
                    normalizedFormId);
                continue;
            }

            // FILE_UPLOAD controls nested inside a TABLE/DYNAMIC_TABLE (e.g. a per-row receipt
            // upload) are not standalone ezfb columns -- their value lives inside the parent
            // table's row JSON. Writing targetJsonId as a flat field would either be silently
            // skipped (jsonId matches the nested control) or, if targetJsonId is mistakenly the
            // table's own jsonId, would overwrite every row with this raw itemId. Merge into the
            // existing row JSON instead.
            var control = controls.FirstOrDefault(c =>
                string.Equals(c.JsonId, targetJsonId, StringComparison.OrdinalIgnoreCase));
            var parent = control is { ParentId: > 0 }
                ? controls.FirstOrDefault(c => c.Id == control.ParentId)
                : null;

            if (parent != null && IsTableControl(parent.Type))
            {
                var merged = await _apAgentMoveNext.MergeFileItemIntoTableCellAsync(
                    normalizedFormId,
                    formEntryItemId,
                    targetJsonId,
                    itemId,
                    cancellationToken);
                if (merged)
                {
                    updated++;
                    _logger.LogInformation(
                        "StagedFileEzfbBinder: merged itemId {ItemId} into table cell {JsonId} (form {FormId}, entry {FormEntryId}).",
                        itemId,
                        targetJsonId,
                        normalizedFormId,
                        formEntryItemId);
                }
                else
                {
                    _logger.LogWarning(
                        "StagedFileEzfbBinder: could not merge itemId {ItemId} into table cell {JsonId} (form {FormId}, entry {FormEntryId}).",
                        itemId,
                        targetJsonId,
                        normalizedFormId,
                        formEntryItemId);
                }

                continue;
            }

            // Legacy form FILE controls and WorkflowAttachments FormJsonId use 32-char N format.
            fields[targetJsonId] = itemId.ToString("N");
        }

        if (fields.Count > 0)
        {
            var flatUpdated = await _apAgentMoveNext.ApplyFormDataToEzfbAsync(
                normalizedFormId,
                formEntryItemId,
                fields,
                lineItemsJson: null,
                cancellationToken);
            updated += flatUpdated;

            _logger.LogInformation(
                "StagedFileEzfbBinder: form {FormId}, entry {FormEntryId}, wrote {Updated} FILE field(s) from staged archive.",
                normalizedFormId,
                formEntryItemId,
                flatUpdated);
        }

        return updated;
    }

    private sealed record ControlRow(int Id, string JsonId, string? Type, int ParentId);

    private static async Task<IReadOnlyList<ControlRow>> LoadControlsAsync(
        NpgsqlConnection connection,
        string normalizedFormId,
        CancellationToken cancellationToken)
    {
        var candidates = await FormWFormIdResolver.BuildCandidatesAsync(connection, normalizedFormId, cancellationToken);
        foreach (var wFormId in candidates)
        {
            var controls = await QueryControlsAsync(connection, wFormId, cancellationToken);
            if (controls.Count > 0)
                return controls;
        }

        return Array.Empty<ControlRow>();
    }

    private static async Task<List<ControlRow>> QueryControlsAsync(
        NpgsqlConnection connection,
        object wFormIdValue,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, "jsonId", type, COALESCE("parentId", 0)
            FROM dbo."wFormControl"
            WHERE "wFormId" = @FormId
              AND COALESCE("isDeleted", false) = false
              AND "jsonId" IS NOT NULL
              AND LTRIM(RTRIM("jsonId")) <> ''
            ORDER BY id
            """;

        var list = new List<ControlRow>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", wFormIdValue);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ControlRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3)));
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

    private static bool IsTableControl(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;

        var t = type.Trim();
        return t.Contains("TABLE", StringComparison.OrdinalIgnoreCase);
    }
}
