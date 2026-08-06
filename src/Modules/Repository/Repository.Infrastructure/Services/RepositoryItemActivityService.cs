using Microsoft.Data.SqlClient;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class RepositoryItemActivityService : IRepositoryItemActivityService
{
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IStaticRepositoryProvisioner _provisioner;

    public RepositoryItemActivityService(
        ITenantConnectionProvider connectionProvider,
        IStaticRepositoryProvisioner provisioner)
    {
        _connectionProvider = connectionProvider;
        _provisioner = provisioner;
    }

    public async Task<RepositoryItemTimelineResultDto?> GetTimelineAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        if (!await ItemExistsAsync(repositoryId, tenantId, itemId, cancellationToken))
            return null;

        var connectionString = RequireConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var stored = new List<RepositoryItemTimelineEventDto>();
        if (await TimelineTableExistsAsync(connection, cancellationToken))
        {
            const string sql = """
                SELECT Id, EventType, Title, Description, ActorType, ActorName, ActorUserId, CreatedAtUtc
                FROM repository.ItemTimelineEvents
                WHERE RepositoryId = @RepositoryId AND ItemId = @ItemId AND TenantId = @TenantId AND IsDeleted = 0
                ORDER BY CreatedAtUtc ASC;
                """;

            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
            cmd.Parameters.AddWithValue("@ItemId", itemId);
            cmd.Parameters.AddWithValue("@TenantId", tenantId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var title = reader.GetString(2);
                // Hide noisy metadata-change rows from the FE timeline.
                if (string.Equals(title, "Metadata updated", StringComparison.OrdinalIgnoreCase))
                    continue;

                stored.Add(new RepositoryItemTimelineEventDto(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    title,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetDateTime(7),
                    IsDerived: false));
            }
        }

        var fields = await LoadItemFieldsForTimelineAsync(repositoryId, tenantId, itemId, cancellationToken);
        string? createdByName = null;
        Guid? linkedInstanceId = null;
        Guid? linkedWorkflowId = null;
        string? linkedReference = null;

        if (fields != null)
        {
            if (fields.TryGetValue("CreatedBy", out var createdByRaw)
                && RepositoryUserNameResolver.TryParseUserId(createdByRaw, out var createdById))
            {
                var profile = await RepositoryUserNameResolver.ResolveProfileAsync(connection, createdById, cancellationToken);
                createdByName = RepositoryUserNameResolver.PreferEmail(profile?.Email, profile?.DisplayName, createdById);
            }

            if (fields.TryGetValue("WorkflowInstanceId", out var wfRaw)
                && RepositoryUserNameResolver.TryParseUserId(wfRaw, out var wfId))
            {
                linkedInstanceId = wfId;
            }
        }

        var events = new List<RepositoryItemTimelineEventDto>();
        if (fields != null)
            events.AddRange(RepositoryItemTimelineDeriver.Derive(fields, createdByName));

        events.AddRange(stored);

        if (linkedInstanceId is Guid instanceId)
        {
            var workflowEvents = await LoadWorkflowHistoryEventsAsync(connection, instanceId, cancellationToken);
            events.AddRange(workflowEvents.Events);
            linkedWorkflowId = workflowEvents.WorkflowId;
            linkedReference = workflowEvents.ReferenceNumber;
        }

        events = await ResolveActorNamesAsync(connection, events, cancellationToken);
        events = events
            .OrderBy(e => e.CreatedAtUtc)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RepositoryItemTimelineResultDto(
            events,
            events.Count,
            linkedInstanceId,
            linkedWorkflowId,
            linkedReference);
    }

    public async Task<RepositoryItemTimelineEventDto?> AddTimelineEventAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        AddRepositoryItemTimelineEventRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ArgumentException("Title is required.");

        if (!await ItemExistsAsync(repositoryId, tenantId, itemId, cancellationToken))
            return null;

        var eventType = string.IsNullOrWhiteSpace(request.EventType) ? "user" : request.EventType.Trim();
        var actorName = request.ActorName;
        if (string.IsNullOrWhiteSpace(actorName) && userId.HasValue)
        {
            await using var connection = new SqlConnection(RequireConnectionString());
            await connection.OpenAsync(cancellationToken);
            var profile = await RepositoryUserNameResolver.ResolveProfileAsync(connection, userId.Value, cancellationToken);
            actorName = RepositoryUserNameResolver.PreferEmail(profile?.Email, profile?.DisplayName, userId);
        }

        var connectionString = RequireConnectionString();
        await using var writeConnection = new SqlConnection(connectionString);
        await writeConnection.OpenAsync(cancellationToken);
        if (!await TimelineTableExistsAsync(writeConnection, cancellationToken))
            throw new InvalidOperationException("Timeline is not enabled for this tenant database. Apply repository schema or run CreateRepositorySchema.sql.");

        var id = Guid.NewGuid();
        await RecordTimelineEventInternalAsync(
            id,
            repositoryId,
            tenantId,
            itemId,
            eventType,
            request.Title.Trim(),
            string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            eventType.Equals("system", StringComparison.OrdinalIgnoreCase) ? "System" : "User",
            actorName,
            userId,
            userId,
            cancellationToken);

        return new RepositoryItemTimelineEventDto(
            id,
            eventType,
            request.Title.Trim(),
            string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            eventType.Equals("system", StringComparison.OrdinalIgnoreCase) ? "System" : "User",
            actorName,
            DateTime.UtcNow);
    }

    public Task RecordTimelineEventAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        string eventType,
        string title,
        string? description,
        string actorType,
        string? actorName,
        Guid? actorUserId,
        Guid? createdBy,
        CancellationToken cancellationToken = default) =>
        RecordTimelineEventInternalAsync(
            Guid.NewGuid(),
            repositoryId,
            tenantId,
            itemId,
            eventType,
            title,
            description,
            actorType,
            actorName,
            actorUserId,
            createdBy,
            cancellationToken);

    public async Task<RepositoryItemCommentsResultDto?> GetCommentsAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (!await ItemExistsAsync(repositoryId, tenantId, itemId, cancellationToken))
            return null;

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var offset = (page - 1) * pageSize;

        var connectionString = RequireConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await CommentsTableExistsAsync(connection, cancellationToken))
            return new RepositoryItemCommentsResultDto(Array.Empty<RepositoryItemCommentDto>(), 0, page, pageSize);

        const string countSql = """
            SELECT COUNT(1)
            FROM repository.ItemComments
            WHERE RepositoryId = @RepositoryId AND ItemId = @ItemId AND TenantId = @TenantId AND IsDeleted = 0;
            """;

        int total;
        await using (var countCmd = new SqlCommand(countSql, connection))
        {
            countCmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
            countCmd.Parameters.AddWithValue("@ItemId", itemId);
            countCmd.Parameters.AddWithValue("@TenantId", tenantId);
            total = (int)(await countCmd.ExecuteScalarAsync(cancellationToken) ?? 0);
        }

        const string sql = """
            SELECT Id, Body, CreatedBy, CreatedAtUtc, ModifiedAtUtc
            FROM repository.ItemComments
            WHERE RepositoryId = @RepositoryId AND ItemId = @ItemId AND TenantId = @TenantId AND IsDeleted = 0
            ORDER BY CreatedAtUtc DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        var raw = new List<(Guid Id, string Body, Guid AuthorUserId, DateTime CreatedAtUtc, DateTime? ModifiedAtUtc)>();
        await using (var cmd = new SqlCommand(sql, connection))
        {
            cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
            cmd.Parameters.AddWithValue("@ItemId", itemId);
            cmd.Parameters.AddWithValue("@TenantId", tenantId);
            cmd.Parameters.AddWithValue("@Offset", offset);
            cmd.Parameters.AddWithValue("@PageSize", pageSize);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                raw.Add((
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetGuid(2),
                    reader.GetDateTime(3),
                    reader.IsDBNull(4) ? null : reader.GetDateTime(4)));
            }
        }

        // Reader must be closed before another command uses this connection.
        var profiles = await RepositoryUserNameResolver.ResolveProfilesAsync(
            connection, raw.Select(r => r.AuthorUserId), cancellationToken);

        var comments = raw.Select(r =>
        {
            profiles.TryGetValue(r.AuthorUserId, out var profile);
            var authorEmail = profile.Email;
            var authorName = RepositoryUserNameResolver.PreferEmail(authorEmail, profile.DisplayName, r.AuthorUserId);
            return new RepositoryItemCommentDto(
                r.Id,
                r.Body,
                r.AuthorUserId,
                r.CreatedAtUtc,
                r.ModifiedAtUtc,
                authorName,
                authorEmail);
        }).ToList();

        return new RepositoryItemCommentsResultDto(comments, total, page, pageSize);
    }

    public async Task<AddRepositoryItemCommentResult?> AddCommentAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        AddRepositoryItemCommentRequest request,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            throw new ArgumentException("Comment body is required.");

        if (!await ItemExistsAsync(repositoryId, tenantId, itemId, cancellationToken))
            return null;

        var commentId = Guid.NewGuid();
        var connectionString = RequireConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await CommentsTableExistsAsync(connection, cancellationToken))
            throw new InvalidOperationException("Comments are not enabled for this tenant database. Apply repository schema or run CreateRepositorySchema.sql.");

        const string sql = """
            INSERT INTO repository.ItemComments (Id, TenantId, RepositoryId, ItemId, Body, CreatedBy)
            VALUES (@Id, @TenantId, @RepositoryId, @ItemId, @Body, @CreatedBy);
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", commentId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        cmd.Parameters.AddWithValue("@ItemId", itemId);
        cmd.Parameters.AddWithValue("@Body", request.Body.Trim());
        cmd.Parameters.AddWithValue("@CreatedBy", userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        var profile = await RepositoryUserNameResolver.ResolveProfileAsync(connection, userId, cancellationToken);
        return new AddRepositoryItemCommentResult(
            commentId,
            RepositoryUserNameResolver.PreferEmail(profile?.Email, profile?.DisplayName, userId),
            profile?.Email);
    }

    private async Task RecordTimelineEventInternalAsync(
        Guid id,
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        string eventType,
        string title,
        string? description,
        string actorType,
        string? actorName,
        Guid? actorUserId,
        Guid? createdBy,
        CancellationToken cancellationToken)
    {
        var connectionString = RequireConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await TimelineTableExistsAsync(connection, cancellationToken))
            return;

        if (string.IsNullOrWhiteSpace(actorName) && actorUserId is Guid uid)
        {
            var profile = await RepositoryUserNameResolver.ResolveProfileAsync(connection, uid, cancellationToken);
            actorName = RepositoryUserNameResolver.PreferEmail(profile?.Email, profile?.DisplayName, uid);
        }

        const string sql = """
            INSERT INTO repository.ItemTimelineEvents
                (Id, TenantId, RepositoryId, ItemId, EventType, Title, Description, ActorType, ActorName, ActorUserId, CreatedBy)
            VALUES
                (@Id, @TenantId, @RepositoryId, @ItemId, @EventType, @Title, @Description, @ActorType, @ActorName, @ActorUserId, @CreatedBy);
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        cmd.Parameters.AddWithValue("@ItemId", itemId);
        cmd.Parameters.AddWithValue("@EventType", eventType);
        cmd.Parameters.AddWithValue("@Title", title);
        cmd.Parameters.AddWithValue("@Description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ActorType", actorType);
        cmd.Parameters.AddWithValue("@ActorName", (object?)actorName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ActorUserId", (object?)actorUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, object?>?> LoadItemFieldsForTimelineAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken);
        if (repo == null)
            return null;

        var table = RepositorySqlHelper.QualifiedItemsTable(repo.ItemsTableName);
        var connectionString = RequireConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var columns = await RepositoryItemTableColumns.LoadAsync(connection, repo.ItemsTableName, cancellationToken);
        var selectCols = new List<string> { "CreatedAtUtc" };
        foreach (var col in new[] { "Source", "OcrScore", "AiStatus", "MatchedStatus", "WorkflowInstanceId", "CreatedBy" })
        {
            if (RepositoryItemTableColumns.Has(columns, col))
                selectCols.Add(col);
        }

        var sql = $"""
            SELECT {string.Join(", ", selectCols.Select(c => $"[{c}]"))}
            FROM {table}
            WHERE Id = @ItemId AND RepositoryId = @RepositoryId AND TenantId = @TenantId AND IsDeleted = 0;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@ItemId", itemId);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < selectCols.Count; i++)
            map[selectCols[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);

        return map;
    }

    private static async Task<(IReadOnlyList<RepositoryItemTimelineEventDto> Events, Guid? WorkflowId, string? ReferenceNumber)> LoadWorkflowHistoryEventsAsync(
        SqlConnection connection,
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        if (!await WorkflowTableExistsAsync(connection, "WorkflowInstanceLookup", cancellationToken))
            return (Array.Empty<RepositoryItemTimelineEventDto>(), null, null);

        Guid? workflowId = null;
        string? referenceNumber = null;
        const string lookupSql = """
            SELECT TOP 1 WorkflowId, WorkflowName
            FROM workflow.WorkflowInstanceLookup
            WHERE InstanceId = @InstanceId;
            """;
        string? workflowName = null;
        await using (var lookupCmd = new SqlCommand(lookupSql, connection))
        {
            lookupCmd.Parameters.AddWithValue("@InstanceId", workflowInstanceId);
            await using var lookupReader = await lookupCmd.ExecuteReaderAsync(cancellationToken);
            if (!await lookupReader.ReadAsync(cancellationToken))
                return (Array.Empty<RepositoryItemTimelineEventDto>(), null, null);
            workflowId = lookupReader.GetGuid(0);
            workflowName = lookupReader.IsDBNull(1) ? null : lookupReader.GetString(1);
        }

        var suffix = workflowId.Value.ToString("N")[..8];
        var instancesTable = $"WorkflowInstances_{suffix}";
        if (await WorkflowTableExistsAsync(connection, instancesTable, cancellationToken))
        {
            var refSql = $"""
                SELECT TOP 1 ReferenceNumber
                FROM workflow.[{instancesTable}]
                WHERE Id = @InstanceId;
                """;
            await using var refCmd = new SqlCommand(refSql, connection);
            refCmd.Parameters.AddWithValue("@InstanceId", workflowInstanceId);
            var refObj = await refCmd.ExecuteScalarAsync(cancellationToken);
            if (refObj is string s && !string.IsNullOrWhiteSpace(s))
                referenceNumber = s.Trim();
        }

        referenceNumber ??= workflowName;
        var txTable = $"transaction_{suffix}";
        if (!await WorkflowTableExistsAsync(connection, txTable, cancellationToken))
            return (Array.Empty<RepositoryItemTimelineEventDto>(), workflowId, referenceNumber);

        var sql = $"""
            SELECT Id, StageName, StageType, Review, ActionStatus, ActivityUserId, CreatedBy, ModifiedBy, CreatedAt, ModifiedAt
            FROM workflow.[{txTable}]
            WHERE WorkflowInstanceId = @InstanceId AND IsDeleted = 0
            ORDER BY Id ASC;
            """;

        var rows = new List<(int Id, string? StageName, string? StageType, string? Review, int ActionStatus, Guid? ActivityUserId, Guid? CreatedBy, Guid? ModifiedBy, DateTime CreatedAt, DateTime? ModifiedAt)>();
        await using (var cmd = new SqlCommand(sql, connection))
        {
            cmd.Parameters.AddWithValue("@InstanceId", workflowInstanceId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5),
                    reader.IsDBNull(6) ? null : reader.GetGuid(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    reader.GetDateTime(8),
                    reader.IsDBNull(9) ? null : reader.GetDateTime(9)));
            }
        }

        var userIds = rows
            .SelectMany(r => new[] { r.ActivityUserId, r.CreatedBy, r.ModifiedBy })
            .Where(id => id is Guid g && g != Guid.Empty)
            .Select(id => id!.Value);
        var names = await RepositoryUserNameResolver.ResolveDisplayNamesAsync(connection, userIds, cancellationToken);

        var events = new List<RepositoryItemTimelineEventDto>();
        foreach (var row in rows)
        {
            var stage = string.IsNullOrWhiteSpace(row.StageName) ? row.StageType : row.StageName;
            stage = string.IsNullOrWhiteSpace(stage) ? "Stage" : stage.Trim();
            var performerId = row.ModifiedBy ?? row.ActivityUserId ?? row.CreatedBy;
            names.TryGetValue(performerId ?? Guid.Empty, out var performerName);
            if (string.IsNullOrWhiteSpace(performerName))
                performerName = "System";

            string title;
            string? description = null;
            var when = row.ModifiedAt ?? row.CreatedAt;

            if (string.Equals(row.StageType, "END", StringComparison.OrdinalIgnoreCase))
            {
                title = "Workflow completed";
                description = string.IsNullOrWhiteSpace(row.Review) ? null : row.Review;
            }
            else if (!string.IsNullOrWhiteSpace(row.Review))
            {
                var review = row.Review.Trim();
                if (review.Contains("approv", StringComparison.OrdinalIgnoreCase))
                    title = $"{stage} approval granted";
                else if (review.Contains("verif", StringComparison.OrdinalIgnoreCase))
                    title = $"{stage} verified";
                else if (review.Contains("reject", StringComparison.OrdinalIgnoreCase)
                         || review.Contains("escalat", StringComparison.OrdinalIgnoreCase))
                    title = $"{stage}: {review}";
                else
                    title = $"{stage} — {review}";
                description = review;
            }
            else if (row.ActionStatus == 0)
            {
                title = $"Pending — {stage}";
                if (row.ActivityUserId is Guid assignee && names.TryGetValue(assignee, out var assigneeName))
                    description = $"Assigned to {assigneeName}";
            }
            else
            {
                title = $"Moved to {stage}";
            }

            events.Add(new RepositoryItemTimelineEventDto(
                Guid.Empty,
                "workflow",
                title,
                description,
                "User",
                performerName,
                when,
                IsDerived: true));
        }

        return (events, workflowId, referenceNumber);
    }

    private static async Task<List<RepositoryItemTimelineEventDto>> ResolveActorNamesAsync(
        SqlConnection connection,
        List<RepositoryItemTimelineEventDto> events,
        CancellationToken cancellationToken)
    {
        var guidActors = new List<Guid>();
        foreach (var evt in events)
        {
            if (RepositoryUserNameResolver.TryParseUserId(evt.ActorName, out var id))
                guidActors.Add(id);
        }

        if (guidActors.Count == 0)
            return events;

        var names = await RepositoryUserNameResolver.ResolveDisplayNamesAsync(connection, guidActors, cancellationToken);
        for (var i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            if (RepositoryUserNameResolver.TryParseUserId(evt.ActorName, out var id)
                && names.TryGetValue(id, out var name))
            {
                events[i] = evt with { ActorName = name };
            }
        }

        return events;
    }

    private async Task<bool> ItemExistsAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken);
        if (repo == null)
            throw new InvalidOperationException("Repository not found.");

        var table = RepositorySqlHelper.QualifiedItemsTable(repo.ItemsTableName);
        var connectionString = RequireConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"SELECT 1 FROM {table} WHERE Id = @ItemId AND RepositoryId = @RepositoryId AND TenantId = @TenantId AND IsDeleted = 0;";
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@ItemId", itemId);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        return await cmd.ExecuteScalarAsync(cancellationToken) != null;
    }

    private string RequireConnectionString() =>
        _connectionProvider.ConnectionString
        ?? throw new InvalidOperationException("Tenant connection string not resolved.");

    private static Task<bool> TimelineTableExistsAsync(SqlConnection connection, CancellationToken cancellationToken) =>
        TableExistsAsync(connection, "repository", "ItemTimelineEvents", cancellationToken);

    private static Task<bool> CommentsTableExistsAsync(SqlConnection connection, CancellationToken cancellationToken) =>
        TableExistsAsync(connection, "repository", "ItemComments", cancellationToken);

    private static Task<bool> WorkflowTableExistsAsync(SqlConnection connection, string tableName, CancellationToken cancellationToken) =>
        TableExistsAsync(connection, "workflow", tableName, cancellationToken);

    private static async Task<bool> TableExistsAsync(
        SqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1 FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = @Schema AND t.name = @Table;
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@Table", table);
        return await cmd.ExecuteScalarAsync(cancellationToken) != null;
    }
}
