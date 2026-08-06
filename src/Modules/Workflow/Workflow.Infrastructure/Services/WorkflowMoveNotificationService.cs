using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class WorkflowMoveNotificationService : IWorkflowMoveNotificationService
{
    private const string TicketSubmitted = "Ticket Submitted";
    private const string TicketReceived = "Ticket Received";

    private static readonly JsonSerializerOptions RemarksJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ITenantContext _tenantContext;
    private readonly IUserEmailLookup _userEmailLookup;
    private readonly WorkflowMoveNotificationOptions _options;
    private readonly ILogger<WorkflowMoveNotificationService> _logger;

    public WorkflowMoveNotificationService(
        ITenantContext tenantContext,
        IUserEmailLookup userEmailLookup,
        IOptions<WorkflowMoveNotificationOptions> options,
        ILogger<WorkflowMoveNotificationService> logger)
    {
        _tenantContext = tenantContext;
        _userEmailLookup = userEmailLookup;
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

            var submittedUserId = context.SubmittedModifiedByUserId;
            var receivedUserId = context.ReceivedCreatedByUserId is Guid createdBy && createdBy != Guid.Empty
                ? createdBy
                : submittedUserId;

            var userIds = new List<Guid> { submittedUserId };
            if (receivedUserId != Guid.Empty && receivedUserId != submittedUserId)
                userIds.Add(receivedUserId);

            var profiles = await _userEmailLookup.GetProfilesAsync(userIds, cancellationToken);
            var submittedUserName = ResolveUserName(submittedUserId, profiles);
            var receivedUserName = ResolveUserName(receivedUserId, profiles);

            var receivedStageName = !string.IsNullOrWhiteSpace(context.NextStageName)
                ? context.NextStageName!
                : context.CurrentStageName;
            var receivedStageType = !string.IsNullOrWhiteSpace(context.NextStageName)
                ? context.NextStageType
                : context.CurrentStageType;

            var inputJson = new
            {
                workflowId = context.WorkflowId.ToString("D"),
                processId = context.InstanceId.ToString("D")
            };

            var category = string.IsNullOrWhiteSpace(_options.Category) ? "workflow" : _options.Category.Trim();
            var review = context.Review?.Trim() ?? string.Empty;

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await FormMasterFileNotificationStore.EnsureTableAsync(connection, cancellationToken);
            var createdByLegacyId = await FormMasterFileNotificationStore.TryResolveLegacyUserIdAsync(
                connection,
                submittedUserId,
                cancellationToken);

            var submittedRemarks = JsonSerializer.Serialize(new
            {
                workflowName = context.WorkflowName,
                instanceId = context.InstanceId.ToString("D"),
                review,
                stageName = context.CurrentStageName,
                stageType = context.CurrentStageType ?? string.Empty,
                userName = submittedUserName
            }, RemarksJsonOptions);

            var receivedRemarks = JsonSerializer.Serialize(new
            {
                workflowName = context.WorkflowName,
                instanceId = context.InstanceId.ToString("D"),
                review,
                stageName = receivedStageName,
                stageType = receivedStageType ?? string.Empty,
                userName = receivedUserName
            }, RemarksJsonOptions);

            await FormMasterFileNotificationStore.InsertAsync(
                connection,
                title: TicketSubmitted,
                status: TicketSubmitted,
                remarks: submittedRemarks,
                inputJson: inputJson,
                category: category,
                createdByLegacyId,
                cancellationToken);

            await FormMasterFileNotificationStore.InsertAsync(
                connection,
                title: TicketReceived,
                status: TicketReceived,
                remarks: receivedRemarks,
                inputJson: inputJson,
                category: category,
                createdByLegacyId,
                cancellationToken);

            _logger.LogInformation(
                "Inserted Ticket Submitted and Ticket Received notifications for instance {InstanceId}.",
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

    private static string ResolveUserName(
        Guid userId,
        IReadOnlyDictionary<Guid, UserProfileLookupDto> profiles)
    {
        if (userId == Guid.Empty)
            return string.Empty;

        if (!profiles.TryGetValue(userId, out var profile))
            return userId.ToString("D");

        var fullName = string.Join(
            " ",
            new[] { profile.FirstName, profile.LastName }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim()));

        if (!string.IsNullOrWhiteSpace(fullName))
            return fullName;

        if (!string.IsNullOrWhiteSpace(profile.DisplayName))
            return profile.DisplayName.Trim();

        if (!string.IsNullOrWhiteSpace(profile.Email))
            return profile.Email.Trim();

        return userId.ToString("D");
    }
}
