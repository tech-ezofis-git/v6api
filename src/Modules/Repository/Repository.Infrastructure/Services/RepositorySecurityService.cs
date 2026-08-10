using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class RepositorySecurityService : IRepositorySecurityService
{
    private static readonly ConcurrentDictionary<string, byte> SchemaEnsured = new(StringComparer.OrdinalIgnoreCase);

    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IStaticRepositoryProvisioner _provisioner;

    public RepositorySecurityService(
        ITenantConnectionProvider connectionProvider,
        IStaticRepositoryProvisioner provisioner)
    {
        _connectionProvider = connectionProvider;
        _provisioner = provisioner;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var cs = RequireConnectionString();
        if (SchemaEnsured.ContainsKey(cs))
            return;

        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(EnsureSchemaSql, connection) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        SchemaEnsured.TryAdd(cs, 0);
    }

    public async Task<RepositoryFolderSecurityDto> GetFolderSecurityAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid? folderId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureRepositoryExistsAsync(repositoryId, tenantId, cancellationToken);

        // Repository = folder: load repository-scoped policies (FolderId NULL or = repositoryId).
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT "Id", "FolderId", "CanView", "CanUpload", "CanDownload", "CanPrint", "CanDelete",
                   "CanEditMetadata", "CanEditDocument", "CanCheckOut", "CanCheckIn", "CanSendForSignature"
            FROM repository."FolderSecurityPolicies"
            WHERE "RepositoryId" = @RepositoryId AND "IsDeleted" = false
            ORDER BY "CreatedAtUtc"
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);

        var policies = new List<(Guid Id, RepositoryPermissionFlagsDto Perms)>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                policies.Add((
                    reader.GetGuid(0),
                    new RepositoryPermissionFlagsDto(
                        reader.GetBoolean(2),
                        reader.GetBoolean(3),
                        reader.GetBoolean(4),
                        reader.GetBoolean(5),
                        reader.GetBoolean(6),
                        reader.GetBoolean(7),
                        reader.GetBoolean(8),
                        reader.GetBoolean(9),
                        reader.GetBoolean(10),
                        reader.GetBoolean(11))));
            }
        }

        var result = new List<RepositoryFolderSecurityPolicyDto>();
        foreach (var (policyId, perms) in policies)
        {
            var (userIds, groupIds) = await LoadPrincipalsAsync(connection, "FolderSecurityPrincipals", "PolicyId", policyId, cancellationToken);
            result.Add(new RepositoryFolderSecurityPolicyDto(userIds, groupIds, perms, FolderId: null));
        }

        return new RepositoryFolderSecurityDto(repositoryId, result);
    }

    public async Task<RepositoryFolderSecurityDto> SaveFolderSecurityAsync(
        Guid repositoryId,
        Guid tenantId,
        RepositoryFolderSecurityUpsertRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureRepositoryExistsAsync(repositoryId, tenantId, cancellationToken);

        // Repository = folder: ignore client folderId / treat repo id as whole-repository scope.
        _ = NormalizeFolderId(repositoryId, request.FolderId);
        var policies = request.Policies ?? Array.Empty<RepositoryFolderSecurityPolicyDto>();

        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Replace all policies for this repository (any FolderId), so repo-as-folder stays simple.
            await using (var delPrincipals = new NpgsqlCommand("""
                DELETE FROM repository."FolderSecurityPrincipals" p
                USING repository."FolderSecurityPolicies" pol
                WHERE pol."Id" = p."PolicyId" AND pol."RepositoryId" = @RepositoryId
                """, connection, tx))
            {
                delPrincipals.Parameters.AddWithValue("@RepositoryId", repositoryId);
                await delPrincipals.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var delPolicies = new NpgsqlCommand("""
                DELETE FROM repository."FolderSecurityPolicies"
                WHERE "RepositoryId" = @RepositoryId
                """, connection, tx))
            {
                delPolicies.Parameters.AddWithValue("@RepositoryId", repositoryId);
                await delPolicies.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var policy in policies)
            {
                var userIds = DistinctGuids(policy.UserIds);
                var groupIds = DistinctGuids(policy.GroupIds);
                if (userIds.Count == 0 && groupIds.Count == 0)
                    continue;

                var perms = policy.Permissions ?? new RepositoryPermissionFlagsDto();
                var canView = perms.View;
                var policyId = Guid.NewGuid();

                await using (var insert = new NpgsqlCommand("""
                    INSERT INTO repository."FolderSecurityPolicies"
                        ("Id", "RepositoryId", "FolderId", "CanView", "CanUpload", "CanDownload", "CanPrint", "CanDelete",
                         "CanEditMetadata", "CanEditDocument", "CanCheckOut", "CanCheckIn", "CanSendForSignature",
                         "CreatedAtUtc", "CreatedBy", "IsDeleted")
                    VALUES
                        (@Id, @RepositoryId, NULL, @CanView, @CanUpload, @CanDownload, @CanPrint, @CanDelete,
                         @CanEditMetadata, @CanEditDocument, @CanCheckOut, @CanCheckIn, @CanSendForSignature,
                         now(), @CreatedBy, false)
                    """, connection, tx))
                {
                    insert.Parameters.AddWithValue("@Id", policyId);
                    insert.Parameters.AddWithValue("@RepositoryId", repositoryId);
                    insert.Parameters.AddWithValue("@CanView", canView);
                    insert.Parameters.AddWithValue("@CanUpload", perms.Upload);
                    insert.Parameters.AddWithValue("@CanDownload", perms.Download);
                    insert.Parameters.AddWithValue("@CanPrint", perms.Print);
                    insert.Parameters.AddWithValue("@CanDelete", perms.Delete);
                    insert.Parameters.AddWithValue("@CanEditMetadata", perms.EditMetadata);
                    insert.Parameters.AddWithValue("@CanEditDocument", perms.EditDocument);
                    insert.Parameters.AddWithValue("@CanCheckOut", perms.CheckOut);
                    insert.Parameters.AddWithValue("@CanCheckIn", perms.CheckIn);
                    insert.Parameters.AddWithValue("@CanSendForSignature", perms.SendForSignature);
                    insert.Parameters.AddWithValue("@CreatedBy", (object?)userId ?? DBNull.Value);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                await InsertPrincipalsAsync(
                    connection, tx, "FolderSecurityPrincipals", "PolicyId", policyId, userIds, groupIds, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }

        return await GetFolderSecurityAsync(repositoryId, tenantId, folderId: null, cancellationToken);
    }

    public async Task<RepositoryDocumentSecurityDto> GetDocumentSecurityAsync(
        Guid repositoryId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureRepositoryExistsAsync(repositoryId, tenantId, cancellationToken);

        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT "Id", "Action", "MatchMode", "ConditionsJson", "SortOrder", "Source"
            FROM repository."DocumentSecurityRules"
            WHERE "RepositoryId" = @RepositoryId AND "IsDeleted" = false
            ORDER BY "SortOrder", "CreatedAtUtc"
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);

        var rows = new List<(Guid Id, string Action, string Match, string ConditionsJson, string? Source)>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? "[]" : reader.GetString(3),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        var rules = new List<RepositoryDocumentSecurityRuleDto>();
        foreach (var row in rows)
        {
            var (userIds, groupIds) = await LoadPrincipalsAsync(connection, "DocumentSecurityPrincipals", "RuleId", row.Id, cancellationToken);
            rules.Add(new RepositoryDocumentSecurityRuleDto(
                row.Action,
                row.Match,
                ParseConditions(row.ConditionsJson),
                userIds,
                groupIds,
                row.Source));
        }

        return new RepositoryDocumentSecurityDto(repositoryId, rules);
    }

    public async Task<RepositoryDocumentSecurityDto> SaveDocumentSecurityAsync(
        Guid repositoryId,
        Guid tenantId,
        RepositoryDocumentSecurityUpsertRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureRepositoryExistsAsync(repositoryId, tenantId, cancellationToken);

        // Keep invite-share rules; admin PUT only replaces manual rules.
        var rules = (request.Rules ?? Array.Empty<RepositoryDocumentSecurityRuleDto>())
            .Where(r => !string.Equals(r.Source, "Share", StringComparison.OrdinalIgnoreCase))
            .ToList();

        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using (var delPrincipals = new NpgsqlCommand("""
                DELETE FROM repository."DocumentSecurityPrincipals" p
                USING repository."DocumentSecurityRules" r
                WHERE r."Id" = p."RuleId"
                  AND r."RepositoryId" = @RepositoryId
                  AND (r."Source" IS NULL OR r."Source" <> 'Share')
                """, connection, tx))
            {
                delPrincipals.Parameters.AddWithValue("@RepositoryId", repositoryId);
                await delPrincipals.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var delRules = new NpgsqlCommand("""
                DELETE FROM repository."DocumentSecurityRules"
                WHERE "RepositoryId" = @RepositoryId
                  AND ("Source" IS NULL OR "Source" <> 'Share')
                """, connection, tx))
            {
                delRules.Parameters.AddWithValue("@RepositoryId", repositoryId);
                await delRules.ExecuteNonQueryAsync(cancellationToken);
            }

            var sort = 0;
            foreach (var rule in rules)
            {
                var userIds = DistinctGuids(rule.UserIds);
                var groupIds = DistinctGuids(rule.GroupIds);
                if (userIds.Count == 0 && groupIds.Count == 0)
                    continue;

                var action = NormalizeAction(rule.Action);
                var match = NormalizeMatch(rule.Match);
                var conditions = rule.Conditions ?? Array.Empty<RepositoryDocumentSecurityConditionDto>();
                if (conditions.Count == 0)
                    continue;

                var ruleId = Guid.NewGuid();
                var conditionsJson = JsonSerializer.Serialize(conditions.Select(c => new
                {
                    field = c.Field,
                    op = string.IsNullOrWhiteSpace(c.Op) ? "equals" : c.Op.Trim().ToLowerInvariant(),
                    value = c.Value
                }));

                await using (var insert = new NpgsqlCommand("""
                    INSERT INTO repository."DocumentSecurityRules"
                        ("Id", "RepositoryId", "Action", "MatchMode", "ConditionsJson", "SortOrder", "CreatedAtUtc", "CreatedBy", "IsDeleted", "Source")
                    VALUES
                        (@Id, @RepositoryId, @Action, @MatchMode, @ConditionsJson, @SortOrder, now(), @CreatedBy, false, NULL)
                    """, connection, tx))
                {
                    insert.Parameters.AddWithValue("@Id", ruleId);
                    insert.Parameters.AddWithValue("@RepositoryId", repositoryId);
                    insert.Parameters.AddWithValue("@Action", action);
                    insert.Parameters.AddWithValue("@MatchMode", match);
                    insert.Parameters.AddWithValue("@ConditionsJson", conditionsJson);
                    insert.Parameters.AddWithValue("@SortOrder", sort++);
                    insert.Parameters.AddWithValue("@CreatedBy", (object?)userId ?? DBNull.Value);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                await InsertPrincipalsAsync(
                    connection, tx, "DocumentSecurityPrincipals", "RuleId", ruleId, userIds, groupIds, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }

        return await GetDocumentSecurityAsync(repositoryId, tenantId, cancellationToken);
    }

    public async Task EnsureShareRecipientAccessAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid recipientUserId,
        Guid sharedItemId,
        bool canUpload,
        Guid? sharedByUserId = null,
        CancellationToken cancellationToken = default)
    {
        if (recipientUserId == Guid.Empty || sharedItemId == Guid.Empty)
            throw new ArgumentException("Recipient user id and shared item id are required.");

        await EnsureSchemaAsync(cancellationToken);
        await EnsureRepositoryExistsAsync(repositoryId, tenantId, cancellationToken);

        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Upsert share-scoped recipient (does not lock repo for other users). ON CONFLICT
            // targets the IX_ShareRecipients_Repo_User unique index (Postgres's MERGE equivalent).
            await using (var upsert = new NpgsqlCommand("""
                INSERT INTO repository."ShareRecipients" ("Id", "RepositoryId", "UserId", "CanUpload", "CreatedAtUtc", "CreatedBy", "IsDeleted")
                VALUES (@Id, @RepositoryId, @UserId, @CanUpload, now(), @CreatedBy, false)
                ON CONFLICT ("RepositoryId", "UserId") DO UPDATE SET
                    "CanUpload" = (EXCLUDED."CanUpload" OR repository."ShareRecipients"."CanUpload"),
                    "IsDeleted" = false,
                    "ModifiedAtUtc" = now()
                """, connection, tx))
            {
                upsert.Parameters.AddWithValue("@Id", Guid.NewGuid());
                upsert.Parameters.AddWithValue("@RepositoryId", repositoryId);
                upsert.Parameters.AddWithValue("@UserId", recipientUserId);
                upsert.Parameters.AddWithValue("@CanUpload", canUpload);
                upsert.Parameters.AddWithValue("@CreatedBy", (object?)sharedByUserId ?? DBNull.Value);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            await EnsureShareGrantRuleAsync(
                connection, tx, repositoryId, recipientUserId, "ItemId", sharedItemId.ToString("D"), sharedByUserId, cancellationToken);
            await EnsureShareGrantRuleAsync(
                connection, tx, repositoryId, recipientUserId, "CreatedBy", recipientUserId.ToString("D"), sharedByUserId, cancellationToken);

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> CanAccessRepositoryAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        string permission = RepositorySecurityPermissions.View,
        CancellationToken cancellationToken = default)
    {
        if (isAdmin)
            return true;

        await EnsureSchemaAsync(cancellationToken);

        // File-invite guests: can open repo; upload only when Can Edit was granted.
        var shareAccess = await GetShareRecipientAccessAsync(repositoryId, userId, cancellationToken);
        if (shareAccess != null)
        {
            if (string.Equals(permission, RepositorySecurityPermissions.View, StringComparison.OrdinalIgnoreCase)
                || string.Equals(permission, RepositorySecurityPermissions.Download, StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(permission, RepositorySecurityPermissions.Upload, StringComparison.OrdinalIgnoreCase))
                return shareAccess.CanUpload;

            return false;
        }

        var principalIds = await ResolvePrincipalIdsAsync(tenantId, userId, cancellationToken);
        var access = await GetEffectiveFolderAccessAsync(repositoryId, principalIds, cancellationToken);

        // No policies saved yet → open for all TenantUsers (same as before security was configured).
        if (!access.PoliciesConfigured)
            return true;

        if (HasPermission(access.Permissions, permission))
            return true;

        // Grant-only users may open the repository to see granted documents.
        if (string.Equals(permission, RepositorySecurityPermissions.View, StringComparison.OrdinalIgnoreCase))
        {
            var docs = await GetDocumentSecurityAsync(repositoryId, tenantId, cancellationToken);
            return docs.Rules.Any(r =>
                string.Equals(r.Action, RepositorySecurityActions.Grant, StringComparison.OrdinalIgnoreCase)
                && PrincipalsMatch(r.UserIds, r.GroupIds, principalIds));
        }

        return false;
    }

    public async Task<IReadOnlyList<Guid>> FilterAccessibleRepositoryIdsAsync(
        IReadOnlyList<Guid> repositoryIds,
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        if (isAdmin || repositoryIds.Count == 0)
            return repositoryIds;

        await EnsureSchemaAsync(cancellationToken);
        var allowed = new List<Guid>();
        foreach (var id in repositoryIds)
        {
            if (await CanAccessRepositoryAsync(id, tenantId, userId, isAdmin: false, cancellationToken: cancellationToken))
                allowed.Add(id);
        }

        return allowed;
    }

    public async Task<bool> CanAccessItemAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        IReadOnlyDictionary<string, string?> itemFields,
        string permission = RepositorySecurityPermissions.View,
        CancellationToken cancellationToken = default)
    {
        if (isAdmin)
            return true;

        await EnsureSchemaAsync(cancellationToken);
        var principalIds = await ResolvePrincipalIdsAsync(tenantId, userId, cancellationToken);
        var docs = await GetDocumentSecurityAsync(repositoryId, tenantId, cancellationToken);

        var hide = docs.Rules.Where(r =>
            string.Equals(r.Action, RepositorySecurityActions.Hide, StringComparison.OrdinalIgnoreCase)
            && PrincipalsMatch(r.UserIds, r.GroupIds, principalIds)
            && RuleMatchesItem(r, itemFields)).ToList();
        if (hide.Count > 0)
            return false;

        // File-invite guests: only shared file + their uploaded files (grant rules), never open-all.
        var shareAccess = await GetShareRecipientAccessAsync(repositoryId, userId, cancellationToken);
        if (shareAccess != null)
        {
            if (!string.Equals(permission, RepositorySecurityPermissions.View, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(permission, RepositorySecurityPermissions.Download, StringComparison.OrdinalIgnoreCase))
                return false;

            return docs.Rules.Any(r =>
                string.Equals(r.Action, RepositorySecurityActions.Grant, StringComparison.OrdinalIgnoreCase)
                && PrincipalsMatch(r.UserIds, r.GroupIds, principalIds)
                && RuleMatchesItem(r, itemFields));
        }

        var access = await GetEffectiveFolderAccessAsync(repositoryId, principalIds, cancellationToken);

        // Open by default until folder policies exist.
        if (!access.PoliciesConfigured)
            return true;

        if (HasPermission(access.Permissions, permission))
            return true;

        if (string.Equals(permission, RepositorySecurityPermissions.View, StringComparison.OrdinalIgnoreCase)
            || string.Equals(permission, RepositorySecurityPermissions.Download, StringComparison.OrdinalIgnoreCase))
        {
            return docs.Rules.Any(r =>
                string.Equals(r.Action, RepositorySecurityActions.Grant, StringComparison.OrdinalIgnoreCase)
                && PrincipalsMatch(r.UserIds, r.GroupIds, principalIds)
                && RuleMatchesItem(r, itemFields));
        }

        return false;
    }

    public async Task<IReadOnlyList<T>> FilterAccessibleItemsAsync<T>(
        Guid repositoryId,
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        IReadOnlyList<T> items,
        Func<T, IReadOnlyDictionary<string, string?>> fieldSelector,
        string permission = RepositorySecurityPermissions.View,
        CancellationToken cancellationToken = default)
    {
        if (isAdmin || items.Count == 0)
            return items;

        var result = new List<T>(items.Count);
        foreach (var item in items)
        {
            if (await CanAccessItemAsync(
                    repositoryId, tenantId, userId, isAdmin: false, fieldSelector(item), permission, cancellationToken))
                result.Add(item);
        }

        return result;
    }

    public async Task<(string? Sql, IReadOnlyList<(string Name, object Value)> Parameters)> BuildHideDocumentsSqlFilterAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        IReadOnlyDictionary<string, string> fieldToSqlColumn,
        string paramPrefix,
        CancellationToken cancellationToken = default)
    {
        if (isAdmin || userId == Guid.Empty)
            return (null, Array.Empty<(string, object)>());

        await EnsureSchemaAsync(cancellationToken);
        var principalIds = await ResolvePrincipalIdsAsync(tenantId, userId, cancellationToken);
        var docs = await GetDocumentSecurityAsync(repositoryId, tenantId, cancellationToken);

        var hideRules = docs.Rules
            .Where(r =>
                string.Equals(r.Action, RepositorySecurityActions.Hide, StringComparison.OrdinalIgnoreCase)
                && PrincipalsMatch(r.UserIds, r.GroupIds, principalIds)
                && r.Conditions is { Count: > 0 })
            .ToList();

        if (hideRules.Count == 0)
            return (null, Array.Empty<(string, object)>());

        var ruleSql = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        var p = 0;

        foreach (var rule in hideRules)
        {
            var matchAll = string.Equals(rule.Match, RepositorySecurityMatchModes.All, StringComparison.OrdinalIgnoreCase);
            var parts = new List<string>();
            var allMapped = true;

            foreach (var condition in rule.Conditions)
            {
                if (!TryResolveSqlColumn(condition.Field, fieldToSqlColumn, out var column))
                {
                    allMapped = false;
                    break;
                }

                var paramName = $"@{paramPrefix}{p++}";
                var op = (condition.Op ?? "equals").Trim().ToLowerInvariant();
                var value = condition.Value?.Trim() ?? string.Empty;

                var columnRef = RepositorySqlHelper.ColumnRef(column);

                if (op is "contains")
                {
                    parts.Add($"LTRIM(RTRIM(CAST({columnRef} AS TEXT))) LIKE {paramName}");
                    parameters.Add((paramName, $"%{value}%"));
                }
                else if (op is "notequals" or "ne" or "!=")
                {
                    parts.Add($"(LTRIM(RTRIM(CAST({columnRef} AS TEXT))) <> {paramName} OR {columnRef} IS NULL)");
                    parameters.Add((paramName, value));
                }
                else
                {
                    parts.Add($"LTRIM(RTRIM(CAST({columnRef} AS TEXT))) = {paramName}");
                    parameters.Add((paramName, value));
                }
            }

            if (!allMapped || parts.Count == 0)
                continue;

            ruleSql.Add(matchAll
                ? "(" + string.Join(" AND ", parts) + ")"
                : "(" + string.Join(" OR ", parts) + ")");
        }

        if (ruleSql.Count == 0)
            return (null, Array.Empty<(string, object)>());

        return ("NOT (" + string.Join(" OR ", ruleSql) + ")", parameters);
    }

    private static bool TryResolveSqlColumn(
        string? field,
        IReadOnlyDictionary<string, string> fieldToSqlColumn,
        out string column)
    {
        column = string.Empty;
        if (string.IsNullOrWhiteSpace(field) || fieldToSqlColumn.Count == 0)
            return false;

        var key = field.Trim();
        if (fieldToSqlColumn.TryGetValue(key, out var direct) && !string.IsNullOrWhiteSpace(direct))
        {
            column = direct;
            return true;
        }

        try
        {
            var sanitized = RepositorySqlHelper.SanitizeColumnName(key);
            if (fieldToSqlColumn.TryGetValue(sanitized, out var bySanitized) && !string.IsNullOrWhiteSpace(bySanitized))
            {
                column = bySanitized;
                return true;
            }
        }
        catch (ArgumentException)
        {
            // invalid field label
        }

        return false;
    }

    private async Task EnsureRepositoryExistsAsync(Guid repositoryId, Guid tenantId, CancellationToken cancellationToken)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken);
        if (repo is null)
            throw new InvalidOperationException("Repository not found.");
    }

    private string RequireConnectionString() =>
        _connectionProvider.ConnectionString
        ?? throw new InvalidOperationException("Tenant connection string not resolved.");

    private async Task<PrincipalSet> ResolvePrincipalIdsAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var groupIds = new HashSet<Guid>();
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);

        // users.UserGroups may not exist on every DB — fail soft.
        try
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT "GroupId"
                FROM users."UserGroups"
                WHERE "TenantId" = @TenantId AND "UserId" = @UserId
                """, connection);
            cmd.Parameters.AddWithValue("@TenantId", tenantId);
            cmd.Parameters.AddWithValue("@UserId", userId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                groupIds.Add(reader.GetGuid(0));
        }
        catch (PostgresException)
        {
            // ignore missing schema
        }

        return new PrincipalSet(userId, groupIds);
    }

    private async Task<FolderAccessEvaluation> GetEffectiveFolderAccessAsync(
        Guid repositoryId,
        PrincipalSet principals,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);

        // Repository = folder: any FolderId on the row is ignored (legacy clients sent repo id as folderId).
        const string sql = """
            SELECT pol."CanView", pol."CanUpload", pol."CanDownload", pol."CanPrint", pol."CanDelete",
                   pol."CanEditMetadata", pol."CanEditDocument", pol."CanCheckOut", pol."CanCheckIn", pol."CanSendForSignature",
                   p."PrincipalType", p."PrincipalId"
            FROM repository."FolderSecurityPolicies" pol
            INNER JOIN repository."FolderSecurityPrincipals" p ON p."PolicyId" = pol."Id"
            WHERE pol."RepositoryId" = @RepositoryId
              AND pol."IsDeleted" = false
            """;

        await using var countCmd = new NpgsqlCommand("""
            SELECT COUNT(1) FROM repository."FolderSecurityPolicies"
            WHERE "RepositoryId" = @RepositoryId
              AND "IsDeleted" = false
            """, connection);
        countCmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        var anyPolicyExists = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
        if (!anyPolicyExists)
            return new FolderAccessEvaluation(PoliciesConfigured: false, Permissions: null);

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);

        RepositoryPermissionFlagsDto? merged = null;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var principalType = reader.GetString(10);
            var principalId = reader.GetGuid(11);
            if (!principals.Matches(principalType, principalId))
                continue;

            var flags = new RepositoryPermissionFlagsDto(
                reader.GetBoolean(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9));

            merged = merged is null ? flags : Or(merged, flags);
        }

        return new FolderAccessEvaluation(PoliciesConfigured: true, Permissions: merged);
    }

    /// <summary>Repository is the folder for this security model — never use a separate folder id.</summary>
    private static Guid? NormalizeFolderId(Guid repositoryId, Guid? folderId) =>
        folderId is null || folderId == Guid.Empty || folderId == repositoryId
            ? null
            : null; // always repository-scoped

    private sealed record FolderAccessEvaluation(bool PoliciesConfigured, RepositoryPermissionFlagsDto? Permissions);

    private static bool HasPermission(RepositoryPermissionFlagsDto? flags, string permission)
    {
        if (flags is null)
            return false;

        return permission.Trim().ToLowerInvariant() switch
        {
            RepositorySecurityPermissions.View => flags.View,
            RepositorySecurityPermissions.Upload => flags.Upload,
            RepositorySecurityPermissions.Download => flags.Download || flags.View,
            RepositorySecurityPermissions.Print => flags.Print,
            RepositorySecurityPermissions.Delete => flags.Delete,
            RepositorySecurityPermissions.EditMetadata => flags.EditMetadata,
            RepositorySecurityPermissions.EditDocument => flags.EditDocument,
            RepositorySecurityPermissions.CheckOut => flags.CheckOut,
            RepositorySecurityPermissions.CheckIn => flags.CheckIn,
            RepositorySecurityPermissions.SendForSignature => flags.SendForSignature,
            _ => flags.View
        };
    }

    private static RepositoryPermissionFlagsDto Or(RepositoryPermissionFlagsDto a, RepositoryPermissionFlagsDto b) =>
        new(
            a.View || b.View,
            a.Upload || b.Upload,
            a.Download || b.Download,
            a.Print || b.Print,
            a.Delete || b.Delete,
            a.EditMetadata || b.EditMetadata,
            a.EditDocument || b.EditDocument,
            a.CheckOut || b.CheckOut,
            a.CheckIn || b.CheckIn,
            a.SendForSignature || b.SendForSignature);

    private static bool PrincipalsMatch(
        IReadOnlyList<Guid> userIds,
        IReadOnlyList<Guid> groupIds,
        PrincipalSet principals)
    {
        if (userIds.Any(id => id == principals.UserId))
            return true;
        return groupIds.Any(principals.GroupIds.Contains);
    }

    private static bool RuleMatchesItem(
        RepositoryDocumentSecurityRuleDto rule,
        IReadOnlyDictionary<string, string?> itemFields)
    {
        var conditions = rule.Conditions ?? Array.Empty<RepositoryDocumentSecurityConditionDto>();
        if (conditions.Count == 0)
            return false;

        var matchAll = string.Equals(rule.Match, RepositorySecurityMatchModes.All, StringComparison.OrdinalIgnoreCase);
        if (matchAll)
            return conditions.All(c => ConditionMatches(c, itemFields));
        return conditions.Any(c => ConditionMatches(c, itemFields));
    }

    private static bool ConditionMatches(
        RepositoryDocumentSecurityConditionDto condition,
        IReadOnlyDictionary<string, string?> itemFields)
    {
        if (string.IsNullOrWhiteSpace(condition.Field))
            return false;

        itemFields.TryGetValue(condition.Field.Trim(), out var actual);
        // also try case-insensitive lookup
        if (actual is null)
        {
            foreach (var kv in itemFields)
            {
                if (string.Equals(kv.Key, condition.Field.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    actual = kv.Value;
                    break;
                }
            }
        }

        var expected = condition.Value?.Trim() ?? string.Empty;
        var left = actual?.Trim() ?? string.Empty;
        var op = (condition.Op ?? "equals").Trim().ToLowerInvariant();
        return op switch
        {
            "notequals" or "ne" or "!=" => !string.Equals(left, expected, StringComparison.OrdinalIgnoreCase),
            "contains" => left.Contains(expected, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(left, expected, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static async Task<(IReadOnlyList<Guid> UserIds, IReadOnlyList<Guid> GroupIds)> LoadPrincipalsAsync(
        NpgsqlConnection connection,
        string tableName,
        string fkColumn,
        Guid parentId,
        CancellationToken cancellationToken)
    {
        var users = new List<Guid>();
        var groups = new List<Guid>();
        var sql = $"""
            SELECT "PrincipalType", "PrincipalId"
            FROM repository."{tableName}"
            WHERE "{fkColumn}" = @ParentId
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@ParentId", parentId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.GetString(0);
            var id = reader.GetGuid(1);
            if (string.Equals(type, "User", StringComparison.OrdinalIgnoreCase))
                users.Add(id);
            else if (string.Equals(type, "Group", StringComparison.OrdinalIgnoreCase))
                groups.Add(id);
        }

        return (users, groups);
    }

    private static async Task InsertPrincipalsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string tableName,
        string fkColumn,
        Guid parentId,
        IReadOnlyList<Guid> userIds,
        IReadOnlyList<Guid> groupIds,
        CancellationToken cancellationToken)
    {
        foreach (var userId in userIds)
        {
            var sql = $"""
                INSERT INTO repository."{tableName}" ("Id", "{fkColumn}", "PrincipalType", "PrincipalId")
                VALUES (@Id, @ParentId, 'User', @PrincipalId)
                """;
            await using var cmd = new NpgsqlCommand(sql, connection, tx);
            cmd.Parameters.AddWithValue("@Id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("@ParentId", parentId);
            cmd.Parameters.AddWithValue("@PrincipalId", userId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var groupId in groupIds)
        {
            var sql = $"""
                INSERT INTO repository."{tableName}" ("Id", "{fkColumn}", "PrincipalType", "PrincipalId")
                VALUES (@Id, @ParentId, 'Group', @PrincipalId)
                """;
            await using var cmd = new NpgsqlCommand(sql, connection, tx);
            cmd.Parameters.AddWithValue("@Id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("@ParentId", parentId);
            cmd.Parameters.AddWithValue("@PrincipalId", groupId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static List<Guid> DistinctGuids(IReadOnlyList<Guid>? ids) =>
        (ids ?? Array.Empty<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList();

    private static string NormalizeAction(string? action) =>
        string.Equals(action, RepositorySecurityActions.Hide, StringComparison.OrdinalIgnoreCase)
            ? RepositorySecurityActions.Hide
            : RepositorySecurityActions.Grant;

    private static string NormalizeMatch(string? match) =>
        string.Equals(match, RepositorySecurityMatchModes.Any, StringComparison.OrdinalIgnoreCase)
            ? RepositorySecurityMatchModes.Any
            : RepositorySecurityMatchModes.All;

    private static IReadOnlyList<RepositoryDocumentSecurityConditionDto> ParseConditions(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            var list = new List<RepositoryDocumentSecurityConditionDto>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var field = el.TryGetProperty("field", out var f) ? f.GetString()
                    : el.TryGetProperty("Field", out var f2) ? f2.GetString()
                    : null;
                var op = el.TryGetProperty("op", out var o) ? o.GetString()
                    : el.TryGetProperty("Op", out var o2) ? o2.GetString()
                    : "equals";
                var value = el.TryGetProperty("value", out var v) ? v.GetString()
                    : el.TryGetProperty("Value", out var v2) ? v2.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(field))
                    list.Add(new RepositoryDocumentSecurityConditionDto(field!, op ?? "equals", value));
            }

            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<RepositoryDocumentSecurityConditionDto>();
        }
    }

    private sealed record ShareRecipientAccess(bool CanUpload);

    private async Task<ShareRecipientAccess?> GetShareRecipientAccessAsync(
        Guid repositoryId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            SELECT "CanUpload"
            FROM repository."ShareRecipients"
            WHERE "RepositoryId" = @RepositoryId
              AND "UserId" = @UserId
              AND "IsDeleted" = false
            """, connection);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        cmd.Parameters.AddWithValue("@UserId", userId);
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar is DBNull)
            return null;
        return new ShareRecipientAccess(Convert.ToBoolean(scalar, CultureInfo.InvariantCulture));
    }

    private static async Task EnsureShareGrantRuleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid repositoryId,
        Guid recipientUserId,
        string field,
        string value,
        Guid? sharedByUserId,
        CancellationToken cancellationToken)
    {
        // Avoid duplicate share grants for same user+field+value.
        await using (var find = new NpgsqlCommand("""
            SELECT r."Id", r."ConditionsJson"
            FROM repository."DocumentSecurityRules" r
            INNER JOIN repository."DocumentSecurityPrincipals" p ON p."RuleId" = r."Id"
            WHERE r."RepositoryId" = @RepositoryId
              AND r."IsDeleted" = false
              AND r."Source" = 'Share'
              AND r."Action" = 'grant'
              AND p."PrincipalType" = 'User'
              AND p."PrincipalId" = @UserId
            """, connection, tx))
        {
            find.Parameters.AddWithValue("@RepositoryId", repositoryId);
            find.Parameters.AddWithValue("@UserId", recipientUserId);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var conditionsJson = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                var conditions = ParseConditions(conditionsJson);
                if (conditions.Any(c =>
                        string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
            }
        }

        var ruleId = Guid.NewGuid();
        var conditionsPayload = JsonSerializer.Serialize(new[]
        {
            new { field, op = "equals", value }
        });

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO repository."DocumentSecurityRules"
                ("Id", "RepositoryId", "Action", "MatchMode", "ConditionsJson", "SortOrder", "CreatedAtUtc", "CreatedBy", "IsDeleted", "Source")
            VALUES
                (@Id, @RepositoryId, 'grant', 'all', @ConditionsJson, 0, now(), @CreatedBy, false, 'Share')
            """, connection, tx))
        {
            insert.Parameters.AddWithValue("@Id", ruleId);
            insert.Parameters.AddWithValue("@RepositoryId", repositoryId);
            insert.Parameters.AddWithValue("@ConditionsJson", conditionsPayload);
            insert.Parameters.AddWithValue("@CreatedBy", (object?)sharedByUserId ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertPrincipalsAsync(
            connection,
            tx,
            "DocumentSecurityPrincipals",
            "RuleId",
            ruleId,
            new[] { recipientUserId },
            Array.Empty<Guid>(),
            cancellationToken);
    }

    private sealed record PrincipalSet(Guid UserId, HashSet<Guid> GroupIds)
    {
        public bool Matches(string principalType, Guid principalId) =>
            string.Equals(principalType, "User", StringComparison.OrdinalIgnoreCase)
                ? principalId == UserId
                : GroupIds.Contains(principalId);
    }

    // Matches scripts/postgres/CreateRepositorySchema.sql's shape for these 5 tables exactly
    // (including the two Phase 4 findings patched into that script: DocumentSecurityRules.Source
    // and the ShareRecipients table, both of which only ever existed via this inline path on
    // SQL Server too). Native CREATE TABLE/ADD COLUMN IF NOT EXISTS replaces the sys.tables
    // existence-check ceremony.
    private const string EnsureSchemaSql = """
        CREATE TABLE IF NOT EXISTS repository."FolderSecurityPolicies" (
            "Id" uuid NOT NULL CONSTRAINT "PK_FolderSecurityPolicies" PRIMARY KEY,
            "RepositoryId" uuid NOT NULL,
            "FolderId" uuid NULL,
            "CanView" boolean NOT NULL DEFAULT true,
            "CanUpload" boolean NOT NULL DEFAULT false,
            "CanDownload" boolean NOT NULL DEFAULT false,
            "CanPrint" boolean NOT NULL DEFAULT false,
            "CanDelete" boolean NOT NULL DEFAULT false,
            "CanEditMetadata" boolean NOT NULL DEFAULT false,
            "CanEditDocument" boolean NOT NULL DEFAULT false,
            "CanCheckOut" boolean NOT NULL DEFAULT false,
            "CanCheckIn" boolean NOT NULL DEFAULT false,
            "CanSendForSignature" boolean NOT NULL DEFAULT false,
            "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
            "ModifiedAtUtc" timestamptz NULL,
            "CreatedBy" uuid NULL,
            "ModifiedBy" uuid NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        CREATE INDEX IF NOT EXISTS "IX_FolderSecurityPolicies_Repo_Folder"
            ON repository."FolderSecurityPolicies" ("RepositoryId", "FolderId", "IsDeleted");

        CREATE TABLE IF NOT EXISTS repository."FolderSecurityPrincipals" (
            "Id" uuid NOT NULL CONSTRAINT "PK_FolderSecurityPrincipals" PRIMARY KEY,
            "PolicyId" uuid NOT NULL,
            "PrincipalType" varchar(16) NOT NULL,
            "PrincipalId" uuid NOT NULL,
            CONSTRAINT "FK_FolderSecurityPrincipals_Policy"
                FOREIGN KEY ("PolicyId") REFERENCES repository."FolderSecurityPolicies" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_FolderSecurityPrincipals_Policy" ON repository."FolderSecurityPrincipals" ("PolicyId");
        CREATE INDEX IF NOT EXISTS "IX_FolderSecurityPrincipals_Principal"
            ON repository."FolderSecurityPrincipals" ("PrincipalType", "PrincipalId");

        CREATE TABLE IF NOT EXISTS repository."DocumentSecurityRules" (
            "Id" uuid NOT NULL CONSTRAINT "PK_DocumentSecurityRules" PRIMARY KEY,
            "RepositoryId" uuid NOT NULL,
            "Action" varchar(16) NOT NULL,
            "MatchMode" varchar(8) NOT NULL CONSTRAINT "DF_DocumentSecurityRules_MatchMode" DEFAULT 'all',
            "ConditionsJson" text NOT NULL,
            "SortOrder" integer NOT NULL CONSTRAINT "DF_DocumentSecurityRules_SortOrder" DEFAULT 0,
            "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
            "ModifiedAtUtc" timestamptz NULL,
            "CreatedBy" uuid NULL,
            "ModifiedBy" uuid NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        CREATE INDEX IF NOT EXISTS "IX_DocumentSecurityRules_Repo"
            ON repository."DocumentSecurityRules" ("RepositoryId", "IsDeleted", "SortOrder");

        ALTER TABLE repository."DocumentSecurityRules" ADD COLUMN IF NOT EXISTS "Source" varchar(32) NULL;

        CREATE TABLE IF NOT EXISTS repository."DocumentSecurityPrincipals" (
            "Id" uuid NOT NULL CONSTRAINT "PK_DocumentSecurityPrincipals" PRIMARY KEY,
            "RuleId" uuid NOT NULL,
            "PrincipalType" varchar(16) NOT NULL,
            "PrincipalId" uuid NOT NULL,
            CONSTRAINT "FK_DocumentSecurityPrincipals_Rule"
                FOREIGN KEY ("RuleId") REFERENCES repository."DocumentSecurityRules" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_DocumentSecurityPrincipals_Rule" ON repository."DocumentSecurityPrincipals" ("RuleId");
        CREATE INDEX IF NOT EXISTS "IX_DocumentSecurityPrincipals_Principal"
            ON repository."DocumentSecurityPrincipals" ("PrincipalType", "PrincipalId");

        CREATE TABLE IF NOT EXISTS repository."ShareRecipients" (
            "Id" uuid NOT NULL CONSTRAINT "PK_ShareRecipients" PRIMARY KEY,
            "RepositoryId" uuid NOT NULL,
            "UserId" uuid NOT NULL,
            "CanUpload" boolean NOT NULL CONSTRAINT "DF_ShareRecipients_CanUpload" DEFAULT false,
            "CreatedAtUtc" timestamptz NOT NULL CONSTRAINT "DF_ShareRecipients_CreatedAtUtc" DEFAULT now(),
            "ModifiedAtUtc" timestamptz NULL,
            "CreatedBy" uuid NULL,
            "IsDeleted" boolean NOT NULL CONSTRAINT "DF_ShareRecipients_IsDeleted" DEFAULT false
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ShareRecipients_Repo_User"
            ON repository."ShareRecipients" ("RepositoryId", "UserId");
        """;
}
