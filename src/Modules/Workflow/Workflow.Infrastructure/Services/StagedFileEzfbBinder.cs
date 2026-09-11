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

        var fileControlJsonIds = await LoadFileControlJsonIdsAsync(connection, normalizedFormId, cancellationToken);

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var explicitIndex = 0;

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

            // Legacy form FILE controls and WorkflowAttachments FormJsonId use 32-char N format.
            fields[targetJsonId] = itemId.ToString("N");
        }

        if (fields.Count == 0)
            return 0;

        var updated = await _apAgentMoveNext.ApplyFormDataToEzfbAsync(
            normalizedFormId,
            formEntryItemId,
            fields,
            lineItemsJson: null,
            cancellationToken);

        _logger.LogInformation(
            "StagedFileEzfbBinder: form {FormId}, entry {FormEntryId}, wrote {Updated} FILE field(s) from staged archive.",
            normalizedFormId,
            formEntryItemId,
            updated);

        return updated;
    }

    private static async Task<IReadOnlyList<string>> LoadFileControlJsonIdsAsync(
        NpgsqlConnection connection,
        string normalizedFormId,
        CancellationToken cancellationToken)
    {
        var candidates = await FormWFormIdResolver.BuildCandidatesAsync(connection, normalizedFormId, cancellationToken);
        foreach (var wFormId in candidates)
        {
            var jsonIds = await QueryFileControlJsonIdsAsync(connection, wFormId, cancellationToken);
            if (jsonIds.Count > 0)
                return jsonIds;
        }

        return Array.Empty<string>();
    }

    private static async Task<List<string>> QueryFileControlJsonIdsAsync(
        NpgsqlConnection connection,
        object wFormIdValue,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "jsonId", type
            FROM dbo."wFormControl"
            WHERE "wFormId" = @FormId
              AND COALESCE("isDeleted", false) = false
              AND "jsonId" IS NOT NULL
              AND LTRIM(RTRIM("jsonId")) <> ''
            ORDER BY id
            """;

        var list = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", wFormIdValue);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var jsonId = reader.GetString(0);
            var type = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (IsFileControl(type))
                list.Add(jsonId);
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
}
