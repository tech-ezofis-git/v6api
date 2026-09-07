using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows.Commands.CreateWorkflow;
using SaaSApp.Workflow.Domain.Entities;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>Calls Python PDF service on step completion when generatePDF is enabled.</summary>
public sealed class WorkflowPdfGenerationService : IWorkflowPdfGenerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IWorkflowJsonStorageService _jsonStorage;
    private readonly IWorkflowEzfbFormDataLoader _ezfbFormDataLoader;
    private readonly WorkflowPdfFormDataMapper _formDataMapper;
    private readonly IWorkflowAttachmentArchiveService _attachmentArchive;
    private readonly IWorkflowApAgentMoveNextService _apAgentMoveNext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<WorkflowPdfGenerationOptions> _options;
    private readonly ILogger<WorkflowPdfGenerationService> _logger;

    public WorkflowPdfGenerationService(
        IWorkflowJsonStorageService jsonStorage,
        IWorkflowEzfbFormDataLoader ezfbFormDataLoader,
        WorkflowPdfFormDataMapper formDataMapper,
        IWorkflowAttachmentArchiveService attachmentArchive,
        IWorkflowApAgentMoveNextService apAgentMoveNext,
        IHttpClientFactory httpClientFactory,
        IOptions<WorkflowPdfGenerationOptions> options,
        ILogger<WorkflowPdfGenerationService> logger)
    {
        _jsonStorage = jsonStorage;
        _ezfbFormDataLoader = ezfbFormDataLoader;
        _formDataMapper = formDataMapper;
        _attachmentArchive = attachmentArchive;
        _apAgentMoveNext = apAgentMoveNext;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<WorkflowPdfGenerationResult?> TryGenerateOnStepCompleteAsync(
        Domain.Entities.Workflow workflow,
        WorkflowInstance instance,
        WorkflowStep completedStep,
        string? formId,
        Guid? formEntryId,
        Guid userId,
        int? transactionId,
        CancellationToken cancellationToken = default,
        string? submittedFormDataJson = null)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
            return null;

        var block = await ResolveBlockAsync(workflow.Id, completedStep.ActivityId, cancellationToken);
        if (block?.Settings?.GeneratePDF != true)
            return null;

        if (block.Settings.PdfTemplate is not { } pdfTemplate
            || pdfTemplate.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            _logger.LogWarning(
                "Workflow PDF: generatePDF is true but pdfTemplate is missing for activity {ActivityId} workflow {WorkflowId}",
                completedStep.ActivityId,
                workflow.Id);
            return null;
        }

        if (string.IsNullOrWhiteSpace(opts.ServiceUrl))
        {
            _logger.LogInformation(
                "Workflow PDF: ServiceUrl not configured; skipping PDF for activity {ActivityId}. Set Workflow:PdfGeneration:ServiceUrl when Python is ready.",
                completedStep.ActivityId);
            return null;
        }

        if (!TryParseRepositoryId(workflow.RepositoryId, out var repositoryId))
            throw new InvalidOperationException("Workflow repositoryId is not configured for PDF archive.");

        var resolvedFormId = !string.IsNullOrWhiteSpace(formId) ? formId : workflow.FormId;
        if (string.IsNullOrWhiteSpace(resolvedFormId) || formEntryId is not { } entryId || entryId == Guid.Empty)
            throw new InvalidOperationException("Form id and formEntryId are required to generate workflow PDF.");

        var ezfbJson = await _ezfbFormDataLoader.LoadFormDataJsonAsync(
            resolvedFormId,
            entryId,
            cancellationToken);

        var sourceJson = !string.IsNullOrWhiteSpace(submittedFormDataJson)
            ? submittedFormDataJson
            : ezfbJson;

        if (string.IsNullOrWhiteSpace(sourceJson) && string.IsNullOrWhiteSpace(ezfbJson))
            throw new InvalidOperationException("Form data not found for PDF generation.");

        var pdfFormData = await _formDataMapper.MapMergedAsync(
            resolvedFormId,
            submittedFormDataJson,
            ezfbJson,
            templateDataKeys: null,
            cancellationToken);

        if (pdfFormData.Count == 0 && !string.IsNullOrWhiteSpace(sourceJson))
        {
            pdfFormData = await _formDataMapper.MapAsync(
                resolvedFormId,
                sourceJson,
                templateDataKeys: null,
                cancellationToken);
        }

        var label = block.Settings.Label ?? completedStep.Name ?? "Document";
        var reference = instance.ReferenceNumber ?? instance.Id.ToString("N")[..8];
        var fileName = SanitizeFileName($"{label}-{reference}.pdf");

        var pythonMetadata = new
        {
            tenantId = instance.TenantId,
            workflowId = workflow.Id,
            instanceId = instance.Id,
            activityId = completedStep.ActivityId,
            stepName = completedStep.Name,
            referenceNumber = instance.ReferenceNumber
        };

        var requestBody = new
        {
            templateJson = pdfTemplate,
            formData = pdfFormData,
            fileName,
            metadata = pythonMetadata
        };

        await using var pdfStream = await PostToPythonAsync(opts, requestBody, cancellationToken);
        var archiveMetadata = BuildArchiveFolderMetadata(
            pdfFormData,
            completedStep.ActivityId,
            completedStep.Name,
            instance.ReferenceNumber);
        var archive = await _attachmentArchive.UploadAsync(
            instance.TenantId,
            workflow.Id,
            instance.Id,
            repositoryId,
            pdfStream,
            fileName,
            "application/pdf",
            pdfStream.Length,
            metadataJson: JsonSerializer.Serialize(archiveMetadata),
            transactionId,
            userId,
            cancellationToken,
            allowIncompleteFolderMetadata: true);

        await BindGeneratedPdfToFormAsync(
            block.Settings.GeneratePDFFields,
            resolvedFormId,
            entryId,
            archive.ItemId,
            cancellationToken);

        _logger.LogInformation(
            "Workflow PDF generated for instance {InstanceId}, activity {ActivityId}, file {FileName}, attachment {AttachmentId}",
            instance.Id,
            completedStep.ActivityId,
            fileName,
            archive.AttachmentId);

        return new WorkflowPdfGenerationResult(
            archive.AttachmentId,
            archive.ItemId,
            fileName,
            new WorkflowPdfPythonRequestDto(
                pdfFormData,
                fileName,
                pythonMetadata,
                pdfTemplate));
    }

    private async Task BindGeneratedPdfToFormAsync(
        string[]? generatePdfFields,
        string formId,
        Guid formEntryId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        if (generatePdfFields is not { Length: > 0 } || itemId == Guid.Empty)
            return;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var jsonId in generatePdfFields)
        {
            if (string.IsNullOrWhiteSpace(jsonId))
                continue;
            fields[jsonId.Trim()] = itemId.ToString("N");
        }

        if (fields.Count == 0)
            return;

        await _apAgentMoveNext.ApplyFormDataToEzfbAsync(
            formId,
            formEntryId,
            fields,
            lineItemsJson: null,
            cancellationToken);
    }

    private async Task<WorkflowBlockDto?> ResolveBlockAsync(
        Guid workflowId,
        string? activityId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(activityId))
            return null;

        var json = await _jsonStorage.GetWorkflowJsonAsync(workflowId, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        var workflowJson = JsonSerializer.Deserialize<WorkflowJsonDto>(json, JsonOptions);
        return workflowJson?.Blocks?
            .FirstOrDefault(b => string.Equals(b.Id, activityId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string?> BuildArchiveFolderMetadata(
        IReadOnlyDictionary<string, string> pdfFormData,
        string? activityId,
        string? stepName,
        string? referenceNumber)
    {
        var metadata = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = "workflow-pdf-generation",
            ["activityId"] = activityId,
            ["stepName"] = stepName,
            ["referenceNumber"] = referenceNumber
        };

        foreach (var (key, value) in pdfFormData)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;
            metadata[key.Trim()] = value.Trim();
        }

        SetIfMissing(metadata, "Customer",
            FirstNonEmpty(pdfFormData, "Customer / Principal", "Customer", "CustomerName"));
        SetIfMissing(metadata, "Vessel",
            FirstNonEmpty(pdfFormData, "Vessel Name", "Vessel", "VesselName"));
        SetIfMissing(metadata, "Port",
            FirstNonEmpty(pdfFormData, "Port of Call", "Port", "PortOfCall"));
        SetIfMissing(metadata, "JobNo",
            FirstNonEmpty(pdfFormData, "Job No", "JobNo", "Document No.", "Voyage Number", "PDA Number", "FDA Number"));
        SetIfMissing(metadata, "Job No", metadata.GetValueOrDefault("JobNo"));

        var year = FirstNonEmpty(pdfFormData, "Year", "Document Date", "DocumentDate");
        if (!string.IsNullOrWhiteSpace(year))
        {
            if (DateTime.TryParse(year, out var dt))
                year = dt.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);
            else if (year.Length >= 4 && int.TryParse(year.AsSpan(0, 4), out var y) && y is >= 2000 and <= 2100)
                year = y.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(year))
            year = DateTime.UtcNow.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);

        SetIfMissing(metadata, "Year", year);

        return metadata;
    }

    private static void SetIfMissing(Dictionary<string, string?> metadata, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (!metadata.TryGetValue(key, out var existing) || string.IsNullOrWhiteSpace(existing))
            metadata[key] = value.Trim();
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string> data, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        foreach (var key in keys)
        {
            foreach (var (k, v) in data)
            {
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }
        }

        return null;
    }

    private async Task<MemoryStream> PostToPythonAsync(
        WorkflowPdfGenerationOptions opts,
        object requestBody,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(WorkflowPdfGenerationService));
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opts.TimeoutSeconds, 5, 600));

        var payload = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(opts.ServiceUrl, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Workflow PDF Python service returned {(int)response.StatusCode}: {Truncate(errorBody, 500)}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (mediaType.Contains("application/pdf", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms, cancellationToken);
            ms.Position = 0;
            return ms;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Workflow PDF Python service returned an empty response.");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (TryGetBase64Pdf(root, out var pdfBytes))
            return new MemoryStream(pdfBytes);

        throw new InvalidOperationException(
            "Workflow PDF Python service response must be application/pdf or JSON with pdf_base64/pdfBase64/contentBase64.");
    }

    private static bool TryGetBase64Pdf(JsonElement root, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        foreach (var name in new[] { "pdf_base64", "pdfBase64", "content_base64", "contentBase64", "base64", "pdf" })
        {
            if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
                continue;

            var value = prop.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var comma = value.IndexOf(',');
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                value = value[(comma + 1)..];

            bytes = Convert.FromBase64String(value);
            return bytes.Length > 0;
        }

        return false;
    }

    private static bool TryParseRepositoryId(string? value, out Guid repositoryId)
    {
        repositoryId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (Guid.TryParse(value, out repositoryId))
            return true;

        var hex = new string(value.Where(Uri.IsHexDigit).ToArray());
        return hex.Length >= 32 && Guid.TryParse(hex, out repositoryId);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "workflow-document.pdf" : cleaned;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
