using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.SharedKernel.Options;
using SaaSApp.Workflow.Application;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// On completion of a Quantics "New Packaging Requisition" ticket, sends the ticket's
/// form fields (ezfb column names) to the Python chat API with intent <c>form_sync</c>,
/// which upserts the packaging master form records.
/// </summary>
public sealed class QuanticsFormSyncService : IQuanticsFormSyncService
{
    private static readonly HashSet<string> SystemColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "item_id", "created_at", "modified_at", "created_by", "modified_by", "is_deleted", "today_task", "is_marked"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly AgentsChatOptions _agentsChat;
    private readonly ILogger<QuanticsFormSyncService> _logger;

    public QuanticsFormSyncService(
        IHttpClientFactory httpClientFactory,
        ITenantConnectionProvider connectionProvider,
        IOptions<AgentsChatOptions> agentsChat,
        ILogger<QuanticsFormSyncService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _connectionProvider = connectionProvider;
        _agentsChat = agentsChat.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(QuanticsFormSyncJobArgs args, string hangfireJobId, CancellationToken cancellationToken = default)
    {
        if (!QuanticsPackaging.IsThisWorkflow(args.WorkflowId, args.TenantId))
            throw new InvalidOperationException("Quantics form sync job was queued for a different workflow.");

        var chatUrl = _agentsChat.ResolveChatUrl();
        if (string.IsNullOrWhiteSpace(chatUrl))
            throw new InvalidOperationException("Agents:ChatUrl is not configured.");

        var tenantCs = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection is not set.");

        var sourceFormId = string.IsNullOrWhiteSpace(args.FormId)
            ? QuanticsPackaging.SourceFormId.ToString("D")
            : args.FormId!;

        await using var connection = new NpgsqlConnection(tenantCs);
        await connection.OpenAsync(cancellationToken);

        var formEntryId = args.FormEntryId is { } entry && entry != Guid.Empty
            ? entry
            : await ResolveFormEntryIdAsync(connection, args.WorkflowId, args.InstanceId, cancellationToken)
              ?? throw new InvalidOperationException($"No form entry found for instance {args.InstanceId:D}.");

        var fields = await ReadSourceFieldsAsync(connection, sourceFormId, formEntryId, cancellationToken);
        if (fields.Count == 0)
            throw new InvalidOperationException($"Source form entry {formEntryId:D} has no field values to sync.");

        var body = JsonSerializer.Serialize(new
        {
            session_id = $"form-sync-{args.InstanceId:N}",
            intent = QuanticsPackaging.FormSyncIntent,
            message = "Sync packaging requisition ticket",
            payload = new
            {
                source_form_id = sourceFormId,
                target_form_id = QuanticsPackaging.TargetFormId.ToString("D"),
                payload = fields
            }
        });

        _logger.LogInformation(
            "Quantics form sync for instance {InstanceId}: POST {ChatUrl} intent {Intent} with {FieldCount} field(s).",
            args.InstanceId,
            chatUrl,
            QuanticsPackaging.FormSyncIntent,
            fields.Count);

        var client = _httpClientFactory.CreateClient(nameof(QuanticsFormSyncService));
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(chatUrl, content, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Form sync /chat returned {(int)response.StatusCode}: {Truncate(responseJson, 500)}");

        using var doc = JsonDocument.Parse(responseJson);
        var result = FindProperty(doc.RootElement, "form_sync_result");
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
        {
            var message = result.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : "Python reported form_sync failure.";
            throw new InvalidOperationException(message);
        }

        var reply = FindProperty(doc.RootElement, "reply");
        _logger.LogInformation(
            "Quantics form sync completed for instance {InstanceId}: {Reply}",
            args.InstanceId,
            reply.ValueKind == JsonValueKind.String ? reply.GetString() : responseJson);
    }

    private static async Task<Guid?> ResolveFormEntryIdAsync(
        NpgsqlConnection connection,
        Guid workflowId,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var suffix = workflowId.ToString("N")[..8];
        var sql = $"""
            SELECT form_entry_id
            FROM workflow.process_form_{suffix}
            WHERE workflow_instance_id = @InstanceId AND is_deleted = false
            ORDER BY id DESC
            LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@InstanceId", instanceId);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is Guid id && id != Guid.Empty ? id : null;
    }

    /// <summary>Reads the source ezfb row as column-name → value (empty values skipped).</summary>
    private static async Task<Dictionary<string, string>> ReadSourceFieldsAsync(
        NpgsqlConnection connection,
        string sourceFormId,
        Guid formEntryId,
        CancellationToken cancellationToken)
    {
        var suffix = FormIdNaming.GetEzfbTableSuffix(FormIdNaming.NormalizeFormId(sourceFormId));
        var tableName = $"ezfb_{suffix}_items";

        var columns = new List<string>();
        await using (var colsCmd = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'dbo' AND table_name = @TableName ORDER BY ordinal_position",
            connection))
        {
            colsCmd.Parameters.AddWithValue("@TableName", tableName);
            await using var reader = await colsCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var column = reader.GetString(0);
                if (!SystemColumns.Contains(column))
                    columns.Add(column);
            }
        }

        if (columns.Count == 0)
            throw new InvalidOperationException($"Source form table dbo.{tableName} was not found.");

        var selectList = string.Join(", ", columns.Select(c => $"\"{c.Replace("\"", "\"\"")}\""));
        var sql = $"SELECT {selectList} FROM dbo.\"{tableName}\" WHERE item_id = @ItemId AND (is_deleted = false OR is_deleted IS NULL)";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@ItemId", formEntryId);
        await using var dataReader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await dataReader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"Source form entry {formEntryId:D} was not found in dbo.{tableName}.");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < dataReader.FieldCount; i++)
        {
            if (dataReader.IsDBNull(i))
                continue;
            var value = Convert.ToString(dataReader.GetValue(i), CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;
            fields[dataReader.GetName(i)] = FlattenValue(value);
        }

        return fields;
    }

    /// <summary>Multi-choice values are stored as JSON arrays; send them comma-joined.</summary>
    private static string FlattenValue(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('['))
            return value;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var parts = doc.RootElement.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
                    .Where(s => !string.IsNullOrWhiteSpace(s));
                return string.Join(", ", parts);
            }
        }
        catch (JsonException)
        {
        }

        return value;
    }

    private static JsonElement FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return default;
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return prop.Value;
        }

        return default;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
