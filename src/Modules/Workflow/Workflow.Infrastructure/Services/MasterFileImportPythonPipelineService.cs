using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>POST master file import payload to Python data-import service.</summary>
public sealed class MasterFileImportPythonPipelineService : IMasterFileImportPythonPipelineService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITenantConnectionStringResolver _connectionStringResolver;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly JobExecutionContext _jobContext;
    private readonly IOptions<FormMasterFileImportOptions> _options;
    private readonly ILogger<MasterFileImportPythonPipelineService> _logger;

    public MasterFileImportPythonPipelineService(
        IHttpClientFactory httpClientFactory,
        ITenantConnectionStringResolver connectionStringResolver,
        ITenantConnectionProvider connectionProvider,
        JobExecutionContext jobContext,
        IOptions<FormMasterFileImportOptions> options,
        ILogger<MasterFileImportPythonPipelineService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _connectionStringResolver = connectionStringResolver;
        _connectionProvider = connectionProvider;
        _jobContext = jobContext;
        _options = options;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        MasterFileImportPythonJobArgs args,
        string? hangfireJobId = null,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!FormMasterFileImportDefaults.Enabled)
        {
            _logger.LogInformation(
                "Master file import disabled; skipping process {ProcessId}.",
                args.MasterFileProcessId);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.PythonServiceUrl))
        {
            throw new InvalidOperationException(
                "FormMasterFileImport:PythonServiceUrl is not configured.");
        }

        var connectionString = await _connectionStringResolver.GetConnectionStringAsync(args.TenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Tenant connection string not found for {args.TenantId:D}.");

        _connectionProvider.SetConnectionString(connectionString);
        _jobContext.Set(args.TenantId, args.UserId);

        try
        {
            var requestBody = BuildPythonRequestBody(args, hangfireJobId);
            await PostToPythonAsync(options, requestBody, cancellationToken);

            _logger.LogInformation(
                "Master file import Python call finished for process {ProcessId}, notification {NotificationId}.",
                args.MasterFileProcessId,
                args.NotificationId);
        }
        finally
        {
            _jobContext.Clear();
        }
    }

    private static string BuildPythonRequestBody(MasterFileImportPythonJobArgs args, string? hangfireJobId)
    {
        if (string.IsNullOrWhiteSpace(args.PayloadJson))
            throw new InvalidOperationException("Master file import payload JSON is empty.");

        using var doc = JsonDocument.Parse(args.PayloadJson);
        var root = doc.RootElement;

        // Python expects a flat body (fileName, formId, tenantId, …) — not nested under importPayload.
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("importPayload", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            root = nested;
        }

        if (string.IsNullOrWhiteSpace(hangfireJobId)
            && HasProperty(root, "notifyId")
            && HasProperty(root, "filepath")
            && HasProperty(root, "masterFileProcessId"))
        {
            return root.GetRawText();
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                // Python expects notifyId, not notificationId.
                if (string.Equals(prop.Name, "notificationId", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteNumber("notifyId", prop.Value.GetInt32());
                    continue;
                }

                prop.WriteTo(writer);
            }

            if (!HasProperty(root, "notifyId") && !HasProperty(root, "notificationId"))
                writer.WriteNumber("notifyId", args.NotificationId);
            if (!HasProperty(root, "filepath") && !HasProperty(root, "filePath"))
            {
                var blobPath = TryGetStringProperty(root, "filepath")
                    ?? TryGetStringProperty(root, "filePath")
                    ?? TryGetStringProperty(root, "fileName");
                if (!string.IsNullOrWhiteSpace(blobPath))
                    writer.WriteString("filepath", blobPath);
            }
            if (!HasProperty(root, "masterFileProcessId"))
                writer.WriteNumber("masterFileProcessId", args.MasterFileProcessId);
            if (!string.IsNullOrWhiteSpace(hangfireJobId) && !HasProperty(root, "hangfireJobId"))
                writer.WriteString("hangfireJobId", hangfireJobId);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool HasProperty(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? TryGetStringProperty(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var prop in obj.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()
                : prop.Value.GetRawText();
        }

        return null;
    }

    private async Task PostToPythonAsync(
        FormMasterFileImportOptions options,
        string requestBody,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(MasterFileImportPythonPipelineService));
        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, FormMasterFileImportDefaults.TimeoutMinutes)));

        _logger.LogInformation(
            "Posting master file import payload to Python at {Url}",
            options.PythonServiceUrl);

        using var response = await client.PostAsync(options.PythonServiceUrl, content, timeoutCts.Token);
        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Master file import Python returned {(int)response.StatusCode}: {Truncate(body, 500)}");
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
