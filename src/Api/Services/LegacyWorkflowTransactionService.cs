using System.Text;
using System.Text.Json;
using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Domain.Entities;
using SaaSApp.Workflow.Domain.Enums;

namespace SaaSApp.Api.Services;

/// <inheritdoc />
public sealed class LegacyWorkflowTransactionService : ILegacyWorkflowTransactionService
{
    private static readonly JsonSerializerOptions JsonCamel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ITenantConnectionProvider _tenantConnection;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<LegacyWorkflowTransactionService> _logger;
    private readonly IWorkflowTableCreator _workflowTableCreator;
    private readonly IWorkflowRepository _workflowRepository;
    private readonly IWorkflowLegacyMailboxSyncService _mailboxSync;

    public LegacyWorkflowTransactionService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ITenantConnectionProvider tenantConnection,
        ITenantProvider tenantProvider,
        IWorkflowTableCreator workflowTableCreator,
        IWorkflowRepository workflowRepository,
        IWorkflowLegacyMailboxSyncService mailboxSync,
        ILogger<LegacyWorkflowTransactionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _tenantConnection = tenantConnection;
        _tenantProvider = tenantProvider;
        _workflowTableCreator = workflowTableCreator;
        _workflowRepository = workflowRepository;
        _mailboxSync = mailboxSync;
        _logger = logger;
    }

    public async Task<(int StatusCode, string ContentType, string Body)> ExecuteAsync(JsonElement body, HttpRequest httpRequest, CancellationToken cancellationToken)
    {
        var proxyUrl = _configuration["LegacyV5:WorkflowTransactionUrl"]?.Trim();
        if (!string.IsNullOrEmpty(proxyUrl))
        {
            return await ProxyToV5Async(proxyUrl, body, httpRequest, cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteLocalLegacySubsetAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int StatusCode, string ContentType, string Body)> ProxyToV5Async(
        string proxyUrl,
        JsonElement body,
        HttpRequest httpRequest,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(LegacyWorkflowTransactionService));
        client.Timeout = TimeSpan.FromMinutes(5);

        using var req = new HttpRequestMessage(HttpMethod.Post, proxyUrl);
        req.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");

        static void CopyHeader(HttpRequest from, HttpRequestMessage to, string name)
        {
            if (!from.Headers.TryGetValue(name, out var values))
                return;
            var v = values.ToString();
            if (!string.IsNullOrEmpty(v))
                to.Headers.TryAddWithoutValidation(name, v);
        }

        CopyHeader(httpRequest, req, "Authorization");
        CopyHeader(httpRequest, req, "X-Tenant-Id");
        CopyHeader(httpRequest, req, "Cookie");

        try
        {
            using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var ct = resp.Content.Headers.ContentType?.MediaType ?? "application/json";
            return ((int)resp.StatusCode, ct, text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Legacy v5 workflow transaction proxy failed: {Url}", proxyUrl);
            var err = JsonSerializer.Serialize(new V5ProcessResultDto
            {
                RequestNo = "Legacy proxy error",
                ProcessId = -1,
                TransactionId = null,
                Log = ex.Message
            }, JsonCamel);
            return (502, "application/json", err);
        }
    }

    /// <summary>
    /// In-process path: updates <c>workflow.transaction_{suffix}</c> when <c>workflowId</c> is a GUID and
    /// <c>transactionId</c> in the body is a GUID string matching <c>TransactionGuid</c> on the row, or initiate when omitted / empty / <see cref="Guid.Empty"/>.
    /// Does not replicate full <c>ProcessTransactionFn</c> (next-stage inserts, forms, notifications, sub-workflows, etc.).
    /// </summary>
    private async Task<(int StatusCode, string ContentType, string Body)> ExecuteLocalLegacySubsetAsync(JsonElement body, CancellationToken cancellationToken)
    {
        var conn = _tenantConnection.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn))
        {
            var bad = JsonSerializer.Serialize(new V5ProcessResultDto { RequestNo = "Tenant connection not resolved", ProcessId = -1, Log = "X-Tenant-Id / tenant DB missing." }, JsonCamel);
            return (400, "application/json", bad);
        }

        if (!TryGetWorkflowGuid(body, out var workflowId))
        {
            var bad = JsonSerializer.Serialize(new V5ProcessResultDto
            {
                RequestNo = "Invalid workflowId",
                ProcessId = -1,
                Log = "workflowId is required in the request body and must be a valid GUID string."
            }, JsonCamel);
            return (400, "application/json", bad);
        }

        if (!TryParseTransactionReference(body, out var transactionRef, out var transactionParseError))
        {
            var bad = JsonSerializer.Serialize(new V5ProcessResultDto
            {
                RequestNo = "Invalid transactionId",
                ProcessId = -1,
                Log = transactionParseError ?? "transactionId is invalid."
            }, JsonCamel);
            return (400, "application/json", bad);
        }

        // Resolve "createdBy" (GUID or email) similar to v5 behavior.
        var createdBy = await ResolveCreatedByAsync(conn, body, cancellationToken).ConfigureAwait(false);

        var review = body.TryGetProperty("review", out var rEl) && rEl.ValueKind == JsonValueKind.String
            ? rEl.GetString()
            : null;

        var userIdStr = body.TryGetProperty("createdBy", out var cEl) && cEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cEl.GetString())
            ? cEl.GetString()!.Trim()
            : null;

        var suffix = workflowId.ToString("N")[..8];
        var table = $"workflow.transaction_{suffix}";
        var instancesTable = $"workflow.workflow_instances_{suffix}";
        var workflowFormsTable = $"workflow.workflow_forms_{suffix}";
        var workflowAttachmentsTable = $"workflow.workflow_attachments_{suffix}";

        int actionStatus = 1;
        if (!string.IsNullOrEmpty(review) &&
            (review.Equals("Reject", StringComparison.OrdinalIgnoreCase) ||
             review.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ||
             review.Contains("reject", StringComparison.OrdinalIgnoreCase)))
        {
            actionStatus = 2;
        }

        await using var connection = new NpgsqlConnection(conn);
        await connection.OpenAsync(cancellationToken);

        // Ensure per-workflow dynamic tables exist (instances/transaction/forms/attachments/etc.)
        await _workflowTableCreator.CreateWorkflowTablesAsync(workflowId, conn, cancellationToken).ConfigureAwait(false);

        int transactionIntId;
        if (transactionRef.IsInitiate)
        {
            transactionIntId = 0;
        }
        else
        {
            var (resolved, id, resolveLog) = await ResolveTransactionIntIdByGuidAsync(connection, table, transactionRef.Guid, cancellationToken).ConfigureAwait(false);
            if (!resolved)
            {
                var nf = JsonSerializer.Serialize(new V5ProcessResultDto
                {
                    RequestNo = "Transaction not found",
                    ProcessId = 0,
                    TransactionId = FormatTransactionId(transactionRef.Guid),
                    Log = resolveLog ?? "Transaction not found."
                }, JsonCamel);
                return (404, "application/json", nf);
            }

            transactionIntId = id;
        }

        // Load normalized workflow definition (steps) for Phase 1 next-stage behavior.
        var workflowDef = await _workflowRepository.GetByIdWithStepsAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (workflowDef == null)
        {
            var bad = JsonSerializer.Serialize(new V5ProcessResultDto
            {
                RequestNo = "Workflow not found",
                ProcessId = -1,
                TransactionId = !transactionRef.IsInitiate ? FormatTransactionId(transactionRef.Guid) : null,
                Log = $"No workflow definition found for workflowId {workflowId}."
            }, JsonCamel);
            return (404, "application/json", bad);
        }

        // Initiate (v5: transactionId == 0) - create a process + first transaction row.
        if (transactionIntId <= 0)
        {
            // Resolve first step for ActivityId/StageName defaults (if not provided).
            var firstStep = workflowDef.Steps
                .Where(s => s != null)
                .OrderBy(s => s.Order)
                .FirstOrDefault();

            var referenceNumber = body.TryGetProperty("requestNo", out var rnEl) && rnEl.ValueKind == JsonValueKind.String
                ? rnEl.GetString()
                : null;
            referenceNumber = string.IsNullOrWhiteSpace(referenceNumber)
                ? $"REQ-{DateTime.UtcNow:yyyyMMddHHmmss}"
                : referenceNumber.Trim();

            var newWorkflowInstanceId = Guid.NewGuid();
            if (body.TryGetProperty("workflowInstanceId", out var wiEl) && wiEl.ValueKind == JsonValueKind.String
                && Guid.TryParse(wiEl.GetString(), out var parsedWi))
            {
                newWorkflowInstanceId = parsedWi;
            }

            var tenantId = _tenantProvider.GetTenantId() ?? Guid.Empty;
            var insertInstanceSql = $"""
                INSERT INTO {instancesTable}
                    (id, tenant_id, workflow_id, workflow_name, workflow_version, status, created_at_utc, started_at_utc, started_by, reference_number, priority, view_count, is_archived)
                VALUES
                    (@Id, @TenantId, @WorkflowId, @WorkflowName, @WorkflowVersion, @Status, now(), now(), @StartedBy, @ReferenceNumber, 1, 0, false)
                ON CONFLICT (id) DO NOTHING;
                """;
            await using (var insInst = new NpgsqlCommand(insertInstanceSql, connection))
            {
                insInst.Parameters.AddWithValue("@Id", newWorkflowInstanceId);
                insInst.Parameters.AddWithValue("@TenantId", tenantId);
                insInst.Parameters.AddWithValue("@WorkflowId", workflowId);
                insInst.Parameters.AddWithValue("@WorkflowName", workflowDef.Name);
                insInst.Parameters.AddWithValue("@WorkflowVersion", workflowDef.Version);
                insInst.Parameters.AddWithValue("@Status", (int)WorkflowInstanceStatus.Running);
                insInst.Parameters.AddWithValue("@StartedBy", (object?)createdBy ?? Guid.Empty);
                insInst.Parameters.AddWithValue("@ReferenceNumber", referenceNumber);
                await insInst.ExecuteNonQueryAsync(cancellationToken);
            }

            // Optional fields for first transaction row
            string? activityId = body.TryGetProperty("activityId", out var aEl) && aEl.ValueKind == JsonValueKind.String ? aEl.GetString() : null;
            string? ruleId = body.TryGetProperty("ruleId", out var ruleEl) && ruleEl.ValueKind == JsonValueKind.String ? ruleEl.GetString() : null;
            string? stageType = body.TryGetProperty("stageType", out var stEl) && stEl.ValueKind == JsonValueKind.String ? stEl.GetString() : null;
            string? stageName = body.TryGetProperty("stageName", out var snEl) && snEl.ValueKind == JsonValueKind.String ? snEl.GetString() : null;

            if (string.IsNullOrWhiteSpace(activityId) && firstStep != null)
                activityId = firstStep.Id.ToString("D");
            if (string.IsNullOrWhiteSpace(stageName) && firstStep != null)
                stageName = firstStep.Name;
            if (string.IsNullOrWhiteSpace(stageType) && firstStep != null)
                stageType = firstStep.StepType.ToString();

            Guid? activityUserId = null;
            if (body.TryGetProperty("activityUserId", out var auEl) && auEl.ValueKind == JsonValueKind.String && Guid.TryParse(auEl.GetString(), out var au))
                activityUserId = au;
            if (activityUserId == null && firstStep?.AssignedToUserId != null)
                activityUserId = firstStep.AssignedToUserId;

            int? activityGroupId = null;
            if (body.TryGetProperty("activityGroupId", out var agEl) && agEl.ValueKind == JsonValueKind.Number)
                activityGroupId = agEl.GetInt32();

            var insertTransactionSql = $"""
                INSERT INTO {table}
                    (workflow_instance_id, activity_id, rule_id, stage_type, stage_name, review, action_status, activity_user_id, activity_group_id, created_at, created_by, is_deleted, transaction_guid)
                VALUES
                    (@WorkflowInstanceId, @ActivityId, @RuleId, @StageType, @StageName, @Review, 0, @ActivityUserId, @ActivityGroupId, now(), @CreatedBy, false, gen_random_uuid())
                RETURNING id, transaction_guid;
                """;

            int newTransactionId;
            Guid newTransactionGuid;
            await using (var insTran = new NpgsqlCommand(insertTransactionSql, connection))
            {
                insTran.Parameters.AddWithValue("@WorkflowInstanceId", newWorkflowInstanceId);
                insTran.Parameters.AddWithValue("@ActivityId", (object?)activityId ?? DBNull.Value);
                insTran.Parameters.AddWithValue("@RuleId", (object?)ruleId ?? DBNull.Value);
                insTran.Parameters.AddWithValue("@StageType", (object?)stageType ?? DBNull.Value);
                insTran.Parameters.AddWithValue("@StageName", (object?)stageName ?? DBNull.Value);
                insTran.Parameters.AddWithValue("@Review", (object?)review ?? DBNull.Value);
                insTran.Parameters.AddWithValue("@ActivityUserId", (object?)activityUserId ?? DBNull.Value);
                insTran.Parameters.AddWithValue("@ActivityGroupId", (object?)activityGroupId ?? DBNull.Value);
                insTran.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
                await using (var reader = await insTran.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    newTransactionId = reader.GetInt32(0);
                    newTransactionGuid = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1);
                }
            }

            var formId = 0;
            if (body.TryGetProperty("formData", out var fdEl) && fdEl.ValueKind == JsonValueKind.Object)
            {
                formId = fdEl.TryGetProperty("formId", out var fIdEl) && fIdEl.ValueKind == JsonValueKind.Number ? fIdEl.GetInt32() : 0;
            }

            await EnsureEzfbItemsTableAsync(connection, suffix, cancellationToken).ConfigureAwait(false);
            var ezfbFormEntryId = await InsertEzfbItemAsync(connection, suffix, createdBy, cancellationToken).ConfigureAwait(false);

            var insFormSql = $"""
                INSERT INTO {workflowFormsTable}
                    (id, tenant_id, workflow_instance_id, w_form_id, form_entry_id, created_at_utc, created_by, is_deleted)
                VALUES
                    (gen_random_uuid(), @TenantId, @WorkflowInstanceId, @WFormId, @FormEntryId, now(), @CreatedBy, false);
                """;
            await using (var insForm = new NpgsqlCommand(insFormSql, connection))
            {
                insForm.Parameters.AddWithValue("@TenantId", tenantId);
                insForm.Parameters.AddWithValue("@WorkflowInstanceId", newWorkflowInstanceId);
                insForm.Parameters.AddWithValue("@WFormId", formId);
                insForm.Parameters.AddWithValue("@FormEntryId", ezfbFormEntryId);
                insForm.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? Guid.Empty);
                await insForm.ExecuteNonQueryAsync(cancellationToken);
            }

            string? formJsonId = body.TryGetProperty("formJsonId", out var fjEl) && fjEl.ValueKind == JsonValueKind.String ? fjEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(formJsonId))
            {
                var insAttachSql = $"""
                    INSERT INTO {workflowAttachmentsTable}
                        (id, tenant_id, workflow_instance_id, form_json_id, file_name, file_path, created_at_utc, created_by, is_deleted)
                    VALUES
                        (gen_random_uuid(), @TenantId, @WorkflowInstanceId, @FormJsonId, @FileName, @FilePath, now(), @CreatedBy, false);
                    """;
                await using var insAttach = new NpgsqlCommand(insAttachSql, connection);
                insAttach.Parameters.AddWithValue("@TenantId", tenantId);
                insAttach.Parameters.AddWithValue("@WorkflowInstanceId", newWorkflowInstanceId);
                insAttach.Parameters.AddWithValue("@FormJsonId", formJsonId!.Trim());
                insAttach.Parameters.AddWithValue("@FileName", "legacy-form-json");
                insAttach.Parameters.AddWithValue("@FilePath", formJsonId!.Trim());
                insAttach.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? Guid.Empty);
                await insAttach.ExecuteNonQueryAsync(cancellationToken);
            }

            await _mailboxSync.SyncTransactionRowAsync(workflowId, newTransactionId, cancellationToken)
                .ConfigureAwait(false);

            var okNew = JsonSerializer.Serialize(new V5ProcessResultDto
            {
                RequestNo = referenceNumber,
                ProcessId = 0,
                WorkflowInstanceId = newWorkflowInstanceId,
                TransactionId = FormatTransactionId(newTransactionGuid),
                FormEntryId = ezfbFormEntryId,
                Log = "In-process legacy initiate: created workflow instance + first transaction row (subset of v5). For full v5 behavior configure LegacyV5:WorkflowTransactionUrl."
            }, JsonCamel);
            return (201, "application/json", okNew);
        }

        Guid workflowInstanceId;
        string? currentActivityId;
        await using (var getProc = new NpgsqlCommand($"SELECT workflow_instance_id, activity_id FROM {table} WHERE id = @Tid AND is_deleted = false", connection))
        {
            getProc.Parameters.AddWithValue("@Tid", transactionIntId);
            await using var r = await getProc.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var nf = JsonSerializer.Serialize(new V5ProcessResultDto
                {
                    RequestNo = "Transaction not found",
                    ProcessId = 0,
                    TransactionId = FormatTransactionId(transactionRef.Guid),
                    Log = $"No row in transaction_{suffix} for transactionId {FormatTransactionId(transactionRef.Guid)}."
                }, JsonCamel);
                return (404, "application/json", nf);
            }
            workflowInstanceId = r.GetGuid(0);
            currentActivityId = r.IsDBNull(1) ? null : r.GetString(1);
        }

        var modifiedBy = createdBy;

        // Optional v5 parity: userIds/groupIds arrays -> comma separated
        var userIdsCsv = body.TryGetProperty("userIds", out var uidsEl) && uidsEl.ValueKind == JsonValueKind.Array
            ? string.Join(",", uidsEl.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)))
            : null;
        var groupIdsCsv = body.TryGetProperty("groupIds", out var gidsEl) && gidsEl.ValueKind == JsonValueKind.Array
            ? string.Join(",", gidsEl.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32().ToString() : x.ValueKind == JsonValueKind.String ? x.GetString() : null).Where(s => !string.IsNullOrWhiteSpace(s)))
            : null;
        var slaTransactionId = body.TryGetProperty("slaTransactionId", out var slaEl) && slaEl.ValueKind == JsonValueKind.Number
            ? (int?)slaEl.GetInt32()
            : null;

        // PHASE 4: workflow.transaction_{suffix} always has user_ids/group_ids/sla_transaction_id
        // as fixed columns from day one (see WorkflowTableCreator.cs's
        // GenerateLegacyTransactionTableScript) -- no cross-install column-presence drift to guard
        // against with a COL_LENGTH-style check the way SQL Server needed, so the CASE WHEN
        // COL_LENGTH(...) IS NULL fallback was dropped and these columns are updated directly.
        var updateSql = $"""
            UPDATE {table}
            SET review = @Review,
                action_status = @ActionStatus,
                modified_at = now(),
                modified_by = @ModifiedBy,
                user_ids = @UserIds,
                group_ids = @GroupIds,
                sla_transaction_id = @SlaTransactionId
            WHERE id = @Tid AND is_deleted = false;
            """;

        await using (var upd = new NpgsqlCommand(updateSql, connection))
        {
            upd.Parameters.AddWithValue("@Review", (object?)review ?? DBNull.Value);
            upd.Parameters.AddWithValue("@ActionStatus", actionStatus);
            upd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
            upd.Parameters.AddWithValue("@UserIds", (object?)userIdsCsv ?? DBNull.Value);
            upd.Parameters.AddWithValue("@GroupIds", (object?)groupIdsCsv ?? DBNull.Value);
            upd.Parameters.AddWithValue("@SlaTransactionId", (object?)slaTransactionId ?? DBNull.Value);
            upd.Parameters.AddWithValue("@Tid", transactionIntId);
            var n = await upd.ExecuteNonQueryAsync(cancellationToken);
            if (n == 0)
            {
                var rowGuid = await TryGetTransactionGuidForRowAsync(connection, table, transactionIntId, cancellationToken).ConfigureAwait(false);
                var nf = JsonSerializer.Serialize(new V5ProcessResultDto { RequestNo = "Update failed", ProcessId = 0, WorkflowInstanceId = workflowInstanceId, TransactionId = FormatTransactionId(rowGuid ?? Guid.Empty), Log = "No row updated." }, JsonCamel);
                return (404, "application/json", nf);
            }
        }

        _logger.LogInformation("Legacy transaction updated workflow {WorkflowId} transactionRowId {TransactionRowId} instance {WorkflowInstanceId}", workflowId, transactionIntId, workflowInstanceId);

        await _mailboxSync.SyncTransactionRowAsync(workflowId, transactionIntId, cancellationToken)
            .ConfigureAwait(false);

        // Phase 1: determine next step from normalized workflow steps and insert the next transaction row.
        var isReject = actionStatus == 2;
        WorkflowStep? currentStep = null;
        if (!string.IsNullOrWhiteSpace(currentActivityId) && Guid.TryParse(currentActivityId, out var currentStepId))
            currentStep = workflowDef.Steps.FirstOrDefault(s => s.Id == currentStepId);

        // Fallback: if ActivityId is not a GUID, try by name match.
        currentStep ??= !string.IsNullOrWhiteSpace(currentActivityId)
            ? workflowDef.Steps.FirstOrDefault(s => s.Name.Equals(currentActivityId, StringComparison.OrdinalIgnoreCase))
            : null;

        Guid? nextStepId = null;
        if (currentStep != null)
            nextStepId = isReject ? currentStep.RejectedNextStepId : currentStep.ApprovedNextStepId;

        // If explicit next step links are missing, fall back to sequential order.
        if (nextStepId == null && currentStep != null)
        {
            nextStepId = workflowDef.Steps
                .Where(s => s.Order > currentStep.Order)
                .OrderBy(s => s.Order)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefault();
        }

        if (nextStepId == null)
        {
            // No next step -> complete the workflow instance.
            await using (var updInst = new NpgsqlCommand(
                $"UPDATE {instancesTable} SET status = @Status, completed_at_utc = now(), last_activity_at_utc = now() WHERE id = @WorkflowInstanceId",
                connection))
            {
                updInst.Parameters.AddWithValue("@Status", (int)WorkflowInstanceStatus.Completed);
                updInst.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
                await updInst.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await _mailboxSync.SyncInstanceEndTransactionsAsync(workflowId, workflowInstanceId, cancellationToken)
                .ConfigureAwait(false);

            var doneGuid = await TryGetTransactionGuidForRowAsync(connection, table, transactionIntId, cancellationToken).ConfigureAwait(false);
            var done = JsonSerializer.Serialize(new V5ProcessResultDto
            {
                RequestNo = "Workflow completed",
                ProcessId = 0,
                WorkflowInstanceId = workflowInstanceId,
                TransactionId = FormatTransactionId(doneGuid ?? Guid.Empty),
                Log = "No next step found; workflow instance marked completed."
            }, JsonCamel);
            return (201, "application/json", done);
        }

        var nextStep = workflowDef.Steps.FirstOrDefault(s => s.Id == nextStepId.Value);
        if (nextStep == null)
        {
            var currentTxGuid = await TryGetTransactionGuidForRowAsync(connection, table, transactionIntId, cancellationToken).ConfigureAwait(false);
            var bad = JsonSerializer.Serialize(new V5ProcessResultDto
            {
                RequestNo = "Next step not found",
                ProcessId = -1,
                TransactionId = FormatTransactionId(currentTxGuid ?? Guid.Empty),
                Log = $"Next step id {nextStepId} not found in workflow definition."
            }, JsonCamel);
            return (400, "application/json", bad);
        }

        var nextActivityUserId = nextStep.AssignedToUserId;
        var insertNextSql = $"""
            INSERT INTO {table}
                (workflow_instance_id, activity_id, rule_id, stage_type, stage_name, review, action_status, activity_user_id, activity_group_id, created_at, created_by, is_deleted, transaction_guid)
            VALUES
                (@WorkflowInstanceId, @ActivityId, NULL, @StageType, @StageName, NULL, 0, @ActivityUserId, NULL, now(), @CreatedBy, false, gen_random_uuid())
            RETURNING id, transaction_guid;
            """;

        int nextTransactionId;
        Guid nextTransactionGuid;
        await using (var insNext = new NpgsqlCommand(insertNextSql, connection))
        {
            insNext.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            insNext.Parameters.AddWithValue("@ActivityId", (object?)nextStep.Id.ToString("D") ?? DBNull.Value);
            insNext.Parameters.AddWithValue("@StageType", nextStep.StepType.ToString());
            insNext.Parameters.AddWithValue("@StageName", nextStep.Name);
            insNext.Parameters.AddWithValue("@ActivityUserId", (object?)nextActivityUserId ?? DBNull.Value);
            insNext.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            await using (var reader = await insNext.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                nextTransactionId = reader.GetInt32(0);
                nextTransactionGuid = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1);
            }
        }

        await _mailboxSync.SyncTransactionRowAsync(workflowId, nextTransactionId, cancellationToken)
            .ConfigureAwait(false);

        var ok = JsonSerializer.Serialize(new V5ProcessResultDto
        {
            RequestNo = "Process moved to Next Stage",
            ProcessId = 0,
            WorkflowInstanceId = workflowInstanceId,
            TransactionId = FormatTransactionId(nextTransactionGuid),
            Log = "Phase 1: transaction updated and next-stage transaction inserted (normalized step mapping). For full v5 behavior configure LegacyV5:WorkflowTransactionUrl."
        }, JsonCamel);
        return (201, "application/json", ok);
    }

    /// <summary>Minimal fallback shape matching FormService.cs's ezfb_{suffix}_items system columns (snake_case).</summary>
    private static async Task EnsureEzfbItemsTableAsync(NpgsqlConnection connection, string suffix, CancellationToken cancellationToken)
    {
        var table = $"dbo.ezfb_{suffix}_items";
        var sql = $"""
            CREATE SCHEMA IF NOT EXISTS dbo;
            CREATE TABLE IF NOT EXISTS {table} (
                item_id uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NULL,
                modified_at timestamptz NULL,
                modified_by uuid NULL,
                is_deleted boolean NOT NULL DEFAULT false
            );
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Guid> InsertEzfbItemAsync(NpgsqlConnection connection, string suffix, Guid? createdBy, CancellationToken cancellationToken)
    {
        var table = $"dbo.ezfb_{suffix}_items";
        var sql = $"""
            INSERT INTO {table} (created_at, created_by, is_deleted)
            VALUES (now(), @CreatedBy, false)
            RETURNING item_id;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
        return (Guid)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<Guid?> ResolveCreatedByAsync(string tenantConnectionString, JsonElement body, CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.GetTenantId();
        if (tenantId == null)
            return null;

        if (!body.TryGetProperty("createdBy", out var cEl) || cEl.ValueKind != JsonValueKind.String)
            return null;

        var raw = cEl.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (Guid.TryParse(raw, out var g))
            return g;

        // Treat createdBy as email (v5 behavior). Try resolve to existing tenant user, else create a minimal user row.
        if (!raw.Contains("@", StringComparison.OrdinalIgnoreCase))
            return null;

        await using var connection = new NpgsqlConnection(tenantConnectionString);
        await connection.OpenAsync(cancellationToken);

        var lookupSql = """SELECT "Id" FROM users."Users" WHERE "TenantId" = @TenantId AND "Email" = @Email AND "IsDeleted" = false ORDER BY "CreatedAtUtc" DESC LIMIT 1;""";
        await using (var lookup = new NpgsqlCommand(lookupSql, connection))
        {
            lookup.Parameters.AddWithValue("@TenantId", tenantId.Value);
            lookup.Parameters.AddWithValue("@Email", raw);
            var o = await lookup.ExecuteScalarAsync(cancellationToken);
            if (o != null && o != DBNull.Value && Guid.TryParse(o.ToString(), out var existing))
                return existing;
        }

        var newId = Guid.NewGuid();
        const string insertSql = """
            INSERT INTO users."Users" ("Id", "TenantId", "Email", "DisplayName", "Role", "CreatedAtUtc", "IsDeleted")
            VALUES (@Id, @TenantId, @Email, @DisplayName, @Role, now(), false);
            """;
        await using (var ins = new NpgsqlCommand(insertSql, connection))
        {
            ins.Parameters.AddWithValue("@Id", newId);
            ins.Parameters.AddWithValue("@TenantId", tenantId.Value);
            ins.Parameters.AddWithValue("@Email", raw);
            ins.Parameters.AddWithValue("@DisplayName", raw);
            ins.Parameters.AddWithValue("@Role", "TenantUser");
            await ins.ExecuteNonQueryAsync(cancellationToken);
        }

        return newId;
    }

    /// <summary>Parses <c>workflowId</c> from the JSON body as a GUID (string form).</summary>
    private static bool TryGetWorkflowGuid(JsonElement body, out Guid workflowId)
    {
        workflowId = default;
        if (!body.TryGetProperty("workflowId", out var w) || w.ValueKind != JsonValueKind.String)
            return false;

        return Guid.TryParse(w.GetString(), out workflowId);
    }

    /// <summary>API-facing transaction id: canonical GUID string, or null to omit from JSON.</summary>
    private static string? FormatTransactionId(Guid g) => g == Guid.Empty ? null : g.ToString("D");

    private readonly struct TransactionRef
    {
        public bool IsInitiate { get; }
        /// <summary>Body <c>transactionId</c> when updating an existing row.</summary>
        public Guid Guid { get; }

        private TransactionRef(bool initiate, Guid guid)
        {
            IsInitiate = initiate;
            Guid = guid;
        }

        public static TransactionRef Initiate() => new(true, default);

        public static TransactionRef Existing(Guid guid) => new(false, guid);
    }

    private static bool TryParseTransactionReference(JsonElement body, out TransactionRef reference, out string? errorMessage)
    {
        reference = TransactionRef.Initiate();
        errorMessage = null;

        if (!body.TryGetProperty("transactionId", out var tEl) || tEl.ValueKind == JsonValueKind.Null)
        {
            reference = TransactionRef.Initiate();
            return true;
        }

        if (tEl.ValueKind != JsonValueKind.String)
        {
            errorMessage = "transactionId must be a GUID string, or null/omitted to initiate a new transaction.";
            return false;
        }

        var s = tEl.GetString()?.Trim() ?? "";
        if (s.Length == 0 || s == "0")
        {
            reference = TransactionRef.Initiate();
            return true;
        }

        if (!Guid.TryParse(s, out var g))
        {
            errorMessage = "transactionId must be a valid GUID string (use empty or omit to initiate).";
            return false;
        }

        reference = g == Guid.Empty ? TransactionRef.Initiate() : TransactionRef.Existing(g);
        return true;
    }

    private static async Task<(bool Ok, int Id, string? Log)> ResolveTransactionIntIdByGuidAsync(
        NpgsqlConnection connection,
        string table,
        Guid transactionGuid,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT id FROM {table} WHERE transaction_guid = @g AND is_deleted = false ORDER BY id DESC LIMIT 1",
            connection);
        cmd.Parameters.AddWithValue("@g", transactionGuid);
        var o = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (o == null || o == DBNull.Value)
            return (false, 0, $"No row in {table} for transactionId (GUID) {transactionGuid:D}.");

        return (true, Convert.ToInt32(o), null);
    }

    private static async Task<Guid?> TryGetTransactionGuidForRowAsync(
        NpgsqlConnection connection,
        string table,
        int transactionIntId,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT transaction_guid FROM {table} WHERE id = @id AND is_deleted = false",
            connection);
        cmd.Parameters.AddWithValue("@id", transactionIntId);
        var o = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (o == null || o == DBNull.Value)
            return null;

        return (Guid)o;
    }
}

/// <summary>v5 <c>Processresult</c> JSON shape (camelCase via default serializer).</summary>
public sealed class V5ProcessResultDto
{
    public string? RequestNo { get; set; }
    public int ProcessId { get; set; }
    public Guid? WorkflowInstanceId { get; set; }
    /// <summary>Workflow transaction row id (GUID string, "D" format). Omitted when null.</summary>
    public string? TransactionId { get; set; }
    public string? AutoGenDocNo { get; set; }
    public string Log { get; set; } = "";
    public Guid FormEntryId { get; set; }
    public string ActivityId { get; set; } = "";
    public string ApplicationId { get; set; } = "";
    public string PersonalEntryId { get; set; } = "";
}
