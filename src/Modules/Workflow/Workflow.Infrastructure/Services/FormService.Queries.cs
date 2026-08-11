using System.Globalization;
using System.Text.Json;
using Npgsql;
using SaaSApp.Workflow.Application.Forms;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed partial class FormService
{
    // createdBy/modifiedBy are stored as text (userId.ToString("D")) since wForm predates a
    // proper FK to users.Users. Postgres has no TRY_CONVERT; this regex-guarded cast is the
    // direct equivalent (values are always a well-formed dashed GUID string when set by this
    // service, but defensively falls back to NULL like TRY_CONVERT did for anything else).
    private const string TryCastUuidGuard = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";

    private static string TryCastUuid(string column) =>
        $"(CASE WHEN {column} ~ '{TryCastUuidGuard}' THEN {column}::uuid ELSE NULL END)";

    public async Task<FormListResponse> ListAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        var tenantGuid = _tenantContext.TenantId
            ?? throw new InvalidOperationException("Tenant context is required.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, "wForm", cancellationToken))
            return new FormListResponse(Array.Empty<FormListItem>());

        var sql = $"""
            SELECT
                f.id,
                f.name,
                f."createdBy",
                f."modifiedBy",
                cb."Email" AS "CreatedByName",
                COALESCE(mb."Email", cb."Email") AS "ModifiedByName"
            FROM dbo."wForm" f
            LEFT JOIN users."Users" cb ON cb."Id" = {TryCastUuid("f.\"createdBy\"")} AND cb."IsDeleted" = false
            LEFT JOIN users."Users" mb ON mb."Id" = {TryCastUuid("f.\"modifiedBy\"")} AND mb."IsDeleted" = false
            WHERE f."isDeleted" = false AND f."tenantId" = @TenantId
            ORDER BY f.name;
            """;

        var items = new List<FormListItem>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantGuid);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new FormListItem(
                FormId: reader.GetString(0),
                FormName: reader.GetString(1),
                CreatedBy: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                ModifiedBy: reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedByName: reader.IsDBNull(4) ? null : reader.GetString(4),
                ModifiedByName: reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return new FormListResponse(items);
    }

    public async Task<FormAllResponse> QueryAllAsync(
        FormAllRequest request,
        Guid? currentUserId,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        var tenantGuid = _tenantContext.TenantId
            ?? throw new InvalidOperationException("Tenant context is required.");

        var page = request.CurrentPage <= 0 ? 1 : request.CurrentPage;
        var pageSize = request.ItemsPerPage < 0 ? 0 : request.ItemsPerPage;
        var skip = (page - 1) * (pageSize == 0 ? int.MaxValue : pageSize);
        var mode = (request.Mode ?? "browse").Trim().ToLowerInvariant();
        var includeDeleted = mode != "browse";

        var sortColumn = MapFormSortColumn(request.SortBy?.Criteria);
        var sortOrder = string.Equals(request.SortBy?.Order, "ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, "wForm", cancellationToken))
            return new FormAllResponse(new List<FormAllGroup> { new(string.Empty, new List<FormAllItem>()) }, new FormAllMeta(page, pageSize, 0));

        var hasSecurityTable = await TableExistsAsync(connection, "wFormSecurity", cancellationToken);

        var whereParts = new List<string>
        {
            includeDeleted ? "f.\"isDeleted\" = true" : "f.\"isDeleted\" = false",
            "f.\"tenantId\" = @TenantId"
        };
        var parameters = new List<NpgsqlParameter> { new("@TenantId", tenantGuid) };

        if (request.HasSecurity && currentUserId != null && !isAdmin && hasSecurityTable)
        {
            var userIdText = currentUserId.Value.ToString("D");
            whereParts.Add("""
                (
                    EXISTS (SELECT 1 FROM dbo."wFormSecurity" s WHERE s."wFormId" = f.id AND s."isDeleted" = false AND s."userId" = @CurrentUserId)
                    OR f."createdBy" = @CurrentUserId
                )
                """);
            parameters.Add(new NpgsqlParameter("@CurrentUserId", userIdText));
        }
        else if (request.HasReport && currentUserId != null && hasSecurityTable)
        {
            var userIdText = currentUserId.Value.ToString("D");
            whereParts.Add("""
                (
                    EXISTS (SELECT 1 FROM dbo."wFormSecurity" s WHERE s."wFormId" = f.id AND s."isDeleted" = false AND s."userId" = @CurrentUserId)
                    OR f."createdBy" = @CurrentUserId
                )
                """);
            parameters.Add(new NpgsqlParameter("@CurrentUserId", userIdText));
        }

        foreach (var filter in (request.FilterBy ?? new List<FormAllFilterGroup>()).SelectMany(g => g.Filters ?? new List<FormAllFilter>()))
        {
            if (TryBuildFormFilterCondition(filter, out var condition, out var filterParam))
            {
                whereParts.Add(condition);
                if (filterParam != null)
                    parameters.Add(filterParam);
            }
        }

        var whereSql = string.Join(" AND ", whereParts);
        var countSql = $"SELECT COUNT(*) FROM dbo.\"wForm\" f WHERE {whereSql};";
        var selectSql = $"""
            SELECT
                f.id,
                f.name,
                f.description,
                f."publishOption",
                f."createdBy",
                f."modifiedBy",
                f."createdAt",
                f."modifiedAt",
                cb."Email" AS "CreatedByName",
                COALESCE(mb."Email", cb."Email") AS "ModifiedByName"
            FROM dbo."wForm" f
            LEFT JOIN users."Users" cb ON cb."Id" = {TryCastUuid("f.\"createdBy\"")} AND cb."IsDeleted" = false
            LEFT JOIN users."Users" mb ON mb."Id" = {TryCastUuid("f.\"modifiedBy\"")} AND mb."IsDeleted" = false
            WHERE {whereSql}
            ORDER BY {sortColumn} {sortOrder}
            OFFSET @Skip ROWS
            FETCH NEXT @Take ROWS ONLY;
            """;

        int totalItems;
        await using (var countCmd = new NpgsqlCommand(countSql, connection))
        {
            foreach (var p in parameters)
                countCmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
            totalItems = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<FormAllItem>();
        await using (var listCmd = new NpgsqlCommand(selectSql, connection))
        {
            foreach (var p in parameters)
                listCmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
            listCmd.Parameters.AddWithValue("@Skip", Math.Max(skip, 0));
            listCmd.Parameters.AddWithValue("@Take", pageSize == 0 ? Math.Max(totalItems, 1) : pageSize);
            await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new FormAllItem(
                    Id: reader.GetString(0),
                    Name: reader.GetString(1),
                    Description: reader.IsDBNull(2) ? null : reader.GetString(2),
                    PublishOption: reader.IsDBNull(3) ? "DRAFT" : reader.GetString(3),
                    CreatedBy: reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    ModifiedBy: reader.IsDBNull(5) ? null : reader.GetString(5),
                    CreatedAt: ParseFormDate(reader.IsDBNull(6) ? null : reader.GetValue(6)),
                    ModifiedAt: ParseFormDate(reader.IsDBNull(7) ? null : reader.GetValue(7)),
                    CreatedByName: reader.IsDBNull(8) ? null : reader.GetString(8),
                    ModifiedByName: reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
        }

        var grouped = GroupFormItems(items, request.GroupBy);
        return new FormAllResponse(grouped, new FormAllMeta(page, pageSize, totalItems));
    }

    public async Task<FormByIdResult?> GetByIdAsync(string formId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formId))
            return null;

        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        var tenantGuid = _tenantContext.TenantId
            ?? throw new InvalidOperationException("Tenant context is required.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, "wForm", cancellationToken))
            return null;

        const string sql = """
            SELECT
                f.id,
                f.name,
                f.description,
                f.type,
                f.layout,
                f."publishOption",
                f."createdBy",
                f."modifiedBy",
                f."createdAt",
                f."modifiedAt"
            FROM dbo."wForm" f
            WHERE f.id = @Id AND f."tenantId" = @TenantId AND f."isDeleted" = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", formId.Trim());
        cmd.Parameters.AddWithValue("@TenantId", tenantGuid);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var storedId = reader.GetString(0);
        var formJson = await LoadFormJsonElementAsync(storedId, cancellationToken);

        return new FormByIdResult(
            Id: storedId,
            Name: reader.GetString(1),
            Description: reader.IsDBNull(2) ? null : reader.GetString(2),
            Type: reader.IsDBNull(3) ? null : reader.GetString(3),
            Layout: reader.IsDBNull(4) ? null : reader.GetString(4),
            PublishOption: reader.IsDBNull(5) ? "DRAFT" : reader.GetString(5),
            CreatedBy: reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            ModifiedBy: reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt: ParseFormDate(reader.IsDBNull(8) ? null : reader.GetValue(8)),
            ModifiedAt: ParseFormDate(reader.IsDBNull(9) ? null : reader.GetValue(9)),
            FormJson: formJson);
    }

    private async Task<JsonElement?> LoadFormJsonElementAsync(string formId, CancellationToken cancellationToken)
    {
        var json = await _formJsonStorage.GetFormJsonAsync(formId, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static DateTime? ParseFormDate(object? value)
    {
        if (value == null || value == DBNull.Value)
            return null;
        if (value is DateTime dt)
            return dt;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static string MapFormSortColumn(string? criteria)
    {
        var c = (criteria ?? "modifiedAt").Trim().ToLowerInvariant();
        return c switch
        {
            "name" => "f.name",
            "description" => "f.description",
            "createdby" => "f.\"createdBy\"",
            "createdat" => "f.\"createdAt\"",
            "modifiedby" => "f.\"modifiedBy\"",
            "modifiedat" => "f.\"modifiedAt\"",
            "publishoption" or "flowstatus" => "f.\"publishOption\"",
            _ => "COALESCE(f.\"modifiedAt\", f.\"createdAt\")"
        };
    }

    private static List<FormAllGroup> GroupFormItems(List<FormAllItem> items, string? groupBy)
    {
        var g = (groupBy ?? string.Empty).Trim().ToLowerInvariant();
        var groups = g switch
        {
            "name" => items.GroupBy(x => x.Name ?? string.Empty),
            "description" => items.GroupBy(x => x.Description ?? string.Empty),
            "publishoption" or "flowstatus" => items.GroupBy(x => x.PublishOption ?? string.Empty),
            "createdby" => items.GroupBy(x => x.CreatedBy ?? string.Empty),
            "modifiedby" => items.GroupBy(x => x.ModifiedBy ?? string.Empty),
            _ => items.GroupBy(_ => string.Empty)
        };
        return groups.Select(x => new FormAllGroup(x.Key, x.ToList())).ToList();
    }

    private static bool TryBuildFormFilterCondition(FormAllFilter filter, out string conditionSql, out NpgsqlParameter? parameter)
    {
        conditionSql = string.Empty;
        parameter = null;
        if (filter == null || string.IsNullOrWhiteSpace(filter.Criteria) || string.IsNullOrWhiteSpace(filter.Condition))
            return false;

        var col = filter.Criteria.Trim().ToLowerInvariant() switch
        {
            "name" => "f.name",
            "description" => "f.description",
            "publishoption" or "flowstatus" => "f.\"publishOption\"",
            "createdby" => "f.\"createdBy\"",
            "modifiedby" => "f.\"modifiedBy\"",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(col))
            return false;

        var paramName = $"@f_{Math.Abs((filter.Criteria + filter.Condition + filter.Value).GetHashCode())}";
        var cond = filter.Condition.Trim().ToLowerInvariant();
        if (cond is "contains" or "like")
        {
            conditionSql = $"{col} LIKE {paramName}";
            parameter = new NpgsqlParameter(paramName, $"%{filter.Value ?? string.Empty}%");
            return true;
        }
        if (cond is "eq" or "=" or "equal")
        {
            conditionSql = $"{col} = {paramName}";
            parameter = new NpgsqlParameter(paramName, filter.Value ?? string.Empty);
            return true;
        }
        if (cond is "neq" or "!=" or "notequal")
        {
            conditionSql = $"{col} <> {paramName}";
            parameter = new NpgsqlParameter(paramName, filter.Value ?? string.Empty);
            return true;
        }

        return false;
    }
}
