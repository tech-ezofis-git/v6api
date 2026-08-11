using Npgsql;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class WorkflowProcessAddonService : IWorkflowProcessAddonService
{
    private readonly ITenantContext _tenantContext;
    private readonly IWorkflowTableCreator _tableCreator;

    public WorkflowProcessAddonService(ITenantContext tenantContext, IWorkflowTableCreator tableCreator)
    {
        _tenantContext = tenantContext;
        _tableCreator = tableCreator;
    }

    public async Task<int> InsertAsync(
        Guid workflowId,
        Guid processId,
        Guid repositoryId,
        Guid itemId,
        string? fileName,
        int? transactionId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await _tableCreator.CreateWorkflowTablesAsync(workflowId, connectionString, cancellationToken);

        var suffix = workflowId.ToString("N")[..8];
        var table = $"workflow.process_addon_{suffix}";

        var sql = $"""
            INSERT INTO {table}
                (process_id, repository_id, item_id, file_name, transaction_id, created_at, created_by, is_deleted)
            VALUES
                (@ProcessId, @RepositoryId, @ItemId, @FileName, @TransactionId, now(), @CreatedBy, false)
            RETURNING id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@ProcessId", processId);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        cmd.Parameters.AddWithValue("@ItemId", itemId);
        cmd.Parameters.AddWithValue("@FileName", (object?)fileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TransactionId", (object?)transactionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedBy", userId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<WorkflowProcessAddonRow>> ListByProcessAsync(
        Guid workflowId,
        Guid processId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        var suffix = workflowId.ToString("N")[..8];
        var table = $"process_addon_{suffix}";

        if (!await TableExistsAsync(connectionString, table, cancellationToken))
            return Array.Empty<WorkflowProcessAddonRow>();

        var sql = $"""
            SELECT id, process_id, repository_id, item_id, file_name, transaction_id, created_at, created_by
            FROM workflow.{table}
            WHERE process_id = @ProcessId AND is_deleted = false
            ORDER BY created_at DESC, id DESC;
            """;

        var rows = new List<WorkflowProcessAddonRow>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@ProcessId", processId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new WorkflowProcessAddonRow(
                reader.GetInt32(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetDateTime(6),
                reader.GetGuid(7)));
        }

        return rows;
    }

    private static async Task<bool> TableExistsAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = 'workflow' AND table_name = @TableName;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        return await cmd.ExecuteScalarAsync(cancellationToken) != null;
    }
}
