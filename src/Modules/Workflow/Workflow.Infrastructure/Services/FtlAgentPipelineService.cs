using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Storage.Blobs;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.SharedKernel.Options;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Domain.Entities;
using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Application.Workflows.Commands.MoveToNextStep;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// FTL qualifier (file), quote estimator (RFQ file + qualifier payload), and document PDF.
/// Does not call the AP Agent pipeline.
/// </summary>
public sealed class FtlAgentPipelineService : IFtlAgentPipelineService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWorkflowRepository _repository;
    private readonly IDynamicTableRepository _attachments;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IWorkflowLegacyMailboxSyncService _mailbox;
    private readonly IWorkflowJsonStorageService _workflowJsonStorage;
    private readonly IWorkflowAttachmentArchiveService _attachmentArchive;
    private readonly IWorkflowApAgentMoveNextService _moveNextSupport;
    private readonly IMediator _mediator;
    private readonly IFormJsonStorageService _formJsonStorage;
    private readonly IConfiguration _configuration;
    private readonly IOptions<ApAgentOptions> _apAgentOptions;
    private readonly AgentsChatOptions _agentsChat;
    private readonly ILogger<FtlAgentPipelineService> _logger;

    public FtlAgentPipelineService(
        IHttpClientFactory httpClientFactory,
        IWorkflowRepository repository,
        IDynamicTableRepository attachments,
        ITenantConnectionProvider connectionProvider,
        IWorkflowLegacyMailboxSyncService mailbox,
        IWorkflowJsonStorageService workflowJsonStorage,
        IWorkflowAttachmentArchiveService attachmentArchive,
        IWorkflowApAgentMoveNextService moveNextSupport,
        IMediator mediator,
        IFormJsonStorageService formJsonStorage,
        IConfiguration configuration,
        IOptions<ApAgentOptions> apAgentOptions,
        IOptions<AgentsChatOptions> agentsChat,
        ILogger<FtlAgentPipelineService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _repository = repository;
        _attachments = attachments;
        _connectionProvider = connectionProvider;
        _mailbox = mailbox;
        _workflowJsonStorage = workflowJsonStorage;
        _attachmentArchive = attachmentArchive;
        _moveNextSupport = moveNextSupport;
        _mediator = mediator;
        _formJsonStorage = formJsonStorage;
        _configuration = configuration;
        _apAgentOptions = apAgentOptions;
        _agentsChat = agentsChat.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(FtlAgentJobArgs args, string hangfireJobId, CancellationToken cancellationToken = default)
    {
        if (string.Equals(args.Mode, FtlAgentStepDetector.Document, StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteDocumentAsync(args, hangfireJobId, cancellationToken);
            return;
        }

        var chatUrl = _agentsChat.ResolveChatUrl();
        if (string.IsNullOrWhiteSpace(chatUrl))
            throw new InvalidOperationException("Agents:ChatUrl is not configured.");

        var workflow = await _repository.GetByIdWithStepsAsync(args.WorkflowId, cancellationToken)
            ?? throw new InvalidOperationException("Workflow not found.");
        var step = workflow.Steps.FirstOrDefault(s =>
            string.Equals(s.ActivityId, args.ActivityId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Id.ToString("D"), args.ActivityId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"FTL step {args.ActivityId} was not found.");

        var responseJson = args.Mode switch
        {
            FtlAgentStepDetector.Qualifier => await PostQualifierAsync(args, hangfireJobId, chatUrl, cancellationToken),
            FtlAgentStepDetector.Quote => await PostQuoteAsync(args, hangfireJobId, chatUrl, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown FTL mode '{args.Mode}'.")
        };

        var storedJson = PreferLongestQuoteLineItems(StripPdf(responseJson));
        EnsureAgentOutput(args.Mode, storedJson);
        if (string.Equals(args.Mode, FtlAgentStepDetector.Quote, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "FTL quote agent Line Item rows for instance {InstanceId}: {Count}",
                args.InstanceId,
                CountJsonArrayRows(ExtractQuoteLineItems(storedJson)));
        }

        var formId = args.FormId ?? workflow.FormId;
        var identity = await _mailbox.TryGetProcessFormIdentityAsync(args.WorkflowId, args.InstanceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(formId))
            formId = identity?.FormId;
        var formEntryId = identity?.FormEntryId;
        var attachment = (await _attachments.GetAttachmentsAsync(args.WorkflowId, args.InstanceId, cancellationToken))
            .FirstOrDefault(r => r.ItemId is { } item && item != Guid.Empty);
        var repositoryId = ParseGuid(args.RepositoryId) ?? attachment?.RepositoryId ?? ParseGuid(workflow.RepositoryId);
        var isQuote = string.Equals(args.Mode, FtlAgentStepDetector.Quote, StringComparison.OrdinalIgnoreCase);
        var fields = isQuote
            ? ExtractQuoteFormFields(storedJson)
            : ExtractFormFields(storedJson);
        var lineItemsJson = isQuote ? ExtractQuoteLineItems(storedJson) : null;
        var mapped = await RemapFieldsToJsonIdsAsync(formId, fields, lineItemsJson, cancellationToken);
        if (isQuote)
        {
            var existingFormData = await LoadExistingFormDataAsync(args.WorkflowId, args.InstanceId, cancellationToken);
            var protectedIds = NonEmptyFormKeys(existingFormData);
            var lineItemIds = mapped.TableColumns
                .Where(column => NormalizeFieldKey(column.Label) is "lineitem" or "lineitems")
                .Select(column => column.JsonId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in lineItemIds)
                protectedIds.Remove(id);

            mapped = mapped with
            {
                FormDataFields = MergeQuoteFormData(existingFormData, mapped.FormDataFields, lineItemIds),
                TableFields = KeepAddedFields(mapped.TableFields, mapped.FormDataFields, protectedIds),
                TableColumns = mapped.TableColumns.Where(column => !protectedIds.Contains(column.JsonId)).ToList()
            };
        }

        if (!string.IsNullOrWhiteSpace(formId)
            && formEntryId is { } entryId && entryId != Guid.Empty
            && repositoryId is { } repoId && repoId != Guid.Empty
            && attachment?.ItemId is { } itemId
            && mapped.TableFields.Count > 0)
        {
            await _moveNextSupport.ApplyMetadataAsync(
                args.TenantId,
                new ApAgentMetadataApplyRequest(
                    args.WorkflowId,
                    args.InstanceId,
                    formId,
                    entryId,
                    repoId,
                    itemId,
                    mapped.TableFields,
                    LineItemsJson: null),
                args.UserId,
                cancellationToken);
        }

        if (string.Equals(args.Mode, FtlAgentStepDetector.Qualifier, StringComparison.OrdinalIgnoreCase)
            || string.Equals(args.Mode, FtlAgentStepDetector.Quote, StringComparison.OrdinalIgnoreCase))
        {
            await _moveNextSupport.SaveAgentValidationAsync(
                args.WorkflowId,
                args.InstanceId,
                step,
                args.UserId,
                new MoveToNextStepApAgentPayload(
                    TransactionId: null,
                    InstanceId: args.InstanceId,
                    AiAgentResponseJson: storedJson,
                    AiAgentHtml: null,
                    RepositoryItemId: attachment?.ItemId,
                    RepositoryId: repositoryId,
                    FormId: formId,
                    FormEntryId: formEntryId),
                legacyTransactionId: null,
                cancellationToken);
        }

        var review = args.Mode == FtlAgentStepDetector.Qualifier
            ? ResolveQualifyReview(storedJson, step)
            : "Submit";

        // Move-next ApplyFormDataToEzfb writes FormDataFields by jsonId, and FormLineItemsJson
        // from quote_result["Line Item"] onto ezfb_*_items.lineitem.
        var moved = await _mediator.Send(
            new MoveToNextStepCommand(
                args.InstanceId,
                args.ActivityId,
                Review: review,
                Comments: $"FTL {args.Mode}",
                ActivityUserId: args.UserId,
                FormId: formId,
                FormEntryId: formEntryId,
                FormDataFields: mapped.FormDataFields,
                FormLineItemsJson: lineItemsJson,
                SubmittedFormDataJson: BuildFormFieldsJson(mapped.FormDataFields, null)),
            cancellationToken);

        if (!moved.Success)
            throw new InvalidOperationException(moved.Message ?? "FTL move-next failed.");

        if (!string.IsNullOrWhiteSpace(formId) && formEntryId is { } savedEntry && savedEntry != Guid.Empty)
            await WriteFormTableColumnsAsync(formId, savedEntry, mapped.TableColumns, cancellationToken);

        await SyncLineItemsToMailboxAsync(args.WorkflowId, args.InstanceId, mapped.TableColumns, cancellationToken);

        _logger.LogInformation(
            "FTL {Mode} moved instance {InstanceId} with review {Review} to {NextStep}.",
            args.Mode,
            args.InstanceId,
            review,
            moved.NextStepName);
    }

    private async Task ExecuteDocumentAsync(FtlAgentJobArgs args, string jobId, CancellationToken cancellationToken)
    {
        await GenerateDocumentAsync(args, cancellationToken);

        var identity = await _mailbox.TryGetProcessFormIdentityAsync(args.WorkflowId, args.InstanceId, cancellationToken);
        var formData = await LoadExistingFormDataAsync(args.WorkflowId, args.InstanceId, cancellationToken);
        var moved = await _mediator.Send(
            new MoveToNextStepCommand(
                args.InstanceId,
                args.ActivityId,
                Review: "Generated",
                Comments: "FTL document",
                ActivityUserId: args.UserId,
                FormId: args.FormId,
                FormEntryId: identity?.FormEntryId,
                SubmittedFormDataJson: formData),
            cancellationToken);

        if (!moved.Success)
            throw new InvalidOperationException(moved.Message ?? "FTL document move-next failed.");

        _logger.LogInformation(
            "FTL document archived {FileName} for instance {InstanceId} and moved to {NextStep}.",
            moved.GeneratedPdfFileName,
            args.InstanceId,
            moved.NextStepName);
    }

    public async Task<FtlDocumentGenerateResult> GenerateDocumentAsync(
        FtlAgentJobArgs args,
        CancellationToken cancellationToken = default)
    {
        var jobId = args.InstanceId.ToString("N");
        var chatUrl = _agentsChat.ResolveChatUrl();
        if (string.IsNullOrWhiteSpace(chatUrl))
            throw new InvalidOperationException("Agents:ChatUrl is not configured.");

        var workflow = await _repository.GetByIdWithStepsAsync(args.WorkflowId, cancellationToken)
            ?? throw new InvalidOperationException("Workflow not found.");
        var step = workflow.Steps.FirstOrDefault(s =>
            string.Equals(s.ActivityId, args.ActivityId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Id.ToString("D"), args.ActivityId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"FTL step {args.ActivityId} was not found.");

        var formId = args.FormId ?? workflow.FormId;
        var identity = await _mailbox.TryGetProcessFormIdentityAsync(args.WorkflowId, args.InstanceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(formId))
            formId = identity?.FormId;

        var formData = await LoadExistingFormDataAsync(args.WorkflowId, args.InstanceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(formData))
            throw new InvalidOperationException("FTL document did not find form data for this instance.");

        var pythonFormData = await RemapFormDataKeysToColumnNamesAsync(formId, formData, cancellationToken);
        var template = await LoadPdfTemplateAsync(args.WorkflowId, args.ActivityId, cancellationToken);
        var (responseJson, pythonRequest) = await PostDocumentAsync(jobId, chatUrl, pythonFormData, template, cancellationToken);
        var (pdfBytes, fileName) = ReadGeneratedPdf(responseJson);
        var repositoryId = ParseGuid(args.RepositoryId) ?? ParseGuid(workflow.RepositoryId)
            ?? throw new InvalidOperationException("Workflow repositoryId is not configured for the document PDF.");

        await using var pdfStream = new MemoryStream(pdfBytes);
        var archived = await _attachmentArchive.UploadAsync(
            args.TenantId,
            args.WorkflowId,
            args.InstanceId,
            repositoryId,
            pdfStream,
            fileName,
            "application/pdf",
            pdfBytes.Length,
            metadataJson: null,
            transactionId: null,
            args.UserId,
            cancellationToken,
            allowIncompleteFolderMetadata: true);

        await _moveNextSupport.SaveAgentValidationAsync(
            args.WorkflowId,
            args.InstanceId,
            step,
            args.UserId,
            new MoveToNextStepApAgentPayload(
                TransactionId: null,
                InstanceId: args.InstanceId,
                AiAgentResponseJson: StripPdf(responseJson),
                AiAgentHtml: null,
                RepositoryItemId: archived.ItemId,
                RepositoryId: repositoryId,
                FormId: formId,
                FormEntryId: identity?.FormEntryId),
            legacyTransactionId: null,
            cancellationToken);

        _logger.LogInformation(
            "FTL document archived {FileName} for instance {InstanceId}.",
            fileName,
            args.InstanceId);

        return new FtlDocumentGenerateResult(
            archived.AttachmentId,
            archived.ItemId,
            fileName,
            pythonRequest);
    }

    public async Task<FtlDocumentPreviewResult> PreviewDocumentAsync(
        string? formId,
        string formDataJson,
        JsonElement templateJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formDataJson))
            throw new InvalidOperationException("formData is required.");
        if (templateJson.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new InvalidOperationException("templateJson is required.");

        var chatUrl = _agentsChat.ResolveChatUrl();
        if (string.IsNullOrWhiteSpace(chatUrl))
            throw new InvalidOperationException("Agents:ChatUrl is not configured.");

        if (string.IsNullOrWhiteSpace(formId))
            formId = await ResolveFormIdFromFormDataAsync(formDataJson, cancellationToken);

        var namedFormData = await RemapFormDataKeysToColumnNamesAsync(formId, formDataJson, cancellationToken);
        var template = CloneTemplate(templateJson);
        var sessionId = Guid.NewGuid().ToString("N");
        var (responseJson, _) = await PostDocumentAsync(sessionId, chatUrl, namedFormData, template, cancellationToken);
        var (pdfBytes, fileName) = ReadGeneratedPdf(responseJson);
        return new FtlDocumentPreviewResult(fileName, Convert.ToBase64String(pdfBytes), namedFormData);
    }

    private async Task<string?> ResolveFormIdFromFormDataAsync(string formDataJson, CancellationToken cancellationToken)
    {
        string? jsonId = null;
        try
        {
            using var doc = JsonDocument.Parse(formDataJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!string.IsNullOrWhiteSpace(prop.Name))
                    {
                        jsonId = prop.Name.Trim();
                        break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(jsonId))
            return null;

        var tenantCs = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(tenantCs))
            return null;

        await using var connection = new NpgsqlConnection(tenantCs);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT "wFormId"
            FROM dbo."wFormControl"
            WHERE "isDeleted" = false AND "jsonId" = @JsonId
            LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@JsonId", jsonId);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }

    private async Task<JsonElement> LoadPdfTemplateAsync(
        Guid workflowId,
        string activityId,
        CancellationToken cancellationToken)
    {
        var json = await _workflowJsonStorage.GetWorkflowJsonAsync(workflowId, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Workflow JSON was not found for the document template.");

        using var doc = JsonDocument.Parse(json);
        if (!TryGetPropertyIgnoreCase(doc.RootElement, "blocks", out var blocks)
            || blocks.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Workflow JSON has no blocks.");

        foreach (var block in blocks.EnumerateArray())
        {
            if (!TryGetPropertyIgnoreCase(block, "id", out var idEl)
                || idEl.ValueKind != JsonValueKind.String
                || !string.Equals(idEl.GetString(), activityId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryGetPropertyIgnoreCase(block, "settings", out var settings)
                || settings.ValueKind != JsonValueKind.Object)
                break;

            if (TryGetPropertyIgnoreCase(settings, "templateJson", out var templateJson)
                && templateJson.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return CloneTemplate(templateJson);

            if (TryGetPropertyIgnoreCase(settings, "pdfTemplate", out var pdfTemplate)
                && pdfTemplate.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return CloneTemplate(pdfTemplate);

            break;
        }

        throw new InvalidOperationException(
            $"templateJson is missing on the document node {activityId}.");
    }

    private static JsonElement CloneTemplate(JsonElement template)
    {
        if (template.ValueKind != JsonValueKind.String)
            return template.Clone();

        var text = template.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return template.Clone();

        var trimmed = text.Trim();
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            || (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            try
            {
                using var parsed = JsonDocument.Parse(trimmed);
                return parsed.RootElement.Clone();
            }
            catch (JsonException)
            {
            }
        }

        return template.Clone();
    }

    private async Task<(string ResponseJson, WorkflowPdfPythonRequestDto Request)> PostDocumentAsync(
        string jobId,
        string chatUrl,
        string formDataJson,
        JsonElement template,
        CancellationToken cancellationToken)
    {
        object? formData = formDataJson;
        try
        {
            using var parsed = JsonDocument.Parse(formDataJson);
            formData = JsonSerializer.Deserialize<object>(parsed.RootElement.GetRawText());
        }
        catch (JsonException)
        {
        }

        var templateNode = JsonSerializer.Deserialize<object>(template.GetRawText());
        var payload = new Dictionary<string, object?>
        {
            ["formData"] = formData,
            ["templateJson"] = templateNode
        };

        var body = JsonSerializer.Serialize(new
        {
            session_id = jobId,
            intent = "ftl_quote_estimator",
            payload
        });
        _logger.LogInformation(
            "FTL document Python chat input session {SessionId}. formData={FormData}",
            jobId,
            formDataJson);
        _logger.LogInformation(
            "FTL document Python chat request body session {SessionId}: {RequestBody}",
            jobId,
            body);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var responseJson = await PostAsync(chatUrl, content, cancellationToken);
        using var templateDoc = JsonDocument.Parse(template.GetRawText());
        var request = new WorkflowPdfPythonRequestDto(
            FlattenFormData(formDataJson),
            "quote.pdf",
            new Dictionary<string, string>
            {
                ["session_id"] = jobId,
                ["intent"] = "ftl_quote_estimator"
            },
            templateDoc.RootElement.Clone());
        return (responseJson, request);
    }

    private static IReadOnlyDictionary<string, string> FlattenFormData(string formDataJson)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(formDataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                fields["formData"] = formDataJson;
                return fields;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                fields[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.GetRawText();
            }
        }
        catch (JsonException)
        {
            fields["formData"] = formDataJson;
        }

        return fields;
    }

    private static (byte[] Bytes, string FileName) ReadGeneratedPdf(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var pdf = FindProperty(doc.RootElement, "pdf_base64");
        if (pdf.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pdf.GetString()))
            throw new InvalidOperationException("FTL document response did not include pdf_base64.");

        var raw = pdf.GetString()!.Trim();
        var comma = raw.IndexOf(',');
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            raw = raw[(comma + 1)..];

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(raw);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("FTL document pdf_base64 is not valid base64.", ex);
        }

        var name = FindProperty(doc.RootElement, "pdf_filename");
        var fileName = name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            var estimate = FindProperty(doc.RootElement, "estimate_number");
            fileName = estimate.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(estimate.GetString())
                ? estimate.GetString() + ".pdf"
                : "quote.pdf";
        }

        fileName = Path.GetFileName(fileName.Trim());
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            fileName += ".pdf";
        return (bytes, fileName);
    }

    private async Task<string> PostQualifierAsync(
        FtlAgentJobArgs args,
        string jobId,
        string chatUrl,
        CancellationToken cancellationToken)
    {
        var file = await ReadFirstAttachmentAsync(args, cancellationToken);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(jobId), "session_id");
        form.Add(new StringContent("ftl_qualifier"), "intent");
        AddTrackingFields(form, args, jobId);
        var fileContent = new ByteArrayContent(file.Bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
        form.Add(fileContent, "file", file.FileName);
        return await PostAsync(chatUrl, form, cancellationToken);
    }

    private async Task<string> PostQuoteAsync(
        FtlAgentJobArgs args,
        string jobId,
        string chatUrl,
        CancellationToken cancellationToken)
    {
        // Agents ftl_quote_estimator prices the RFQ .eml/PDF file. JSON-only calls omit the
        // attachment and often return quote_result["Line Item"]: [] (freight/tax only).
        var file = await ReadFirstAttachmentAsync(args, cancellationToken);
        var prior = await LoadLatestAgentResponseAsync(
            args.WorkflowId,
            args.InstanceId,
            cancellationToken,
            "QUALIFY_AGENT");
        using var priorDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(prior) ? "{}" : prior);
        var qualifier = FindRootOrNestedObject(priorDoc.RootElement, "qualifier_result");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(jobId), "session_id");
        form.Add(new StringContent("ftl_quote_estimator"), "intent");
        form.Add(new StringContent("inflow"), "template_type");
        AddTrackingFields(form, args, jobId);
        if (qualifier.ValueKind == JsonValueKind.Object)
        {
            form.Add(
                new StringContent(qualifier.GetRawText(), Encoding.UTF8, "application/json"),
                "qualifier_result");
        }

        var fileContent = new ByteArrayContent(file.Bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
        form.Add(fileContent, "file", file.FileName);
        return await PostAsync(chatUrl, form, cancellationToken);
    }

    private async Task<string> PostAsync(string chatUrl, HttpContent content, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(FtlAgentPipelineService));
        using var response = await client.PostAsync(chatUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"FTL /chat returned {(int)response.StatusCode}: {Truncate(body, 500)}");
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("FTL /chat returned an empty body.");
        return body;
    }

    private void AddTrackingFields(MultipartFormDataContent form, FtlAgentJobArgs args, string jobId)
    {
        form.Add(new StringContent(args.WorkflowId.ToString("D")), "workflowId");
        form.Add(new StringContent(args.TenantId.ToString("D")), "tenantId");
        form.Add(new StringContent(args.TenantId.ToString("D")), "tenant_id");
        form.Add(new StringContent(args.InstanceId.ToString("D")), "instanceId");
        form.Add(new StringContent(args.ActivityId), "activityId");
        if (!string.IsNullOrWhiteSpace(args.RepositoryId))
            form.Add(new StringContent(args.RepositoryId), "repositoryId");
        if (!string.IsNullOrWhiteSpace(args.FormId))
            form.Add(new StringContent(args.FormId), "formId");
        form.Add(new StringContent(jobId), "apAgentJobId");
        var status = StatusUrl(jobId);
        if (!string.IsNullOrWhiteSpace(status))
            form.Add(new StringContent(status), "apAgentJobStatusUrl");
        var progress = ProgressUrl(args);
        if (!string.IsNullOrWhiteSpace(progress))
            form.Add(new StringContent(progress), "apAgentProgressUrl");
    }

    private string? StatusUrl(string jobId)
    {
        var baseUrl = _apAgentOptions.Value.ApiBaseUrl?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl}/ap-agent/jobs/{jobId}";
    }

    private string? ProgressUrl(FtlAgentJobArgs args)
    {
        var baseUrl = _apAgentOptions.Value.ApiBaseUrl?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : $"{baseUrl}/{args.WorkflowId:D}/instances/{args.InstanceId:D}/ap-agent/progress";
    }

    private async Task<(byte[] Bytes, string FileName, string? ContentType)> ReadFirstAttachmentAsync(
        FtlAgentJobArgs args,
        CancellationToken cancellationToken)
    {
        var rows = await _attachments.GetAttachmentsAsync(args.WorkflowId, args.InstanceId, cancellationToken);
        var row = rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.FilePath))
            ?? throw new InvalidOperationException("No archived RFQ file found for the FTL agent.");

        var connectionString = _configuration["EzofisBlobStorage:ConnectionString"]
            ?? _configuration["WorkflowJsonStorage:Blob:ConnectionString"]
            ?? _configuration["WorkflowJsonStorage:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Blob connection string is not configured.");

        var prefix = (_configuration["EzofisBlobStorage:ContainerPrefix"]
            ?? _configuration["WorkflowJsonStorage:Blob:ContainerPrefix"]
            ?? "ezts").ToLowerInvariant();
        var container = new BlobServiceClient(connectionString)
            .GetBlobContainerClient($"{prefix}{args.TenantId:N}");
        var blob = container.GetBlobClient(row.FilePath!.Trim().Replace('\\', '/'));
        var download = await blob.DownloadContentAsync(cancellationToken);
        return (download.Value.Content.ToArray(), row.FileName ?? "upload.bin", row.ContentType);
    }

    private async Task<string?> LoadLatestAgentResponseAsync(
        Guid workflowId,
        Guid instanceId,
        CancellationToken cancellationToken,
        string? agentType = null)
    {
        var suffix = workflowId.ToString("N")[..8];
        var table = $"workflow.agent_data_validation_{suffix}";
        var typeFilter = string.IsNullOrWhiteSpace(agentType)
            ? string.Empty
            : """AND UPPER(TRIM(COALESCE(type, ''))) = @AgentType""";
        var sql = $"""
SELECT agent_response
FROM {table}
WHERE process_id = @InstanceId AND is_deleted = false
{typeFilter}
ORDER BY created_at DESC
LIMIT 1;
""";
        var tenantCs = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection is not set.");
        await using var connection = new NpgsqlConnection(tenantCs);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@InstanceId", instanceId);
        if (!string.IsNullOrWhiteSpace(agentType))
            cmd.Parameters.AddWithValue("@AgentType", agentType.Trim().ToUpperInvariant());
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value == null || value == DBNull.Value ? null : Convert.ToString(value);
    }

    private static string? BuildFormFieldsJson(
        IReadOnlyDictionary<string, string> fields,
        string? lineItemsJson)
    {
        var map = new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(lineItemsJson) && !map.Keys.Any(k => k.Contains("item", StringComparison.OrdinalIgnoreCase)))
            map["line_items"] = lineItemsJson;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in map)
            {
                writer.WritePropertyName(key);
                WriteFormDataValue(writer, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteFormDataValue(Utf8JsonWriter writer, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.WriteStringValue(string.Empty);
            return;
        }

        var trimmed = value.Trim();
        if ((trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            || (trimmed.StartsWith('{') && trimmed.EndsWith('}')))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                doc.RootElement.WriteTo(writer);
                return;
            }
            catch (JsonException)
            {
            }
        }

        writer.WriteStringValue(value);
    }

    /// <summary>
    /// Quote Line Item rows come only from quote_result. Never use qualifier matched_items.
    /// Prefers the longest Line Item array in case nested quote_result copies are empty.
    /// </summary>
    private static string? ExtractQuoteLineItems(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var lineItems = FindLongestNamedArray(doc.RootElement, "line_items", "Line Item", "Line Items");
        if (lineItems.ValueKind == JsonValueKind.Array && lineItems.GetArrayLength() > 0)
            return lineItems.GetRawText();
        return null;
    }

    private static Guid? ParseGuid(string? value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static void EnsureAgentOutput(string mode, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var resultName = string.Equals(mode, FtlAgentStepDetector.Qualifier, StringComparison.OrdinalIgnoreCase)
            ? "qualifier_result"
            : "quote_result";
        var result = FindRootOrNestedObject(doc.RootElement, resultName);
        if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"FTL {mode} response did not include {resultName}. The ticket was not moved.");
        }
    }

    private static string ResolveQualifyReview(string json, WorkflowStep step)
    {
        var decision = ReadQualifyDecision(json);
        var matched = WorkflowStepActionsHelper.FindMatchingAction(step, decision);
        if (!string.IsNullOrWhiteSpace(matched?.ProceedAction))
            return matched.ProceedAction!;

        foreach (var action in WorkflowStepActionsHelper.ParseActions(step.ActionsJson))
        {
            var label = action.ProceedAction;
            if (string.IsNullOrWhiteSpace(label))
                continue;
            var isDisqualify = label.Contains("disqual", StringComparison.OrdinalIgnoreCase);
            if (decision == "disqualify" && isDisqualify)
                return label;
            if (decision == "qualify"
                && label.Contains("qualif", StringComparison.OrdinalIgnoreCase)
                && !isDisqualify)
                return label;
        }

        return decision;
    }

    private static string ReadQualifyDecision(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = FindProperty(doc.RootElement, "qualifier_result");
        if (result.ValueKind != JsonValueKind.Object)
            return "qualify";

        foreach (var prop in result.EnumerateObject())
        {
            if (!prop.Name.Equals("qualify", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()
                : prop.Value.GetRawText();
            if (!string.IsNullOrWhiteSpace(value)
                && value.Contains("disqual", StringComparison.OrdinalIgnoreCase))
                return "disqualify";
            return "qualify";
        }

        return "qualify";
    }

    private async Task<FtlMappedForm> RemapFieldsToJsonIdsAsync(
        string? formId,
        Dictionary<string, string> fields,
        string? lineItemsJson,
        CancellationToken cancellationToken)
    {
        var empty = new FtlMappedForm(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            []);
        if (string.IsNullOrWhiteSpace(formId) || (fields.Count == 0 && string.IsNullOrWhiteSpace(lineItemsJson)))
            return empty;

        var tenantCs = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(tenantCs))
            return empty;

        var controls = await LoadFtlFormControlsAsync(formId, tenantCs, cancellationToken);
        if (controls.Count == 0)
            return empty;

        var roots = controls.Where(c => c.ParentId == 0).ToList();
        var tableFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var formDataFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tableColumns = new List<FtlTableColumn>();
        // Quote remap passes lineItemsJson. Never map qualifier matched_items onto the form.
        var ignoreMatchedItems = !string.IsNullOrWhiteSpace(lineItemsJson);
        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
                continue;
            if (ignoreMatchedItems && IsMatchedItemsField(key))
                continue;

            var control = ResolveQualifierControl(key, roots);
            if (control is null)
            {
                _logger.LogInformation(
                    "FTL field {Field} was not written. Form {FormId} has no control with that name.",
                    key,
                    formId);
                continue;
            }

            AddMappedControl(control, value, controls, tableFields, formDataFields, tableColumns);
        }

        var lineItemTable = roots.FirstOrDefault(c =>
            c.Type.Contains("TABLE", StringComparison.OrdinalIgnoreCase)
            && NormalizeFieldKey(c.Label) is "lineitem" or "lineitems");
        if (lineItemTable is not null
            && !string.IsNullOrWhiteSpace(lineItemsJson)
            && !HasLongerOrEqualTable(formDataFields, lineItemTable.JsonId, lineItemsJson))
            AddMappedControl(lineItemTable, lineItemsJson, controls, tableFields, formDataFields, tableColumns);

        foreach (var root in roots)
        {
            if (formDataFields.ContainsKey(root.JsonId))
                continue;

            formDataFields[root.JsonId] = root.Type.Contains("TABLE", StringComparison.OrdinalIgnoreCase)
                ? "[]"
                : string.Empty;
        }

        return new FtlMappedForm(tableFields, formDataFields, tableColumns);
    }

    private static void AddMappedControl(
        FtlFormControl control,
        string raw,
        IReadOnlyList<FtlFormControl> controls,
        Dictionary<string, string> tableFields,
        Dictionary<string, string> formDataFields,
        List<FtlTableColumn> tableColumns)
    {
        var isTable = control.Type.Contains("TABLE", StringComparison.OrdinalIgnoreCase);
        if (isTable)
        {
            var lineItemTable = NormalizeFieldKey(control.Label) is "lineitem" or "lineitems";
            var tableJson = FormatQualifierTable(raw, control, controls, lineItemTable, useJsonId: true);
            if (string.IsNullOrWhiteSpace(tableJson))
                return;

            if (formDataFields.TryGetValue(control.JsonId, out var existing)
                && CountJsonArrayRows(existing) >= CountJsonArrayRows(tableJson))
                return;

            // Inbox form_data binds by child jsonId. The ezfb form table binds by child name
            // (Product, Qty, Price), the same way Matched Items is stored.
            var ezfbJson = RemapTableKeysToControlNames(tableJson, control, controls) ?? tableJson;
            tableColumns.RemoveAll(column =>
                string.Equals(column.JsonId, control.JsonId, StringComparison.OrdinalIgnoreCase));
            tableColumns.Add(new FtlTableColumn(control.ColumnName, control.Label, control.JsonId, tableJson, ezfbJson));
            formDataFields[control.JsonId] = tableJson;
            return;
        }

        var text = TryJoinPrimitiveArray(raw, out var joined) ? joined : raw;
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
            return;

        tableFields[control.Label] = text;
        formDataFields[control.JsonId] = text;
    }

    private async Task WriteFormTableColumnsAsync(
        string formId,
        Guid formEntryId,
        IReadOnlyList<FtlTableColumn> columns,
        CancellationToken cancellationToken)
    {
        if (columns.Count == 0)
            return;

        var tenantCs = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(tenantCs))
            return;

        var suffix = FormIdNaming.GetEzfbTableSuffix(formId);
        var tableName = $"ezfb_{suffix}_items";
        var table = $"dbo.\"{tableName}\"";
        await using var connection = new NpgsqlConnection(tenantCs);
        await connection.OpenAsync(cancellationToken);
        var existing = await LoadEzfbColumnsAsync(connection, tableName, cancellationToken);
        foreach (var column in columns)
        {
            if (!EzfbColumnNaming.TryResolveEzfbColumn(
                    column.ColumnName,
                    column.Label,
                    column.JsonId,
                    existing,
                    out var physical,
                    out _))
            {
                if (!string.IsNullOrWhiteSpace(column.ColumnName))
                    physical = column.ColumnName.Trim();
                else if (!EzfbColumnNaming.TryToColumnNameFromLabel(column.Label, out physical)
                    && !EzfbColumnNaming.TryToColumnName(column.JsonId, out physical))
                    continue;
            }

            var escaped = physical.Replace("\"", "\"\"", StringComparison.Ordinal);
            await using (var alter = new NpgsqlCommand(
                $"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS \"{escaped}\" text NULL;",
                connection))
                await alter.ExecuteNonQueryAsync(cancellationToken);

            await using var update = new NpgsqlCommand(
                $"UPDATE {table} SET \"{escaped}\" = @Value WHERE item_id = @ItemId;",
                connection);
            update.Parameters.AddWithValue("@Value", string.IsNullOrWhiteSpace(column.EzfbJson) ? column.Json : column.EzfbJson);
            update.Parameters.AddWithValue("@ItemId", formEntryId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            existing.Add(physical);
        }
    }

    /// <summary>
    /// Inbox form_data is what the open ticket renders. ezfb is updated separately, so copy the
    /// same Line Item array onto the mailbox row or the table stays on the previous payload.
    /// </summary>
    private async Task SyncLineItemsToMailboxAsync(
        Guid workflowId,
        Guid instanceId,
        IReadOnlyList<FtlTableColumn> columns,
        CancellationToken cancellationToken)
    {
        var lineItems = columns
            .Where(column => NormalizeFieldKey(column.Label) is "lineitem" or "lineitems")
            .Where(column => !string.IsNullOrWhiteSpace(column.JsonId) && !string.IsNullOrWhiteSpace(column.Json))
            .ToList();
        if (lineItems.Count == 0)
            return;

        var tenantCs = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(tenantCs))
            return;

        var suffix = workflowId.ToString("N")[..8];
        var instanceKey = instanceId.ToString("N");
        await using var connection = new NpgsqlConnection(tenantCs);
        await connection.OpenAsync(cancellationToken);
        foreach (var prefix in new[] { "inbox", "sent", "completed" })
        {
            var table = $"workflow.{prefix}_{suffix}";
            string? current;
            try
            {
                await using var read = new NpgsqlCommand(
                    $"""
                    SELECT form_data
                    FROM {table}
                    WHERE REPLACE(LOWER(workflow_instance_id::text), '-', '') = @InstanceId
                    ORDER BY id DESC
                    LIMIT 1
                    """,
                    connection);
                read.Parameters.AddWithValue("@InstanceId", instanceKey);
                var value = await read.ExecuteScalarAsync(cancellationToken);
                current = value == null || value == DBNull.Value ? null : Convert.ToString(value);
            }
            catch (PostgresException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(current))
                continue;

            JsonObject? root;
            try
            {
                root = JsonNode.Parse(current) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (root == null)
                continue;

            foreach (var column in lineItems)
            {
                try
                {
                    root[column.JsonId] = JsonNode.Parse(column.Json);
                }
                catch (JsonException)
                {
                    root[column.JsonId] = column.Json;
                }
            }

            await using var update = new NpgsqlCommand(
                $"""
                UPDATE {table}
                SET form_data = @FormData
                WHERE REPLACE(LOWER(workflow_instance_id::text), '-', '') = @InstanceId
                """,
                connection);
            update.Parameters.AddWithValue("@FormData", root.ToJsonString());
            update.Parameters.AddWithValue("@InstanceId", instanceKey);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<HashSet<string>> LoadEzfbColumnsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'dbo' AND table_name = @TableName
            """;
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));
        return columns;
    }

    private async Task<List<FtlFormControl>> LoadFtlFormControlsAsync(
        string formId,
        string tenantCs,
        CancellationToken cancellationToken)
    {
        var labels = await LoadFormFieldLabelsAsync(formId, cancellationToken);
        var controls = new List<FtlFormControl>();
        await using var connection = new NpgsqlConnection(tenantCs);
        await connection.OpenAsync(cancellationToken);
        await using (var alter = new NpgsqlCommand(
            """ALTER TABLE dbo."wFormControl" ADD COLUMN IF NOT EXISTS "columnName" varchar(200) NULL;""",
            connection))
            await alter.ExecuteNonQueryAsync(cancellationToken);
        const string sql = """
            SELECT id, "parentId", "jsonId", name, type, "columnName"
            FROM dbo."wFormControl"
            WHERE "wFormId" = @FormId AND "isDeleted" = false
              AND "jsonId" IS NOT NULL AND BTRIM("jsonId") <> ''
            ORDER BY "parentId", id
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", formId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var jsonId = reader.GetString(2).Trim();
            var dbName = reader.IsDBNull(3) ? null : reader.GetString(3);
            var label = labels.TryGetValue(jsonId, out var fromJson) && !string.IsNullOrWhiteSpace(fromJson)
                ? fromJson.Trim()
                : !string.IsNullOrWhiteSpace(dbName) && !string.Equals(dbName.Trim(), jsonId, StringComparison.OrdinalIgnoreCase)
                    ? dbName.Trim()
                    : jsonId;
            controls.Add(new FtlFormControl(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                jsonId,
                label,
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                string.IsNullOrWhiteSpace(dbName) ? null : dbName.Trim()));
        }

        return controls;
    }

    private async Task<Dictionary<string, string>> LoadFormFieldLabelsAsync(
        string formId,
        CancellationToken cancellationToken)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = await _formJsonStorage.GetFormJsonAsync(formId, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                return labels;

            using var doc = JsonDocument.Parse(json);
            CollectFieldLabels(doc.RootElement, labels);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FTL form label load failed for form {FormId}. Control names from wFormControl are used.", formId);
        }

        return labels;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = prop.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static void CollectFieldLabels(JsonElement element, Dictionary<string, string> labels)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGetPropertyIgnoreCase(element, "id", out var idEl)
                    && idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    var title = ReadFieldTitle(element);
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(title))
                        labels[id] = title;
                }

                foreach (var prop in element.EnumerateObject())
                    CollectFieldLabels(prop.Value, labels);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectFieldLabels(item, labels);
                break;
        }
    }

    /// <summary>
    /// Top-level fields use label ("Order Number"). Table columns store the title in name
    /// ("Product", "Qty", "Item") and leave label empty.
    /// </summary>
    private static string? ReadFieldTitle(JsonElement element)
    {
        if (TryGetPropertyIgnoreCase(element, "label", out var labelEl)
            && labelEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(labelEl.GetString()))
            return labelEl.GetString()!.Trim();

        if (TryGetPropertyIgnoreCase(element, "name", out var nameEl)
            && nameEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(nameEl.GetString()))
            return nameEl.GetString()!.Trim();

        return null;
    }

    private static FtlFormControl? ResolveQualifierControl(string key, IReadOnlyList<FtlFormControl> roots)
    {
        var norm = NormalizeFieldKey(key);
        var exact = roots.FirstOrDefault(c => string.Equals(NormalizeFieldKey(c.Label), norm, StringComparison.Ordinal));
        if (exact is not null)
            return exact;

        if (norm is "projectname" or "project")
            return roots.FirstOrDefault(c => string.Equals(NormalizeFieldKey(c.Label), "project", StringComparison.Ordinal));

        if (norm is "customername" or "customer")
            return roots.FirstOrDefault(c => string.Equals(NormalizeFieldKey(c.Label), "companyname", StringComparison.Ordinal));

        if (norm is "contactname" or "contactperson")
            return roots.FirstOrDefault(c => string.Equals(NormalizeFieldKey(c.Label), "contact", StringComparison.Ordinal));

        if (norm is "contactphone" or "phone" or "phonenumber")
            return roots.FirstOrDefault(c => string.Equals(NormalizeFieldKey(c.Label), "phonenumber", StringComparison.Ordinal));

        if (norm is "estimatenumber" or "estimate" or "ordernumber")
            return roots.FirstOrDefault(c => string.Equals(NormalizeFieldKey(c.Label), "ordernumber", StringComparison.Ordinal));

        if (norm is "matcheditems" or "matcheditem")
        {
            return roots.FirstOrDefault(c =>
                c.Type.Contains("TABLE", StringComparison.OrdinalIgnoreCase)
                && NormalizeFieldKey(c.Label).StartsWith("matchedit", StringComparison.Ordinal));
        }

        if (norm is "lineitem" or "lineitems")
        {
            return roots.FirstOrDefault(c =>
                c.Type.Contains("TABLE", StringComparison.OrdinalIgnoreCase)
                && NormalizeFieldKey(c.Label) is "lineitem" or "lineitems");
        }

        return null;
    }

    private static string? FormatQualifierValue(
        string raw,
        FtlFormControl control,
        IReadOnlyList<FtlFormControl> controls)
    {
        if (control.Type.Contains("TABLE", StringComparison.OrdinalIgnoreCase))
        {
            var lineItemTable = NormalizeFieldKey(control.Label) is "lineitem" or "lineitems";
            return FormatQualifierTable(raw, control, controls, lineItemTable, useJsonId: false);
        }

        return TryJoinPrimitiveArray(raw, out var joined) ? joined : raw;
    }

    private static string? FormatQualifierTable(
        string raw,
        FtlFormControl table,
        IReadOnlyList<FtlFormControl> controls,
        bool lineItemTable,
        bool useJsonId)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;

            var children = controls.Where(c => c.ParentId == table.Id).OrderBy(c => c.Id).ToList();
            var roles = RolesForTable(table);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                var wroteRow = false;
                var rowNumber = 0;
                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object)
                        continue;

                    rowNumber++;
                    var cells = new List<(string Key, string Value)>();
                    for (var i = 0; i < children.Count; i++)
                    {
                        var child = children[i];
                        var role = i < roles.Length ? roles[i] : null;
                        var cell = ReadChildCell(row, child, lineItemTable);
                        if (string.IsNullOrWhiteSpace(cell) && role is not null)
                            cell = ReadCellByRole(row, role) ?? ReadLineItemAlias(row, role);
                        if (string.IsNullOrWhiteSpace(cell) && lineItemTable && role == "#")
                            cell = rowNumber.ToString();
                        if (string.IsNullOrWhiteSpace(cell) && lineItemTable && role == "Subtotal")
                            cell = TryLineSubtotal(row);
                        var cellKey = useJsonId ? child.JsonId : child.Label;
                        cells.Add((cellKey, cell ?? string.Empty));
                    }

                    if (cells.All(c => string.IsNullOrWhiteSpace(c.Value)))
                        continue;

                    if (cells.Count == 0)
                        continue;

                    writer.WriteStartObject();
                    foreach (var (key, cell) in cells)
                    {
                        writer.WritePropertyName(key);
                        writer.WriteStringValue(cell);
                    }

                    writer.WriteEndObject();
                    wroteRow = true;
                }

                writer.WriteEndArray();
                if (!wroteRow)
                    return null;
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? RemapTableKeysToControlNames(
        string tableJson,
        FtlFormControl table,
        IReadOnlyList<FtlFormControl> controls)
    {
        var names = controls
            .Where(child => child.ParentId == table.Id)
            .ToDictionary(
                child => child.JsonId,
                child => !string.IsNullOrWhiteSpace(child.Name)
                    && !string.Equals(child.Name, child.JsonId, StringComparison.OrdinalIgnoreCase)
                    ? child.Name.Trim()
                    : child.Label,
                StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
            return tableJson;

        try
        {
            using var doc = JsonDocument.Parse(tableJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return tableJson;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object)
                    {
                        row.WriteTo(writer);
                        continue;
                    }

                    writer.WriteStartObject();
                    foreach (var prop in row.EnumerateObject())
                    {
                        writer.WritePropertyName(names.TryGetValue(prop.Name, out var name) ? name : prop.Name);
                        prop.Value.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return tableJson;
        }
    }

    private static string[] RolesForTable(FtlFormControl table)
    {
        var key = NormalizeFieldKey(table.Label);
        if (key.StartsWith("matchedit", StringComparison.Ordinal))
            return ["Item", "Category", "Match", "Catalog Ref", "Note"];
        if (key is "excludeditems" or "excludeditem")
            return ["Item", "Reason"];
        if (key is "lineitem" or "lineitems")
            return ["#", "Product", "Qty", "Price", "Subtotal"];
        return [];
    }

    private async Task<string?> LoadExistingFormDataAsync(
        Guid workflowId,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var suffix = workflowId.ToString("N")[..8];
        var tenantCs = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(tenantCs))
            return null;

        await using var connection = new NpgsqlConnection(tenantCs);
        await connection.OpenAsync(cancellationToken);
        foreach (var prefix in new[] { "inbox", "sent", "completed" })
        {
            var sql = $"""
                SELECT form_data
                FROM workflow.{prefix}_{suffix}
                WHERE workflow_instance_id::text = @InstanceId
                  AND form_data IS NOT NULL
                  AND BTRIM(form_data) <> ''
                ORDER BY id DESC
                LIMIT 1
                """;
            try
            {
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@InstanceId", instanceId.ToString("D"));
                var value = await cmd.ExecuteScalarAsync(cancellationToken);
                if (value is string text && !string.IsNullOrWhiteSpace(text))
                    return text;
            }
            catch (PostgresException)
            {
            }
        }

        return null;
    }

    private async Task<string> RemapFormDataKeysToColumnNamesAsync(
        string? formId,
        string formDataJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(formId))
            return formDataJson;

        var tenantCs = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(tenantCs))
            return formDataJson;

        var controls = await LoadFtlFormControlsAsync(formId, tenantCs, cancellationToken);
        if (controls.Count == 0)
            return formDataJson;

        try
        {
            using var doc = JsonDocument.Parse(formDataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return formDataJson;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var control = FindControl(controls, prop.Name);
                    var key = ColumnOutputKey(control) ?? prop.Name;
                    if (!written.Add(key))
                        continue;

                    writer.WritePropertyName(key);
                    if (!TryWriteTableWithColumnNames(writer, prop.Value, control, controls))
                        prop.Value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return formDataJson;
        }
    }

    private static bool TryWriteTableWithColumnNames(
        Utf8JsonWriter writer,
        JsonElement value,
        FtlFormControl? table,
        IReadOnlyList<FtlFormControl> controls)
    {
        if (table is null || !table.Type.Contains("TABLE", StringComparison.OrdinalIgnoreCase))
            return false;

        JsonDocument? owned = null;
        try
        {
            JsonElement array;
            if (value.ValueKind == JsonValueKind.Array)
            {
                array = value;
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('['))
                    return false;

                owned = JsonDocument.Parse(text);
                if (owned.RootElement.ValueKind != JsonValueKind.Array)
                    return false;

                array = owned.RootElement;
            }
            else
            {
                return false;
            }

            var children = controls.Where(c => c.ParentId == table.Id).ToList();
            writer.WriteStartArray();
            foreach (var row in array.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    row.WriteTo(writer);
                    continue;
                }

                writer.WriteStartObject();
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var cell in row.EnumerateObject())
                {
                    var child = children.Count == 0
                        ? FindControl(controls, cell.Name)
                        : children.FirstOrDefault(c => string.Equals(c.JsonId, cell.Name, StringComparison.OrdinalIgnoreCase))
                            ?? children.FirstOrDefault(c =>
                                string.Equals(c.Name, cell.Name, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(c.Label, cell.Name, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(c.ColumnName, cell.Name, StringComparison.OrdinalIgnoreCase));
                    var key = ColumnOutputKey(child) ?? cell.Name;
                    if (!written.Add(key))
                        continue;

                    writer.WritePropertyName(key);
                    cell.Value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static FtlFormControl? FindControl(IReadOnlyList<FtlFormControl> controls, string key)
    {
        var match = controls.FirstOrDefault(c => string.Equals(c.JsonId, key, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match;

        return controls.FirstOrDefault(c =>
            string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Label, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, key, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ColumnOutputKey(FtlFormControl? control)
    {
        if (control is null)
            return null;
        if (!string.IsNullOrWhiteSpace(control.Name)
            && !string.Equals(control.Name, control.JsonId, StringComparison.OrdinalIgnoreCase))
            return control.Name.Trim();
        if (!string.IsNullOrWhiteSpace(control.Label)
            && !string.Equals(control.Label, control.JsonId, StringComparison.OrdinalIgnoreCase))
            return control.Label.Trim();
        return null;
    }

    private static Dictionary<string, string> MergeQuoteFormData(
        string? existingJson,
        IReadOnlyDictionary<string, string> incoming,
        IReadOnlySet<string>? replaceKeys = null)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        merged[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? string.Empty
                            : prop.Value.GetRawText();
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        foreach (var (key, value) in incoming)
        {
            if (replaceKeys is not null && replaceKeys.Contains(key) && !IsEmptyFormValue(value))
            {
                merged[key] = value;
                continue;
            }

            if (merged.TryGetValue(key, out var current) && !IsEmptyFormValue(current))
                continue;

            merged[key] = value;
        }

        return merged;
    }

    private static HashSet<string> NonEmptyFormKeys(string? existingJson)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(existingJson))
            return keys;

        try
        {
            using var doc = JsonDocument.Parse(existingJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return keys;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var raw = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.GetRawText();
                if (!IsEmptyFormValue(raw))
                    keys.Add(prop.Name);
            }
        }
        catch (JsonException)
        {
        }

        return keys;
    }

    private static Dictionary<string, string> KeepAddedFields(
        IReadOnlyDictionary<string, string> tableFields,
        IReadOnlyDictionary<string, string> formDataFields,
        IReadOnlySet<string> protectedIds)
    {
        var protectedValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (jsonId, value) in formDataFields)
        {
            if (protectedIds.Contains(jsonId) && !IsEmptyFormValue(value))
                protectedValues.Add(value);
        }

        return tableFields
            .Where(pair => !protectedValues.Contains(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsEmptyFormValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;
        var trimmed = value.Trim();
        return trimmed is "[]" or "{}" or "null";
    }

    private static string? ReadLineItemAlias(JsonElement row, string role)
    {
        if (!LineItemCellAliases.TryGetValue(role, out var aliases))
            return null;

        foreach (var prop in row.EnumerateObject())
        {
            var propKey = NormalizeFieldKey(prop.Name);
            foreach (var alias in aliases)
            {
                if (!string.Equals(NormalizeFieldKey(alias), propKey, StringComparison.Ordinal)
                    && !string.Equals(alias, prop.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                return prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.Value.GetRawText(),
                    _ => null
                };
            }
        }

        return null;
    }

    private static string? TryLineSubtotal(JsonElement row)
    {
        if (!TryReadDecimal(row, ["qty", "quantity"], out var qty)
            || !TryReadDecimal(row, ["unit_price", "unitprice", "price", "rate"], out var price))
            return null;

        return (qty * price).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryReadDecimal(JsonElement row, string[] names, out decimal value)
    {
        foreach (var prop in row.EnumerateObject())
        {
            var propKey = NormalizeFieldKey(prop.Name);
            if (!names.Any(name => string.Equals(NormalizeFieldKey(name), propKey, StringComparison.Ordinal)))
                continue;

            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDecimal(out value))
                return true;
            if (prop.Value.ValueKind == JsonValueKind.String
                && decimal.TryParse(prop.Value.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out value))
                return true;
        }

        value = 0;
        return false;
    }

    private static string? ReadCellByRole(JsonElement row, string role)
    {
        foreach (var prop in row.EnumerateObject())
        {
            if (!RoleMatches(prop.Name, role))
                continue;

            return prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.Value.GetRawText(),
                _ => null
            };
        }

        return null;
    }

    private static bool RoleMatches(string propName, string role)
    {
        if (string.Equals(propName.Trim(), role.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        var propKey = NormalizeFieldKey(propName);
        var roleKey = NormalizeFieldKey(role);
        if (roleKey.Length > 0 && string.Equals(propKey, roleKey, StringComparison.Ordinal))
            return true;

        return roleKey switch
        {
            "catalogref" => propKey is "catalogreference" or "catalog",
            "note" => propKey is "notes" or "comment",
            "match" => propKey is "matchtype",
            _ => false
        };
    }

    private static readonly Dictionary<string, string[]> LineItemCellAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#"] = ["#", "line", "line_no", "lineno", "sno", "no"],
        ["Product"] = ["product", "item", "description", "product_name", "name"],
        ["Qty"] = ["qty", "quantity"],
        ["Price"] = ["price", "rate", "unit_price", "unitprice", "unit cost"],
        ["Subtotal"] = ["subtotal", "amount", "extended", "line_amount", "lineamount", "total"]
    };

    private static string? ReadChildCell(JsonElement row, FtlFormControl child, bool lineItemTable)
    {
        foreach (var prop in row.EnumerateObject())
        {
            if (!CellKeyMatches(prop.Name, child, lineItemTable))
                continue;

            return prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.Value.GetRawText(),
                _ => null
            };
        }

        return null;
    }

    private static bool CellKeyMatches(string propName, FtlFormControl child, bool lineItemTable)
    {
        if (string.Equals(propName, child.JsonId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(propName.Trim(), child.Label.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        var propKey = NormalizeFieldKey(propName);
        var labelKey = NormalizeFieldKey(child.Label);
        if (labelKey.Length > 0 && string.Equals(propKey, labelKey, StringComparison.Ordinal))
            return true;

        if (!lineItemTable || !LineItemCellAliases.TryGetValue(child.Label.Trim(), out var aliases))
            return false;

        foreach (var alias in aliases)
        {
            if (string.Equals(alias, propName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeFieldKey(alias), propKey, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryJoinPrimitiveArray(string raw, out string joined)
    {
        joined = string.Empty;
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            var parts = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    return false;
                if (item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue;
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText();
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text);
            }

            joined = string.Join(", ", parts);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record FtlFormControl(int Id, int ParentId, string JsonId, string Label, string Type, string? ColumnName, string? Name);

    private sealed record FtlTableColumn(string? ColumnName, string Label, string JsonId, string Json, string? EzfbJson = null);

    private sealed record FtlMappedForm(
        Dictionary<string, string> TableFields,
        Dictionary<string, string> FormDataFields,
        List<FtlTableColumn> TableColumns);

    private static string NormalizeFieldKey(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    private static Dictionary<string, string> ExtractFormFields(string json)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        CopyFlat(FindProperty(doc.RootElement, "qualifier_result"), fields);
        CopyFlat(FindProperty(doc.RootElement, "quote_result"), fields);
        if (doc.RootElement.TryGetProperty("estimate_number", out var estimate)
            && estimate.ValueKind == JsonValueKind.String)
            fields["estimate_number"] = estimate.GetString() ?? string.Empty;
        return fields;
    }

    /// <summary>
    /// Quote save uses only quote_result. Matched items from qualifier/payload are ignored.
    /// </summary>
    private static Dictionary<string, string> ExtractQuoteFormFields(string json)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        CopyFlat(FindRootOrNestedObject(doc.RootElement, "quote_result"), fields, skipMatchedItems: true);
        if (doc.RootElement.TryGetProperty("estimate_number", out var estimate)
            && estimate.ValueKind == JsonValueKind.String)
            fields["estimate_number"] = estimate.GetString() ?? string.Empty;
        if (fields.TryGetValue("estimate_number", out _) is false
            && fields.TryGetValue("Order Number", out var orderNumber)
            && !string.IsNullOrWhiteSpace(orderNumber))
            fields["estimate_number"] = orderNumber;
        return fields;
    }

    private static bool IsMatchedItemsField(string name)
    {
        var key = NormalizeFieldKey(name);
        return key is "matcheditems" or "matcheditem"
            || key.StartsWith("matchedit", StringComparison.Ordinal);
    }

    private static void CopyFlat(
        JsonElement element,
        Dictionary<string, string> fields,
        bool skipMatchedItems = false)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;
            if (prop.NameEquals("pdf_base64"))
                continue;
            if (skipMatchedItems && IsMatchedItemsField(prop.Name))
                continue;
            fields[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? string.Empty
                : prop.Value.GetRawText();
        }
    }

    private static JsonElement FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.NameEquals(name))
                    return prop.Value;
                var nested = FindProperty(prop.Value, name);
                if (nested.ValueKind != JsonValueKind.Undefined)
                    return nested;
            }
        }

        return default;
    }

    /// <summary>Prefer a root-level object property before any nested copy with the same name.</summary>
    private static JsonElement FindRootOrNestedObject(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.NameEquals(name) && prop.Value.ValueKind == JsonValueKind.Object)
                    return prop.Value;
            }
        }

        return FindProperty(element, name);
    }

    /// <summary>
    /// Rewrite root quote_result["Line Item"] to the longest line-item array in the payload so
    /// agent_data_validation does not keep an empty nested copy while the agent returned rows.
    /// </summary>
    private static string PreferLongestQuoteLineItems(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return json;

            var best = FindLongestNamedArray(doc.RootElement, "line_items", "Line Item", "Line Items");
            if (best.ValueKind != JsonValueKind.Array || best.GetArrayLength() == 0)
                return json;

            var rootQuote = FindRootOrNestedObject(doc.RootElement, "quote_result");
            if (rootQuote.ValueKind != JsonValueKind.Object)
                return json;

            var current = FindLongestNamedArray(rootQuote, "line_items", "Line Item", "Line Items");
            if (current.ValueKind == JsonValueKind.Array
                && current.GetArrayLength() >= best.GetArrayLength())
                return json;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                var wroteQuote = false;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("quote_result") && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        writer.WritePropertyName(prop.Name);
                        WriteQuoteWithLineItems(prop.Value, best, writer);
                        wroteQuote = true;
                        continue;
                    }

                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }

                if (!wroteQuote)
                {
                    writer.WritePropertyName("quote_result");
                    writer.WriteStartObject();
                    writer.WritePropertyName("Line Item");
                    best.WriteTo(writer);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static void WriteQuoteWithLineItems(
        JsonElement quote,
        JsonElement lineItems,
        Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        var wroteLineItem = false;
        foreach (var prop in quote.EnumerateObject())
        {
            if (prop.NameEquals("Line Item")
                || prop.NameEquals("Line Items")
                || prop.NameEquals("line_items"))
            {
                if (wroteLineItem)
                    continue;
                writer.WritePropertyName("Line Item");
                lineItems.WriteTo(writer);
                wroteLineItem = true;
                continue;
            }

            writer.WritePropertyName(prop.Name);
            prop.Value.WriteTo(writer);
        }

        if (!wroteLineItem)
        {
            writer.WritePropertyName("Line Item");
            lineItems.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static JsonElement FindLongestNamedArray(JsonElement element, params string[] names)
    {
        JsonElement best = default;
        var bestLength = 0;
        Walk(element);
        return best;

        void Walk(JsonElement current)
        {
            if (current.ValueKind != JsonValueKind.Object)
                return;

            foreach (var prop in current.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array
                    && names.Any(name => prop.NameEquals(name)))
                {
                    var length = prop.Value.GetArrayLength();
                    if (length > bestLength)
                    {
                        best = prop.Value;
                        bestLength = length;
                    }

                    continue;
                }

                Walk(prop.Value);
            }
        }
    }

    private static bool HasLongerOrEqualTable(
        IReadOnlyDictionary<string, string> formDataFields,
        string jsonId,
        string candidateJson)
    {
        if (!formDataFields.TryGetValue(jsonId, out var existing))
            return false;
        return CountJsonArrayRows(existing) >= CountJsonArrayRows(candidateJson);
    }

    private static int CountJsonArrayRows(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string StripPdf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return json;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteWithoutPdf(doc.RootElement, writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteWithoutPdf(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("pdf_base64"))
                        continue;
                    writer.WritePropertyName(prop.Name);
                    WriteWithoutPdf(prop.Value, writer);
                }
                writer.WriteEndObject();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
