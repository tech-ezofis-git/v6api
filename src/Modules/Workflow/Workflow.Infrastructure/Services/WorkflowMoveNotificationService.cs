using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class WorkflowMoveNotificationService : IWorkflowMoveNotificationService
{
    private const string TicketReceived = "Ticket Received";
    private const string SeverityInfo = "Info";
    private const string DetailsTab = "Details";

    private readonly ITenantContext _tenantContext;
    private readonly WorkflowMoveNotificationOptions _options;
    private readonly ILogger<WorkflowMoveNotificationService> _logger;

    public WorkflowMoveNotificationService(
        ITenantContext tenantContext,
        IOptions<WorkflowMoveNotificationOptions> options,
        ILogger<WorkflowMoveNotificationService> logger)
    {
        _tenantContext = tenantContext;
        _options = options.Value;
        _logger = logger;
    }

    public async Task TryInsertMoveNotificationsAsync(
        WorkflowMoveNotificationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return;

        try
        {
            var connectionString = _tenantContext.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogWarning(
                    "Skip move-next notifications for instance {InstanceId}: tenant connection string not resolved.",
                    context.InstanceId);
                return;
            }

            var category = string.IsNullOrWhiteSpace(_options.Category) ? "workflow" : _options.Category.Trim();
            var review = context.Review?.Trim() ?? string.Empty;
            var suffix = context.WorkflowId.ToString("N")[..8];
            var transactionTable = $"workflow.[transaction_{suffix}]";

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await FormMasterFileNotificationStore.EnsureTableAsync(connection, cancellationToken);

            var submittedTxn = await LoadTransactionAsync(
                connection,
                transactionTable,
                context.CurrentTransactionId,
                context.InstanceId,
                openOnly: false,
                cancellationToken);

            var receivedTxn = context.NextTransactionId is int nextId
                ? await LoadTransactionByIdAsync(connection, transactionTable, nextId, cancellationToken)
                : await LoadOpenTransactionAsync(connection, transactionTable, context.InstanceId, cancellationToken);

            receivedTxn ??= submittedTxn;

            var receivedStageName = FirstNonEmpty(receivedTxn?.StageName, context.NextStageName, context.CurrentStageName) ?? string.Empty;
            var receivedStageType = FirstNonEmpty(receivedTxn?.StageType, context.NextStageType, context.CurrentStageType) ?? string.Empty;

            var submittedActor = submittedTxn?.ActivityUserId is Guid submittedActorId && submittedActorId != Guid.Empty
                ? submittedActorId
                : context.SubmittedModifiedByUserId;
            var receivedActor = receivedTxn?.ActivityUserId is Guid receivedActorId && receivedActorId != Guid.Empty
                ? receivedActorId
                : context.ReceivedCreatedByUserId is Guid receivedFallback && receivedFallback != Guid.Empty
                    ? receivedFallback
                    : submittedActor;

            var receivedCreatedAtUtc = receivedTxn?.CreatedAt;
            var receivedLegacyId = await FormMasterFileNotificationStore.TryResolveLegacyUserIdAsync(
                connection, receivedActor, cancellationToken);

            var receivedData = BuildData(
                context,
                receivedStageName,
                receivedStageType,
                review,
                receivedTxn);

            var receivedText = WorkflowNotificationMessageMapper.ReceivedMessage(
                context.WorkflowName,
                context.InstanceId);

            await FormMasterFileNotificationStore.InsertMoveNotificationAsync(
                connection,
                title: receivedText,
                status: TicketReceived,
                message: receivedText,
                data: receivedData,
                severity: SeverityInfo,
                createdAtUtc: receivedCreatedAtUtc,
                createdByGuid: receivedActor,
                createdByLegacyId: receivedLegacyId,
                category: category,
                cancellationToken);

            _logger.LogInformation(
                "Inserted Ticket Received notification for instance {InstanceId}.",
                context.InstanceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to insert move-next notifications for instance {InstanceId}; move-next was not affected.",
                context.InstanceId);
        }
    }

    private static object BuildData(
        WorkflowMoveNotificationContext context,
        string stageName,
        string stageType,
        string review,
        TransactionNotifyRow? txn) =>
        new
        {
            instanceId = context.InstanceId.ToString("D"),
            stageName,
            stageType,
            review,
            tab = DetailsTab,
            transactionId = txn?.TransactionGuid is Guid guid && guid != Guid.Empty
                ? guid.ToString("D")
                : txn?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            workflowId = context.WorkflowId.ToString("D"),
            workflowName = context.WorkflowName?.Trim() ?? string.Empty
        };

    private static async Task<TransactionNotifyRow?> LoadTransactionAsync(
        SqlConnection connection,
        string transactionTable,
        int? transactionId,
        Guid instanceId,
        bool openOnly,
        CancellationToken cancellationToken)
    {
        if (transactionId is int id)
        {
            var byId = await LoadTransactionByIdAsync(connection, transactionTable, id, cancellationToken);
            if (byId != null)
                return byId;
        }

        return openOnly
            ? await LoadOpenTransactionAsync(connection, transactionTable, instanceId, cancellationToken)
            : await LoadLatestTransactionAsync(connection, transactionTable, instanceId, cancellationToken);
    }

    private static Task<TransactionNotifyRow?> LoadTransactionByIdAsync(
        SqlConnection connection,
        string transactionTable,
        int transactionId,
        CancellationToken cancellationToken) =>
        LoadSingleTransactionAsync(
            connection,
            $"""
            SELECT TOP 1 Id, TransactionGuid, ActivityUserId, CreatedAt, ModifiedAt, StageName, StageType, Review
            FROM {transactionTable}
            WHERE Id = @Id AND IsDeleted = 0
            """,
            cmd => cmd.Parameters.AddWithValue("@Id", transactionId),
            cancellationToken);

    private static Task<TransactionNotifyRow?> LoadOpenTransactionAsync(
        SqlConnection connection,
        string transactionTable,
        Guid instanceId,
        CancellationToken cancellationToken) =>
        LoadSingleTransactionAsync(
            connection,
            $"""
            SELECT TOP 1 Id, TransactionGuid, ActivityUserId, CreatedAt, ModifiedAt, StageName, StageType, Review
            FROM {transactionTable}
            WHERE WorkflowInstanceId = @InstanceId AND IsDeleted = 0 AND ActionStatus = 0
            ORDER BY CreatedAt DESC, Id DESC
            """,
            cmd => cmd.Parameters.AddWithValue("@InstanceId", instanceId),
            cancellationToken);

    private static Task<TransactionNotifyRow?> LoadLatestTransactionAsync(
        SqlConnection connection,
        string transactionTable,
        Guid instanceId,
        CancellationToken cancellationToken) =>
        LoadSingleTransactionAsync(
            connection,
            $"""
            SELECT TOP 1 Id, TransactionGuid, ActivityUserId, CreatedAt, ModifiedAt, StageName, StageType, Review
            FROM {transactionTable}
            WHERE WorkflowInstanceId = @InstanceId AND IsDeleted = 0
            ORDER BY ISNULL(ModifiedAt, CreatedAt) DESC, Id DESC
            """,
            cmd => cmd.Parameters.AddWithValue("@InstanceId", instanceId),
            cancellationToken);

    private static async Task<TransactionNotifyRow?> LoadSingleTransactionAsync(
        SqlConnection connection,
        string sql,
        Action<SqlCommand> bind,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 15 };
            bind(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new TransactionNotifyRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7));
        }
        catch (SqlException)
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private sealed record TransactionNotifyRow(
        int Id,
        Guid? TransactionGuid,
        Guid? ActivityUserId,
        DateTime CreatedAt,
        DateTime? ModifiedAt,
        string? StageName,
        string? StageType,
        string? Review);
}
