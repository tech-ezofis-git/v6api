using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Forms;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class WorkflowTicketSearchService : IWorkflowTicketSearchService
{
    private static readonly string[] TextOperators =
    [
        "eq", "neq", "contains", "startsWith", "endsWith", "in", "isNull", "isNotNull"
    ];

    private static readonly string[] NumberDateOperators =
    [
        "eq", "neq", "gt", "gte", "lt", "lte", "between", "in", "isNull", "isNotNull"
    ];

    private static readonly string[] BoolOperators = ["eq", "neq", "isNull", "isNotNull"];

    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IUserEmailLookup _userEmails;
    private readonly IFormEntryService _formEntryService;

    public WorkflowTicketSearchService(
        ITenantConnectionProvider connectionProvider,
        IUserEmailLookup userEmails,
        IFormEntryService formEntryService)
    {
        _connectionProvider = connectionProvider;
        _userEmails = userEmails;
        _formEntryService = formEntryService;
    }

    public async Task<WorkflowTicketFilterSchemaDto?> GetFilterFieldsAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var formId = await LoadWorkflowFormIdAsync(connection, workflowId, cancellationToken);
        if (formId == null && !await WorkflowExistsAsync(connection, workflowId, cancellationToken))
            return null;

        if (string.IsNullOrWhiteSpace(formId))
            return new WorkflowTicketFilterSchemaDto(workflowId, null, Array.Empty<WorkflowTicketFilterFieldDto>());

        var normalizedFormId = FormIdNaming.NormalizeFormId(formId);
        var wFormIdValue = await ResolveWFormIdParameterAsync(connection, normalizedFormId, cancellationToken);
        var controls = await LoadFormControlsAsync(connection, wFormIdValue, cancellationToken);

        var fields = new List<WorkflowTicketFilterFieldDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in controls)
        {
            if (string.IsNullOrWhiteSpace(control.JsonId))
                continue;

            if (!EzfbColumnNaming.TryToColumnName(control.JsonId, out var column))
                column = control.JsonId.Trim();

            if (!seen.Add(column))
                continue;

            var name = string.IsNullOrWhiteSpace(control.Name) ? column : control.Name.Trim();
            var rawType = string.IsNullOrWhiteSpace(control.Type) ? null : control.Type.Trim();
            var operatorKind = InferDataType(control.Type);
            fields.Add(new WorkflowTicketFilterFieldDto(
                name,
                column,
                rawType,
                GetSupportedOperators(operatorKind)));
        }

        return new WorkflowTicketFilterSchemaDto(workflowId, normalizedFormId, fields);
    }

    public async Task<FormControlDistinctValuesResult?> GetDistinctControlValuesAsync(
        Guid workflowId,
        string controlName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(controlName))
            return new FormControlDistinctValuesResult(
                FormControlDistinctValuesStatus.ControlNotFound,
                null,
                controlName,
                null,
                null,
                null);

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await WorkflowExistsAsync(connection, workflowId, cancellationToken))
            return null;

        var formId = await LoadWorkflowFormIdAsync(connection, workflowId, cancellationToken);
        if (string.IsNullOrWhiteSpace(formId))
        {
            return new FormControlDistinctValuesResult(
                FormControlDistinctValuesStatus.FormNotFound,
                null,
                controlName.Trim(),
                null,
                null,
                null);
        }

        return await _formEntryService.GetDistinctControlValuesAsync(
            formId,
            controlName.Trim(),
            cancellationToken);
    }

    public async Task<WorkflowTicketSearchOutcome> SearchAsync(
        Guid workflowId,
        WorkflowTicketSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await WorkflowExistsAsync(connection, workflowId, cancellationToken))
            return new WorkflowTicketSearchOutcome(WorkflowTicketSearchStatus.WorkflowNotFound, null);

        var page = request.CurrentPage <= 0 ? 1 : request.CurrentPage;
        var pageSize = request.ItemsPerPage <= 0 ? 20 : Math.Min(request.ItemsPerPage, 500);

        var workflowMeta = await LoadWorkflowMetaAsync(connection, workflowId, cancellationToken);
        if (string.IsNullOrWhiteSpace(workflowMeta.FormId))
            return new WorkflowTicketSearchOutcome(
                WorkflowTicketSearchStatus.FormNotConfigured,
                EmptyResult(page, pageSize, tableExists: true, request.GroupBy));

        var normalizedFormId = FormIdNaming.NormalizeFormId(workflowMeta.FormId);
        var formSuffix = FormIdNaming.GetEzfbTableSuffix(normalizedFormId);
        var ezfbTable = $"ezfb_{formSuffix}_items";
        var workflowSuffix = workflowId.ToString("N")[..8];
        var processFormTable = $"workflow.processForm_{workflowSuffix}";
        var instancesTable = $"workflow.[WorkflowInstances_{workflowSuffix}]";
        var transactionTable = $"workflow.[transaction_{workflowSuffix}]";
        var workflowFormsTable = $"workflow.WorkflowForms_{workflowSuffix}";
        var workflowAttachmentsTable = $"workflow.WorkflowAttachments_{workflowSuffix}";
        var workflowCommentsTable = $"workflow.WorkflowComments_{workflowSuffix}";
        var agentDataValidationTable = $"workflow.[agentDataValidation_{workflowSuffix}]";

        if (!await TableExistsAsync(connection, "dbo", ezfbTable, cancellationToken)
            || !await TableExistsAsync(connection, "workflow", $"processForm_{workflowSuffix}", cancellationToken)
            || !await TableExistsAsync(connection, "workflow", $"WorkflowInstances_{workflowSuffix}", cancellationToken)
            || !await TableExistsAsync(connection, "workflow", $"transaction_{workflowSuffix}", cancellationToken))
        {
            return new WorkflowTicketSearchOutcome(
                WorkflowTicketSearchStatus.TablesMissing,
                EmptyResult(page, pageSize, tableExists: false, request.GroupBy));
        }

        var offset = (page - 1) * pageSize;
        var ezfbColumns = await LoadTableColumnsAsync(connection, ezfbTable, cancellationToken);
        var wFormIdValue = await ResolveWFormIdParameterAsync(connection, normalizedFormId, cancellationToken);
        var controls = await LoadFormControlsAsync(connection, wFormIdValue, cancellationToken);

        var ezfbWhereParts = new List<string> { "(e.isDeleted = 0 OR e.isDeleted IS NULL)" };
        var filterParameters = new List<SqlParameter>();
        var filters = request.FilterBy ?? Array.Empty<WorkflowTicketSearchFilter>();
        var index = 0;
        foreach (var filter in filters)
        {
            if (!TryResolveColumn(filter.Criteria, controls, ezfbColumns, out var column))
                throw new ArgumentException($"Unknown filter field '{filter.Criteria}'.");

            if (!TryBuildOperatorCondition(
                    column,
                    filter.Condition,
                    filter.Value,
                    filter.ValueTo,
                    filter.DataType,
                    index++,
                    out var sqlPart,
                    out var filterParams,
                    tableAlias: "e"))
                throw new ArgumentException($"Unsupported filter condition '{filter.Condition}' for field '{filter.Criteria}'.");

            ezfbWhereParts.Add(sqlPart);
            filterParameters.AddRange(filterParams);
        }

        var ezfbWhereSql = string.Join(" AND ", ezfbWhereParts);
        var matchedInstancesCte = filters.Count == 0
            ? $"""
                matched AS (
                    SELECT DISTINCT pf.WorkflowInstanceId
                    FROM {processFormTable} pf
                    WHERE pf.IsDeleted = 0
                )
                """
            : $"""
                matched AS (
                    SELECT DISTINCT pf.WorkflowInstanceId
                    FROM {processFormTable} pf
                    INNER JOIN dbo.[{ezfbTable}] e ON e.itemId = pf.FormEntryId
                    WHERE pf.IsDeleted = 0
                      AND {ezfbWhereSql}
                )
                """;

        var sortColumn = MapSortColumn(request.SortBy?.Criteria);
        var sortOrder = string.Equals(request.SortBy?.Order, "ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        var hasCompletedAt = await ColumnExistsAsync(connection, "workflow", $"WorkflowInstances_{workflowSuffix}", "CompletedAtUtc", cancellationToken);
        var hasModifiedBy = await ColumnExistsAsync(connection, "workflow", $"transaction_{workflowSuffix}", "ModifiedBy", cancellationToken);
        var completedAtSelect = hasCompletedAt ? "wi.CompletedAtUtc" : "CAST(NULL AS datetime2)";
        var modifiedBySelect = hasModifiedBy ? "t.ModifiedBy" : "CAST(NULL AS uniqueidentifier)";

        var countSql = $"""
            WITH {matchedInstancesCte}
            SELECT COUNT(1) FROM matched;
            """;

        int totalItems;
        await using (var countCmd = new SqlCommand(countSql, connection))
        {
            countCmd.Parameters.AddRange(CloneParameters(filterParameters));
            totalItems = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        if (totalItems == 0)
            return new WorkflowTicketSearchOutcome(
                WorkflowTicketSearchStatus.Found,
                EmptyResult(page, pageSize, tableExists: true, request.GroupBy));

        var dataSql = $"""
            WITH {matchedInstancesCte},
            ranked AS (
                SELECT
                    t.Id AS TransactionId,
                    t.WorkflowInstanceId,
                    wi.ReferenceNumber,
                    wi.StartedAtUtc AS InstanceStartedAtUtc,
                    {completedAtSelect} AS CompletedAtUtc,
                    wi.StartedBy AS RaisedByUserId,
                    t.ActivityId,
                    t.RuleId,
                    t.StageType,
                    t.StageName,
                    t.Review,
                    t.CreatedAt AS TransactionCreatedAt,
                    t.CreatedBy AS TransactionCreatedBy,
                    t.ModifiedAt AS TransactionModifiedAt,
                    {modifiedBySelect} AS TransactionModifiedBy,
                    t.ActivityUserId,
                    t.ActivityGroupId,
                    ROW_NUMBER() OVER (
                        PARTITION BY t.WorkflowInstanceId
                        ORDER BY t.CreatedAt DESC, t.Id DESC
                    ) AS rn
                FROM {transactionTable} t
                INNER JOIN {instancesTable} wi ON wi.Id = t.WorkflowInstanceId
                INNER JOIN matched m ON m.WorkflowInstanceId = t.WorkflowInstanceId
                WHERE t.IsDeleted = 0
            )
            SELECT *
            FROM ranked
            WHERE rn = 1
            ORDER BY {sortColumn} {sortOrder}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        var pagedRows = new List<TicketSearchRow>();
        await using (var dataCmd = new SqlCommand(dataSql, connection))
        {
            dataCmd.Parameters.AddRange(CloneParameters(filterParameters));
            dataCmd.Parameters.AddWithValue("@Offset", Math.Max(offset, 0));
            dataCmd.Parameters.AddWithValue("@PageSize", pageSize);
            await using var reader = await dataCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                pagedRows.Add(ReadSearchRow(reader));
        }

        var emailIds = new HashSet<Guid>();
        foreach (var row in pagedRows)
        {
            if (row.TransactionCreatedBy.HasValue)
                emailIds.Add(row.TransactionCreatedBy.Value);
            if (row.ActivityUserId.HasValue)
                emailIds.Add(row.ActivityUserId.Value);
        }

        var emails = emailIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _userEmails.GetEmailsAsync(emailIds, cancellationToken);

        var items = new List<LegacyMailboxRowDto>();
        foreach (var row in pagedRows)
        {
            var form = await TryGetFormIdentityAsync(
                connection,
                workflowFormsTable,
                processFormTable,
                workflowAttachmentsTable,
                normalizedFormId,
                row.WorkflowInstanceId,
                cancellationToken);
            var repo = await TryGetRepositoryDataAsync(
                connection,
                workflowAttachmentsTable,
                row.WorkflowInstanceId,
                cancellationToken);
            var commentsCount = await TryGetCommentsCountAsync(
                connection,
                workflowCommentsTable,
                row.WorkflowInstanceId,
                cancellationToken);
            var agent = await TryGetAgentValidationAsync(
                connection,
                agentDataValidationTable,
                row.WorkflowInstanceId,
                cancellationToken);

            emails.TryGetValue(row.TransactionCreatedBy ?? Guid.Empty, out var createdByEmail);
            emails.TryGetValue(row.ActivityUserId ?? Guid.Empty, out var activityUserEmail);

            items.Add(new LegacyMailboxRowDto(
                Id: row.TransactionId,
                UserId: row.ActivityUserId?.ToString("D"),
                GroupId: row.ActivityGroupId,
                WorkflowId: workflowId.ToString("D"),
                Name: workflowMeta.Name,
                WorkflowInstanceId: row.WorkflowInstanceId.ToString("D"),
                ReferenceNumber: row.ReferenceNumber,
                CreatedAtUtc: row.InstanceStartedAtUtc,
                StartedAtUtc: row.InstanceStartedAtUtc,
                CompletedAtUtc: row.CompletedAtUtc,
                Context: null,
                TransactionId: row.TransactionId.ToString(CultureInfo.InvariantCulture),
                ActivityId: row.ActivityId,
                RuleId: row.RuleId,
                StageType: row.StageType,
                Stage: row.StageName,
                Review: row.Review,
                TransactionCreatedAt: row.TransactionCreatedAt,
                TransactionCreatedBy: row.TransactionCreatedBy?.ToString("D"),
                TransactionCreatedByEmail: createdByEmail,
                TransactionModifiedAt: row.TransactionModifiedAt,
                TransactionModifiedBy: row.TransactionModifiedBy?.ToString("D"),
                RepositoryId: repo.RepositoryId?.ToString("D") ?? form.RepositoryId,
                ItemId: repo.ItemId?.ToString("D") ?? form.ItemId,
                FormId: form.FormId,
                FormEntryId: form.FormEntryId?.ToString(CultureInfo.InvariantCulture),
                FormData: form.FormDataJson,
                MlPrediction: null,
                MlCondition: null,
                UserType: null,
                CreatedByName: null,
                LastActionStageType: null,
                LastActionStageName: null,
                LastAction: null,
                CommentsCount: commentsCount,
                AttachmentCount: null,
                ActivityUserEmail: activityUserEmail,
                ActivityGroupName: null,
                AgentValidationWorkflowId: agent.WorkflowId ?? workflowId.ToString("D"),
                AgentResponse: agent.AgentResponse,
                AgentHtml: agent.AgentHtml ?? string.Empty,
                Action: 1));
        }

        return new WorkflowTicketSearchOutcome(
            WorkflowTicketSearchStatus.Found,
            ToGroupedResult(items, totalItems, page, pageSize, request.GroupBy, tableExists: true, controls, ezfbColumns));
    }

    private static WorkflowFilterSearchResult EmptyResult(
        int page,
        int pageSize,
        bool tableExists,
        string? groupBy = null) =>
        ToGroupedResult(
            Array.Empty<LegacyMailboxRowDto>(),
            0,
            page,
            pageSize,
            groupBy,
            tableExists,
            controls: null,
            ezfbColumns: null);

    private static WorkflowFilterSearchResult ToGroupedResult(
        IReadOnlyList<LegacyMailboxRowDto> items,
        int totalItems,
        int page,
        int pageSize,
        string? groupBy,
        bool tableExists,
        IReadOnlyList<FormControlRow>? controls,
        IReadOnlySet<string>? ezfbColumns) =>
        new(
            GroupItems(items, groupBy, controls, ezfbColumns),
            new WorkflowFilterSearchMeta(page, pageSize, totalItems),
            tableExists);

    private static IReadOnlyList<WorkflowFilterSearchGroup> GroupItems(
        IReadOnlyList<LegacyMailboxRowDto> items,
        string? groupBy,
        IReadOnlyList<FormControlRow>? controls,
        IReadOnlySet<string>? ezfbColumns)
    {
        var g = (groupBy ?? string.Empty).Trim();
        if (g.Length == 0)
        {
            return items
                .GroupBy(_ => string.Empty)
                .Select(x => new WorkflowFilterSearchGroup(x.Key, x.ToList()))
                .ToList();
        }

        string? formColumn = null;
        if (controls != null && ezfbColumns != null)
            TryResolveColumn(g, controls, ezfbColumns, out formColumn);

        var isMailbox = IsMailboxGroupBy(g);
        if (!isMailbox && string.IsNullOrWhiteSpace(formColumn))
        {
            // Still allow grouping by a raw FormData JSON property name even if not in wFormControl.
            if (items.Count > 0 && !items.Any(i => FormDataHasProperty(i.FormData, g)))
                throw new ArgumentException($"Unknown groupBy field '{groupBy}'.");
        }

        var groups = items.GroupBy(row => ResolveGroupKey(row, g, formColumn));
        return groups
            .Select(x => new WorkflowFilterSearchGroup(x.Key, x.ToList()))
            .ToList();
    }

    private static bool IsMailboxGroupBy(string groupBy)
    {
        var g = groupBy.Trim().ToLowerInvariant();
        return g is "id" or "userid" or "groupid" or "workflowid" or "name"
            or "workflowinstanceid" or "referencenumber" or "requestno"
            or "createdatutc" or "startedatutc" or "completedatutc" or "context"
            or "transactionid" or "activityid" or "ruleid" or "stagetype"
            or "stage" or "stagename" or "review"
            or "transactioncreatedat" or "transactioncreatedby" or "transactioncreatedbyemail"
            or "transactionmodifiedat" or "transactionmodifiedby"
            or "repositoryid" or "itemid" or "formid" or "formentryid"
            or "mlprediction" or "mlcondition" or "usertype" or "createdbyname"
            or "lastactionstagetype" or "lastactionstagename" or "lastaction"
            or "commentscount" or "attachmentcount"
            or "activityuseremail" or "activitygroupname"
            or "agentvalidationworkflowid" or "agentresponse" or "agenthtml" or "action";
    }

    private static string ResolveGroupKey(LegacyMailboxRowDto row, string groupBy, string? formColumn)
    {
        if (TryGetMailboxGroupValue(row, groupBy, out var mailboxValue))
            return mailboxValue;

        var fromForm = GetFormDataValue(row.FormData, formColumn, groupBy);
        return fromForm ?? string.Empty;
    }

    private static bool TryGetMailboxGroupValue(LegacyMailboxRowDto row, string groupBy, out string value)
    {
        value = string.Empty;
        var g = groupBy.Trim().ToLowerInvariant();
        string? raw = g switch
        {
            "id" => row.Id.ToString(CultureInfo.InvariantCulture),
            "userid" => row.UserId,
            "groupid" => row.GroupId?.ToString(CultureInfo.InvariantCulture),
            "workflowid" => row.WorkflowId,
            "name" => row.Name,
            "workflowinstanceid" => row.WorkflowInstanceId,
            "referencenumber" or "requestno" => row.ReferenceNumber,
            "createdatutc" => FormatDate(row.CreatedAtUtc),
            "startedatutc" => FormatDate(row.StartedAtUtc),
            "completedatutc" => FormatDate(row.CompletedAtUtc),
            "context" => row.Context,
            "transactionid" => row.TransactionId,
            "activityid" => row.ActivityId,
            "ruleid" => row.RuleId,
            "stagetype" => row.StageType,
            "stage" or "stagename" => row.Stage,
            "review" => row.Review,
            "transactioncreatedat" => FormatDate(row.TransactionCreatedAt),
            "transactioncreatedby" => row.TransactionCreatedBy,
            "transactioncreatedbyemail" => row.TransactionCreatedByEmail,
            "transactionmodifiedat" => FormatDate(row.TransactionModifiedAt),
            "transactionmodifiedby" => row.TransactionModifiedBy,
            "repositoryid" => row.RepositoryId,
            "itemid" => row.ItemId,
            "formid" => row.FormId,
            "formentryid" => row.FormEntryId,
            "mlprediction" => row.MlPrediction,
            "mlcondition" => row.MlCondition,
            "usertype" => row.UserType,
            "createdbyname" => row.CreatedByName,
            "lastactionstagetype" => row.LastActionStageType,
            "lastactionstagename" => row.LastActionStageName,
            "lastaction" => row.LastAction,
            "commentscount" => row.CommentsCount?.ToString(CultureInfo.InvariantCulture),
            "attachmentcount" => row.AttachmentCount?.ToString(CultureInfo.InvariantCulture),
            "activityuseremail" => row.ActivityUserEmail,
            "activitygroupname" => row.ActivityGroupName,
            "agentvalidationworkflowid" => row.AgentValidationWorkflowId,
            "agentresponse" => row.AgentResponse,
            "agenthtml" => row.AgentHtml,
            "action" => row.Action.ToString(CultureInfo.InvariantCulture),
            _ => null
        };

        if (raw is null && !IsMailboxGroupBy(groupBy))
            return false;

        value = raw ?? string.Empty;
        return IsMailboxGroupBy(groupBy);
    }

    private static string? FormatDate(DateTime? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static bool FormDataHasProperty(string? formDataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(formDataJson) || string.IsNullOrWhiteSpace(propertyName))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(formDataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static string? GetFormDataValue(string? formDataJson, params string?[] candidates)
    {
        if (string.IsNullOrWhiteSpace(formDataJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(formDataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, candidate, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return prop.Value.ValueKind switch
                    {
                        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                        JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.Value.GetRawText(),
                        JsonValueKind.Object or JsonValueKind.Array => prop.Value.GetRawText(),
                        _ => prop.Value.ToString()
                    };
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private sealed record WorkflowMeta(string? FormId, string? Name);

    private static async Task<WorkflowMeta> LoadWorkflowMetaAsync(
        SqlConnection connection,
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT FormId, Name FROM workflow.Workflows WHERE Id = @Id AND IsDeleted = 0;";
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", workflowId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new WorkflowMeta(null, null);

        return new WorkflowMeta(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task<bool> ColumnExistsAsync(
        SqlConnection connection,
        string schema,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        cmd.Parameters.AddWithValue("@ColumnName", columnName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<bool> WorkflowExistsAsync(
        SqlConnection connection,
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(1) FROM workflow.Workflows WHERE Id = @Id AND IsDeleted = 0;";
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", workflowId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<string?> LoadWorkflowFormIdAsync(
        SqlConnection connection,
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT FormId FROM workflow.Workflows WHERE Id = @Id AND IsDeleted = 0;";
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", workflowId);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value == null || value == DBNull.Value ? null : Convert.ToString(value)?.Trim();
    }

    private static async Task<bool> TableExistsAsync(
        SqlConnection connection,
        string schema,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @TableName
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<HashSet<string>> LoadTableColumnsAsync(
        SqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = N'dbo' AND TABLE_NAME = @TableName
            """;
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));
        return columns;
    }

    private sealed record FormControlRow(string JsonId, string? Name, string? Type);

    private static async Task<List<FormControlRow>> LoadFormControlsAsync(
        SqlConnection connection,
        object wFormIdValue,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT jsonId, name, [type]
            FROM dbo.wFormControl
            WHERE wFormId = @FormId
              AND isDeleted = 0
              AND jsonId IS NOT NULL
              AND LTRIM(RTRIM(jsonId)) <> ''
            """;
        var rows = new List<FormControlRow>();
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", wFormIdValue);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FormControlRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    private static async Task<object> ResolveWFormIdParameterAsync(
        SqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DATA_TYPE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = N'dbo' AND TABLE_NAME = N'wFormControl' AND COLUMN_NAME = N'wFormId'
            """;
        await using var cmd = new SqlCommand(sql, connection);
        var type = (await cmd.ExecuteScalarAsync(cancellationToken))?.ToString()?.ToLowerInvariant();
        if (type is "int" or "bigint" or "smallint" or "tinyint")
        {
            if (int.TryParse(formId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n;
            var hex = new string(formId.Where(Uri.IsHexDigit).ToArray());
            if (hex.Length > 8)
                hex = hex[..8];
            if (uint.TryParse(hex.PadLeft(8, '0'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u))
                return unchecked((int)u);
        }

        return formId;
    }

    private static bool TryResolveColumn(
        string criteria,
        IReadOnlyList<FormControlRow> controls,
        IReadOnlySet<string> ezfbColumns,
        out string column)
    {
        column = string.Empty;
        if (string.IsNullOrWhiteSpace(criteria))
            return false;

        var key = criteria.Trim();
        foreach (var control in controls)
        {
            if (!string.IsNullOrWhiteSpace(control.Name)
                && string.Equals(control.Name.Trim(), key, StringComparison.OrdinalIgnoreCase)
                && TryResolveEzfbColumn(control.JsonId, ezfbColumns, out column))
            {
                return true;
            }
        }

        foreach (var control in controls)
        {
            if (string.Equals(control.JsonId, key, StringComparison.OrdinalIgnoreCase)
                && TryResolveEzfbColumn(control.JsonId, ezfbColumns, out column))
            {
                return true;
            }
        }

        return TryResolveEzfbColumn(key, ezfbColumns, out column);
    }

    private static bool TryResolveEzfbColumn(string jsonId, IReadOnlySet<string> ezfbColumns, out string column)
    {
        column = string.Empty;
        if (string.IsNullOrWhiteSpace(jsonId))
            return false;

        var trimmed = jsonId.Trim();
        if (ezfbColumns.Contains(trimmed))
        {
            column = trimmed;
            return true;
        }

        if (EzfbColumnNaming.TryToColumnName(trimmed, out var fromJsonId) && ezfbColumns.Contains(fromJsonId))
        {
            column = fromJsonId;
            return true;
        }

        if (EzfbColumnNaming.TryToColumnName(trimmed, out var baseName)
            && baseName.Length > 0
            && char.IsDigit(baseName[0]))
        {
            var legacy = "F_" + baseName;
            if (ezfbColumns.Contains(legacy))
            {
                column = legacy;
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildOperatorCondition(
        string column,
        string condition,
        JsonElement value,
        string? valueTo,
        string? dataType,
        int index,
        out string sql,
        out List<SqlParameter> parameters,
        string tableAlias = "")
    {
        sql = string.Empty;
        parameters = new List<SqlParameter>();
        var escaped = EscapeColumn(column);
        var prefix = string.IsNullOrEmpty(tableAlias) ? string.Empty : $"{tableAlias}.";
        var columnExpr = $"{prefix}[{escaped}]";
        var cond = (condition ?? string.Empty).Trim().ToLowerInvariant();
        var paramBase = $"@p{index}";
        var typeHint = (dataType ?? string.Empty).Trim().ToLowerInvariant();
        var isDate = typeHint is "date" or "datetime" or "time";

        if (isDate && value.ValueKind == JsonValueKind.Array)
            throw new ArgumentException("For dataType 'date', value must be a string (use valueTo for the range end).");

        var scalar = GetScalarValue(value);
        var listValues = GetValueList(value);

        switch (cond)
        {
            case "eq" or "=" or "equal":
                sql = $"{columnExpr} = {paramBase}";
                parameters.Add(new SqlParameter(paramBase, scalar ?? string.Empty));
                return true;
            case "neq" or "!=" or "notequal":
                sql = $"{columnExpr} <> {paramBase}";
                parameters.Add(new SqlParameter(paramBase, scalar ?? string.Empty));
                return true;
            case "contains" or "like":
                sql = $"{columnExpr} LIKE {paramBase}";
                parameters.Add(new SqlParameter(paramBase, $"%{scalar ?? string.Empty}%"));
                return true;
            case "startswith":
                sql = $"{columnExpr} LIKE {paramBase}";
                parameters.Add(new SqlParameter(paramBase, $"{scalar ?? string.Empty}%"));
                return true;
            case "endswith":
                sql = $"{columnExpr} LIKE {paramBase}";
                parameters.Add(new SqlParameter(paramBase, $"%{scalar ?? string.Empty}"));
                return true;
            case "gt":
                return BuildComparison(columnExpr, ">", paramBase, scalar, out sql, out parameters);
            case "gte":
                return BuildComparison(columnExpr, ">=", paramBase, scalar, out sql, out parameters);
            case "lt":
                return BuildComparison(columnExpr, "<", paramBase, scalar, out sql, out parameters);
            case "lte":
                return BuildComparison(columnExpr, "<=", paramBase, scalar, out sql, out parameters);
            case "between":
                return BuildBetween(columnExpr, paramBase, scalar, valueTo, typeHint, out sql, out parameters);
            case "in":
            {
                var values = listValues.Count > 0 ? listValues : ParseInValues(scalar);
                if (values.Count == 0)
                    throw new ArgumentException("Filter condition 'in' requires a non-empty value array or comma-separated string.");
                var names = new List<string>();
                for (var i = 0; i < values.Count; i++)
                {
                    var name = $"{paramBase}_{i}";
                    names.Add(name);
                    parameters.Add(new SqlParameter(name, values[i]));
                }
                sql = $"{columnExpr} IN ({string.Join(", ", names)})";
                return true;
            }
            case "isnull":
                sql = $"{columnExpr} IS NULL";
                return true;
            case "isnotnull":
                sql = $"{columnExpr} IS NOT NULL";
                return true;
            default:
                return false;
        }
    }

    private static string? GetScalarValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => value.GetArrayLength() > 0 ? GetScalarValue(value[0]) : null,
        _ => value.GetRawText()
    };

    private static List<string> GetValueList(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return value.EnumerateArray()
            .Select(GetScalarValue)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .ToList();
    }

    private static bool BuildBetween(
        string columnExpr,
        string paramBase,
        string? valueFrom,
        string? valueTo,
        string typeHint,
        out string sql,
        out List<SqlParameter> parameters)
    {
        parameters = new List<SqlParameter>();
        if (string.IsNullOrWhiteSpace(valueFrom) || string.IsNullOrWhiteSpace(valueTo))
            throw new ArgumentException("Filter condition 'between' requires value and valueTo.");

        var fromName = $"{paramBase}_from";
        var toName = $"{paramBase}_to";
        var isDate = typeHint is "date" or "datetime" or "time"
            || (DateTime.TryParse(valueFrom, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _)
                && !decimal.TryParse(valueFrom, NumberStyles.Number, CultureInfo.InvariantCulture, out _));

        if (isDate
            && DateTime.TryParse(valueFrom, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var fromDt)
            && DateTime.TryParse(valueTo, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var toDt))
        {
            sql = $"TRY_CONVERT(datetime2, {columnExpr}) >= {fromName} AND TRY_CONVERT(datetime2, {columnExpr}) <= {toName}";
            parameters.Add(new SqlParameter(fromName, fromDt));
            parameters.Add(new SqlParameter(toName, toDt));
            return true;
        }

        if (decimal.TryParse(valueFrom, NumberStyles.Any, CultureInfo.InvariantCulture, out var fromNum)
            && decimal.TryParse(valueTo, NumberStyles.Any, CultureInfo.InvariantCulture, out var toNum))
        {
            sql = $"TRY_CONVERT(float, {columnExpr}) >= {fromName} AND TRY_CONVERT(float, {columnExpr}) <= {toName}";
            parameters.Add(new SqlParameter(fromName, fromNum));
            parameters.Add(new SqlParameter(toName, toNum));
            return true;
        }

        sql = $"{columnExpr} >= {fromName} AND {columnExpr} <= {toName}";
        parameters.Add(new SqlParameter(fromName, valueFrom));
        parameters.Add(new SqlParameter(toName, valueTo));
        return true;
    }

    private static SqlParameter[] CloneParameters(IEnumerable<SqlParameter> source) =>
        source.Select(p => new SqlParameter(p.ParameterName, p.Value ?? DBNull.Value)).ToArray();

    private sealed record TicketSearchRow(
        int TransactionId,
        Guid WorkflowInstanceId,
        string? ReferenceNumber,
        DateTime InstanceStartedAtUtc,
        DateTime? CompletedAtUtc,
        Guid? RaisedByUserId,
        string? ActivityId,
        string? RuleId,
        string? StageType,
        string? StageName,
        string? Review,
        DateTime TransactionCreatedAt,
        Guid? TransactionCreatedBy,
        DateTime? TransactionModifiedAt,
        Guid? TransactionModifiedBy,
        Guid? ActivityUserId,
        int? ActivityGroupId);

    private sealed record FormIdentityResult(
        string? FormId,
        int? FormEntryId,
        string? FormDataJson,
        string? RepositoryId,
        string? ItemId);

    private sealed record RepoIds(Guid? RepositoryId, Guid? ItemId);

    private sealed record AgentValidationResult(string? WorkflowId, string? AgentResponse, string? AgentHtml);

    private static TicketSearchRow ReadSearchRow(SqlDataReader reader) =>
        new(
            TransactionId: reader.GetInt32(reader.GetOrdinal("TransactionId")),
            WorkflowInstanceId: reader.GetGuid(reader.GetOrdinal("WorkflowInstanceId")),
            ReferenceNumber: reader.IsDBNull(reader.GetOrdinal("ReferenceNumber")) ? null : reader.GetString(reader.GetOrdinal("ReferenceNumber")),
            InstanceStartedAtUtc: reader.GetDateTime(reader.GetOrdinal("InstanceStartedAtUtc")),
            CompletedAtUtc: reader.IsDBNull(reader.GetOrdinal("CompletedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("CompletedAtUtc")),
            RaisedByUserId: reader.IsDBNull(reader.GetOrdinal("RaisedByUserId")) ? null : reader.GetGuid(reader.GetOrdinal("RaisedByUserId")),
            ActivityId: reader.IsDBNull(reader.GetOrdinal("ActivityId")) ? null : reader.GetString(reader.GetOrdinal("ActivityId")),
            RuleId: reader.IsDBNull(reader.GetOrdinal("RuleId")) ? null : reader.GetString(reader.GetOrdinal("RuleId")),
            StageType: reader.IsDBNull(reader.GetOrdinal("StageType")) ? null : reader.GetString(reader.GetOrdinal("StageType")),
            StageName: reader.IsDBNull(reader.GetOrdinal("StageName")) ? null : reader.GetString(reader.GetOrdinal("StageName")),
            Review: reader.IsDBNull(reader.GetOrdinal("Review")) ? null : reader.GetString(reader.GetOrdinal("Review")),
            TransactionCreatedAt: reader.GetDateTime(reader.GetOrdinal("TransactionCreatedAt")),
            TransactionCreatedBy: reader.IsDBNull(reader.GetOrdinal("TransactionCreatedBy")) ? null : reader.GetGuid(reader.GetOrdinal("TransactionCreatedBy")),
            TransactionModifiedAt: reader.IsDBNull(reader.GetOrdinal("TransactionModifiedAt")) ? null : reader.GetDateTime(reader.GetOrdinal("TransactionModifiedAt")),
            TransactionModifiedBy: reader.IsDBNull(reader.GetOrdinal("TransactionModifiedBy")) ? null : reader.GetGuid(reader.GetOrdinal("TransactionModifiedBy")),
            ActivityUserId: reader.IsDBNull(reader.GetOrdinal("ActivityUserId")) ? null : reader.GetGuid(reader.GetOrdinal("ActivityUserId")),
            ActivityGroupId: reader.IsDBNull(reader.GetOrdinal("ActivityGroupId")) ? null : reader.GetInt32(reader.GetOrdinal("ActivityGroupId")));

    private static bool BuildComparison(
        string columnExpr,
        string op,
        string paramName,
        string? value,
        out string sql,
        out List<SqlParameter> parameters)
    {
        parameters = new List<SqlParameter>();
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            sql = $"TRY_CONVERT(datetime2, {columnExpr}) {op} {paramName}";
            parameters.Add(new SqlParameter(paramName, dt));
            return true;
        }

        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
        {
            sql = $"TRY_CONVERT(float, {columnExpr}) {op} {paramName}";
            parameters.Add(new SqlParameter(paramName, d));
            return true;
        }

        sql = $"{columnExpr} {op} {paramName}";
        parameters.Add(new SqlParameter(paramName, value ?? string.Empty));
        return true;
    }

    private static List<string> ParseInValues(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new List<string>();

        var trimmed = value.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return doc.RootElement.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!.Trim())
                        .ToList();
                }
            }
            catch (JsonException)
            {
            }
        }

        return trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string MapSortColumn(string? criteria)
    {
        var c = (criteria ?? "raisedAt").Trim().ToLowerInvariant();
        return c switch
        {
            "requestno" or "referencenumber" => "ReferenceNumber",
            "stagename" => "StageName",
            "activityid" => "ActivityId",
            "transactioncreatedat" or "createdat" => "TransactionCreatedAt",
            "transactionmodifiedat" or "modifiedat" => "TransactionModifiedAt",
            "raisedat" or "startedat" or _ => "InstanceStartedAtUtc"
        };
    }

    private async Task<FormIdentityResult> TryGetFormIdentityAsync(
        SqlConnection connection,
        string workflowFormsTable,
        string processFormTable,
        string workflowAttachmentsTable,
        string fallbackFormId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        int? formEntryId = null;
        string? formGuid = null;

        // Prefer processForm (same FormEntryId path as ezfb filter join).
        try
        {
            var processSql = $"""
                SELECT TOP 1 WFormId, FormEntryId
                FROM {processFormTable}
                WHERE WorkflowInstanceId = @WorkflowInstanceId AND IsDeleted = 0
                ORDER BY Id DESC;
                """;
            await using var cmd = new SqlCommand(processSql, connection);
            cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                formGuid = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0))?.Trim();
                if (!reader.IsDBNull(1))
                {
                    var entryId = reader.GetInt32(1);
                    if (entryId > 0)
                        formEntryId = entryId;
                }
            }
        }
        catch (SqlException)
        {
        }

        if (formEntryId is not > 0)
        {
            try
            {
                var formsSql = $"""
                    SELECT TOP 1 FormEntryId
                    FROM {workflowFormsTable}
                    WHERE WorkflowInstanceId = @WorkflowInstanceId AND IsDeleted = 0
                    ORDER BY CreatedAtUtc DESC;
                    """;
                await using var cmd = new SqlCommand(formsSql, connection);
                cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0))
                {
                    var entryId = reader.GetInt32(0);
                    if (entryId > 0)
                        formEntryId = entryId;
                }
            }
            catch (SqlException)
            {
            }
        }

        if (string.IsNullOrWhiteSpace(formGuid))
        {
            try
            {
                var attachmentSql = $"""
                    SELECT TOP 1 FormJsonId
                    FROM {workflowAttachmentsTable}
                    WHERE WorkflowInstanceId = @WorkflowInstanceId AND IsDeleted = 0
                    ORDER BY ISNULL(ModifiedAtUtc, CreatedAtUtc) DESC, CreatedAtUtc DESC;
                    """;
                await using var cmd = new SqlCommand(attachmentSql, connection);
                cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
                var value = await cmd.ExecuteScalarAsync(cancellationToken);
                formGuid = value == null || value == DBNull.Value ? null : Convert.ToString(value)?.Trim();
            }
            catch (SqlException)
            {
            }
        }

        if (string.IsNullOrWhiteSpace(formGuid))
            formGuid = fallbackFormId;

        // Always load live field values from ezfb_{formSuffix}_items on the current tenant connection.
        string? fieldsJson = null;
        if (formEntryId is > 0 && !string.IsNullOrWhiteSpace(formGuid))
        {
            fieldsJson = await WorkflowEzfbFormDataLoader.LoadFormDataJsonAsync(
                connection,
                formGuid,
                formEntryId.Value,
                cancellationToken);
        }

        return new FormIdentityResult(formGuid, formEntryId, fieldsJson, null, null);
    }

    private static async Task<RepoIds> TryGetRepositoryDataAsync(
        SqlConnection connection,
        string workflowAttachmentsTable,
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var sql = $"SELECT TOP 1 RepositoryId, ItemId FROM {workflowAttachmentsTable} WHERE WorkflowInstanceId = @WorkflowInstanceId AND IsDeleted = 0 ORDER BY CreatedAtUtc DESC;";
            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return new RepoIds(
                    reader.IsDBNull(0) ? null : reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1));
            }
        }
        catch (SqlException)
        {
        }

        return new RepoIds(null, null);
    }

    private static async Task<int?> TryGetCommentsCountAsync(
        SqlConnection connection,
        string workflowCommentsTable,
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var sql = $"SELECT COUNT(1) FROM {workflowCommentsTable} WHERE WorkflowInstanceId = @WorkflowInstanceId AND IsDeleted = 0;";
            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        catch (SqlException)
        {
            return null;
        }
    }

    private static async Task<AgentValidationResult> TryGetAgentValidationAsync(
        SqlConnection connection,
        string agentDataValidationTable,
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var sql = $"""
                SELECT TOP 1 WorkflowId, AgentResponse, AgentHtmlResponse
                FROM {agentDataValidationTable}
                WHERE IsDeleted = 0
                  AND ProcessId = @ProcessId
                ORDER BY CreatedAt DESC, Id DESC;
                """;
            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@ProcessId", workflowInstanceId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return new AgentValidationResult(
                    reader.IsDBNull(0) ? null : reader.GetGuid(0).ToString("D"),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }
        catch (SqlException)
        {
        }

        return new AgentValidationResult(null, null, null);
    }

    private static string EscapeColumn(string column) => column.Replace("]", "]]", StringComparison.Ordinal);

    private static string InferDataType(string? controlType)
    {
        var t = (controlType ?? "text").Trim().ToLowerInvariant();
        if (t is "number" or "decimal" or "currency" or "integer" or "int" or "float")
            return "number";
        if (t is "date" or "datetime" or "time")
            return "date";
        if (t is "bool" or "boolean" or "checkbox")
            return "bool";
        // short_text, textbox, long_text, and other control types → text operators
        return "text";
    }

    private static IReadOnlyList<string> GetSupportedOperators(string dataType) => dataType switch
    {
        "number" or "date" => NumberDateOperators,
        "bool" => BoolOperators,
        _ => TextOperators
    };
}
