using Npgsql;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Persistence;

/// <summary>
/// Repository for managing workflow-specific dynamic tables.
/// Handles transactions and CRUD operations for workflow_comments_x, workflow_attachments_x, etc.
/// </summary>
public sealed class DynamicTableRepository : IDynamicTableRepository
{
    private readonly ITenantContext _tenantContext;

    public DynamicTableRepository(ITenantContext tenantContext)
    {
        _tenantContext = tenantContext;
    }

    public string GetTableName(Guid workflowId, string entityType)
    {
        var suffix = workflowId.ToString("N").Substring(0, 8);
        return $"workflow.{entityType}_{suffix}";
    }

    public async Task<Guid> AddCommentAsync(Guid workflowId, Guid workflowInstanceId, string comments, Guid createdBy, Guid? stepInstanceId = null, string? externalCommentsBy = null, int showTo = 0, CancellationToken cancellationToken = default)
    {
        var tableName = GetTableName(workflowId, "workflow_comments");
        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");
        var connectionString = _tenantContext.ConnectionString ?? throw new InvalidOperationException("Connection string is required.");
        var commentId = Guid.NewGuid();

        var sql = $@"
            INSERT INTO {tableName}
            (id, tenant_id, workflow_instance_id, step_instance_id, comments, external_comments_by, show_to, created_by, created_at_utc, is_deleted, embed_status)
            VALUES
            (@Id, @TenantId, @WorkflowInstanceId, @StepInstanceId, @Comments, @ExternalCommentsBy, @ShowTo, @CreatedBy, now(), false, false)";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Id", commentId);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        command.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        command.Parameters.AddWithValue("@StepInstanceId", (object?)stepInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@Comments", comments);
        command.Parameters.AddWithValue("@ExternalCommentsBy", (object?)externalCommentsBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@ShowTo", showTo);
        command.Parameters.AddWithValue("@CreatedBy", createdBy);

        await command.ExecuteNonQueryAsync(cancellationToken);
        return commentId;
    }

    public async Task<Guid> AddAttachmentAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        string fileName,
        string filePath,
        Guid createdBy,
        long? fileSize = null,
        string? contentType = null,
        Guid? stepInstanceId = null,
        Guid? repositoryId = null,
        Guid? itemId = null,
        string? formJsonId = null,
        CancellationToken cancellationToken = default)
    {
        var tableName = GetTableName(workflowId, "workflow_attachments");
        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");
        var connectionString = _tenantContext.ConnectionString ?? throw new InvalidOperationException("Connection string is required.");

        var attachmentId = Guid.NewGuid();
        var itemGuid = itemId ?? TryParseGuid(formJsonId);
        var sql = $@"
            INSERT INTO {tableName}
            (id, tenant_id, workflow_instance_id, step_instance_id, repository_id, item_id, form_json_id,
             file_name, file_path, file_size, content_type, created_by, modified_by, created_at_utc, modified_at_utc, is_deleted)
            VALUES
            (@Id, @TenantId, @WorkflowInstanceId, @StepInstanceId, @RepositoryId, @ItemId, @FormJsonId,
             @FileName, @FilePath, @FileSize, @ContentType, @CreatedBy, @ModifiedBy, now(), now(), false)";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Id", attachmentId);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        command.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        command.Parameters.AddWithValue("@StepInstanceId", (object?)stepInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@RepositoryId", (object?)repositoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ItemId", (object?)itemGuid ?? DBNull.Value);
        command.Parameters.AddWithValue("@FormJsonId", (object?)(formJsonId ?? itemGuid?.ToString("N")) ?? DBNull.Value);
        command.Parameters.AddWithValue("@FileName", fileName);
        command.Parameters.AddWithValue("@FilePath", filePath);
        command.Parameters.AddWithValue("@FileSize", (object?)fileSize ?? DBNull.Value);
        command.Parameters.AddWithValue("@ContentType", (object?)contentType ?? DBNull.Value);
        command.Parameters.AddWithValue("@CreatedBy", createdBy);
        command.Parameters.AddWithValue("@ModifiedBy", createdBy);

        await command.ExecuteNonQueryAsync(cancellationToken);
        return attachmentId;
    }

    public async Task<IReadOnlyList<WorkflowCommentRowDto>> GetCommentsAsync(Guid workflowId, Guid workflowInstanceId, CancellationToken cancellationToken = default)
    {
        var tableName = GetTableName(workflowId, "workflow_comments");
        var connectionString = _tenantContext.ConnectionString ?? throw new InvalidOperationException("Connection string is required.");

        var sql = $@"
            SELECT
                id, workflow_instance_id, step_instance_id, comments, external_comments_by,
                show_to, embed_json, embed_status, created_at_utc, created_by
            FROM {tableName}
            WHERE workflow_instance_id = @WorkflowInstanceId AND is_deleted = false
            ORDER BY created_at_utc DESC";

        var results = new List<WorkflowCommentRowDto>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new WorkflowCommentRowDto(
                Id: reader.GetGuid(0),
                WorkflowInstanceId: reader.GetGuid(1),
                StepInstanceId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
                Comments: reader.GetString(3),
                ExternalCommentsBy: reader.IsDBNull(4) ? null : reader.GetString(4),
                ShowTo: reader.GetInt32(5),
                EmbedJson: reader.IsDBNull(6) ? null : reader.GetString(6),
                EmbedStatus: reader.GetBoolean(7),
                CreatedAtUtc: reader.GetDateTime(8),
                CreatedBy: reader.GetGuid(9)));
        }

        return results;
    }

    public async Task<IReadOnlyList<WorkflowAttachmentRowDto>> GetAttachmentsAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken = default)
    {
        var tableName = GetTableName(workflowId, "workflow_attachments");
        var connectionString = _tenantContext.ConnectionString ?? throw new InvalidOperationException("Connection string is required.");

        var sql = $@"
            SELECT
                id, workflow_instance_id, file_name, file_path, file_size, content_type,
                created_at_utc, created_by, modified_at_utc, modified_by,
                repository_id, item_id, form_json_id
            FROM {tableName}
            WHERE workflow_instance_id = @WorkflowInstanceId AND is_deleted = false
            ORDER BY created_at_utc DESC";

        var results = new List<WorkflowAttachmentRowDto>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(MapAttachmentRow(reader));

        return results;
    }

    private static WorkflowAttachmentRowDto MapAttachmentRow(NpgsqlDataReader reader)
    {
        var formJsonId = GetNullableString(reader, "form_json_id");
        var repositoryId = ReadRepositoryOrItemGuid(reader, "repository_id");
        var itemId = ReadRepositoryOrItemGuid(reader, "item_id") ?? TryParseGuid(formJsonId);

        return new WorkflowAttachmentRowDto(
            Id: reader.GetGuid(reader.GetOrdinal("id")),
            WorkflowInstanceId: reader.GetGuid(reader.GetOrdinal("workflow_instance_id")),
            FileName: GetNullableString(reader, "file_name"),
            FilePath: GetNullableString(reader, "file_path"),
            FileSize: GetNullableInt64(reader, "file_size"),
            ContentType: GetNullableString(reader, "content_type"),
            CreatedAtUtc: reader.GetDateTime(reader.GetOrdinal("created_at_utc")),
            CreatedBy: reader.GetGuid(reader.GetOrdinal("created_by")),
            ModifiedAtUtc: GetNullableDateTime(reader, "modified_at_utc"),
            ModifiedBy: GetNullableGuid(reader, "modified_by"),
            RepositoryId: repositoryId,
            ItemId: itemId,
            FormJsonId: formJsonId);
    }

    private static Guid? ReadRepositoryOrItemGuid(NpgsqlDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        if (reader.IsDBNull(i))
            return null;

        return reader.GetFieldType(i) == typeof(Guid)
            ? reader.GetGuid(i)
            : TryParseGuid(reader.GetValue(i)?.ToString());
    }

    private static Guid? TryParseGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (Guid.TryParse(trimmed, out var guid))
            return guid;

        return trimmed.Length == 32 && Guid.TryParseExact(trimmed, "N", out guid) ? guid : null;
    }

    private static string? GetNullableString(NpgsqlDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetString(i);
    }

    private static long? GetNullableInt64(NpgsqlDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetInt64(i);
    }

    private static int? GetNullableInt32(NpgsqlDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetInt32(i);
    }

    private static DateTime? GetNullableDateTime(NpgsqlDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetDateTime(i);
    }

    private static Guid? GetNullableGuid(NpgsqlDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetGuid(i);
    }
}
