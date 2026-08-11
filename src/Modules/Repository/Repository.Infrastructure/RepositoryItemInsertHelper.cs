using Npgsql;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure;

internal static class RepositoryItemInsertHelper
{
    private static readonly (string Column, Func<CreateRepositoryItemRequest, object?> Value)[] OptionalCoreColumns =
    [
        ("Status", r => r.Status),
        ("OcrScore", r => r.OcrPercent),
        ("AiStatus", r => r.AiStatus),
        ("WorkflowInstanceId", r => r.WorkflowInstanceId),
        ("FileVersion", r => r.FileVersion ?? 1),
    ];

    public static async Task InsertItemAsync(
        NpgsqlConnection connection,
        RepositoryDetailDto repo,
        Guid tenantId,
        Guid repositoryId,
        Guid itemId,
        Guid storageProviderId,
        CreateRepositoryItemRequest request,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var table = RepositorySqlHelper.QualifiedItemsTable(repo.ItemsTableName);
        var tableColumns = await RepositoryItemTableColumns.LoadAsync(connection, repo.ItemsTableName, cancellationToken);
        var allowedColumns = RepositoryItemFilterHelper.BuildFilterableColumns(repo, tableColumns);
        var metadata = request.FieldValues ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        request = RepositoryItemMetadataMerger.Apply(request, metadata);
        var fieldValues = request.FieldValues ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var columns = new List<string>();
        var values = new List<string>();
        var parameters = new List<NpgsqlParameter>();
        var usedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfExists(string column, string param, object? value)
        {
            if (!RepositoryItemTableColumns.Has(tableColumns, column))
                return;

            columns.Add(RepositorySqlHelper.ColumnRef(column));
            values.Add(param);
            parameters.Add(new NpgsqlParameter(param, value ?? DBNull.Value));
            usedColumns.Add(column);
        }

        AddIfExists("Id", "@Id", itemId);
        AddIfExists("TenantId", "@TenantId", tenantId);
        AddIfExists("RepositoryId", "@RepositoryId", repositoryId);
        AddIfExists("FolderId", "@FolderId", request.FolderId);
        AddIfExists("StorageProviderId", "@StorageProviderId", storageProviderId);
        AddIfExists("FilePath", "@FilePath", request.FilePath);
        AddIfExists("FileName", "@FileName", request.FileName);
        AddIfExists("FileType", "@FileType", request.FileType);
        AddIfExists("FileSize", "@FileSize", request.FileSize);
        AddIfExists("CreatedBy", "@CreatedBy", userId);

        foreach (var (column, getValue) in OptionalCoreColumns)
            AddIfExists(column, $"@{column}", getValue(request));

        var extraIndex = 0;
        foreach (var (key, value) in fieldValues)
        {
            if (!RepositoryItemFilterHelper.TryResolveFilterColumn(key, allowedColumns, repo, out var col))
                continue;

            var column = RepositoryItemTableColumns.TryGetCanonicalName(tableColumns, col, out var canonicalCol)
                ? canonicalCol
                : col;

            if (!RepositoryItemTableColumns.Has(tableColumns, column) || !usedColumns.Add(column))
                continue;

            var param = $"@F{extraIndex++}";
            columns.Add(RepositorySqlHelper.PhysicalColumnRef(column));
            values.Add(param);
            // Coerce to the field's declared type (Number/Date/Boolean/etc.) before binding -- a plain
            // string parameter always binds as `text`, and Postgres won't implicitly cast that into a
            // typed column the way SQL Server would (see RepositoryFieldValueCoercion for detail).
            var coerced = RepositoryFieldValueCoercion.TryCoerce(repo.Fields, column, value) ?? value;
            parameters.Add(RepositoryFieldValueCoercion.CreateParameter(param, coerced));
        }

        if (columns.Count == 0)
            throw new InvalidOperationException("No insertable columns resolved for repository item.");

        var sql = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)});";
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
