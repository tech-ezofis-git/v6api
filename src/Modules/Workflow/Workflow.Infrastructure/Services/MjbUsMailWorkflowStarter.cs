using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Application.Workflows.Commands.StartWorkflow;
using WorkflowEntity = SaaSApp.Workflow.Domain.Entities.Workflow;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class MjbUsMailWorkflowStarter : IMjbUsMailWorkflowStarter
{
    private readonly ITenantContext _tenantContext;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly IRepositoryFileStorage _fileStorage;
    private readonly IMediator _mediator;
    private readonly IMjbUsAgentJobClient _jobs;
    private readonly ILogger<MjbUsMailWorkflowStarter> _logger;

    public MjbUsMailWorkflowStarter(
        ITenantContext tenantContext,
        ITenantConnectionProvider connectionProvider,
        ICurrentUserProvider currentUserProvider,
        IRepositoryFileStorage fileStorage,
        IMediator mediator,
        IMjbUsAgentJobClient jobs,
        ILogger<MjbUsMailWorkflowStarter> logger)
    {
        _tenantContext = tenantContext;
        _connectionProvider = connectionProvider;
        _currentUserProvider = currentUserProvider;
        _fileStorage = fileStorage;
        _mediator = mediator;
        _jobs = jobs;
        _logger = logger;
    }

    public async Task<StartWorkflowCommandResult> StartAsync(
        WorkflowEntity workflow,
        byte[] attachmentBytes,
        string fileName,
        string? contentType,
        string? fromEmail,
        string? subject,
        DateTime? receivedAtUtc,
        string? messageId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.TenantId
            ?? throw new InvalidOperationException("Tenant context is required.");

        if (!MjbUsAgent.IsThisWorkflow(workflow.Id, tenantId))
            throw new InvalidOperationException("This starter only runs the MJB_US workflow.");

        if (string.IsNullOrWhiteSpace(workflow.RepositoryId))
            throw new InvalidOperationException("Workflow RepositoryId is not configured.");

        var connectionString = _tenantContext.ConnectionString
            ?? _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        var repositoryId = await ResolveRepositoryGuidAsync(
                connectionString, tenantId, workflow.RepositoryId, cancellationToken)
            ?? throw new InvalidOperationException($"Could not resolve repository id '{workflow.RepositoryId}'.");

        var safeName = SanitizeFileName(fileName);
        var blobPath = $"monitor/{MjbUsAgent.MonitorFolder}/{DateTime.UtcNow:yyyyMMddHHmmssfff}/{safeName}";
        await using (var stream = new MemoryStream(attachmentBytes, writable: false))
        {
            await _fileStorage.SaveAsync(
                tenantId,
                repositoryId,
                Guid.NewGuid(),
                safeName,
                stream,
                "EZOFIS",
                blobPath,
                cancellationToken);
        }

        var receivedAt = receivedAtUtc?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MjbUsForm.Set(fields, MjbUsForm.EmailSubject, MjbUsForm.EmailSubjectId, subject);
        MjbUsForm.Set(fields, MjbUsForm.FromEmail, MjbUsForm.FromEmailId, fromEmail);
        MjbUsForm.Set(fields, MjbUsForm.EmailReceivedAt, MjbUsForm.EmailReceivedAtId, receivedAt);
        MjbUsForm.Set(fields, MjbUsForm.ReceivedFilename, MjbUsForm.ReceivedFilenameId, safeName);
        MjbUsForm.Set(fields, MjbUsForm.MessageId, MjbUsForm.MessageIdId, messageId);

        var userId = _currentUserProvider.GetUserId() ?? EmailIngestActorResolver.SystemUserId;
        var firstStep = workflow.Steps.OrderBy(step => step.Order).FirstOrDefault()
            ?? throw new InvalidOperationException("MJB_US workflow has no steps.");

        var started = await _mediator.Send(
            new StartWorkflowCommand(
                workflow.Id,
                Context: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["emailIngest"] = true,
                    ["mjbUs"] = true,
                    ["blobPath"] = blobPath,
                    ["messageId"] = messageId,
                    ["from"] = fromEmail,
                    ["subject"] = subject,
                    ["receivedAtUtc"] = receivedAt,
                    ["attachmentFileName"] = safeName
                }),
                EnvType: "live",
                TriggerApAgentPythonJob: false,
                FormDataFields: fields),
            cancellationToken);

        var jobId = await _jobs.EnqueueAsync(
            new MjbUsAgentJobArgs(
                tenantId,
                userId,
                workflow.Id,
                started.InstanceId,
                MjbUsAgent.ActivityIdOf(firstStep),
                blobPath,
                safeName,
                fromEmail,
                subject,
                receivedAt,
                messageId,
                repositoryId.ToString("D"),
                workflow.FormId ?? MjbUsAgent.FormId.ToString("D")),
            cancellationToken);

        _logger.LogInformation(
            "MJB_US mail stored {BlobPath} and queued classification job {JobId} for instance {InstanceId}.",
            blobPath,
            jobId,
            started.InstanceId);

        return started;
    }

    private static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "document.pdf" : fileName.Trim());
        if (string.IsNullOrWhiteSpace(name))
            name = "document.pdf";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return name.Length > 180 ? name[..180] : name;
    }

    private static async Task<Guid?> ResolveRepositoryGuidAsync(
        string connectionString,
        Guid tenantGuid,
        string? repositoryIdLink,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryIdLink))
            return null;

        var trimmed = repositoryIdLink.Trim();
        if (Guid.TryParse(trimmed, out var parsed))
            return parsed;

        if (trimmed.Length == 32 && Guid.TryParseExact(trimmed, "N", out parsed))
            return parsed;

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacyInt))
            return null;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string byTableSql = """
            SELECT "Id"
            FROM repository."Repositories"
            WHERE "TenantId" = @TenantId AND "IsDeleted" = false
              AND ("ItemsTableName" LIKE @LegacyPattern OR "StageTableName" LIKE @LegacyPattern)
            LIMIT 1;
            """;

        await using var cmd = new NpgsqlCommand(byTableSql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantGuid);
        cmd.Parameters.AddWithValue("@LegacyPattern", $"%_{legacyInt}_%");
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }
}
