using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Api.Services;
using SaaSApp.MultiTenancy;
using SaaSApp.Security;
using System.Text.Json;
using System.Security.Claims;
using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Application.Workflows.Commands.AddWorkflowStep;
using SaaSApp.Workflow.Application.Workflows.Commands.CreateWorkflow;
using WorkflowJsonDto = SaaSApp.Workflow.Application.Workflows.Commands.CreateWorkflow.WorkflowJsonDto;
using SaaSApp.Workflow.Application.Workflows.Commands.DeleteWorkflow;
using SaaSApp.Workflow.Application.Workflows.Commands.PublishWorkflow;
using SaaSApp.Workflow.Application.Workflows.Commands.StartWorkflow;
using SaaSApp.Workflow.Application.Workflows.Commands.UpdateWorkflow;
using SaaSApp.Workflow.Application.Workflows.Commands.SyncWorkflowSteps;
using SaaSApp.Workflow.Application.Workflows.Queries.GetWorkflowById;
using SaaSApp.Workflow.Application.Workflows.Queries.GetWorkflowInstanceById;
using SaaSApp.Workflow.Application.Workflows.Queries.ListWorkflows;
using SaaSApp.Workflow.Application.Workflows.Queries.ListWorkflowInstances;
using SaaSApp.Workflow.Application.Workflows.Commands.SetWorkflowSla;
using SaaSApp.Workflow.Application.Workflows.Queries.GetSlaStatus;
using SaaSApp.Workflow.Application.Workflows.Queries.ListSlaBreaches;
using SaaSApp.Workflow.Application.Workflows.Queries.GetWorkflowCounts;
using SaaSApp.Workflow.Application.Workflows.Queries.GetWorkflowWiseInboxCounts;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Forms;
using SaaSApp.Workflow.Application.Workflows.Queries.GetLegacyMailboxInstanceCount;
using SaaSApp.Workflow.Application.Workflows.Queries.GetLegacyMailboxList;
using SaaSApp.Workflow.Application.Workflows.Commands.AddComment;
using SaaSApp.Workflow.Application.Workflows.Commands.AddAttachment;
using SaaSApp.Workflow.Application.Workflows.Commands.ApproveStep;
using SaaSApp.Workflow.Application.Workflows.Commands.RejectStep;
using SaaSApp.Workflow.Application.Workflows.Commands.MoveToNextStep;
using SaaSApp.Workflow.Application.Workflows.Commands.PerformAction;
using SaaSApp.Workflow.Application.Workflows.Queries.GetInstanceComments;
using SaaSApp.Workflow.Application.Workflows.Queries.GetInstanceAttachments;
using SaaSApp.Workflow.Domain.Enums;
using SaaSApp.Api.Helpers;
using SaaSApp.Repository.Application;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Workflow.Application;

namespace SaaSApp.Api.Controllers;

/// <summary>Workflow management for the current tenant. Requires JWT and X-Tenant-Id (or JWT tid).</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class WorkflowsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IWorkflowSchemaService _workflowSchemaService;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly ILegacyWorkflowTransactionService _legacyWorkflowTransaction;
    private readonly ITenantProvider _tenantProvider;
    private readonly IWorkflowApAgentMoveNextService _apAgentMoveNext;
    private readonly IApAgentPythonPipelineService _apAgentPythonPipeline;
    private readonly IWorkflowInstanceHistoryService _instanceHistory;
    private readonly IWorkflowEzfbFormDataLoader _ezfbFormDataLoader;
    private readonly IApAgentJobStatusService _apAgentJobStatus;
    private readonly IApAgentJobProgressService _apAgentJobProgress;
    private readonly IApAgentPythonJobClient _apAgentPythonJobClient;
    private readonly IWorkflowAttachmentArchiveService _attachmentArchive;
    private readonly IRepositoryItemShareService _itemShares;
    private readonly IShareGuestUserProvisioningService _guestProvisioning;
    private readonly IWorkflowInboxShareAssignmentService _inboxShareAssignment;
    private readonly IWorkflowTicketSearchService _ticketSearch;

    public WorkflowsController(
        IMediator mediator,
        IWorkflowSchemaService workflowSchemaService,
        ITenantConnectionProvider connectionProvider,
        ILegacyWorkflowTransactionService legacyWorkflowTransaction,
        ITenantProvider tenantProvider,
        IWorkflowApAgentMoveNextService apAgentMoveNext,
        IApAgentPythonPipelineService apAgentPythonPipeline,
        IWorkflowInstanceHistoryService instanceHistory,
        IWorkflowEzfbFormDataLoader ezfbFormDataLoader,
        IApAgentJobStatusService apAgentJobStatus,
        IApAgentJobProgressService apAgentJobProgress,
        IApAgentPythonJobClient apAgentPythonJobClient,
        IWorkflowAttachmentArchiveService attachmentArchive,
        IRepositoryItemShareService itemShares,
        IShareGuestUserProvisioningService guestProvisioning,
        IWorkflowInboxShareAssignmentService inboxShareAssignment,
        IWorkflowTicketSearchService ticketSearch)
    {
        _mediator = mediator;
        _workflowSchemaService = workflowSchemaService;
        _connectionProvider = connectionProvider;
        _legacyWorkflowTransaction = legacyWorkflowTransaction;
        _tenantProvider = tenantProvider;
        _apAgentMoveNext = apAgentMoveNext;
        _apAgentPythonPipeline = apAgentPythonPipeline;
        _instanceHistory = instanceHistory;
        _ezfbFormDataLoader = ezfbFormDataLoader;
        _apAgentJobStatus = apAgentJobStatus;
        _apAgentJobProgress = apAgentJobProgress;
        _apAgentPythonJobClient = apAgentPythonJobClient;
        _attachmentArchive = attachmentArchive;
        _itemShares = itemShares;
        _guestProvisioning = guestProvisioning;
        _inboxShareAssignment = inboxShareAssignment;
        _ticketSearch = ticketSearch;
    }

    /// <summary>Apply workflow schema to current tenant database. Call this if workflow.Workflows is missing. Requires X-Tenant-Id. In Development, no auth required.</summary>
    [HttpPost("setup-schema")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous] // Override: allow in Dev for schema setup (X-Tenant-Id required)
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetupSchema(CancellationToken cancellationToken)
    {
        var conn = _connectionProvider.ConnectionString;
        if (string.IsNullOrEmpty(conn))
            return BadRequest(new { error = "Tenant connection not resolved. Ensure X-Tenant-Id header is set." });

        await _workflowSchemaService.ApplySchemaAsync(conn, cancellationToken);
        return Ok(new { message = "Workflow schema applied successfully." });
    }

    /// <summary>Create a new workflow definition. Admin only. Supports both simple and full workflow creation (with WorkflowJson).</summary>
    [HttpPost]
    [Consumes("application/json")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(CreateWorkflowCommandResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return BadRequest(new { error = "Invalid JSON object." });

        var root = body;
        var raw = root.GetRawText();
        var workflowJsonRaw = WorkflowJsonBodyHelper.ExtractDesignerJsonRaw(root);

        CreateWorkflowCommand command;
        var emailFromBody = WorkflowEmailIngestOptionsHelper.FromJsonElement(root);

        // Accept: wrapper { workflowJson }, designer { Settings/Blocks } (PascalCase or camelCase), or simple { name }
        if (WorkflowJsonBodyHelper.HasWorkflowJsonWrapper(root))
        {
            var request = JsonSerializer.Deserialize<CreateWorkflowRequest>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (request == null)
                return BadRequest(new { error = "Invalid request payload." });

            var emailOpts = WorkflowEmailIngestOptionsHelper.Merge(
                emailFromBody,
                WorkflowEmailIngestOptionsHelper.FromRequest(
                    request.EmailConnectorId, request.EmailIsEnabled, request.EmailPollIntervalMinutes,
                    request.EmailQueryFilter, request.MasterSource, request.MasterFormId, request.MasterConnectorId));

            if (request.WorkflowJson != null)
            {
                var name = WorkflowJsonBodyHelper.ResolveWorkflowName(root, request.WorkflowJson);
                command = new CreateWorkflowCommand(
                    name,
                    request.Description ?? request.WorkflowJson.Settings?.General?.Description,
                    request.TriggerType,
                    request.TriggerConfig,
                    request.WorkflowJson,
                    request.PublishImmediately || WorkflowJsonBodyHelper.IsPublished(request.WorkflowJson),
                    workflowJsonRaw,
                    emailOpts);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                    return BadRequest(new { error = "Workflow name is required." });

                command = new CreateWorkflowCommand(
                    request.Name,
                    request.Description,
                    request.TriggerType,
                    request.TriggerConfig,
                    null,
                    false,
                    EmailIngest: emailOpts);
            }
        }
        else if (WorkflowJsonBodyHelper.IsDesignerPayload(root))
        {
            var workflowJson = WorkflowJsonBodyHelper.DeserializeDesignerJson(raw);
            if (workflowJson == null)
                return BadRequest(new { error = "Invalid workflowJson payload." });

            command = new CreateWorkflowCommand(
                WorkflowJsonBodyHelper.ResolveWorkflowName(root, workflowJson),
                workflowJson.Settings?.General?.Description,
                TriggerType.Manual,
                null,
                workflowJson,
                WorkflowJsonBodyHelper.IsPublished(workflowJson),
                workflowJsonRaw,
                emailFromBody);
        }
        else
        {
            var request = JsonSerializer.Deserialize<CreateWorkflowRequest>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (request == null)
                return BadRequest(new { error = "Invalid request payload." });

            var emailOpts = WorkflowEmailIngestOptionsHelper.Merge(
                emailFromBody,
                WorkflowEmailIngestOptionsHelper.FromRequest(
                    request.EmailConnectorId, request.EmailIsEnabled, request.EmailPollIntervalMinutes,
                    request.EmailQueryFilter, request.MasterSource, request.MasterFormId, request.MasterConnectorId));

            if (request.WorkflowJson != null)
            {
                command = new CreateWorkflowCommand(
                    WorkflowJsonBodyHelper.ResolveWorkflowName(root, request.WorkflowJson),
                    request.Description,
                    request.TriggerType,
                    request.TriggerConfig,
                    request.WorkflowJson,
                    request.PublishImmediately,
                    workflowJsonRaw,
                    emailOpts);
            }
            else if (string.IsNullOrWhiteSpace(request.Name))
                return BadRequest(new { error = "Workflow name is required. Send Settings.General.Name or name in the body." });
            else
            {
                command = new CreateWorkflowCommand(
                    request.Name,
                    request.Description,
                    request.TriggerType,
                    request.TriggerConfig,
                    null,
                    false,
                    EmailIngest: emailOpts);
            }
        }

        try
        {
            var result = await _mediator.Send(command, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = result.WorkflowId }, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }


    /// <summary>List all workflows in the current tenant (excluding soft-deleted).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ListWorkflowsQueryResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListWorkflowsQuery(), cancellationToken);
        return Ok(result);
    }

    /// <summary>Workflow transaction: optional forward when LegacyV5:WorkflowTransactionUrl is set; otherwise in-process update. Request body must include workflowId as a GUID string.</summary>
    [HttpPost("/api/workflow/transaction")]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<IActionResult> LegacyWorkflowTransaction([FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var (statusCode, contentType, responseBody) = await _legacyWorkflowTransaction.ExecuteAsync(body, Request, cancellationToken);
        return new ContentResult
        {
            StatusCode = statusCode,
            ContentType = string.IsNullOrEmpty(contentType) ? "application/json; charset=utf-8" : contentType,
            Content = responseBody
        };
    }

    /// <summary>Gets workflows for the current tenant with filter, sort, group, pagination, and user-based access security.</summary>
    [HttpPost("/api/workflow/all")]
    [ProducesResponseType(typeof(WorkflowAllResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> WorkflowAll([FromBody] WorkflowAllRequest request, CancellationToken cancellationToken)
    {
        var conn = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn))
            return BadRequest(new { error = "Tenant connection not resolved. Ensure X-Tenant-Id header is set." });

        var page = request.CurrentPage <= 0 ? 1 : request.CurrentPage;
        var pageSize = request.ItemsPerPage < 0 ? 0 : request.ItemsPerPage;
        var skip = (page - 1) * (pageSize == 0 ? int.MaxValue : pageSize);
        var mode = (request.Mode ?? "browse").Trim().ToLowerInvariant();
        var includeDeleted = mode != "browse";

        var sortColumn = MapSortColumn(request.SortBy?.Criteria);
        var sortOrder = string.Equals(request.SortBy?.Order, "ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

        var userId = GetCurrentUserId();
        var isAdmin = User.Claims.Any(c =>
            (c.Type == ClaimTypes.Role || c.Type == "role") &&
            (string.Equals(c.Value, "Admin", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(c.Value, "Administrator", StringComparison.OrdinalIgnoreCase)));

        var whereParts = new List<string>
        {
            includeDeleted ? "w.IsDeleted = 1" : "w.IsDeleted = 0"
        };
        var parameters = new List<SqlParameter>();

        if (request.HasSecurity && userId != null && !isAdmin)
        {
            whereParts.Add("""
                (
                    EXISTS (SELECT 1 FROM workflow.WorkflowSecurity s WHERE s.WorkflowId = w.Id AND s.UserId = @CurrentUserId AND s.IsDeleted = 0)
                    OR EXISTS (SELECT 1 FROM workflow.WorkflowUsers u WHERE u.WorkflowId = w.Id AND u.UserId = @CurrentUserId AND u.IsDeleted = 0)
                    OR (w.CreatedBy = @CurrentUserId)
                )
                """);
            whereParts.Add("NOT (w.Status = 0 AND w.CreatedBy <> @CurrentUserId)");
            parameters.Add(new SqlParameter("@CurrentUserId", userId.Value));
        }
        else if (request.HasReport && userId != null)
        {
            whereParts.Add("""
                (
                    EXISTS (SELECT 1 FROM workflow.WorkflowUsers u WHERE u.WorkflowId = w.Id AND u.UserId = @CurrentUserId AND u.IsDeleted = 0)
                    OR (w.CreatedBy = @CurrentUserId)
                )
                """);
            parameters.Add(new SqlParameter("@CurrentUserId", userId.Value));
        }

        foreach (var filter in (request.FilterBy ?? new List<WorkflowAllFilterGroup>()).SelectMany(g => g.Filters ?? new List<WorkflowAllFilter>()))
        {
            if (TryBuildFilterCondition(filter, out var condition, out var filterParam))
            {
                whereParts.Add(condition);
                if (filterParam != null)
                    parameters.Add(filterParam);
            }
        }

        var whereSql = string.Join(" AND ", whereParts);
        var countSql = $"SELECT COUNT(*) FROM workflow.Workflows w WHERE {whereSql};";
        var selectSql = $"""
            SELECT
                w.Id,
                w.Name,
                w.Description,
                w.Status,
                w.CreatedBy,
                w.ModifiedBy,
                w.CreatedAtUtc,
                w.ModifiedAtUtc,
                cb.Email AS CreatedByName,
                COALESCE(mb.Email, cb.Email) AS ModifiedByName
            FROM workflow.Workflows w
            LEFT JOIN users.Users cb ON cb.Id = w.CreatedBy AND cb.IsDeleted = 0
            LEFT JOIN users.Users mb ON mb.Id = w.ModifiedBy AND mb.IsDeleted = 0
            WHERE {whereSql}
            ORDER BY {sortColumn} {sortOrder}
            OFFSET @Skip ROWS
            FETCH NEXT @Take ROWS ONLY;
            """;

        await using var connection = new SqlConnection(conn);
        await connection.OpenAsync(cancellationToken);

        int totalItems;
        await using (var countCmd = new SqlCommand(countSql, connection))
        {
            countCmd.Parameters.AddRange(parameters.Select(p => new SqlParameter(p.ParameterName, p.Value)).ToArray());
            totalItems = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<WorkflowAllItem>();
        await using (var listCmd = new SqlCommand(selectSql, connection))
        {
            listCmd.Parameters.AddRange(parameters.ToArray());
            listCmd.Parameters.AddWithValue("@Skip", Math.Max(skip, 0));
            listCmd.Parameters.AddWithValue("@Take", pageSize == 0 ? Math.Max(totalItems, 1) : pageSize);
            await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new WorkflowAllItem(
                    Id: reader.GetGuid(0),
                    Name: reader.GetString(1),
                    Description: reader.IsDBNull(2) ? null : reader.GetString(2),
                    FlowStatus: MapStatus(reader.GetInt32(3)),
                    CreatedBy: reader.GetGuid(4).ToString(),
                    ModifiedBy: reader.IsDBNull(5) ? null : reader.GetGuid(5).ToString(),
                    CreatedAt: reader.GetDateTime(6),
                    ModifiedAt: reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                    CreatedByName: reader.IsDBNull(8) ? null : reader.GetString(8),
                    ModifiedByName: reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
        }

        var grouped = GroupItems(items, request.GroupBy);
        var response = new WorkflowAllResponse(
            grouped,
            new WorkflowAllMeta(page, pageSize, totalItems));
        return Ok(response);
    }

    /// <summary>Gets workflow list for current logged-in user, with per-workflow counts (inbox, sent/process, completed, running), optionally filtered by workflow id.</summary>
    [HttpGet("/api/workflow/listByUserId/{wId?}")]
    [ProducesResponseType(typeof(List<WorkflowByUserItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListByUserId(string? wId, CancellationToken cancellationToken)
    {
        var conn = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn))
            return BadRequest(new { error = "Tenant connection not resolved. Ensure X-Tenant-Id header is set." });

        var currentUserId = GetCurrentUserId();
        if (currentUserId == null)
            return Unauthorized(new { error = "User context is required." });

        await using var connection = new SqlConnection(conn);
        await connection.OpenAsync(cancellationToken);

        var whereSql = """
            w.IsDeleted = 0
            AND w.Status = 1
            """;
        var parameters = new List<SqlParameter>
        {
            new("@CurrentUserId", currentUserId.Value)
        };

        if (Guid.TryParse(wId, out var workflowId) && workflowId != Guid.Empty)
        {
            whereSql += " AND w.Id = @WorkflowId";
            parameters.Add(new SqlParameter("@WorkflowId", workflowId));
        }
        else
        {
            // Old API parity intent: public workflows + workflows explicitly assigned to user.
            // In new schema: "public" is approximated as no rows in WorkflowUsers/WorkflowSecurity.
            whereSql += """
                AND (
                    NOT EXISTS (SELECT 1 FROM workflow.WorkflowUsers wu WHERE wu.WorkflowId = w.Id AND wu.IsDeleted = 0)
                    AND NOT EXISTS (SELECT 1 FROM workflow.WorkflowSecurity ws WHERE ws.WorkflowId = w.Id AND ws.IsDeleted = 0)
                    OR EXISTS (SELECT 1 FROM workflow.WorkflowUsers wu WHERE wu.WorkflowId = w.Id AND wu.UserId = @CurrentUserId AND wu.IsDeleted = 0)
                    OR EXISTS (SELECT 1 FROM workflow.WorkflowSecurity ws WHERE ws.WorkflowId = w.Id AND ws.UserId = @CurrentUserId AND ws.IsDeleted = 0)
                    OR w.CreatedBy = @CurrentUserId
                )
                """;
        }

        var sql = $"""
            SELECT
                w.Id,
                w.Name,
                w.Description,
                w.CreatedAtUtc,
                w.ModifiedAtUtc,
                CONVERT(NVARCHAR(36), w.CreatedBy) AS CreatedBy,
                CONVERT(NVARCHAR(36), w.ModifiedBy) AS ModifiedBy,
                (SELECT COUNT(*) FROM workflow.WorkflowInstanceLookup l WHERE l.WorkflowId = w.Id AND l.AssignedToUserId = @CurrentUserId AND l.Status IN (0,1) AND l.IsArchived = 0) AS InboxCount,
                (SELECT COUNT(*) FROM workflow.WorkflowInstanceLookup l WHERE l.WorkflowId = w.Id AND l.StartedBy = @CurrentUserId AND l.IsArchived = 0) AS ProcessCount,
                (SELECT COUNT(*) FROM workflow.WorkflowInstanceLookup l WHERE l.WorkflowId = w.Id AND (l.StartedBy = @CurrentUserId OR l.AssignedToUserId = @CurrentUserId) AND l.Status = 3) AS CompletedCount,
                (SELECT COUNT(*) FROM workflow.WorkflowInstanceLookup l WHERE l.WorkflowId = w.Id AND l.AssignedToUserId = @CurrentUserId AND l.Status IN (0,1) AND l.IsArchived = 0 AND l.CurrentStepInstanceId IS NOT NULL) AS RunningCount,
                0 AS PaymentProcessCount
            FROM workflow.Workflows w
            WHERE {whereSql}
            ORDER BY w.Name ASC;
            """;

        var result = new List<WorkflowByUserItem>();
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddRange(parameters.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WorkflowByUserItem(
                Id: reader.GetGuid(0),
                Name: reader.GetString(1),
                Description: reader.IsDBNull(2) ? null : reader.GetString(2),
                CreatedAt: reader.GetDateTime(3),
                ModifiedAt: reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                CreatedBy: reader.IsDBNull(5) ? null : reader.GetString(5),
                ModifiedBy: reader.IsDBNull(6) ? null : reader.GetString(6),
                TokenUserId: currentUserId.Value.ToString(),
                InboxCount: reader.GetInt32(7),
                ProcessCount: reader.GetInt32(8),
                CompletedCount: reader.GetInt32(9),
                RunningCount: reader.GetInt32(10),
                PaymentProcessCount: reader.GetInt32(11)
            ));
        }

        return Ok(result);
    }

    /// <summary>
    /// Filter-field schema for form-filtered search (from wFormControl for the workflow's FormId).
    /// </summary>
    [HttpGet("{workflowId:guid}/filter-fields")]
    [ProducesResponseType(typeof(WorkflowTicketFilterSchemaDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetFilterFields(Guid workflowId, CancellationToken cancellationToken)
    {
        try
        {
            var schema = await _ticketSearch.GetFilterFieldsAsync(workflowId, cancellationToken);
            if (schema == null)
                return NotFound(new { error = "Workflow not found." });
            return Ok(schema);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Distinct non-empty values for a form control on this workflow's FormId (ezfb_{suffix}_items).
    /// </summary>
    [HttpGet("{workflowId:guid}/control-values/{controlName}")]
    [ProducesResponseType(typeof(FormControlDistinctValuesResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetDistinctControlValues(
        Guid workflowId,
        string controlName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(controlName))
            return BadRequest(new { error = "controlName is required." });

        try
        {
            var result = await _ticketSearch.GetDistinctControlValuesAsync(
                workflowId,
                controlName,
                cancellationToken);

            if (result == null)
                return NotFound(new { error = "Workflow not found." });

            return result.Status switch
            {
                FormControlDistinctValuesStatus.Found => Ok(result),
                FormControlDistinctValuesStatus.FormNotFound => BadRequest(new { error = "Workflow FormId is not configured." }),
                FormControlDistinctValuesStatus.ControlNotFound => NotFound(new { error = $"Form control '{controlName}' not found." }),
                FormControlDistinctValuesStatus.TableNotFound => NotFound(new { error = "Form entry table not found." }),
                FormControlDistinctValuesStatus.ColumnNotFound => BadRequest(new { error = $"Column for control '{controlName}' not found in entry table." }),
                _ => NotFound()
            };
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Search workflow tickets by form-field operator DSL (ezfb → processForm).
    /// Returns grouped legacy mailbox rows (data[].key / data[].value) for all matching instances.
    /// </summary>
    [HttpPost("{workflowId:guid}/filter/search")]
    [ProducesResponseType(typeof(WorkflowFilterSearchResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FilterSearch(
        Guid workflowId,
        [FromBody] WorkflowTicketSearchRequest? request,
        CancellationToken cancellationToken)
    {
        var body = request ?? new WorkflowTicketSearchRequest();
        var page = body.CurrentPage <= 0 ? 1 : body.CurrentPage;
        var pageSize = body.ItemsPerPage <= 0 ? 20 : body.ItemsPerPage;

        try
        {
            var outcome = await _ticketSearch.SearchAsync(workflowId, body, cancellationToken);

            return outcome.Status switch
            {
                WorkflowTicketSearchStatus.WorkflowNotFound => NotFound(new { error = "Workflow not found." }),
                WorkflowTicketSearchStatus.FormNotConfigured => BadRequest(new { error = "Workflow FormId is not configured." }),
                WorkflowTicketSearchStatus.TablesMissing => Ok(new WorkflowFilterSearchResult(
                    Array.Empty<WorkflowFilterSearchGroup>(),
                    new WorkflowFilterSearchMeta(page, pageSize, 0),
                    TableExists: false)),
                WorkflowTicketSearchStatus.Found when outcome.Result != null => Ok(outcome.Result),
                _ => Ok(new WorkflowFilterSearchResult(
                    Array.Empty<WorkflowFilterSearchGroup>(),
                    new WorkflowFilterSearchMeta(page, pageSize, 0),
                    TableExists: true))
            };
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Gets inbox transactions for a workflow, with filter/sort/group/pagination for the current user.</summary>
    [HttpPost("/api/workflow/inboxList/{id}")]
    [ProducesResponseType(typeof(WorkflowInboxResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> InboxList(string id, [FromBody] WorkflowInboxRequest request, CancellationToken cancellationToken)
    {
        var conn = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn))
            return BadRequest(new { error = "Tenant connection not resolved. Ensure X-Tenant-Id header is set." });
        if (!Guid.TryParse(id, out var workflowId))
            return BadRequest(new { error = "Workflow id must be a GUID." });

        var currentUserId = GetCurrentUserId();
        if (currentUserId == null)
            return Unauthorized(new { error = "User context is required." });

        var suffix = workflowId.ToString("N")[..8];
        var instancesTable = $"workflow.[WorkflowInstances_{suffix}]";
        var transactionTable = $"workflow.[transaction_{suffix}]";
        var workflowFormsTable = $"workflow.WorkflowForms_{suffix}";
        var workflowAttachmentsTable = $"workflow.WorkflowAttachments_{suffix}";
        var workflowCommentsTable = $"workflow.WorkflowComments_{suffix}";
        var processFormTable = $"workflow.processForm_{suffix}";

        await using var connection = new SqlConnection(conn);
        await connection.OpenAsync(cancellationToken);

        // Safety: return empty when workflow dynamic tables are not present yet.
        var tableCheckSql = """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = 'workflow' AND TABLE_NAME IN (@InstancesName, @TransactionName)
            """;
        await using (var check = new SqlCommand(tableCheckSql, connection))
        {
            check.Parameters.AddWithValue("@InstancesName", $"WorkflowInstances_{suffix}");
            check.Parameters.AddWithValue("@TransactionName", $"transaction_{suffix}");
            var found = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken));
            if (found < 2)
            {
                return Ok(new WorkflowInboxResponse(new List<WorkflowInboxGroup>(), new WorkflowAllMeta(1, request.ItemsPerPage, 0)));
            }
        }

        var page = request.CurrentPage <= 0 ? 1 : request.CurrentPage;
        var pageSize = request.ItemsPerPage <= 0 ? 20 : request.ItemsPerPage;
        var offset = (page - 1) * pageSize;
        var sortColumn = MapInboxSortColumn(request.SortBy?.Criteria);
        var sortOrder = string.Equals(request.SortBy?.Order, "ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

        var inboxTableExists = false;
        await using (var inboxCheck = new SqlCommand(
            """
            SELECT CASE WHEN OBJECT_ID(@InboxFull, N'U') IS NOT NULL
                         AND COL_LENGTH(@InboxFull, 'action') IS NOT NULL
                        THEN 1 ELSE 0 END
            """,
            connection))
        {
            inboxCheck.Parameters.AddWithValue("@InboxFull", $"workflow.Inbox_{suffix}");
            inboxTableExists = Convert.ToInt32(await inboxCheck.ExecuteScalarAsync(cancellationToken)) == 1;
        }

        var actionSelectSql = inboxTableExists
            ? $"""
                ISNULL((
                    SELECT TOP 1 ISNULL(ib.[action], 1)
                    FROM workflow.[Inbox_{suffix}] ib
                    WHERE TRY_CONVERT(UNIQUEIDENTIFIER, ib.workflowInstanceId) = t.WorkflowInstanceId
                      AND (
                            ib.userId = CONVERT(NVARCHAR(100), @CurrentUserId)
                         OR ib.transaction_modifiedBy = CONVERT(NVARCHAR(255), @CurrentUserId)
                      )
                    ORDER BY ib.id DESC
                ), 1)
                """
            : "1";

        var whereParts = new List<string>
        {
            "t.IsDeleted = 0",
            "wi.Status = @RunningStatus",
            "t.ActionStatus = 0",
            """
            (
                t.ActivityUserId = @CurrentUserId
                OR t.ModifiedBy = @CurrentUserId
                OR EXISTS (
                    SELECT 1 FROM workflow.groupUser gu
                    WHERE gu.GroupId = t.ActivityGroupId
                      AND gu.UserId = @CurrentUserId
                      AND gu.IsDeleted = 0
                )
            )
            """
        };
        var parameters = new List<SqlParameter>
        {
            new("@CurrentUserId", currentUserId.Value),
            new("@RunningStatus", (int)SaaSApp.Workflow.Domain.Enums.WorkflowInstanceStatus.Running)
        };

        foreach (var filter in (request.FilterBy ?? new List<WorkflowAllFilterGroup>()).SelectMany(g => g.Filters ?? new List<WorkflowAllFilter>()))
        {
            if (TryBuildInboxFilterCondition(filter, out var condition, out var p))
            {
                whereParts.Add(condition);
                if (p != null)
                    parameters.Add(p);
            }
        }

        var whereSql = string.Join(" AND ", whereParts);
        var countSql = $"""
            SELECT COUNT(*)
            FROM {transactionTable} t
            INNER JOIN {instancesTable} wi ON wi.Id = t.WorkflowInstanceId
            WHERE {whereSql};
            """;
        var dataSql = $"""
            SELECT
                t.Id AS TransactionId,
                t.WorkflowInstanceId,
                wi.ReferenceNumber,
                wi.Status AS InstanceStatus,
                wi.StartedAtUtc AS InstanceStartedAtUtc,
                wi.StartedBy AS RaisedByUserId,
                t.ActivityId,
                t.RuleId,
                t.StageType,
                t.StageName,
                t.Review,
                t.CreatedAt AS TransactionCreatedAt,
                t.CreatedBy AS TransactionCreatedBy,
                t.ModifiedAt AS TransactionModifiedAt,
                t.ActivityUserId,
                t.ActivityGroupId,
                {actionSelectSql} AS [Action]
            FROM {transactionTable} t
            INNER JOIN {instancesTable} wi ON wi.Id = t.WorkflowInstanceId
            WHERE {whereSql}
            ORDER BY {sortColumn} {sortOrder}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        int totalItems;
        await using (var countCmd = new SqlCommand(countSql, connection))
        {
            countCmd.Parameters.AddRange(parameters.Select(p => new SqlParameter(p.ParameterName, p.Value)).ToArray());
            totalItems = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<WorkflowInboxItem>();
        await using (var cmd = new SqlCommand(dataSql, connection))
        {
            cmd.Parameters.AddRange(parameters.ToArray());
            cmd.Parameters.AddWithValue("@Offset", offset);
            cmd.Parameters.AddWithValue("@PageSize", pageSize);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var instanceStatus = reader.GetInt32(3);
                items.Add(new WorkflowInboxItem(
                    TransactionId: reader.GetInt32(0),
                    WorkflowInstanceId: reader.GetGuid(1),
                    RequestNo: reader.IsDBNull(2) ? null : reader.GetString(2),
                    FlowStatus: instanceStatus == (int)SaaSApp.Workflow.Domain.Enums.WorkflowInstanceStatus.Completed ? 1 : 0,
                    RaisedAt: reader.GetDateTime(4),
                    RaisedByUserId: reader.IsDBNull(5) ? null : reader.GetGuid(5).ToString(),
                    ActivityId: reader.IsDBNull(6) ? null : reader.GetString(6),
                    RuleId: reader.IsDBNull(7) ? null : reader.GetString(7),
                    StageType: reader.IsDBNull(8) ? null : reader.GetString(8),
                    StageName: reader.IsDBNull(9) ? null : reader.GetString(9),
                    Review: reader.IsDBNull(10) ? null : reader.GetString(10),
                    TransactionCreatedAt: reader.GetDateTime(11),
                    TransactionCreatedBy: reader.IsDBNull(12) ? null : reader.GetGuid(12).ToString(),
                    TransactionModifiedAt: reader.IsDBNull(13) ? null : reader.GetDateTime(13),
                    ActivityUserId: reader.IsDBNull(14) ? null : reader.GetGuid(14).ToString(),
                    ActivityGroupId: reader.IsDBNull(15) ? null : reader.GetInt32(15),
                    FormData: null,
                    RepositoryData: null,
                    CommentsCount: 0,
                    Action: reader.IsDBNull(16) ? 1 : reader.GetInt32(16)));
            }
        }

        // Enrich each row with form/attachment/comments from per-workflow tables when available.
        for (var i = 0; i < items.Count; i++)
        {
            var row = items[i];
            var formData = await TryGetFormDataAsync(
                connection,
                workflowFormsTable,
                processFormTable,
                workflowAttachmentsTable,
                workflowId,
                row.WorkflowInstanceId,
                cancellationToken);
            var repoData = await TryGetRepositoryDataAsync(connection, workflowAttachmentsTable, row.WorkflowInstanceId, cancellationToken);
            var commentsCount = await TryGetCommentsCountAsync(connection, workflowCommentsTable, row.WorkflowInstanceId, cancellationToken);
            items[i] = row with { FormData = formData, RepositoryData = repoData, CommentsCount = commentsCount };
        }

        var grouped = GroupInboxItems(items, request.GroupBy);
        return Ok(new WorkflowInboxResponse(grouped, new WorkflowAllMeta(page, pageSize, totalItems)));
    }

    /// <summary>
    /// Share a workflow inbox file with an external user by email.
    /// Creates a guest TenantUser (password set on first login), assigns the open inbox task to them
    /// (owner remains visible on inbox via share CC), and returns a share link for read-only file access.
    /// When the guest moves next, their row goes to Sent and the next open step returns to the sharer inbox.
    /// </summary>
    [HttpPost("instances/{instanceId:guid}/share-file")]
    [ProducesResponseType(typeof(WorkflowInboxShareResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ShareInboxFile(
        Guid instanceId,
        [FromBody] CreateWorkflowInboxShareRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.GetTenantId()
            ?? throw new InvalidOperationException("Tenant context is required.");

        var userId = GetCurrentUserId();
        if (userId == null || userId == Guid.Empty)
            return Unauthorized(new { error = "User id is required." });

        try
        {
            var guestUserId = await _guestProvisioning.EnsureGuestUserAsync(
                tenantId, request.Email, cancellationToken);

            var share = await _itemShares.CreateWorkflowInboxShareAsync(
                tenantId,
                instanceId,
                request.RepositoryId,
                request.ItemId,
                userId.Value,
                request,
                cancellationToken);

            var inboxAssignment = await _inboxShareAssignment.AssignOpenInboxToUserAsync(
                instanceId,
                guestUserId,
                userId.Value,
                request.Action == 0 ? 0 : 1,
                cancellationToken);

            return Created(string.Empty, new WorkflowInboxShareResponse(share, inboxAssignment, guestUserId));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Get a workflow by ID with steps and designer flow JSON (<c>workflowJson</c>) from blob/file storage.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(GetWorkflowByIdQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetWorkflowByIdQuery(id), cancellationToken);
        if (result == null)
            return NotFound();
        return Ok(result);
    }

    /// <summary>Update a workflow definition. Admin only. Only provided fields are updated. When <c>workflowJson</c> is sent, full v5-style definition is saved (same side effects as create). Returns 406 if the new name is already used by another workflow.</summary>
    [HttpPut("{id:guid}")]
    [Consumes("application/json")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status406NotAcceptable)]
    public async Task<IActionResult> Update(Guid id, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        UpdateWorkflowCommand command;

        if (body.ValueKind != JsonValueKind.Object)
            return BadRequest(new { error = "Invalid JSON object." });

        var root = body;
        var raw = root.GetRawText();
        var workflowJsonRaw = WorkflowJsonBodyHelper.ExtractDesignerJsonRaw(root);
        var emailFromBody = WorkflowEmailIngestOptionsHelper.FromJsonElement(root);

        if (WorkflowJsonBodyHelper.HasWorkflowJsonWrapper(root))
        {
            var request = JsonSerializer.Deserialize<UpdateWorkflowRequest>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (request == null)
                return BadRequest(new { error = "Invalid request payload." });

            var emailOpts = WorkflowEmailIngestOptionsHelper.Merge(
                emailFromBody,
                WorkflowEmailIngestOptionsHelper.FromRequest(
                    request.EmailConnectorId, request.EmailIsEnabled, request.EmailPollIntervalMinutes,
                    request.EmailQueryFilter, request.MasterSource, request.MasterFormId, request.MasterConnectorId));

            command = new UpdateWorkflowCommand(
                id,
                request.Name,
                request.Description,
                request.TriggerType,
                request.TriggerConfig,
                request.WorkflowJson,
                request.PublishImmediately,
                workflowJsonRaw,
                emailOpts);
        }
        else if (WorkflowJsonBodyHelper.IsDesignerPayload(root))
        {
            var workflowJson = WorkflowJsonBodyHelper.DeserializeDesignerJson(raw);
            if (workflowJson == null)
                return BadRequest(new { error = "Invalid workflowJson payload." });

            command = new UpdateWorkflowCommand(
                id,
                WorkflowJson: workflowJson,
                PublishImmediately: WorkflowJsonBodyHelper.IsPublished(workflowJson),
                WorkflowJsonRaw: workflowJsonRaw,
                EmailIngest: emailFromBody);
        }
        else
        {
            // Partial update wrapper
            var request = JsonSerializer.Deserialize<UpdateWorkflowRequest>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (request == null)
                return BadRequest(new { error = "Invalid request payload." });

            var emailOpts = WorkflowEmailIngestOptionsHelper.Merge(
                emailFromBody,
                WorkflowEmailIngestOptionsHelper.FromRequest(
                    request.EmailConnectorId, request.EmailIsEnabled, request.EmailPollIntervalMinutes,
                    request.EmailQueryFilter, request.MasterSource, request.MasterFormId, request.MasterConnectorId));

            command = new UpdateWorkflowCommand(
                id,
                request.Name,
                request.Description,
                request.TriggerType,
                request.TriggerConfig,
                request.WorkflowJson,
                request.PublishImmediately,
                workflowJsonRaw,
                emailOpts);
        }

        try
        {
            var result = await _mediator.Send(command, cancellationToken);
            if (!result.Found)
                return NotFound();
            if (result.NameConflict)
                return StatusCode(StatusCodes.Status406NotAcceptable, new { error = "Workflow name already exists." });
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }


    /// <summary>Soft-delete a workflow. Admin only.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new DeleteWorkflowCommand(id), cancellationToken);
        if (!result.Found)
            return NotFound();
        return NoContent();
    }

    /// <summary>
    /// Re-sync workflow.WorkflowSteps from designer JSON in blob (ActivityId, StageType, assignees).
    /// Also refreshes ActivityId/StageType on running WorkflowStepInstances when step counts match.
    /// </summary>
    [HttpPost("{id:guid}/sync-steps")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(SyncWorkflowStepsFromJsonCommandResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SyncStepsFromBlob(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new SyncWorkflowStepsFromJsonCommand(id), cancellationToken);
        if (!result.Found)
            return NotFound();
        return Ok(result);
    }

    /// <summary>Add a step to a workflow definition. Admin only. Workflow must be in Draft.</summary>
    [HttpPost("{id:guid}/steps")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(AddWorkflowStepCommandResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddStep(Guid id, [FromBody] AddWorkflowStepRequest request, CancellationToken cancellationToken)
    {
        var command = new AddWorkflowStepCommand(id, request.Name, request.StepType, request.Order, request.Description, request.Config, request.IsRequired, request.AssignedToUserId, request.AssignedToRole, request.ApprovedNextStepId, request.RejectedNextStepId, request.ApprovalPolicy, request.ApproversJson, request.ActivityId);
        var result = await _mediator.Send(command, cancellationToken);
        if (!result.Found)
            return NotFound();
        return CreatedAtAction(nameof(GetById), new { id }, result);
    }

    /// <summary>Publish a workflow (make it active). Admin only.</summary>
    [HttpPost("{id:guid}/publish")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Publish(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new PublishWorkflowCommand(id), cancellationToken);
        if (!result.Found)
            return NotFound();
        return NoContent();
    }

    /// <summary>
    /// Start a workflow instance with optional file upload (multipart/form-data).
    /// Upload field name: <c>file</c>. Uses legacy flat repository upload. When a file is uploaded, enqueues AP Agent Python processing (not used by start/json).
    /// </summary>
    [HttpPost("{id:guid}/start")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(StartWorkflowCommandResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 104_857_600)]
    public async Task<IActionResult> StartWithFile(
        Guid id,
        IFormFile? file,
        [FromForm] string? context,
        [FromForm] string? envType,
        CancellationToken cancellationToken)
    {
        StartWorkflowAttachmentPayload? attachment = null;
        if (file is { Length: > 0 })
        {
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, cancellationToken);
            attachment = new StartWorkflowAttachmentPayload(
                ms.ToArray(),
                file.FileName,
                file.ContentType);
        }

        return await ExecuteStartAsync(
            id,
            new StartWorkflowCommand(
                id,
                context,
                envType,
                attachment,
                TriggerApAgentPythonJob: attachment is { Content.Length: > 0 }),
            cancellationToken);
    }

    /// <summary>
    /// Start a workflow instance (JSON). Optional base64 attachment in body. Does not enqueue AP Agent Python job.
    /// </summary>
    [HttpPost("{id:guid}/start/json")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(StartWorkflowCommandResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> StartWithJson(
        Guid id,
        [FromBody] StartWorkflowRequest? request,
        CancellationToken cancellationToken) =>
        await ExecuteStartAsync(
            id,
            new StartWorkflowCommand(id, request?.Context, request?.EnvType, request?.Attachment),
            cancellationToken);

    private async Task<IActionResult> ExecuteStartAsync(
        Guid workflowId,
        StartWorkflowCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken);
        return CreatedAtAction(nameof(GetInstance), new { workflowId, instanceId = result.InstanceId }, result);
    }

    /// <summary>List workflow instances for a workflow.</summary>
    [HttpGet("{id:guid}/instances")]
    [ProducesResponseType(typeof(ListWorkflowInstancesQueryResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListInstances(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListWorkflowInstancesQuery(id), cancellationToken);
        return Ok(result);
    }

    /// <summary>Get a workflow instance by ID with step instances.</summary>
    [HttpGet("{workflowId:guid}/instances/{instanceId:guid}")]
    [ProducesResponseType(typeof(GetWorkflowInstanceByIdQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstance(Guid workflowId, Guid instanceId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetWorkflowInstanceByIdQuery(instanceId), cancellationToken);
        if (result == null || result.WorkflowId != workflowId)
            return NotFound();
        return Ok(result);
    }

    /// <summary>
    /// Instance timeline: start → ap_agent → verified → approved → completed (who did it and when).
    /// Data source: <c>workflow.transaction_{suffix}</c> for the instance.
    /// </summary>
    [HttpGet("{workflowId:guid}/instances/{instanceId:guid}/history")]
    [ProducesResponseType(typeof(WorkflowInstanceHistoryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceHistory(
        Guid workflowId,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _instanceHistory.GetHistoryAsync(workflowId, instanceId, cancellationToken);
            if (result == null)
                return NotFound();

            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Set SLA policy for a workflow. Admin only.</summary>
    [HttpPost("{id:guid}/sla")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(SetWorkflowSlaCommandResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetSla(Guid id, [FromBody] SetWorkflowSlaRequest request, CancellationToken cancellationToken)
    {
        var command = new SetWorkflowSlaCommand(id, request.Priority, request.ResponseTimeMinutes, request.ResolutionTimeMinutes, request.EscalationTimeMinutes, request.EscalateToUserId, request.EscalateToRole, request.SendNotificationOnBreach, request.NotificationEmails);
        var result = await _mediator.Send(command, cancellationToken);
        return Ok(result);
    }

    /// <summary>Get SLA status for a workflow instance.</summary>
    [HttpGet("instances/{instanceId:guid}/sla")]
    [ProducesResponseType(typeof(GetSlaStatusQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSlaStatus(Guid instanceId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetSlaStatusQuery(instanceId), cancellationToken);
        if (result == null)
            return NotFound();
        return Ok(result);
    }

    /// <summary>List workflow instances with SLA breaches or at-risk status.</summary>
    [HttpGet("sla/breaches")]
    [ProducesResponseType(typeof(ListSlaBreachesQueryResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSlaBreaches(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListSlaBreachesQuery(), cancellationToken);
        return Ok(result);
    }

    /// <summary>Get workflow counts (Inbox, Sent, Completed) for current user.</summary>
    [HttpGet("counts")]
    [ProducesResponseType(typeof(GetWorkflowCountsQueryResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCounts(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetWorkflowCountsQuery(), cancellationToken);
        return Ok(result);
    }

    /// <summary>Get inbox count grouped by workflow for current user.</summary>
    [HttpGet("inbox/counts-by-workflow")]
    [ProducesResponseType(typeof(GetWorkflowWiseInboxCountsQueryResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWorkflowWiseInboxCounts(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetWorkflowWiseInboxCountsQuery(), cancellationToken);
        return Ok(result);
    }

    /// <summary>Inbox, Sent, and Completed counts for a workflow (current user from token).</summary>
    [HttpGet("instance-count")]
    [ProducesResponseType(typeof(LegacyMailboxInstanceCountResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetInstanceCount(
        [FromQuery] Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        if (workflowId == Guid.Empty)
            return BadRequest(new { error = "workflowId is required." });

        var result = await _mediator.Send(new GetLegacyMailboxInstanceCountQuery(workflowId), cancellationToken);
        return Ok(result);
    }

    /// <summary>List open inbox tasks assigned to the current user (hidden after approve/sent or workflow complete). Requires workflowId.</summary>
    [HttpGet("inbox")]
    [ProducesResponseType(typeof(LegacyMailboxListResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> GetMyInbox(
        [FromQuery] Guid workflowId,
        [FromQuery] Guid? instanceId = null,
        [FromQuery] string? transactionId = null,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool skipTotal = false,
        CancellationToken cancellationToken = default) =>
        GetLegacyMailboxList(LegacyMailboxTableKind.Inbox, workflowId, instanceId, transactionId, pageNumber, pageSize, skipTotal, cancellationToken);

    /// <summary>List sent items for the current user (excludes completed workflows). Requires workflowId.</summary>
    [HttpGet("sent")]
    [ProducesResponseType(typeof(LegacyMailboxListResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> GetMySent(
        [FromQuery] Guid workflowId,
        [FromQuery] Guid? instanceId = null,
        [FromQuery] string? transactionId = null,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool skipTotal = false,
        CancellationToken cancellationToken = default) =>
        GetLegacyMailboxList(LegacyMailboxTableKind.Sent, workflowId, instanceId, transactionId, pageNumber, pageSize, skipTotal, cancellationToken);

    /// <summary>List completed rows (latest per instance only). Requires workflowId; optional instanceId and transactionId.</summary>
    [HttpGet("completed")]
    [ProducesResponseType(typeof(LegacyMailboxListResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> GetMyCompleted(
        [FromQuery] Guid workflowId,
        [FromQuery] Guid? instanceId = null,
        [FromQuery] string? transactionId = null,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool skipTotal = false,
        CancellationToken cancellationToken = default) =>
        GetLegacyMailboxList(LegacyMailboxTableKind.Completed, workflowId, instanceId, transactionId, pageNumber, pageSize, skipTotal, cancellationToken);

    private async Task<IActionResult> GetLegacyMailboxList(
        LegacyMailboxTableKind kind,
        Guid workflowId,
        Guid? instanceId,
        string? transactionId,
        int pageNumber,
        int pageSize,
        bool skipTotal,
        CancellationToken cancellationToken)
    {
        if (workflowId == Guid.Empty)
            return BadRequest(new { error = "workflowId is required." });

        var result = await _mediator.Send(
            new GetLegacyMailboxListQuery(kind, workflowId, instanceId, transactionId, pageNumber, pageSize, skipTotal),
            cancellationToken);
        return Ok(result);
    }

    /// <summary>Add a comment to a workflow instance (stored in workflow-specific table).</summary>
    [HttpPost("{workflowId:guid}/instances/{instanceId:guid}/comments")]
    [ProducesResponseType(typeof(AddCommentCommandResult), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(AddCommentCommandResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddComment(Guid workflowId, Guid instanceId, [FromBody] AddCommentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var command = new AddCommentCommand(workflowId, instanceId, request.Comments, request.StepInstanceId, request.ExternalCommentsBy, request.ShowTo);
            var result = await _mediator.Send(command, cancellationToken);
            if (result.Skipped)
                return Ok(result);
            return CreatedAtAction(nameof(AddComment), new { workflowId, instanceId }, result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("does not belong", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    private IEnumerable<KeyValuePair<string, string?>> EnumerateAttachmentFormFields()
    {
        foreach (var key in Request.Form.Keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            yield return new KeyValuePair<string, string?>(key, Request.Form[key].ToString());
        }
    }

    /// <summary>
    /// Upload and archive a workflow attachment: blob + repository item + processAddon + WorkflowAttachments.
    /// Send multipart: file, repositoryId, metadata (JSON fields), optional transactionId.
    /// </summary>
    [HttpPost("{workflowId:guid}/instances/{instanceId:guid}/attachments")]
    [Consumes("multipart/form-data")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 104_857_600)]
    [ProducesResponseType(typeof(WorkflowAttachmentArchiveResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadAttachmentArchive(
        Guid workflowId,
        Guid instanceId,
        IFormFile? file,
        [FromForm] Guid repositoryId,
        [FromForm] int? transactionId,
        [FromForm] string? metadata,
        CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "file is required." });

        if (repositoryId == Guid.Empty)
            return BadRequest(new { error = "repositoryId is required." });

        var tenantId = _tenantProvider.GetTenantId();
        var userId = GetCurrentUserId();
        if (tenantId is null || userId is null)
            return BadRequest(new { error = "Tenant and authenticated user are required." });

        try
        {
            var mergedMetadata = RepositoryFormMetadataCollector.ToMetadataJson(
                RepositoryFormMetadataCollector.Collect(metadata, EnumerateAttachmentFormFields()));

            await using var stream = file.OpenReadStream();
            var result = await _attachmentArchive.UploadAsync(
                tenantId.Value,
                workflowId,
                instanceId,
                repositoryId,
                stream,
                file.FileName,
                file.ContentType,
                file.Length,
                mergedMetadata,
                transactionId,
                userId.Value,
                cancellationToken);

            return CreatedAtAction(nameof(GetAttachments), new { workflowId, instanceId }, result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("does not belong", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Link an already-uploaded file path to a workflow instance (no blob upload).</summary>
    [HttpPost("{workflowId:guid}/instances/{instanceId:guid}/attachments/link")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(AddAttachmentCommandResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddAttachmentLink(Guid workflowId, Guid instanceId, [FromBody] AddAttachmentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var command = new AddAttachmentCommand(workflowId, instanceId, request.FileName, request.FilePath, request.FileSize, request.ContentType);
            var result = await _mediator.Send(command, cancellationToken);
            return CreatedAtAction(nameof(AddAttachmentLink), new { workflowId, instanceId }, result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("does not belong", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Get comments for a workflow instance from workflow-specific table.</summary>
    [HttpGet("{workflowId:guid}/instances/{instanceId:guid}/comments")]
    [ProducesResponseType(typeof(GetInstanceCommentsQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetComments(Guid workflowId, Guid instanceId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mediator.Send(new GetInstanceCommentsQuery(workflowId, instanceId), cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("does not belong", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Get attachments for a workflow instance from workflow-specific table.</summary>
    [HttpGet("{workflowId:guid}/instances/{instanceId:guid}/attachments")]
    [ProducesResponseType(typeof(GetInstanceAttachmentsQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAttachments(Guid workflowId, Guid instanceId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mediator.Send(new GetInstanceAttachmentsQuery(workflowId, instanceId), cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("does not belong", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Approve a workflow step and optionally move to next stage.</summary>
    [HttpPost("instances/{instanceId:guid}/steps/{stepInstanceId:guid}/approve")]
    [ProducesResponseType(typeof(ApproveStepCommandResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApproveStep(Guid instanceId, Guid stepInstanceId, [FromBody] ApproveStepRequest request, CancellationToken cancellationToken)
    {
        var command = new ApproveStepCommand(instanceId, stepInstanceId, request.Comments, request.MoveToNextStep);
        var result = await _mediator.Send(command, cancellationToken);
        return Ok(result);
    }

    /// <summary>Reject a workflow step and optionally cancel or reassign.</summary>
    [HttpPost("instances/{instanceId:guid}/steps/{stepInstanceId:guid}/reject")]
    [ProducesResponseType(typeof(RejectStepCommandResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RejectStep(Guid instanceId, Guid stepInstanceId, [FromBody] RejectStepRequest request, CancellationToken cancellationToken)
    {
        var command = new RejectStepCommand(instanceId, stepInstanceId, request.Reason, request.CancelWorkflow, request.ReassignToUserId);
        var result = await _mediator.Send(command, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// AP agent metadata: invoice header fields to repository item + ezfb form entry (wFormControl name/jsonId),
    /// plus optional line items JSON on the matching ezfb column.
    /// </summary>
    [HttpPatch("{workflowId:guid}/instances/{instanceId:guid}/ap-agent/metadata")]
    [ProducesResponseType(typeof(ApAgentMetadataApplyResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApplyApAgentMetadata(
        Guid workflowId,
        Guid instanceId,
        [FromBody] JsonElement body,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tenantId = _tenantProvider.GetTenantId()
                ?? throw new InvalidOperationException("Tenant context is required (X-Tenant-Id).");

            var parsed = ParseApAgentMetadataBody(body);
            var request = new ApAgentMetadataApplyRequest(
                workflowId,
                instanceId,
                parsed.FormId,
                parsed.FormEntryId,
                parsed.RepositoryId,
                parsed.ItemId,
                parsed.Fields,
                parsed.LineItemsJson);

            var result = await _apAgentMoveNext.ApplyMetadataAsync(
                tenantId, request, GetWorkflowUserId(), cancellationToken);

            var formDataJson = await _ezfbFormDataLoader.LoadFormDataJsonAsync(
                parsed.FormId,
                parsed.FormEntryId,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(formDataJson))
            {
                try
                {
                    await _apAgentJobProgress.UpdateFormDataByInstanceAsync(
                        workflowId,
                        instanceId,
                        formDataJson,
                        cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // No AP Agent job row for this instance; metadata apply still succeeds.
                }
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (SqlException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "Database error while applying AP agent metadata.",
                detail = ex.Message,
                number = ex.Number
            });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "Failed to apply AP agent metadata.",
                detail = ex.Message
            });
        }
    }

    /// <summary>Get AP Agent Hangfire job status (queued / processing / completed / failed) plus live stage message from Python.</summary>
    [HttpGet("ap-agent/jobs")]
    [ProducesResponseType(typeof(ApAgentJobStatusListResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetApAgentJobStatuses(
        [FromQuery] string jobIds,
        CancellationToken cancellationToken)
    {
        var ids = ParseApAgentJobIds(jobIds);
        if (ids.Count == 0)
            return BadRequest(new { error = "jobIds is required (comma-separated Hangfire job ids, e.g. id1,id2,id3)." });

        var result = await _apAgentJobStatus.GetStatusesAsync(ids, cancellationToken);
        return Ok(result);
    }

    /// <summary>Get one or more AP Agent job statuses. Pass a single id, or comma-separated ids (e.g. id1,id2,id3).</summary>
    [HttpGet("ap-agent/jobs/{jobId}")]
    [ProducesResponseType(typeof(ApAgentJobStatusResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApAgentJobStatusListResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetApAgentJobStatus(string jobId, CancellationToken cancellationToken)
    {
        if (jobId.Contains(',', StringComparison.Ordinal))
        {
            var ids = ParseApAgentJobIds(jobId);
            if (ids.Count == 0)
                return BadRequest(new { error = "At least one job id is required." });

            var result = await _apAgentJobStatus.GetStatusesAsync(ids, cancellationToken);
            return Ok(result);
        }

        var status = await _apAgentJobStatus.GetStatusAsync(jobId, cancellationToken);
        if (status == null)
            return NotFound(new { error = $"AP Agent job '{jobId}' not found." });

        return Ok(status);
    }

    private static List<string> ParseApAgentJobIds(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>Python AP Agent progress callback by Hangfire job id (OCR running, extracting invoice, etc.).</summary>
    [HttpPatch("ap-agent/jobs/{jobId}/progress")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateApAgentJobProgress(
        string jobId,
        [FromBody] ApAgentProgressRequest request,
        CancellationToken cancellationToken)
    {
        var row = await _apAgentJobProgress.GetByJobIdAsync(jobId, cancellationToken);
        if (row == null)
            return NotFound(new { error = $"AP Agent job '{jobId}' not found." });

        await _apAgentJobProgress.UpdateProgressAsync(
            jobId,
            new ApAgentJobProgressUpdate(request.Stage, request.Message, request.Percent, request.FormData),
            cancellationToken);
        return NoContent();
    }

    /// <summary>Python AP Agent progress callback by workflow instance (updates latest active job).</summary>
    [HttpPatch("{workflowId:guid}/instances/{instanceId:guid}/ap-agent/progress")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateApAgentInstanceProgress(
        Guid workflowId,
        Guid instanceId,
        [FromBody] ApAgentProgressRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _apAgentJobProgress.UpdateProgressByInstanceAsync(
                workflowId,
                instanceId,
                new ApAgentJobProgressUpdate(request.Stage, request.Message, request.Percent, request.FormData),
                cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>POST start payload to Python AP Agent only (move-next is inside Python). Use ?background=true to enqueue Hangfire job.</summary>
    [HttpPost("{workflowId:guid}/instances/{instanceId:guid}/ap-agent/run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RunApAgentPython(
        Guid workflowId,
        Guid instanceId,
        [FromBody] JsonElement? body,
        [FromQuery] bool background = false,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantProvider.GetTenantId();
        if (tenantId == null)
            return BadRequest(new { error = "Tenant context is required (X-Tenant-Id or JWT tid)." });

        var userId = GetWorkflowUserId();
        if (userId == null)
            return Unauthorized(new { error = "User context is required." });

        var conn = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn))
            return BadRequest(new { error = "Tenant connection not resolved." });

        string payloadJson;
        if (body is { ValueKind: JsonValueKind.Object })
        {
            payloadJson = ApAgentStartPayloadJson.UnwrapInner(body.Value.GetRawText());
        }
        else
        {
            var loaded = await LoadStartPayloadFromWorkflowFormsAsync(
                conn, workflowId, instanceId, cancellationToken);
            if (loaded == null)
                return NotFound(new { error = "No start payload found. Pass body JSON or ensure WorkflowForms row exists." });
            payloadJson = ApAgentStartPayloadJson.UnwrapInner(loaded);
        }

        var jobArgs = new ApAgentPythonJobArgs(
            tenantId.Value,
            userId.Value,
            workflowId,
            instanceId,
            payloadJson);

        if (background)
        {
            var jobId = await _apAgentPythonJobClient.EnqueueAsync(jobArgs, cancellationToken);
            return Accepted(new
            {
                message = "AP Agent job enqueued.",
                apAgentJobId = jobId,
                workflowId,
                instanceId,
                statusUrl = $"/api/workflows/ap-agent/jobs/{jobId}"
            });
        }

        try
        {
            await _apAgentPythonPipeline.ExecuteAsync(jobArgs, hangfireJobId: null, cancellationToken);

            return Ok(new
            {
                message = "AP Agent Python call completed.",
                workflowId,
                instanceId,
                startPayload = JsonSerializer.Deserialize<JsonElement>(payloadJson)
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<string?> LoadStartPayloadFromWorkflowFormsAsync(
        string connectionString,
        Guid workflowId,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var suffix = workflowId.ToString("N")[..8];
        var table = $"workflow.WorkflowForms_{suffix}";
        var sql = $"""
            SELECT TOP 1 FormData
            FROM {table}
            WHERE WorkflowInstanceId = @InstanceId AND IsDeleted = 0
            ORDER BY CreatedAtUtc DESC
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@InstanceId", instanceId);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        if (value == null || value == DBNull.Value)
            return null;

        var text = Convert.ToString(value)?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Move workflow instance to the next step/stage.</summary>
    [HttpPost("instances/{instanceId:guid}/move-next")]
    [ProducesResponseType(typeof(MoveToNextStepCommandResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MoveToNextStep(Guid instanceId, [FromBody] MoveToNextStepRequest request, CancellationToken cancellationToken)
    {
        var apAgent = request.ToApAgentPayload(instanceId);
        var (formId, formEntryId) = request.ResolveFormIdentity();
        var parsedFormData = MoveToNextStepFormDataParser.Parse(request.FormData);
        var submittedFormDataJson = request.FormData.HasValue
            ? request.FormData.Value.ValueKind == JsonValueKind.String
                ? request.FormData.Value.GetString()
                : request.FormData.Value.GetRawText()
            : null;
        var command = new MoveToNextStepCommand(
            instanceId,
            request.ActivityId,
            request.Review,
            request.Comments,
            request.ActivityUserId,
            apAgent,
            formId,
            formEntryId,
            parsedFormData.Fields,
            parsedFormData.LineItemsJson,
            submittedFormDataJson);
        var result = await _mediator.Send(command, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Bulk move-next: pass comma-separated instance ids, activityId and review. No formData — moves tickets only.
    /// </summary>
    [HttpPost("instances/bulk-move-next")]
    [ProducesResponseType(typeof(BulkMoveToNextStepCommandResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BulkMoveToNextStep(
        [FromBody] BulkMoveToNextStepRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var instanceIds = BulkMoveInstanceIdParser.Parse(request.InstanceId, request.InstanceIds);
            var command = new BulkMoveToNextStepCommand(
                instanceIds,
                request.ActivityId,
                request.Review,
                request.Comments,
                request.ActivityUserId);
            var result = await _mediator.Send(command, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Perform a custom action on workflow (Approve, Reject, Hold, Resume, Cancel, etc.).</summary>
    [HttpPost("instances/{instanceId:guid}/actions")]
    [ProducesResponseType(typeof(PerformActionCommandResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PerformAction(Guid instanceId, [FromBody] PerformActionRequest request, CancellationToken cancellationToken)
    {
        var command = new PerformActionCommand(instanceId, request.Action, request.Comments, request.AssignToUserId, request.MoveToNextStep);
        var result = await _mediator.Send(command, cancellationToken);
        return Ok(result);
    }

    private static (string FormId, int FormEntryId, Guid RepositoryId, Guid ItemId, IReadOnlyDictionary<string, string> Fields, string? LineItemsJson)
        ParseApAgentMetadataBody(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Request body must be a JSON object.");

        var formId = GetRequiredString(body, "formId");
        var formEntryId = GetRequiredInt(body, "formEntryId");
        var repositoryId = GetRequiredGuid(body, "repositoryId");
        var itemId = GetRequiredGuid(body, "itemId");

        IReadOnlyDictionary<string, string> fields;
        string? lineItemsJson;
        if (body.TryGetProperty("fields", out var nestedFields) && nestedFields.ValueKind == JsonValueKind.Object)
        {
            (fields, lineItemsJson) = ApAgentMetadataParser.ParseFieldsPayload(nestedFields);
        }
        else
        {
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "workflowId", "instanceId", "formId", "formEntryId", "repositoryId", "itemId", "fields"
            };
            foreach (var lineKey in ApAgentMetadataParser.LineItemKeys)
                reserved.Add(lineKey);
            fields = RepositoryMetadataParser.Parse(body.GetRawText())
                .Where(kv => !reserved.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            lineItemsJson = null;
        }

        lineItemsJson ??= ApAgentMetadataParser.TryGetRootLineItemsJson(body);

        if (fields.Count == 0 && string.IsNullOrWhiteSpace(lineItemsJson))
            throw new ArgumentException("fields.invoice_header or fields with header values, or line items, is required.");

        return (formId, formEntryId, repositoryId, itemId, fields, lineItemsJson);
    }

    private static string GetRequiredString(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} is required.");
        var value = prop.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.");
        return value.Trim();
    }

    private static int GetRequiredInt(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var prop))
            throw new ArgumentException($"{name} is required.");
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n) && n > 0)
            return n;
        if (prop.ValueKind == JsonValueKind.String
            && int.TryParse(prop.GetString(), out var parsed)
            && parsed > 0)
            return parsed;
        throw new ArgumentException($"{name} must be a positive integer.");
    }

    private static Guid GetRequiredGuid(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var prop))
            throw new ArgumentException($"{name} is required.");
        if (prop.ValueKind == JsonValueKind.String && Guid.TryParse(prop.GetString(), out var g) && g != Guid.Empty)
            return g;
        throw new ArgumentException($"{name} must be a valid GUID.");
    }

    private Guid? GetWorkflowUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? User.FindFirstValue("oid");
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static string MapSortColumn(string? criteria)
    {
        var c = (criteria ?? "modifiedAt").Trim().ToLowerInvariant();
        return c switch
        {
            "name" => "w.Name",
            "description" => "w.Description",
            "createdby" => "w.CreatedBy",
            "createdat" => "w.CreatedAtUtc",
            "modifiedby" => "w.ModifiedBy",
            "modifiedat" => "w.ModifiedAtUtc",
            "flowstatus" => "w.Status",
            _ => "ISNULL(w.ModifiedAtUtc, w.CreatedAtUtc)"
        };
    }

    private static string MapStatus(int status) => status switch
    {
        0 => "DRAFT",
        1 => "PUBLISHED",
        2 => "ARCHIVED",
        _ => status.ToString()
    };

    private static List<WorkflowAllGroup> GroupItems(List<WorkflowAllItem> items, string? groupBy)
    {
        var g = (groupBy ?? string.Empty).Trim().ToLowerInvariant();
        var groups = g switch
        {
            "name" => items.GroupBy(x => x.Name ?? string.Empty),
            "description" => items.GroupBy(x => x.Description ?? string.Empty),
            "flowstatus" => items.GroupBy(x => x.FlowStatus ?? string.Empty),
            "createdby" => items.GroupBy(x => x.CreatedBy ?? string.Empty),
            "modifiedby" => items.GroupBy(x => x.ModifiedBy ?? string.Empty),
            _ => items.GroupBy(_ => string.Empty)
        };
        return groups.Select(x => new WorkflowAllGroup(x.Key, x.ToList())).ToList();
    }

    private static bool TryBuildFilterCondition(WorkflowAllFilter filter, out string conditionSql, out SqlParameter? parameter)
    {
        conditionSql = string.Empty;
        parameter = null;
        if (filter == null || string.IsNullOrWhiteSpace(filter.Criteria) || string.IsNullOrWhiteSpace(filter.Condition))
            return false;

        var col = filter.Criteria.Trim().ToLowerInvariant() switch
        {
            "name" => "w.Name",
            "description" => "w.Description",
            "flowstatus" => "w.Status",
            "createdby" => "CONVERT(NVARCHAR(36), w.CreatedBy)",
            "modifiedby" => "CONVERT(NVARCHAR(36), w.ModifiedBy)",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(col))
            return false;

        var paramName = $"@f_{Math.Abs((filter.Criteria + filter.Condition + filter.Value).GetHashCode())}";
        var cond = filter.Condition.Trim().ToLowerInvariant();
        if (cond is "contains" or "like")
        {
            conditionSql = $"{col} LIKE {paramName}";
            parameter = new SqlParameter(paramName, $"%{filter.Value ?? string.Empty}%");
            return true;
        }
        if (cond is "eq" or "=" or "equal")
        {
            conditionSql = $"{col} = {paramName}";
            parameter = new SqlParameter(paramName, filter.Value ?? string.Empty);
            return true;
        }
        if (cond is "neq" or "!=" or "notequal")
        {
            conditionSql = $"{col} <> {paramName}";
            parameter = new SqlParameter(paramName, filter.Value ?? string.Empty);
            return true;
        }
        return false;
    }

    private static string MapInboxSortColumn(string? criteria)
    {
        var c = (criteria ?? "transaction_createdAt").Trim().ToLowerInvariant();
        return c switch
        {
            "requestno" => "wi.ReferenceNumber",
            "stagename" => "t.StageName",
            "raisedat" => "wi.StartedAtUtc",
            "transaction_createdat" => "t.CreatedAt",
            _ => "t.CreatedAt"
        };
    }

    private static bool TryBuildInboxFilterCondition(WorkflowAllFilter filter, out string conditionSql, out SqlParameter? parameter)
    {
        conditionSql = string.Empty;
        parameter = null;
        if (filter == null || string.IsNullOrWhiteSpace(filter.Criteria) || string.IsNullOrWhiteSpace(filter.Condition))
            return false;

        var column = filter.Criteria.Trim().ToLowerInvariant() switch
        {
            "requestno" => "wi.ReferenceNumber",
            "stagename" => "t.StageName",
            "activityid" => "t.ActivityId",
            "review" => "t.Review",
            "processid" => "CONVERT(NVARCHAR(36), t.WorkflowInstanceId)",
            "workflowinstanceid" => "CONVERT(NVARCHAR(36), t.WorkflowInstanceId)",
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(column))
            return false;

        var paramName = $"@if_{Math.Abs((filter.Criteria + filter.Condition + filter.Value).GetHashCode())}";
        var cond = filter.Condition.Trim().ToLowerInvariant();
        if (cond is "contains" or "like")
        {
            conditionSql = $"{column} LIKE {paramName}";
            parameter = new SqlParameter(paramName, $"%{filter.Value ?? string.Empty}%");
            return true;
        }
        if (cond is "eq" or "=" or "equal")
        {
            conditionSql = $"{column} = {paramName}";
            parameter = new SqlParameter(paramName, filter.Value ?? string.Empty);
            return true;
        }
        if (cond is "neq" or "!=" or "notequal")
        {
            conditionSql = $"{column} <> {paramName}";
            parameter = new SqlParameter(paramName, filter.Value ?? string.Empty);
            return true;
        }
        return false;
    }

    private async Task<WorkflowInboxFormData?> TryGetFormDataAsync(
        SqlConnection connection,
        string workflowFormsTable,
        string processFormTable,
        string workflowAttachmentsTable,
        Guid workflowId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        int? wFormId = null;
        int? formEntryId = null;
        string? storedFormData = null;

        var formsSql = $"SELECT TOP 1 WFormId, FormEntryId, FormData FROM {workflowFormsTable} WHERE WorkflowInstanceId = @WorkflowInstanceId AND IsDeleted = 0 ORDER BY CreatedAtUtc DESC;";
        try
        {
            await using var cmd = new SqlCommand(formsSql, connection);
            cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                wFormId = reader.GetInt32(0);
                formEntryId = reader.GetInt32(1);
                storedFormData = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }
        catch (SqlException)
        {
            // Table may not exist in older tenant DBs.
        }

        string? formGuid = null;

        var attachmentSql = $@"
SELECT TOP 1 FormJsonId
FROM {workflowAttachmentsTable}
WHERE WorkflowInstanceId = @WorkflowInstanceId AND IsDeleted = 0
ORDER BY ISNULL(ModifiedAtUtc, CreatedAtUtc) DESC, CreatedAtUtc DESC;";
        try
        {
            await using var cmd = new SqlCommand(attachmentSql, connection);
            cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            var value = await cmd.ExecuteScalarAsync(cancellationToken);
            formGuid = value == null || value == DBNull.Value ? null : Convert.ToString(value)?.Trim();
        }
        catch (SqlException)
        {
        }

        if (string.IsNullOrWhiteSpace(formGuid))
        {
            var processSql = $@"
SELECT TOP 1 WFormId, FormEntryId
FROM {processFormTable}
WHERE WorkflowInstanceId = @WorkflowInstanceId AND IsDeleted = 0
ORDER BY Id DESC;";
            try
            {
                await using var cmd = new SqlCommand(processSql, connection);
                cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    formGuid = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0))?.Trim();
                    if (!formEntryId.HasValue && !reader.IsDBNull(1))
                        formEntryId = reader.GetInt32(1);
                }
            }
            catch (SqlException)
            {
            }
        }

        if (string.IsNullOrWhiteSpace(formGuid))
        {
            const string wfSql = "SELECT FormId FROM workflow.Workflows WHERE Id = @WorkflowId AND IsDeleted = 0;";
            try
            {
                await using var cmd = new SqlCommand(wfSql, connection);
                cmd.Parameters.AddWithValue("@WorkflowId", workflowId);
                var value = await cmd.ExecuteScalarAsync(cancellationToken);
                formGuid = value == null || value == DBNull.Value ? null : Convert.ToString(value)?.Trim();
            }
            catch (SqlException)
            {
            }
        }

        if (!formEntryId.HasValue || formEntryId.Value <= 0)
            return null;

        var fieldsJson = !string.IsNullOrWhiteSpace(storedFormData)
            ? storedFormData
            : !string.IsNullOrWhiteSpace(formGuid)
                ? await _ezfbFormDataLoader.LoadFormDataJsonAsync(formGuid, formEntryId.Value, cancellationToken)
                : null;

        if (wFormId == null && string.IsNullOrWhiteSpace(formGuid) && string.IsNullOrWhiteSpace(fieldsJson))
            return null;

        return new WorkflowInboxFormData(
            wFormId ?? 0,
            formEntryId.Value,
            formGuid,
            fieldsJson);
    }

    private static async Task<WorkflowInboxRepositoryData?> TryGetRepositoryDataAsync(SqlConnection connection, string workflowAttachmentsTable, Guid workflowInstanceId, CancellationToken cancellationToken)
    {
        var sql = $"SELECT TOP 1 RepositoryId, ItemId, FormJsonId FROM {workflowAttachmentsTable} WHERE WorkflowInstanceId = @WorkflowInstanceId AND IsDeleted = 0 ORDER BY CreatedAtUtc DESC;";
        try
        {
            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return new WorkflowInboxRepositoryData(
                    reader.IsDBNull(0) ? null : reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }
        catch (SqlException)
        {
            // Table may not exist in older tenant DBs.
        }

        return null;
    }

    private static async Task<int> TryGetCommentsCountAsync(SqlConnection connection, string workflowCommentsTable, Guid workflowInstanceId, CancellationToken cancellationToken)
    {
        var sql = $"SELECT COUNT(1) FROM {workflowCommentsTable} WHERE WorkflowInstanceId = @WorkflowInstanceId AND IsDeleted = 0;";
        try
        {
            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        }
        catch (SqlException)
        {
            return 0;
        }
    }

    private static List<WorkflowInboxGroup> GroupInboxItems(List<WorkflowInboxItem> items, string? groupBy)
    {
        var g = (groupBy ?? string.Empty).Trim().ToLowerInvariant();
        var groups = g switch
        {
            "stagename" => items.GroupBy(x => x.StageName ?? string.Empty),
            "activityid" => items.GroupBy(x => x.ActivityId ?? string.Empty),
            "requestno" => items.GroupBy(x => x.RequestNo ?? string.Empty),
            _ => items.GroupBy(_ => string.Empty)
        };
        return groups.Select(x => new WorkflowInboxGroup(x.Key, x.ToList())).ToList();
    }

    private Guid? GetCurrentUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue("sub")
                  ?? User.FindFirstValue("oid");
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}

/// <summary>Request to create a workflow. Supports both simple and full workflow creation.</summary>
public record CreateWorkflowRequest(
    string Name, 
    string? Description, 
    TriggerType TriggerType, 
    string? TriggerConfig = null,
    WorkflowJsonDto? WorkflowJson = null,  // Full workflow JSON from source API
    bool PublishImmediately = false,
    Guid? EmailConnectorId = null,
    bool? EmailIsEnabled = null,
    int? EmailPollIntervalMinutes = null,
    string? EmailQueryFilter = null,
    string? MasterSource = null,
    string? MasterFormId = null,
    Guid? MasterConnectorId = null
);

/// <summary>Request to add a step to a workflow.</summary>
public record AddWorkflowStepRequest(string Name, StepType StepType, int Order, string? Description = null, string? Config = null, bool IsRequired = true, Guid? AssignedToUserId = null, string? AssignedToRole = null, Guid? ApprovedNextStepId = null, Guid? RejectedNextStepId = null, ApprovalPolicy ApprovalPolicy = ApprovalPolicy.AnyOneApprove, string? ApproversJson = null, string? ActivityId = null);

/// <summary>Request to update a workflow. Only non-null fields are updated. Optional full <c>workflowJson</c> for v5 designer parity.</summary>
public record UpdateWorkflowRequest(
    string? Name = null,
    string? Description = null,
    TriggerType? TriggerType = null,
    string? TriggerConfig = null,
    WorkflowJsonDto? WorkflowJson = null,
    bool PublishImmediately = false,
    Guid? EmailConnectorId = null,
    bool? EmailIsEnabled = null,
    int? EmailPollIntervalMinutes = null,
    string? EmailQueryFilter = null,
    string? MasterSource = null,
    string? MasterFormId = null,
    Guid? MasterConnectorId = null);

/// <summary>Request to start a workflow instance.</summary>
public record StartWorkflowRequest(
    string? Context = null,
    string? EnvType = null,
    StartWorkflowAttachmentPayload? Attachment = null);

/// <summary>Request to set SLA policy for a workflow.</summary>
public record SetWorkflowSlaRequest(SlaPriority Priority, int ResponseTimeMinutes, int ResolutionTimeMinutes, int? EscalationTimeMinutes = null, Guid? EscalateToUserId = null, string? EscalateToRole = null, bool SendNotificationOnBreach = true, string? NotificationEmails = null);

/// <summary>Request to add a comment to a workflow instance.</summary>
public record AddCommentRequest(string Comments, Guid? StepInstanceId = null, string? ExternalCommentsBy = null, int ShowTo = 0);

/// <summary>Request to add an attachment to a workflow instance.</summary>
public record AddAttachmentRequest(string FileName, string FilePath, long? FileSize = null, string? ContentType = null);

/// <summary>Request to approve a workflow step.</summary>
public record ApproveStepRequest(string? Comments = null, bool MoveToNextStep = true);

/// <summary>Request to reject a workflow step.</summary>
public record RejectStepRequest(string Reason, bool CancelWorkflow = false, Guid? ReassignToUserId = null);

/// <summary>Request to move workflow to next step (supports AP agent engine JSON field names).</summary>
public record MoveToNextStepRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("activityid")] string ActivityId,
    string? Review = null,
    string? Comments = null,
    Guid? ActivityUserId = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("workflowId")] Guid? WorkflowId = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("transactionId")] string? TransactionId = null,
    Guid? InstanceId = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("processId")] Guid? ProcessId = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("AIAGENTResponse")] JsonElement? AiAgentResponse = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("AIAGENTHtml")] string? AiAgentHtml = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("itemId")] Guid? ItemId = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("repositoryId")] Guid? RepositoryId = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("formData")] JsonElement? FormData = null,
    string? FormId = null,
    int? FormEntryId = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("isItemTable")] bool? IsItemTable = null)
{
    public MoveToNextStepApAgentPayload? ToApAgentPayload(Guid routeInstanceId)
    {
        var resolvedInstanceId = InstanceId ?? ProcessId ?? routeInstanceId;
        if (InstanceId.HasValue && InstanceId.Value != routeInstanceId)
            throw new ArgumentException(
                $"Body instanceId '{InstanceId}' does not match route instance '{routeInstanceId}'.");

        if (AiAgentResponse is null && string.IsNullOrWhiteSpace(AiAgentHtml)
            && !RepositoryId.HasValue && !ItemId.HasValue
            && string.IsNullOrWhiteSpace(FormId) && !FormEntryId.HasValue
            && string.IsNullOrWhiteSpace(TransactionId))
            return null;

        var (formId, formEntryId) = ResolveFormData();
        var agentJson = AiAgentResponse.HasValue
            ? AiAgentResponse.Value.GetRawText()
            : null;

        return new MoveToNextStepApAgentPayload(
            TransactionId,
            resolvedInstanceId,
            agentJson,
            AiAgentHtml,
            ItemId,
            RepositoryId,
            formId,
            formEntryId);
    }

    public (string? FormId, int? FormEntryId) ResolveFormIdentity() => ResolveFormData();

    private (string? FormId, int? FormEntryId) ResolveFormData()
    {
        if (!string.IsNullOrWhiteSpace(FormId) || FormEntryId.HasValue)
            return (FormId, FormEntryId);

        if (!FormData.HasValue)
            return (null, null);

        var el = FormData.Value;
        if (el.ValueKind == JsonValueKind.Object)
        {
            string? fid = null;
            int? entry = null;
            if (el.TryGetProperty("formId", out var f) && f.ValueKind == JsonValueKind.String)
                fid = f.GetString();
            if (el.TryGetProperty("formentryId", out var e) && e.TryGetInt32(out var n))
                entry = n;
            else if (el.TryGetProperty("formEntryId", out var e2) && e2.TryGetInt32(out var n2))
                entry = n2;
            return (fid, entry);
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            var parts = el.GetString()?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts is { Length: >= 2 }
                && int.TryParse(parts[1], out var entryId))
                return (parts[0], entryId);
        }

        return (null, null);
    }
}

/// <summary>Bulk move-next: instanceId comma list (or instanceIds array), activityId, review. No formData.</summary>
public record BulkMoveToNextStepRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("activityid")] string ActivityId,
    string? Review = null,
    string? Comments = null,
    Guid? ActivityUserId = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("instanceId")] string? InstanceId = null,
    IReadOnlyList<Guid>? InstanceIds = null);

/// <summary>Request to perform a custom action on workflow.</summary>
public record PerformActionRequest(
    SaaSApp.Workflow.Application.Workflows.Commands.PerformAction.WorkflowAction Action,
    string? Comments = null,
    Guid? AssignToUserId = null,
    bool MoveToNextStep = true
);

public sealed record WorkflowAllRequest(
    WorkflowAllSortBy? SortBy = null,
    string GroupBy = "",
    List<WorkflowAllFilterGroup>? FilterBy = null,
    int CurrentPage = 1,
    int ItemsPerPage = 20,
    string Mode = "browse",
    bool HasSecurity = true,
    bool HasReport = false);

public sealed record WorkflowAllSortBy(string Criteria = "modifiedAt", string Order = "DESC");
public sealed record WorkflowAllFilterGroup(string GroupCondition = "AND", List<WorkflowAllFilter>? Filters = null)
{
    public List<WorkflowAllFilter> Filters { get; init; } = Filters ?? new();
}
public sealed record WorkflowAllFilter(string Criteria, string Condition, string Value);
public sealed record WorkflowAllMeta(int CurrentPage, int ItemsPerPage, int TotalItems);
public sealed record WorkflowAllGroup(string Key, List<WorkflowAllItem> Value);
public sealed record WorkflowAllResponse(List<WorkflowAllGroup> Data, WorkflowAllMeta Meta);
public sealed record WorkflowAllItem(
    Guid Id,
    string Name,
    string? Description,
    string FlowStatus,
    string CreatedBy,
    string? ModifiedBy,
    DateTime CreatedAt,
    DateTime? ModifiedAt,
    string? CreatedByName = null,
    string? ModifiedByName = null);

public sealed record WorkflowByUserItem(
    Guid Id,
    string Name,
    string? Description,
    DateTime CreatedAt,
    DateTime? ModifiedAt,
    string? CreatedBy,
    string? ModifiedBy,
    string TokenUserId,
    int InboxCount,
    int ProcessCount,
    int CompletedCount,
    int RunningCount,
    int PaymentProcessCount);

public sealed record ApAgentProgressRequest(
    string? Stage = null,
    string? Message = null,
    int? Percent = null,
    string? FormData = null);

public sealed record WorkflowInboxRequest(
    WorkflowAllSortBy? SortBy = null,
    string GroupBy = "",
    List<WorkflowAllFilterGroup>? FilterBy = null,
    int CurrentPage = 1,
    int ItemsPerPage = 20,
    string Mode = "browse");

public sealed record WorkflowInboxShareResponse(
    CreateRepositoryItemShareResult Share,
    WorkflowInboxShareAssignmentResult InboxAssignment,
    Guid GuestUserId);

public sealed record WorkflowInboxResponse(List<WorkflowInboxGroup> Data, WorkflowAllMeta Meta);
public sealed record WorkflowInboxGroup(string Key, List<WorkflowInboxItem> Value);
public sealed record WorkflowInboxFormData(
    int WFormId,
    int FormEntryId,
    string? FormId = null,
    string? FieldsJson = null);
public sealed record WorkflowInboxRepositoryData(int? RepositoryId, int? ItemId, string? FormJsonId);
public sealed record WorkflowInboxItem(
    int TransactionId,
    Guid WorkflowInstanceId,
    string? RequestNo,
    int FlowStatus,
    DateTime RaisedAt,
    string? RaisedByUserId,
    string? ActivityId,
    string? RuleId,
    string? StageType,
    string? StageName,
    string? Review,
    DateTime TransactionCreatedAt,
    string? TransactionCreatedBy,
    DateTime? TransactionModifiedAt,
    string? ActivityUserId,
    int? ActivityGroupId,
    WorkflowInboxFormData? FormData,
    WorkflowInboxRepositoryData? RepositoryData,
    int CommentsCount,
    int Action = 1);
