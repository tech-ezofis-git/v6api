using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.SharedKernel.Options;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Application.Workflows.Commands.MoveToNextStep;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// FTL qualifier (file), quote estimator (JSON), and document PDF.
/// Does not call the AP Agent pipeline.
/// </summary>
public sealed class FtlAgentPipelineService : IFtlAgentPipelineService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWorkflowRepository _repository;
    private readonly IDynamicTableRepository _attachments;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IWorkflowLegacyMailboxSyncService _mailbox;
    private readonly IWorkflowPdfGenerationService _pdfGeneration;
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
        IWorkflowPdfGenerationService pdfGeneration,
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
        _pdfGeneration = pdfGeneration;
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
            await ExecuteDocumentAsync(args, cancellationToken);
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

        var storedJson = StripPdf(responseJson);
        EnsureAgentOutput(args.Mode, storedJson);
        var formId = args.FormId ?? workflow.FormId;
        var identity = await _mailbox.TryGetProcessFormIdentityAsync(args.WorkflowId, args.InstanceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(formId))
            formId = identity?.FormId;
        var formEntryId = identity?.FormEntryId;
        var attachment = (await _attachments.GetAttachmentsAsync(args.WorkflowId, args.InstanceId, cancellationToken))
            .FirstOrDefault(r => r.ItemId is { } item && item != Guid.Empty);
        var repositoryId = ParseGuid(args.RepositoryId) ?? attachment?.RepositoryId ?? ParseGuid(workflow.RepositoryId);
        var fields = ExtractFormFields(storedJson);
        var lineItemsJson = ExtractLineItems(storedJson);
        var mapped = await RemapFieldsToJsonIdsAsync(formId, fields, lineItemsJson, cancellationToken);

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
            ? ResolveQualifyReview(storedJson)
            : "Submit";

        var moved = await _mediator.Send(
            new MoveToNextStepCommand(
                args.InstanceId,
                args.ActivityId,
                Review: review,
                Comments: $"FTL {args.Mode}",
                ActivityUserId: args.UserId,
                FormId: formId,
                FormEntryId: formEntryId,
                FormDataFields: mapped.TableFields,
                FormLineItemsJson: null,
                SubmittedFormDataJson: BuildFormFieldsJson(mapped.FormDataFields, null)),
            cancellationToken);

        if (!moved.Success)
            throw new InvalidOperationException(moved.Message ?? "FTL move-next failed.");

        if (!string.IsNullOrWhiteSpace(formId) && formEntryId is { } savedEntry && savedEntry != Guid.Empty)
            await WriteFormTableColumnsAsync(formId, savedEntry, mapped.TableColumns, cancellationToken);

        _logger.LogInformation(
            "FTL {Mode} moved instance {InstanceId} with review {Review} to {NextStep}.",
            args.Mode,
            args.InstanceId,
            review,
            moved.NextStepName);
    }

    private async Task ExecuteDocumentAsync(FtlAgentJobArgs args, CancellationToken cancellationToken)
    {
        var workflow = await _repository.GetByIdWithStepsAsync(args.WorkflowId, cancellationToken)
            ?? throw new InvalidOperationException("Workflow not found.");
        var instance = await _repository.GetInstanceByIdAsync(args.InstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Workflow instance not found.");
        var step = workflow.Steps.FirstOrDefault(s =>
            string.Equals(s.ActivityId, args.ActivityId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Id.ToString("D"), args.ActivityId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"FTL step {args.ActivityId} was not found.");

        var formId = args.FormId ?? workflow.FormId;
        var identity = await _mailbox.TryGetProcessFormIdentityAsync(args.WorkflowId, args.InstanceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(formId))
            formId = identity?.FormId;
        var formEntryId = identity?.FormEntryId;
        var formDataJson = await LoadLatestAgentResponseAsync(
            args.WorkflowId,
            args.InstanceId,
            cancellationToken,
            "QUALIFY_AGENT");

        var generated = await _pdfGeneration.TryGenerateOnStepCompleteAsync(
            workflow,
            instance,
            step,
            formId,
            formEntryId,
            args.UserId,
            transactionId: null,
            cancellationToken,
            submittedFormDataJson: formDataJson,
            force: true)
            ?? throw new InvalidOperationException("Document PDF was not generated.");

        var storedJson = JsonSerializer.Serialize(new
        {
            fileName = generated.FileName,
            attachmentId = generated.AttachmentId,
            itemId = generated.ItemId
        });
        var repositoryId = ParseGuid(args.RepositoryId) ?? ParseGuid(workflow.RepositoryId);

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
                RepositoryItemId: generated.ItemId,
                RepositoryId: repositoryId,
                FormId: formId,
                FormEntryId: formEntryId),
            legacyTransactionId: null,
            cancellationToken);

        var moved = await _mediator.Send(
            new MoveToNextStepCommand(
                args.InstanceId,
                args.ActivityId,
                Review: "Submit",
                Comments: "FTL document",
                ActivityUserId: args.UserId,
                FormId: formId,
                FormEntryId: formEntryId),
            cancellationToken);

        if (!moved.Success)
            throw new InvalidOperationException(moved.Message ?? "FTL document move-next failed.");

        _logger.LogInformation(
            "FTL document generated {FileName} for instance {InstanceId} and moved to {NextStep}.",
            generated.FileName,
            args.InstanceId,
            moved.NextStepName);
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
        var prior = await LoadLatestAgentResponseAsync(
            args.WorkflowId,
            args.InstanceId,
            cancellationToken,
            "QUALIFY_AGENT");
        using var priorDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(prior) ? "{}" : prior);
        var qualifier = FindProperty(priorDoc.RootElement, "qualifier_result");

        var payload = new Dictionary<string, object?>
        {
            ["template_type"] = "inflow",
            ["workflowId"] = args.WorkflowId.ToString("D"),
            ["instanceId"] = args.InstanceId.ToString("D"),
            ["repositoryId"] = args.RepositoryId,
            ["formId"] = args.FormId,
            ["apAgentJobId"] = jobId,
            ["apAgentJobStatusUrl"] = StatusUrl(jobId)
        };
        if (qualifier.ValueKind != JsonValueKind.Undefined)
            payload["qualifier_result"] = JsonSerializer.Deserialize<object>(qualifier.GetRawText());

        var body = JsonSerializer.Serialize(new
        {
            session_id = jobId,
            intent = "ftl_quote_estimator",
            payload
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await PostAsync(chatUrl, content, cancellationToken);
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
        form.Add(new StringContent(args.InstanceId.ToString("D")), "instanceId");
        if (!string.IsNullOrWhiteSpace(args.RepositoryId))
            form.Add(new StringContent(args.RepositoryId), "repositoryId");
        if (!string.IsNullOrWhiteSpace(args.FormId))
            form.Add(new StringContent(args.FormId), "formId");
        form.Add(new StringContent(jobId), "apAgentJobId");
        var status = StatusUrl(jobId);
        if (!string.IsNullOrWhiteSpace(status))
            form.Add(new StringContent(status), "apAgentJobStatusUrl");
    }

    private string? StatusUrl(string jobId)
    {
        var baseUrl = _apAgentOptions.Value.ApiBaseUrl?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl}/ap-agent/jobs/{jobId}";
    }

    private async Task<(byte[] Bytes, string FileName, string? ContentType)> ReadFirstAttachmentAsync(
        FtlAgentJobArgs args,
        CancellationToken cancellationToken)
    {
        var rows = await _attachments.GetAttachmentsAsync(args.WorkflowId, args.InstanceId, cancellationToken);
        var row = rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.FilePath))
            ?? throw new InvalidOperationException("No archived file found for the FTL qualifier.");

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

    private static string? ExtractLineItems(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var lineItems = FindProperty(doc.RootElement, "line_items");
        if (lineItems.ValueKind != JsonValueKind.Array)
            lineItems = FindProperty(doc.RootElement, "matched_items");
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
        var result = FindProperty(doc.RootElement, resultName);
        if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"FTL {mode} response did not include {resultName}. The ticket was not moved.");
        }
    }

    private static string ResolveQualifyReview(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = FindProperty(doc.RootElement, "qualifier_result");
        var qualify = result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("qualify", out var q)
            ? q.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(qualify)
            && qualify.Contains("disqual", StringComparison.OrdinalIgnoreCase))
            return "DISQUALIFY";
        return "QUALIFY";
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
        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
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
        if (lineItemTable is not null && !string.IsNullOrWhiteSpace(lineItemsJson))
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

            tableColumns.Add(new FtlTableColumn(control.ColumnName, control.Label, control.JsonId, tableJson));
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
            update.Parameters.AddWithValue("@Value", column.Json);
            update.Parameters.AddWithValue("@ItemId", formEntryId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            existing.Add(physical);
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
                reader.IsDBNull(5) ? null : reader.GetString(5)));
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
                    && idEl.ValueKind == JsonValueKind.String
                    && TryGetPropertyIgnoreCase(element, "label", out var labelEl)
                    && labelEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    var label = labelEl.GetString();
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(label))
                        labels[id] = label;
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

    private static FtlFormControl? ResolveQualifierControl(string key, IReadOnlyList<FtlFormControl> roots)
    {
        var norm = NormalizeFieldKey(key);
        var exact = roots.FirstOrDefault(c => string.Equals(NormalizeFieldKey(c.Label), norm, StringComparison.Ordinal));
        if (exact is not null)
            return exact;

        if (norm == "projectname")
            return roots.FirstOrDefault(c => string.Equals(NormalizeFieldKey(c.Label), "project", StringComparison.Ordinal));

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
                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object)
                        continue;

                    var cells = new List<(string Key, string Value)>();
                    for (var i = 0; i < children.Count; i++)
                    {
                        var child = children[i];
                        var cell = ReadChildCell(row, child, lineItemTable);
                        if (string.IsNullOrWhiteSpace(cell) && i < roles.Length)
                            cell = ReadCellByRole(row, roles[i]);
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

    private sealed record FtlFormControl(int Id, int ParentId, string JsonId, string Label, string Type, string? ColumnName);

    private sealed record FtlTableColumn(string? ColumnName, string Label, string JsonId, string Json);

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

    private static void CopyFlat(JsonElement element, Dictionary<string, string> fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;
            if (prop.NameEquals("pdf_base64"))
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
