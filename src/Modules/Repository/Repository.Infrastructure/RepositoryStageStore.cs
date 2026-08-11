using Npgsql;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure;

internal sealed class RepositoryStageRow
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid RepositoryId { get; init; }
    public Guid StorageProviderId { get; init; }
    public string? FilePath { get; init; }
    public string? FileName { get; init; }
    public string? FileType { get; init; }
    public int? FileSize { get; init; }
    public string StageStatus { get; init; } = "Pending";
    public string? Status { get; init; }
    public Guid? PromotedItemId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? ModifiedAtUtc { get; init; }
    public Guid? CreatedBy { get; init; }
    public Guid? ModifiedBy { get; init; }
    public bool IsDeleted { get; init; }
    public string? OcrJson { get; init; }
    public string? SummaryJson { get; init; }
    public Dictionary<string, string> FieldValues { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal static class RepositoryStageStore
{
    // Physical (snake_case) column names -- compared against selectCols below, which are the
    // actual physical names read from information_schema, not the historical PascalCase form.
    private static readonly string[] CoreColumns =
    [
        "id", "tenant_id", "repository_id", "folder_id", "storage_provider_id",
        "file_path", "file_name", "file_type", "file_size", "total_pages",
        "stage_status", "status", "mail_id", "ocr_score", "ai_status",
        "ocr_text", "ocr_json", "summary_json", "promoted_item_id",
        "created_at_utc", "modified_at_utc", "created_by", "modified_by", "is_deleted"
    ];

    public static async Task<Guid> InsertAsync(
        NpgsqlConnection connection,
        RepositoryDetailDto repo,
        Guid tenantId,
        Guid repositoryId,
        Guid storageProviderId,
        string relativePath,
        string fileName,
        string? contentType,
        int? fileSize,
        IReadOnlyDictionary<string, string>? fieldValues,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var stageId = Guid.NewGuid();
        var table = RepositorySqlHelper.QualifiedItemsTable(repo.StageTableName);
        var tableColumns = await RepositoryItemTableColumns.LoadAsync(connection, repo.StageTableName, cancellationToken);
        var allowedColumns = RepositoryItemFilterHelper.BuildFilterableColumns(repo, tableColumns);

        var columns = new List<string>();
        var values = new List<string>();
        var parameters = new List<NpgsqlParameter>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string column, string param, object? value)
        {
            if (!RepositoryItemTableColumns.Has(tableColumns, column))
                return;
            columns.Add(RepositorySqlHelper.ColumnRef(column));
            values.Add(param);
            parameters.Add(new NpgsqlParameter(param, value ?? DBNull.Value));
            used.Add(column);
        }

        Add("Id", "@Id", stageId);
        Add("TenantId", "@TenantId", tenantId);
        Add("RepositoryId", "@RepositoryId", repositoryId);
        Add("StorageProviderId", "@StorageProviderId", storageProviderId);
        Add("FilePath", "@FilePath", relativePath);
        Add("FileName", "@FileName", fileName);
        Add("FileType", "@FileType", contentType);
        Add("FileSize", "@FileSize", fileSize);
        Add("StageStatus", "@StageStatus", "Uploaded");
        Add("Status", "@Status", "Pending");
        Add("CreatedBy", "@CreatedBy", userId);

        var fieldIndex = 0;
        foreach (var (key, value) in fieldValues ?? new Dictionary<string, string>())
        {
            if (!RepositoryItemFilterHelper.TryResolveFilterColumn(key, allowedColumns, repo, out var col))
                continue;

            var canonical = RepositoryItemTableColumns.TryGetCanonicalName(tableColumns, col, out var canonicalCol)
                ? canonicalCol
                : col;

            if (!RepositoryItemTableColumns.Has(tableColumns, canonical) || !used.Add(canonical))
                continue;

            var param = $"@F{fieldIndex++}";
            columns.Add(RepositorySqlHelper.PhysicalColumnRef(canonical));
            values.Add(param);
            parameters.Add(new NpgsqlParameter(param, value));
        }

        var sql = $"""
            INSERT INTO {table} ({string.Join(", ", columns)})
            VALUES ({string.Join(", ", values)});
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return stageId;
    }

    public static async Task<RepositoryStageRow?> GetAsync(
        NpgsqlConnection connection,
        RepositoryDetailDto repo,
        Guid tenantId,
        Guid stageId,
        CancellationToken cancellationToken)
    {
        var table = RepositorySqlHelper.QualifiedItemsTable(repo.StageTableName);
        var tableColumns = await RepositoryItemTableColumns.LoadAsync(connection, repo.StageTableName, cancellationToken);
        var selectCols = tableColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        if (selectCols.Count == 0)
            return null;

        var sql = $"""
            SELECT {string.Join(", ", selectCols.Select(c => RepositorySqlHelper.ColumnRef(c)))}
            FROM {table}
            WHERE id = @Id AND tenant_id = @TenantId AND repository_id = @RepositoryId AND is_deleted = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", stageId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@RepositoryId", repo.Id);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var coreSet = new HashSet<string>(CoreColumns, StringComparer.OrdinalIgnoreCase);
        var fieldValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < selectCols.Count; i++)
        {
            // selectCols carries the physical (already-canonical) column name -- reused here
            // as the logical key too, since it's what the CoreColumns/GetXxx lookups below
            // are matched against case-insensitively regardless of underlying casing.
            var col = selectCols[i];
            var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
            values[col] = val;
            if (!coreSet.Contains(col) && val != null)
                fieldValues[col] = Convert.ToString(val) ?? string.Empty;
        }

        // values' keys are the physical column names read straight from information_schema
        // (see selectCols above) -- reserved columns are always lowercase snake_case since
        // this table is only ever created by StaticRepositoryProvisioner's BuildStageTableScript.
        return new RepositoryStageRow
        {
            Id = GetGuid(values, "id"),
            TenantId = GetGuid(values, "tenant_id"),
            RepositoryId = GetGuid(values, "repository_id"),
            StorageProviderId = GetGuid(values, "storage_provider_id"),
            FilePath = GetString(values, "file_path"),
            FileName = GetString(values, "file_name"),
            FileType = GetString(values, "file_type"),
            FileSize = GetInt(values, "file_size"),
            StageStatus = GetString(values, "stage_status") ?? "Pending",
            Status = GetString(values, "status"),
            PromotedItemId = GetNullableGuid(values, "promoted_item_id"),
            CreatedAtUtc = GetDateTime(values, "created_at_utc") ?? DateTime.UtcNow,
            ModifiedAtUtc = GetDateTime(values, "modified_at_utc"),
            CreatedBy = GetNullableGuid(values, "created_by"),
            ModifiedBy = GetNullableGuid(values, "modified_by"),
            IsDeleted = GetBool(values, "is_deleted"),
            OcrJson = GetString(values, "ocr_json"),
            SummaryJson = GetString(values, "summary_json"),
            FieldValues = fieldValues
        };
    }

    public static async Task UpdateFieldsAsync(
        NpgsqlConnection connection,
        RepositoryDetailDto repo,
        Guid tenantId,
        Guid stageId,
        IReadOnlyDictionary<string, string> fieldValues,
        string? status,
        string? stageStatus,
        string? ocrResult,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var table = RepositorySqlHelper.QualifiedItemsTable(repo.StageTableName);
        var tableColumns = await RepositoryItemTableColumns.LoadAsync(connection, repo.StageTableName, cancellationToken);
        var allowedColumns = RepositoryItemFilterHelper.BuildFilterableColumns(repo, tableColumns);
        var updates = new List<string>();
        var parameters = new List<NpgsqlParameter>
        {
            new("@Id", stageId),
            new("@TenantId", tenantId),
            new("@RepositoryId", repo.Id)
        };

        var index = 0;
        foreach (var (key, value) in fieldValues)
        {
            if (!RepositoryItemFilterHelper.TryResolveFilterColumn(key, allowedColumns, repo, out var col))
                continue;
            var canonical = RepositoryItemTableColumns.TryGetCanonicalName(tableColumns, col, out var canonicalCol)
                ? canonicalCol
                : col;
            if (!RepositoryItemTableColumns.Has(tableColumns, canonical))
                continue;

            var param = $"@U{index++}";
            updates.Add($"{RepositorySqlHelper.PhysicalColumnRef(canonical)} = {param}");
            parameters.Add(new NpgsqlParameter(param, value));
        }

        if (!string.IsNullOrWhiteSpace(status) && RepositoryItemTableColumns.Has(tableColumns, "Status"))
            updates.Add("status = @Status");
        if (!string.IsNullOrWhiteSpace(stageStatus) && RepositoryItemTableColumns.Has(tableColumns, "StageStatus"))
            updates.Add("stage_status = @StageStatus");
        if (!string.IsNullOrWhiteSpace(ocrResult) && RepositoryItemTableColumns.Has(tableColumns, "OcrJson"))
            updates.Add("ocr_json = @OcrJson");

        updates.Add("modified_at_utc = now()");
        if (RepositoryItemTableColumns.Has(tableColumns, "ModifiedBy"))
            updates.Add("modified_by = @ModifiedBy");

        if (!string.IsNullOrWhiteSpace(status))
            parameters.Add(new NpgsqlParameter("@Status", status));
        if (!string.IsNullOrWhiteSpace(stageStatus))
            parameters.Add(new NpgsqlParameter("@StageStatus", stageStatus));
        if (!string.IsNullOrWhiteSpace(ocrResult))
            parameters.Add(new NpgsqlParameter("@OcrJson", ocrResult));
        parameters.Add(new NpgsqlParameter("@ModifiedBy", (object?)userId ?? DBNull.Value));

        var sql = $"""
            UPDATE {table}
            SET {string.Join(", ", updates)}
            WHERE id = @Id AND tenant_id = @TenantId AND repository_id = @RepositoryId AND is_deleted = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task MarkArchivedAsync(
        NpgsqlConnection connection,
        string stageTableName,
        Guid tenantId,
        Guid stageId,
        Guid promotedItemId,
        CancellationToken cancellationToken)
    {
        var table = RepositorySqlHelper.QualifiedItemsTable(stageTableName);
        var sql = $"""
            UPDATE {table}
            SET promoted_item_id = @PromotedItemId,
                stage_status = 'Archived',
                status = 'ARCHIVED',
                modified_at_utc = now()
            WHERE id = @Id AND tenant_id = @TenantId AND is_deleted = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", stageId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@PromotedItemId", promotedItemId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<(IReadOnlyList<RepositoryStageRow> Items, int Total)> ListAsync(
        NpgsqlConnection connection,
        RepositoryDetailDto repo,
        Guid tenantId,
        bool includeDeleted,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var table = RepositorySqlHelper.QualifiedItemsTable(repo.StageTableName);
        var where = includeDeleted
            ? "tenant_id = @TenantId AND repository_id = @RepositoryId"
            : "tenant_id = @TenantId AND repository_id = @RepositoryId AND is_deleted = false AND COALESCE(status, '') <> 'ARCHIVED'";

        var countSql = $"SELECT COUNT(*) FROM {table} WHERE {where};";
        await using var countCmd = new NpgsqlCommand(countSql, connection);
        countCmd.Parameters.AddWithValue("@TenantId", tenantId);
        countCmd.Parameters.AddWithValue("@RepositoryId", repo.Id);
        var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));

        var sql = $"""
            SELECT id
            FROM {table}
            WHERE {where}
            ORDER BY created_at_utc DESC
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
            """;

        var ids = new List<Guid>();
        await using (var listCmd = new NpgsqlCommand(sql, connection))
        {
            listCmd.Parameters.AddWithValue("@TenantId", tenantId);
            listCmd.Parameters.AddWithValue("@RepositoryId", repo.Id);
            listCmd.Parameters.AddWithValue("@Skip", Math.Max(skip, 0));
            listCmd.Parameters.AddWithValue("@Take", Math.Max(take, 1));
            await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetGuid(0));
        }

        var items = new List<RepositoryStageRow>();
        foreach (var id in ids)
        {
            var row = await GetAsync(connection, repo, tenantId, id, cancellationToken);
            if (row != null)
                items.Add(row);
        }

        return (items, total);
    }

    private static Guid GetGuid(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var v) && v is Guid g ? g : Guid.Empty;

    private static Guid? GetNullableGuid(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var v) && v is Guid g ? g : null;

    private static string? GetString(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var v) ? Convert.ToString(v) : null;

    private static int? GetInt(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var v) && v != null && int.TryParse(v.ToString(), out var n) ? n : null;

    private static DateTime? GetDateTime(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var v) && v is DateTime dt ? dt : null;

    private static bool GetBool(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var v) && v is bool b && b;
}
