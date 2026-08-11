using Hangfire;
using Hangfire.Server;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Jobs;

/// <summary>Hangfire: promote staged monitor file into repository archive layout via V6 archive upload.</summary>
public sealed class ArchiveStageItemJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantConnectionStringResolver _connectionStringResolver;
    private readonly ILogger<ArchiveStageItemJob> _logger;

    public ArchiveStageItemJob(
        IServiceScopeFactory scopeFactory,
        ITenantConnectionStringResolver connectionStringResolver,
        ILogger<ArchiveStageItemJob> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionStringResolver = connectionStringResolver;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 2)]
    [JobDisplayName("Archive stage · {0}")]
    public async Task Execute(string tenantDisplay, ArchiveStageJobArgs args, PerformContext? context)
    {
        var jobId = context?.BackgroundJob.Id ?? "unknown";
        context?.SetJobParameter("TenantId", args.TenantId.ToString("D"));
        context?.SetJobParameter("TenantName", tenantDisplay);

        _logger.LogInformation(
            "Archive stage job {JobId} started for tenant {TenantDisplay} ({TenantId}), repository {RepositoryId}, stage {StageId}",
            jobId,
            tenantDisplay,
            args.TenantId,
            args.RepositoryId,
            args.StageId);

        var connectionString = await _connectionStringResolver.GetConnectionStringAsync(args.TenantId);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Tenant connection string not found for {args.TenantId:D}.");

        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<ITenantConnectionProvider>().SetConnectionString(connectionString);
        var jobContext = services.GetRequiredService<JobExecutionContext>();
        jobContext.Set(args.TenantId, args.UserId ?? Guid.Empty);

        try
        {
            var provisioner = services.GetRequiredService<IStaticRepositoryProvisioner>();
            var storageSeed = services.GetRequiredService<IRepositoryStorageSeedService>();
            var fileStorage = services.GetRequiredService<IRepositoryFileStorage>();
            var archiveUpload = services.GetRequiredService<IRepositoryArchiveFileUploadService>();

            var repo = await provisioner.GetRepositoryAsync(args.RepositoryId, args.TenantId, default)
                ?? throw new InvalidOperationException("Repository not found.");

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            var row = await RepositoryStageStore.GetAsync(connection, repo, args.TenantId, args.StageId, default)
                ?? throw new InvalidOperationException("Stage row not found.");

            if (string.IsNullOrWhiteSpace(row.FilePath) || string.IsNullOrWhiteSpace(row.FileName))
                throw new InvalidOperationException("Stage row is missing file path or name.");

            var providers = await storageSeed.ListProvidersAsync(args.TenantId, default);
            var providerCode = providers.First(p => p.Id == row.StorageProviderId).Code;

            await using var source = await fileStorage.OpenReadAsync(
                args.TenantId,
                row.FilePath,
                providerCode,
                default);

            await using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            buffer.Position = 0;

            var metadataJson = System.Text.Json.JsonSerializer.Serialize(row.FieldValues);
            var uploadRequest = new RepositoryUploadItemRequest(
                buffer,
                row.FileName,
                row.FileType,
                FileSize: row.FileSize,
                Metadata: metadataJson);

            var result = await archiveUpload.UploadItemAsync(
                args.RepositoryId,
                args.TenantId,
                uploadRequest,
                args.UserId,
                default);

            await RepositoryStageStore.MarkArchivedAsync(
                connection,
                repo.StageTableName,
                args.TenantId,
                args.StageId,
                result.ItemId,
                default);

            _logger.LogInformation(
                "Archive stage job {JobId} completed. Stage {StageId} promoted to item {ItemId}",
                jobId,
                args.StageId,
                result.ItemId);
        }
        finally
        {
            jobContext.Clear();
        }
    }
}
