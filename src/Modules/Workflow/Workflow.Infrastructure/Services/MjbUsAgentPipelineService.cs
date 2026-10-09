using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.SharedKernel.Options;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Application.Workflows.Commands.MoveToNextStep;
using SaaSApp.Workflow.Domain.Entities;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Classification, then OCR and FTP only when the document is an invoice.
/// Each agent response is written onto the MJB_US form. A non-invoice closes the ticket.
/// </summary>
public sealed class MjbUsAgentPipelineService : IMjbUsAgentPipelineService
{
    private const string EnvType = "live";
    private const string ClassificationModel = "gpt-4.1-nano";
    private const string FtpModel = "OpenAI GPT-4.1";
    private const string ConnectorName = "FTP";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWorkflowRepository _repository;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IMediator _mediator;
    private readonly IApAgentJobProgressService _progress;
    private readonly IWorkflowApAgentMoveNextService _agentValidation;
    private readonly AgentsChatOptions _agentsChat;
    private readonly ILogger<MjbUsAgentPipelineService> _logger;

    public MjbUsAgentPipelineService(
        IHttpClientFactory httpClientFactory,
        IWorkflowRepository repository,
        ITenantConnectionProvider connectionProvider,
        IMediator mediator,
        IApAgentJobProgressService progress,
        IWorkflowApAgentMoveNextService agentValidation,
        IOptions<AgentsChatOptions> agentsChat,
        ILogger<MjbUsAgentPipelineService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _repository = repository;
        _connectionProvider = connectionProvider;
        _mediator = mediator;
        _progress = progress;
        _agentValidation = agentValidation;
        _agentsChat = agentsChat.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(MjbUsAgentJobArgs args, string hangfireJobId, CancellationToken cancellationToken = default)
    {
        if (!MjbUsAgent.IsThisWorkflow(args.WorkflowId, args.TenantId))
            throw new InvalidOperationException("MJB_US agent job was queued for a different workflow.");

        var chatUrl = _agentsChat.ResolveChatUrl();
        if (string.IsNullOrWhiteSpace(chatUrl))
            throw new InvalidOperationException("Agents:ChatUrl is not configured.");

        var workflow = await _repository.GetByIdWithStepsAsync(args.WorkflowId, cancellationToken)
            ?? throw new InvalidOperationException("Workflow not found.");
        var steps = workflow.Steps.OrderBy(step => step.Order).ToList();
        var formId = string.IsNullOrWhiteSpace(args.FormId) ? workflow.FormId : args.FormId;
        if (string.IsNullOrWhiteSpace(formId))
            formId = MjbUsAgent.FormId.ToString("D");

        var activityId = args.ActivityId;
        var fields = MailFields(args);
        var current = steps.FirstOrDefault(step => MjbUsAgent.SameActivity(step, activityId));
        if (current != null && string.Equals(current.StageType, "START", StringComparison.OrdinalIgnoreCase))
        {
            activityId = await MoveByReviewAsync(
                args, steps, activityId, "Submit", fields, formId, "MJB mail", cancellationToken)
                ?? activityId;
        }

        await ReportAsync(hangfireJobId, "PROCESSING", $"{MjbUsAgent.ClassificationLabel} started", 15, cancellationToken);
        using var classificationDoc = await PostJsonAsync(chatUrl, BuildClassificationBody(args, activityId), cancellationToken);
        var classification = Unwrap(classificationDoc.RootElement);
        await SaveAgentJsonAsync(args, steps, activityId, classification, formId, cancellationToken);
        CopyAgentFields(classification, fields);
        var invoice = IsInvoice(classification);
        var ocrText = ReadString(classification, "ocr_text");
        var documentType = ReadNested(classification, "classification", "documentType") ?? "UNKNOWN";
        JsonElement ocr = default;
        var pythonFailed = false;

        if (!invoice || string.IsNullOrWhiteSpace(ocrText))
        {
            pythonFailed = true;
            if (invoice && string.IsNullOrWhiteSpace(ocrText))
            {
                MjbUsForm.Set(fields, MjbUsForm.ClassificationStatus, MjbUsForm.ClassificationStatusId, MjbUsForm.StatusFailed);
                MjbUsForm.Set(
                    fields,
                    MjbUsForm.ErrorCode,
                    MjbUsForm.ErrorCodeId,
                    "Classification did not return OCR text.");
            }

            MjbUsForm.Set(fields, MjbUsForm.RequestStatus, MjbUsForm.RequestStatusId, MjbUsForm.RequestForceClose);
            activityId = await MoveByReviewAsync(
                args, steps, activityId, "Failed", fields, formId, "MJB classification", cancellationToken)
                ?? activityId;
        }
        else
        {
            activityId = await MoveByReviewAsync(
                args, steps, activityId, "SUCCEEDED", fields, formId, "MJB classification", cancellationToken)
                ?? activityId;

            await ReportAsync(hangfireJobId, "PROCESSING", $"{MjbUsAgent.OcrLabel} started", 45, cancellationToken);
            using var ocrDoc = await PostJsonAsync(chatUrl, BuildOcrBody(args, activityId, ocrText, documentType), cancellationToken);
            ocr = Unwrap(ocrDoc.RootElement).Clone();
            await SaveAgentJsonAsync(args, steps, activityId, ocr, formId, cancellationToken);
            CopyAgentFields(ocr, fields);
            var ocrReview = IsSucceeded(ocr, "Extraction Status") ? "SUCCEEDED" : "Failed";
            if (!string.Equals(ocrReview, "SUCCEEDED", StringComparison.OrdinalIgnoreCase))
            {
                pythonFailed = true;
                MjbUsForm.Set(fields, MjbUsForm.RequestStatus, MjbUsForm.RequestStatusId, MjbUsForm.RequestForceClose);
            }

            activityId = await MoveByReviewAsync(
                args, steps, activityId, ocrReview, fields, formId, "MJB OCR", cancellationToken)
                ?? activityId;
        }

        await ReportAsync(hangfireJobId, "PROCESSING", $"{MjbUsAgent.FtpLabel} started", 75, cancellationToken);
        var connector = await EnsureFtpConnectorAsync(args.UserId, cancellationToken);
        using var ftpDoc = await PostJsonAsync(
            chatUrl,
            BuildFtpBody(args, activityId, ocr, documentType, connector),
            cancellationToken);
        var ftp = Unwrap(ftpDoc.RootElement);
        await SaveAgentJsonAsync(args, steps, activityId, ftp, formId, cancellationToken);
        CopyAgentFields(ftp, fields);
        var ftpStatus = MapFtpStatus(ReadString(ftp, "FTP status") ?? ReadString(ftp, "FTP Status"));
        MjbUsForm.Set(fields, MjbUsForm.FtpStatus, MjbUsForm.FtpStatusId, ftpStatus);
        var ftpError = ReadString(ftp, "ERROR") ?? ReadString(ftp, "ERROR CODE");
        if (!string.IsNullOrWhiteSpace(ftpError))
            MjbUsForm.Set(fields, MjbUsForm.ErrorCode, MjbUsForm.ErrorCodeId, ftpError);
        if (!pythonFailed && string.Equals(ftpStatus, MjbUsForm.FtpSuccess, StringComparison.OrdinalIgnoreCase))
            MjbUsForm.Set(fields, MjbUsForm.RequestStatus, MjbUsForm.RequestStatusId, MjbUsForm.RequestSuccess);

        await MoveByReviewAsync(args, steps, activityId, "SUCCESS", fields, formId, "MJB FTP", cancellationToken);
        await ReportAsync(hangfireJobId, "COMPLETED", $"{MjbUsAgent.FtpLabel} completed", 100, cancellationToken);
    }

    /// <summary>
    /// Uses the node's proceed action. The open step stays in inbox.
    /// Each finished step stays in sent so the stage is still visible after the ticket moves on.
    /// Workflow Success stays in completed.
    /// </summary>
    private async Task<string?> MoveByReviewAsync(
        MjbUsAgentJobArgs args,
        IReadOnlyList<WorkflowStep> steps,
        string activityId,
        string review,
        IReadOnlyDictionary<string, string> fields,
        string formId,
        string comments,
        CancellationToken cancellationToken)
    {
        var current = steps.FirstOrDefault(step => MjbUsAgent.SameActivity(step, activityId))
            ?? throw new InvalidOperationException($"MJB_US activity '{activityId}' was not found.");
        var action = WorkflowStepActionsHelper.ParseActions(current.ActionsJson)
            .FirstOrDefault(item => string.Equals(item.ProceedAction, review, StringComparison.OrdinalIgnoreCase));
        if (action == null || string.IsNullOrWhiteSpace(action.ProceedAction))
            throw new InvalidOperationException($"Node '{current.Name}' has no proceed action '{review}'.");

        var target = steps.FirstOrDefault(step => PointsAt(action.ToBlockId, step));
        var moved = await _mediator.Send(
            new MoveToNextStepCommand(
                args.InstanceId,
                activityId,
                Review: action.ProceedAction,
                Comments: comments,
                ActivityUserId: args.UserId,
                FormId: formId,
                FormDataFields: fields,
                EndWorkflow: false),
            cancellationToken);
        if (!moved.Success)
            throw new InvalidOperationException(moved.Message ?? "MJB_US move-next failed.");

        _logger.LogInformation(
            "MJB_US move-next from {FromStep} review {Review} opened {NextStep} in {Mailbox}.",
            current.Name,
            action.ProceedAction,
            moved.NextStepName,
            target != null && string.Equals(target.StageType, "END", StringComparison.OrdinalIgnoreCase)
                ? "completed"
                : "inbox");

        if (moved.WorkflowCompleted || target == null)
            return null;

        return MjbUsAgent.ActivityIdOf(target);
    }

    /// <summary>
    /// Stores this agent's JSON on workflow.agent_data_validation_{workflow8}.
    /// type is the step stage (CLASSIFICATION_AGENT, OCR, FTP_AGENT) so mailbox lists can return each one.
    /// </summary>
    private async Task SaveAgentJsonAsync(
        MjbUsAgentJobArgs args,
        IReadOnlyList<WorkflowStep> steps,
        string activityId,
        JsonElement agent,
        string formId,
        CancellationToken cancellationToken)
    {
        if (agent.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        var step = steps.FirstOrDefault(item => MjbUsAgent.SameActivity(item, activityId));
        if (step == null)
            return;

        var json = agent.GetRawText();
        if (string.IsNullOrWhiteSpace(json))
            return;

        await _agentValidation.SaveAgentValidationAsync(
            args.WorkflowId,
            args.InstanceId,
            step,
            args.UserId,
            new MoveToNextStepApAgentPayload(
                TransactionId: null,
                InstanceId: args.InstanceId,
                AiAgentResponseJson: json,
                AiAgentHtml: null,
                RepositoryItemId: null,
                RepositoryId: null,
                FormId: formId,
                FormEntryId: null),
            legacyTransactionId: null,
            cancellationToken);
    }

    private static bool PointsAt(string? blockId, WorkflowStep target)
    {
        if (string.IsNullOrWhiteSpace(blockId))
            return false;

        return string.Equals(blockId.Trim(), MjbUsAgent.ActivityIdOf(target), StringComparison.OrdinalIgnoreCase)
            || string.Equals(blockId.Trim(), target.Id.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || string.Equals(blockId.Trim(), target.Id.ToString("N"), StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> MailFields(MjbUsAgentJobArgs args)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MjbUsForm.Set(fields, MjbUsForm.EmailSubject, MjbUsForm.EmailSubjectId, args.Subject);
        MjbUsForm.Set(fields, MjbUsForm.FromEmail, MjbUsForm.FromEmailId, args.FromEmail);
        MjbUsForm.Set(fields, MjbUsForm.EmailReceivedAt, MjbUsForm.EmailReceivedAtId, args.ReceivedAt);
        MjbUsForm.Set(fields, MjbUsForm.ReceivedFilename, MjbUsForm.ReceivedFilenameId, args.FileName);
        MjbUsForm.Set(fields, MjbUsForm.MessageId, MjbUsForm.MessageIdId, args.MessageId);
        return fields;
    }

    private static void CopyAgentFields(JsonElement root, IDictionary<string, string> fields)
    {
        MjbUsForm.Set(fields, MjbUsForm.ClassificationStatus, MjbUsForm.ClassificationStatusId, ReadString(root, "Classification Status"));
        MjbUsForm.Set(fields, MjbUsForm.ClassificationCompleted, MjbUsForm.ClassificationCompletedId, ReadString(root, "Classification Completed"));
        MjbUsForm.Set(fields, MjbUsForm.ExtractionStatus, MjbUsForm.ExtractionStatusId, ReadString(root, "Extraction Status"));
        MjbUsForm.Set(fields, MjbUsForm.ExtractionCompleted, MjbUsForm.ExtractionCompletedId, ReadString(root, "Extraction Completed"));
        MjbUsForm.Set(
            fields,
            MjbUsForm.FtpStatus,
            MjbUsForm.FtpStatusId,
            MapFtpStatus(ReadString(root, "FTP status") ?? ReadString(root, "FTP Status")));
        MjbUsForm.Set(
            fields,
            MjbUsForm.ErrorCode,
            MjbUsForm.ErrorCodeId,
            ReadString(root, "ERROR CODE") ?? ReadString(root, "ERROR"));
    }

    private static bool IsInvoice(JsonElement root)
    {
        var status = ReadString(root, "Classification Status");
        var documentType = ReadNested(root, "classification", "documentType");
        if (string.Equals(status, MjbUsForm.StatusFailed, StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(documentType, "INVOICE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSucceeded(JsonElement root, string statusName) =>
        string.Equals(ReadString(root, statusName), MjbUsForm.StatusSucceeded, StringComparison.OrdinalIgnoreCase);

    private static string? MapFtpStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;

        var text = status.Trim();
        if (text.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)
            || text.Equals("SUCCEEDED", StringComparison.OrdinalIgnoreCase)
            || text.Equals(MjbUsForm.FtpSuccess, StringComparison.OrdinalIgnoreCase))
            return MjbUsForm.FtpSuccess;

        return MjbUsForm.FtpQueued;
    }

    private string BuildClassificationBody(MjbUsAgentJobArgs args, string activityId)
    {
        var payload = TrackingPayload(args, activityId);
        payload["agent"] = "CLASSIFICATION";
        payload["blobPath"] = args.BlobPath;
        payload["model"] = ClassificationModel;
        payload["pageno"] = "-1";
        return Wrap("classification", $"cls-{args.InstanceId:N}", payload);
    }

    private string BuildOcrBody(MjbUsAgentJobArgs args, string activityId, string ocrText, string documentType)
    {
        var payload = TrackingPayload(args, activityId);
        payload["agent"] = "OCR";
        payload["ocr_text"] = ocrText;
        payload["blobPath"] = args.BlobPath;
        payload["model"] = ClassificationModel;
        payload["documentType"] = documentType;
        return Wrap("ramco_ocr", $"ocr-{args.InstanceId:N}", payload);
    }

    private string BuildFtpBody(MjbUsAgentJobArgs args, string activityId, JsonElement ocr, string documentType, FtpConnector connector)
    {
        var payload = TrackingPayload(args, activityId);
        payload["agent"] = "FTP";
        payload["blobPath"] = args.BlobPath;
        payload["model"] = FtpModel;
        payload["documentType"] = documentType;
        payload["connectorName"] = connector.Name;
        payload["connectorId"] = connector.Id.ToString("D");
        payload["connector"] = new Dictionary<string, object?>
        {
            ["id"] = connector.Id.ToString("D"),
            ["name"] = connector.Name,
            ["providerCode"] = connector.ProviderCode,
            ["configJson"] = connector.ConfigJson
        };
        payload["ocr_json"] = BuildOcrJson(ocr);
        payload["remarks"] = new Dictionary<string, object?>
        {
            ["unique ref no"] = string.IsNullOrWhiteSpace(args.MessageId) ? args.InstanceId.ToString("N") : args.MessageId,
            ["From"] = args.FromEmail,
            ["Email_Subject"] = args.Subject,
            ["Received Filename"] = args.FileName
        };
        return Wrap("ftp", $"ftp-{args.InstanceId:N}", payload);
    }

    private static Dictionary<string, object?> TrackingPayload(MjbUsAgentJobArgs args, string activityId) =>
        new()
        {
            ["envType"] = EnvType,
            ["tenantId"] = args.TenantId.ToString("D"),
            ["workflowId"] = args.WorkflowId.ToString("D"),
            ["repositoryId"] = args.RepositoryId,
            ["instanceId"] = args.InstanceId.ToString("D"),
            ["activityId"] = activityId
        };

    private static string Wrap(string intent, string sessionId, Dictionary<string, object?> payload) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["intent"] = intent,
            ["payload"] = payload
        }, JsonOptions);

    private static Dictionary<string, object?> BuildOcrJson(JsonElement ocr)
    {
        if (!TryGet(ocr, "extraction", out var extraction))
            return new Dictionary<string, object?>();

        JsonElement? header = TryGet(extraction, "invoiceHeader", out var headerElement) ? headerElement.Clone() : null;
        JsonElement? lines = TryGet(extraction, "lineItems", out var lineElement) ? lineElement.Clone() : null;
        return new Dictionary<string, object?>
        {
            ["invoice_header"] = header,
            ["line_items"] = lines
        };
    }

    private async Task<FtpConnector> EnsureFtpConnectorAsync(Guid userId, CancellationToken cancellationToken)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string findSql = """
            SELECT "Id", "Name", "ProviderCode", "ConfigJson"
            FROM dbo."connector"
            WHERE "IsDeleted" = false
              AND LOWER(BTRIM("Name")) = 'ftp'
            LIMIT 1;
            """;
        await using (var find = new NpgsqlCommand(findSql, connection))
        {
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return new FtpConnector(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3));
            }
        }

        var id = Guid.NewGuid();
        var createdBy = userId == Guid.Empty ? EmailIngestActorResolver.SystemUserId : userId;
        const string config = """{"host":"","port":22,"username":"","password":"","remotePath":"/","note":"Placeholder FTP connector created for MJB_US. Replace with the live FTP settings."}""";
        const string insertSql = """
            INSERT INTO dbo."connector"
                ("Id", "Name", "ProviderCode", "ConfigJson", "OAuthStatus", "IsDefault", "CreatedAtUtc", "CreatedBy", "IsDeleted")
            VALUES
                (@Id, @Name, 'FTP', @ConfigJson, 'Pending', false, now(), @CreatedBy, false);
            """;
        await using var insert = new NpgsqlCommand(insertSql, connection);
        insert.Parameters.AddWithValue("@Id", id);
        insert.Parameters.AddWithValue("@Name", ConnectorName);
        insert.Parameters.AddWithValue("@ConfigJson", config);
        insert.Parameters.AddWithValue("@CreatedBy", createdBy);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Inserted placeholder FTP connector {ConnectorId} for MJB_US.", id);
        return new FtpConnector(id, ConnectorName, "FTP", config);
    }

    private async Task<JsonDocument> PostJsonAsync(string chatUrl, string body, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(MjbUsAgentPipelineService));
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(chatUrl, content, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = responseText.Length > 500 ? responseText[..500] : responseText;
            throw new InvalidOperationException($"MJB agent chat returned {(int)response.StatusCode}: {detail}");
        }

        if (string.IsNullOrWhiteSpace(responseText))
            throw new InvalidOperationException("MJB agent chat returned an empty body.");

        return JsonDocument.Parse(responseText);
    }

    private async Task ReportAsync(
        string jobId,
        string stage,
        string message,
        int percent,
        CancellationToken cancellationToken)
    {
        await _progress.UpdateProgressAsync(
            jobId,
            new ApAgentJobProgressUpdate(stage, message, percent),
            cancellationToken);
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return root;

        if (HasFormStatus(root))
            return root;

        JsonElement? fallback = null;
        foreach (var name in new[]
        {
            "classification_result", "ramco_ocr_result", "ftp_result",
            "payload", "result", "data", "output"
        })
        {
            if (!TryGet(root, name, out var child) || child.ValueKind != JsonValueKind.Object)
                continue;

            if (HasFormStatus(child))
                return child;

            fallback ??= child;
        }

        return fallback ?? root;
    }

    private static bool HasFormStatus(JsonElement element) =>
        HasAny(element, "Classification Status", "Extraction Status", "FTP status", "FTP Status");

    private static bool HasAny(JsonElement element, params string[] names) =>
        names.Any(name => TryGet(element, name, out _));

    private static string? ReadNested(JsonElement element, string objectName, string propertyName)
    {
        if (!TryGet(element, objectName, out var child))
            return null;
        return ReadString(child, propertyName);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            return true;
        }

        return false;
    }

    private sealed record FtpConnector(Guid Id, string Name, string? ProviderCode, string? ConfigJson);
}
