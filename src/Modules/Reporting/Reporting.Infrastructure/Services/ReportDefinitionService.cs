using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Reporting.Application.Contracts;
using SaaSApp.Reporting.Infrastructure.Persistence;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Reporting.Infrastructure.Services;

public sealed class ReportDefinitionService : IReportDefinitionService
{
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IWorkflowRepository _workflows;
    private readonly IUserEmailLookup _userEmails;
    private readonly IReportScheduleService _schedule;

    public ReportDefinitionService(
        ITenantConnectionProvider connectionProvider,
        IWorkflowRepository workflows,
        IUserEmailLookup userEmails,
        IReportScheduleService schedule)
    {
        _connectionProvider = connectionProvider;
        _workflows = workflows;
        _userEmails = userEmails;
        _schedule = schedule;
    }

    public async Task<IReadOnlyList<ReportListItemDto>> ListAsync(
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        ReportListQuery query,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var connectionString = RequireConnection();
        var groupIds = await LoadUserGroupIdsAsync(connectionString, userId, cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT "Id", "Name", "Domain", "Status", "Scheduled", "OwnerUserId", "Runs", "ModifiedAtUtc",
                   "SourceType", "SourceFormId", "WorkflowId", "Visibility", "ConfigJson"
            FROM reporting."ReportDefinitions"
            WHERE "TenantId" = @TenantId AND "IsDeleted" = false
            ORDER BY COALESCE("ModifiedAtUtc", "CreatedAtUtc") DESC;
            """;
        var items = new List<ReportListItemDto>();
        var ownerIds = new HashSet<Guid>();
        var rows = new List<(ReportBuilderConfig Config, Guid OwnerUserId, DateTime? Modified, int Runs, bool Scheduled, string Status, string Name, string Domain, string? SourceType, string? SourceFormId, Guid? WorkflowId, string? Visibility, Guid Id)>();

        await using (var cmd = new NpgsqlCommand(sql, connection))
        {
            cmd.Parameters.AddWithValue("@TenantId", tenantId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var name = reader.GetString(1);
                var domain = reader.GetString(2);
                var status = reader.GetString(3);
                var scheduled = reader.GetBoolean(4);
                var ownerUserId = reader.GetGuid(5);
                var runs = reader.GetInt32(6);
                var modified = reader.IsDBNull(7) ? (DateTime?)null : reader.GetDateTime(7);
                var sourceType = reader.IsDBNull(8) ? null : reader.GetString(8);
                var sourceFormId = reader.IsDBNull(9) ? null : reader.GetString(9);
                var workflowId = reader.IsDBNull(10) ? (Guid?)null : reader.GetGuid(10);
                var visibility = reader.IsDBNull(11) ? null : reader.GetString(11);
                var configJson = reader.GetString(12);
                var config = ReportJson.Parse(configJson);
                ownerIds.Add(ownerUserId);
                rows.Add((config, ownerUserId, modified, runs, scheduled, status, name, domain, sourceType, sourceFormId, workflowId, visibility, id));
            }
        }

        var profiles = await _userEmails.GetProfilesAsync(ownerIds, cancellationToken);

        foreach (var row in rows)
        {
            if (!CanSee(isAdmin, userId, row.OwnerUserId, row.Config, groupIds))
                continue;
            if (!string.IsNullOrWhiteSpace(query.Domain)
                && !string.Equals(row.Domain, query.Domain, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(query.Status)
                && !string.Equals(row.Status, query.Status, StringComparison.OrdinalIgnoreCase))
                continue;
            if (query.Scheduled is bool scheduledFilter && row.Scheduled != scheduledFilter)
                continue;
            if (!string.IsNullOrWhiteSpace(query.Search)
                && row.Name.IndexOf(query.Search.Trim(), StringComparison.OrdinalIgnoreCase) < 0
                && row.Domain.IndexOf(query.Search.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var ownerName = row.OwnerUserId == userId
                ? "You"
                : profiles.TryGetValue(row.OwnerUserId, out var profile)
                    ? profile.DisplayName
                    : "Unknown";

            var canEdit = CanManage(isAdmin, userId, row.OwnerUserId);
            items.Add(new ReportListItemDto(
                row.Id,
                row.Name,
                row.Domain,
                row.Status,
                row.Scheduled,
                ownerName,
                row.Runs,
                row.Modified,
                row.SourceType,
                row.SourceFormId,
                row.WorkflowId,
                row.Visibility,
                canEdit,
                AccessLabel(isAdmin, userId, row.OwnerUserId),
                row.Config.SharedUsers));
        }

        return items;
    }

    public async Task<ReportBuilderConfig?> GetAsync(
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        Guid reportId,
        CancellationToken cancellationToken = default)
    {
        var row = await LoadRowAsync(tenantId, reportId, cancellationToken);
        if (row is null)
            return null;

        var groupIds = await LoadUserGroupIdsAsync(RequireConnection(), userId, cancellationToken);
        if (!CanSee(isAdmin, userId, row.OwnerUserId, row.Config, groupIds))
            return null;

        var ownerName = "Unknown";
        if (row.OwnerUserId == userId)
        {
            ownerName = "You";
        }
        else
        {
            var profiles = await _userEmails.GetProfilesAsync([row.OwnerUserId], cancellationToken);
            if (profiles.TryGetValue(row.OwnerUserId, out var profile)
                && !string.IsNullOrWhiteSpace(profile.DisplayName))
                ownerName = profile.DisplayName;
        }

        row.Config.Owner = ownerName;
        row.Config.OwnerUserId = row.OwnerUserId;
        row.Config.Runs = row.Runs;
        row.Config.Scheduled = row.Scheduled;
        row.Config.Status = row.Status;
        row.Config.CanEdit = CanManage(isAdmin, userId, row.OwnerUserId);
        row.Config.Access = AccessLabel(isAdmin, userId, row.OwnerUserId);
        if (row.Config.WorkflowId is Guid wf && wf != Guid.Empty)
            row.Config.SourceId ??= wf;
        return row.Config;
    }

    public async Task<ReportBuilderConfig> SaveAsync(
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        ReportBuilderConfig config,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            throw new ArgumentException("Report name is required.");
        if (string.IsNullOrWhiteSpace(config.Domain))
            throw new ArgumentException("Domain (workflow name) is required.");

        await EnsureSchemaAsync(cancellationToken);
        NormalizeSharing(config);
        await ResolveWorkflowOnConfigAsync(tenantId, config, cancellationToken);

        var now = DateTime.UtcNow;
        if (config.Id == Guid.Empty)
            config.Id = Guid.NewGuid();

        var existing = await LoadRowAsync(tenantId, config.Id, cancellationToken);
        if (existing is not null && !CanManage(isAdmin, userId, existing.OwnerUserId))
            throw new UnauthorizedAccessException("Only the owner or an Admin can update this report.");

        config.OwnerUserId = existing?.OwnerUserId ?? userId;
        config.CreatedAt ??= existing?.CreatedAtUtc ?? now;
        config.Modified = now;
        config.Runs = existing?.Runs ?? 0;
        if (string.IsNullOrWhiteSpace(config.Status))
            config.Status = existing?.Status ?? ReportStatuses.Draft;
        config.CanEdit = true;
        config.Access = existing is not null && existing.OwnerUserId != userId && isAdmin
            ? "admin"
            : "owner";
        config.Owner = existing is not null && existing.OwnerUserId != userId ? config.Owner : "You";

        var hangfireJobId = _schedule.JobId(tenantId, config.Id);
        var connectionString = RequireConnection();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (existing is null)
        {
            const string insert = """
                INSERT INTO reporting."ReportDefinitions"
                    ("Id", "TenantId", "Name", "Domain", "Description", "SourceType", "SourceFormId", "WorkflowId",
                     "Status", "Visibility", "OwnerUserId", "Scheduled", "Runs", "ConfigJson", "HangfireJobId",
                     "CreatedAtUtc", "ModifiedAtUtc", "CreatedBy", "ModifiedBy", "IsDeleted")
                VALUES
                    (@Id, @TenantId, @Name, @Domain, @Description, @SourceType, @SourceFormId, @WorkflowId,
                     @Status, @Visibility, @OwnerUserId, @Scheduled, 0, @ConfigJson, @HangfireJobId,
                     @CreatedAtUtc, @ModifiedAtUtc, @CreatedBy, @ModifiedBy, false);
                """;
            await using var cmd = new NpgsqlCommand(insert, connection);
            Bind(cmd, tenantId, config, hangfireJobId, now, userId, isInsert: true);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            const string update = """
                UPDATE reporting."ReportDefinitions"
                SET "Name" = @Name,
                    "Domain" = @Domain,
                    "Description" = @Description,
                    "SourceType" = @SourceType,
                    "SourceFormId" = @SourceFormId,
                    "WorkflowId" = @WorkflowId,
                    "Status" = @Status,
                    "Visibility" = @Visibility,
                    "Scheduled" = @Scheduled,
                    "ConfigJson" = @ConfigJson,
                    "HangfireJobId" = @HangfireJobId,
                    "ModifiedAtUtc" = @ModifiedAtUtc,
                    "ModifiedBy" = @ModifiedBy
                WHERE "Id" = @Id AND "TenantId" = @TenantId AND "IsDeleted" = false;
                """;
            await using var cmd = new NpgsqlCommand(update, connection);
            Bind(cmd, tenantId, config, hangfireJobId, now, userId, isInsert: false);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        _schedule.Sync(tenantId, config);
        config.CanEdit = true;
        config.Access = AccessLabel(isAdmin, userId, config.OwnerUserId ?? userId);
        return config;
    }

    public async Task<ReportBuilderConfig> PublishAsync(
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        Guid reportId,
        CancellationToken cancellationToken = default)
    {
        var config = await GetAsync(tenantId, userId, isAdmin, reportId, cancellationToken)
            ?? throw new KeyNotFoundException("Report not found.");
        if (!CanManage(isAdmin, userId, config.OwnerUserId ?? userId))
            throw new UnauthorizedAccessException("Only the owner or an Admin can publish this report.");
        config.Status = ReportStatuses.Published;
        return await SaveAsync(tenantId, userId, isAdmin, config, cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        Guid reportId,
        CancellationToken cancellationToken = default)
    {
        var existing = await LoadRowAsync(tenantId, reportId, cancellationToken);
        if (existing is null)
            return false;
        if (!CanManage(isAdmin, userId, existing.OwnerUserId))
            throw new UnauthorizedAccessException("Only the owner or an Admin can delete this report.");

        await using var connection = new NpgsqlConnection(RequireConnection());
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE reporting."ReportDefinitions"
            SET "IsDeleted" = true, "ModifiedAtUtc" = now(), "ModifiedBy" = @UserId, "Scheduled" = false
            WHERE "Id" = @Id AND "TenantId" = @TenantId AND "IsDeleted" = false;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", reportId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@UserId", userId);
        var n = await cmd.ExecuteNonQueryAsync(cancellationToken);
        _schedule.Remove(tenantId, reportId);
        return n > 0;
    }

    public async Task<ReportBuilderConfig?> GetForJobAsync(
        Guid tenantId,
        Guid reportId,
        CancellationToken cancellationToken = default)
    {
        var row = await LoadRowAsync(tenantId, reportId, cancellationToken);
        if (row is null)
            return null;

        row.Config.OwnerUserId = row.OwnerUserId;
        row.Config.Runs = row.Runs;
        row.Config.Scheduled = row.Scheduled;
        row.Config.Status = row.Status;
        return row.Config;
    }

    public async Task IncrementRunsAsync(Guid tenantId, Guid reportId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new NpgsqlConnection(RequireConnection());
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE reporting."ReportDefinitions"
            SET "Runs" = "Runs" + 1, "ModifiedAtUtc" = now()
            WHERE "Id" = @Id AND "TenantId" = @TenantId AND "IsDeleted" = false;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", reportId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ResolveWorkflowOnConfigAsync(
        Guid tenantId,
        ReportBuilderConfig config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.SourceType))
            config.SourceType = "Workflow";

        await using (var connection = new NpgsqlConnection(RequireConnection()))
        {
            await connection.OpenAsync(cancellationToken);
            foreach (var key in new[] { config.SourceFormId, config.SourceForm, config.Domain })
            {
                var wform = await ReportEzfbNaming.FindWFormAsync(connection, key, cancellationToken);
                if (wform == null)
                    continue;
                config.SourceFormId = ReportEzfbNaming.NormalizeFormId(wform.Value.FormId);
                config.SourceForm = wform.Value.FormName;
                if (string.IsNullOrWhiteSpace(config.Domain))
                    config.Domain = wform.Value.FormName;
                break;
            }
        }

        if (config.WorkflowId is not Guid wfId || wfId == Guid.Empty)
        {
            if (config.SourceId is Guid sid && sid != Guid.Empty)
                config.WorkflowId = sid;
        }
        else
        {
            config.SourceId ??= config.WorkflowId;
        }

        if (!string.IsNullOrWhiteSpace(config.Domain))
        {
            var byName = await _workflows.GetByNameAsync(config.Domain.Trim(), tenantId, cancellationToken);
            if (byName is null)
            {
                var all = await _workflows.ListAsync(cancellationToken);
                byName = all.FirstOrDefault(w =>
                    string.Equals(w.Name, config.Domain.Trim(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(w.FormId, config.SourceFormId, StringComparison.OrdinalIgnoreCase));
            }

            if (byName != null)
            {
                config.WorkflowId = byName.Id;
                config.SourceId ??= byName.Id;
                if (string.IsNullOrWhiteSpace(config.SourceFormId) && !string.IsNullOrWhiteSpace(byName.FormId))
                    config.SourceFormId = byName.FormId;
            }
        }

        if (!string.IsNullOrWhiteSpace(config.SourceFormId))
            config.SourceFormId = ReportEzfbNaming.NormalizeFormId(config.SourceFormId);
    }

    private async Task<StoredRow?> LoadRowAsync(Guid tenantId, Guid reportId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new NpgsqlConnection(RequireConnection());
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT "ConfigJson", "OwnerUserId", "Runs", "Scheduled", "Status", "CreatedAtUtc"
            FROM reporting."ReportDefinitions"
            WHERE "Id" = @Id AND "TenantId" = @TenantId AND "IsDeleted" = false;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", reportId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var config = ReportJson.Parse(reader.GetString(0));
        config.Id = reportId;
        return new StoredRow(
            config,
            reader.GetGuid(1),
            reader.GetInt32(2),
            reader.GetBoolean(3),
            reader.GetString(4),
            reader.GetDateTime(5));
    }

    private static void Bind(
        NpgsqlCommand cmd,
        Guid tenantId,
        ReportBuilderConfig config,
        string hangfireJobId,
        DateTime now,
        Guid userId,
        bool isInsert)
    {
        cmd.Parameters.AddWithValue("@Id", config.Id);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@Name", config.Name.Trim());
        cmd.Parameters.AddWithValue("@Domain", config.Domain.Trim());
        cmd.Parameters.AddWithValue("@Description", (object?)config.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SourceType", (object?)config.SourceType ?? "Workflow");
        cmd.Parameters.AddWithValue("@SourceFormId", (object?)config.SourceFormId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@WorkflowId", (object?)config.WorkflowId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Status", string.IsNullOrWhiteSpace(config.Status) ? ReportStatuses.Draft : config.Status);
        cmd.Parameters.AddWithValue("@Visibility", (object?)config.Visibility ?? "Private");
        cmd.Parameters.AddWithValue("@Scheduled", config.Scheduled);
        cmd.Parameters.AddWithValue("@ConfigJson", ReportJson.Serialize(config));
        cmd.Parameters.AddWithValue("@HangfireJobId", hangfireJobId);
        cmd.Parameters.AddWithValue("@ModifiedAtUtc", now);
        cmd.Parameters.AddWithValue("@ModifiedBy", userId);
        if (isInsert)
        {
            cmd.Parameters.AddWithValue("@OwnerUserId", config.OwnerUserId ?? userId);
            cmd.Parameters.AddWithValue("@CreatedAtUtc", config.CreatedAt ?? now);
            cmd.Parameters.AddWithValue("@CreatedBy", userId);
        }
    }

    private static void NormalizeSharing(ReportBuilderConfig config)
    {
        if (config.WorkflowId is not Guid wfId || wfId == Guid.Empty)
        {
            if (config.SourceId is Guid sourceId && sourceId != Guid.Empty)
                config.WorkflowId = sourceId;
        }
        else
        {
            config.SourceId ??= config.WorkflowId;
        }

        config.SharedUsers = DistinctTokens(config.SharedUsers);
        config.SharedGroups = DistinctTokens(config.SharedGroups);

        var vis = (config.Visibility ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(vis) || vis.Equals("Private", StringComparison.OrdinalIgnoreCase))
        {
            if (config.SharedUsers.Count > 0)
                config.Visibility = "Selected Users";
            else if (config.SharedGroups.Count > 0)
                config.Visibility = "Selected Groups";
            else
                config.Visibility = string.IsNullOrWhiteSpace(vis) ? "Private" : vis;
        }
    }

    private static List<string> DistinctTokens(IEnumerable<string>? values) =>
        (values ?? [])
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(v => v.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static bool CanManage(bool isAdmin, Guid userId, Guid ownerUserId) =>
        isAdmin || ownerUserId == userId;

    private static string AccessLabel(bool isAdmin, Guid userId, Guid ownerUserId)
    {
        if (ownerUserId == userId)
            return "owner";
        return isAdmin ? "admin" : "shared";
    }

    private static bool CanSee(
        bool isAdmin,
        Guid userId,
        Guid ownerUserId,
        ReportBuilderConfig config,
        IReadOnlySet<Guid> groupIds)
    {
        if (CanManage(isAdmin, userId, ownerUserId))
            return true;

        if (ContainsUser(config.SharedUsers, userId))
            return true;

        if (config.SharedGroups.Any(g =>
                Guid.TryParse(g, out var id) && groupIds.Contains(id)))
            return true;

        var vis = config.Visibility ?? "Private";
        if (vis.Equals("Selected Users", StringComparison.OrdinalIgnoreCase)
            || vis.Equals("SelectedUsers", StringComparison.OrdinalIgnoreCase))
        {
            return ContainsUser(config.SharedUsers, userId);
        }

        if (vis.Equals("Selected Groups", StringComparison.OrdinalIgnoreCase)
            || vis.Equals("SelectedGroups", StringComparison.OrdinalIgnoreCase))
        {
            return config.SharedGroups.Any(g =>
                Guid.TryParse(g, out var id) && groupIds.Contains(id));
        }

        return false;
    }

    private static bool ContainsUser(IEnumerable<string>? tokens, Guid userId)
    {
        foreach (var token in tokens ?? [])
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;
            if (Guid.TryParse(token.Trim(), out var id) && id == userId)
                return true;
        }

        return false;
    }

    private static async Task<HashSet<Guid>> LoadUserGroupIdsAsync(
        string connectionString,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            const string sql = """
                SELECT "GroupId"
                FROM users."UserGroups"
                WHERE "UserId" = @UserId;
                """;
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@UserId", userId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetGuid(0));
        }
        catch (PostgresException)
        {
            // UserGroups may not exist on every tenant database.
        }
        catch (NpgsqlException)
        {
            // UserGroups may not exist on every tenant database.
        }

        return ids;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(RequireConnection());
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ReportSchemaSql.Ensure, connection) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private string RequireConnection() =>
        _connectionProvider.ConnectionString
        ?? throw new InvalidOperationException("Tenant connection string not resolved.");

    private sealed record StoredRow(
        ReportBuilderConfig Config,
        Guid OwnerUserId,
        int Runs,
        bool Scheduled,
        string Status,
        DateTime CreatedAtUtc);
}
