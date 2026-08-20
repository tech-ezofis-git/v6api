using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Options;
using SaaSApp.SharedKernel.Options;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class OcrExtractionService : IOcrExtractionService
{
    private readonly HttpClient _httpClient;
    private readonly AgentsChatOptions _agentsChat;
    private readonly ILogger<OcrExtractionService> _logger;

    public OcrExtractionService(
        HttpClient httpClient,
        IOptions<AgentsChatOptions> agentsChat,
        ILogger<OcrExtractionService> logger)
    {
        _httpClient = httpClient;
        _agentsChat = agentsChat.Value;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromMinutes(RepositoryOcrDefaults.TimeoutMinutes);
    }

    public async Task<OcrExtractionResult> ExtractFromFileAsync(
        byte[] fileBytes,
        IReadOnlyList<string> parameters,
        IReadOnlyList<Dictionary<string, IReadOnlyList<string>>>? tableParameters = null,
        string? pageNo = null,
        string? ocrType = null,
        string? validateType = null,
        string? filename = null,
        Guid? repositoryId = null,
        CancellationToken cancellationToken = default)
    {
        if (fileBytes.Length == 0)
            throw new ArgumentException("File is empty.");

        if (parameters.Count == 0)
            throw new ArgumentException("At least one OCR parameter is required in fields.");

        var apiUrl = _agentsChat.ResolveChatUrl();
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            throw new InvalidOperationException(
                "Agents:ChatUrl is not configured in appsettings.");
        }

        var resolvedPageNo = ResolvePageNo(pageNo, RepositoryOcrDefaults.DefaultPageNo);

        _logger.LogInformation(
            "Calling OCR /chat {Url} with {ParameterCount} parameters, pageno={PageNo}, file size {FileSize} bytes",
            apiUrl,
            parameters.Count,
            resolvedPageNo,
            fileBytes.Length);

        var rawJson = await PostMultipartAsync(
            apiUrl, fileBytes, parameters, tableParameters,
            resolvedPageNo, filename, cancellationToken);

        var fieldList = OcrResultParser.TryParseFieldList(rawJson);
        return new OcrExtractionResult(rawJson, fieldList);
    }

    private async Task<string> PostMultipartAsync(
        string apiUrl,
        byte[] fileBytes,
        IReadOnlyList<string> parameters,
        IReadOnlyList<Dictionary<string, IReadOnlyList<string>>>? tableParameters,
        string pageNo,
        string? filename,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();

        form.Add(new StringContent($"ocr-{Guid.NewGuid():N}"), "session_id");
        form.Add(new StringContent("ocr"), "intent");
        form.Add(new StringContent(pageNo), "pageno");
        form.Add(new StringContent(RepositoryOcrDefaults.Instruction), "instruction");

        var parametersJson = JsonSerializer.Serialize(parameters);
        form.Add(new StringContent(parametersJson), "parameters");

        var tableParamsJson = tableParameters is { Count: > 0 }
            ? JsonSerializer.Serialize(tableParameters)
            : "[]";
        form.Add(new StringContent(tableParamsJson), "tableparameters");

        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var resolvedFilename = string.IsNullOrWhiteSpace(filename) ? "document.pdf" : filename.Trim();
        form.Add(fileContent, "file", resolvedFilename);

        using var response = await _httpClient.PostAsync(apiUrl, form, cancellationToken);
        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OCR /chat returned {StatusCode}: {Body}", (int)response.StatusCode, Truncate(rawJson, 500));
            throw new InvalidOperationException(
                $"OCR API failed ({(int)response.StatusCode}): {Truncate(rawJson, 500)}");
        }

        return ExtractOcrPayload(rawJson);
    }

    /// <summary>
    /// Agents /chat wraps OCR output in <c>ocr_result</c>. Extract that for downstream parsing
    /// or fall back to the raw body if shape doesn't match.
    /// </summary>
    private static string ExtractOcrPayload(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return rawJson;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("ocr_result", out var ocrResult)
                && ocrResult.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return ocrResult.GetRawText();
            }
        }
        catch (JsonException)
        {
            // Not JSON; return raw body for legacy parsers.
        }

        return rawJson;
    }

    private static string ResolvePageNo(string? pageNo, string defaultPageNo)
    {
        var value = string.IsNullOrWhiteSpace(pageNo) ? defaultPageNo : pageNo.Trim();
        if (IsPlaceholderValue(value))
            value = defaultPageNo.Trim();

        if (string.Equals(value, "-1", StringComparison.Ordinal))
            return value;

        if (value.Contains('-', StringComparison.Ordinal))
        {
            var parts = value.Split('-', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], out _)
                && int.TryParse(parts[1], out _))
            {
                return value;
            }

            return ResolvePageNo(null, defaultPageNo);
        }

        if (int.TryParse(value, out var page) && page > 0)
            return value;

        return int.TryParse(defaultPageNo, out var defaultPage) && defaultPage > 0
            ? defaultPage.ToString()
            : "1";
    }

    private static bool IsPlaceholderValue(string value) =>
        string.Equals(value, "string", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
