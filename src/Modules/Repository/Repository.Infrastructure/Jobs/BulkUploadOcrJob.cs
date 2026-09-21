using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Jobs;

/// <summary>
/// Hangfire: for each staged bulk-upload file, call UploadForOcr then write OCR fields into the stage row.
/// Files stay in stage until the user exports (PUT index/{id}).
/// </summary>
public sealed class BulkUploadOcrJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantConnectionStringResolver _connectionStringResolver;
    private readonly ILogger<BulkUploadOcrJob> _logger;

    public BulkUploadOcrJob(
        IServiceScopeFactory scopeFactory,
        ITenantConnectionStringResolver connectionStringResolver,
        ILogger<BulkUploadOcrJob> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionStringResolver = connectionStringResolver;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    [AutomaticRetry(Attempts = 1)]
    [JobDisplayName("Bulk OCR · {0}")]
    public async Task Execute(string tenantDisplay, BulkOcrJobArgs args, PerformContext? context)
    {
        var jobId = context?.BackgroundJob.Id ?? "unknown";
        var stageIds = ParseStageIds(args.StageIdsCsv);

        context?.SetJobParameter("TenantId", args.TenantId.ToString("D"));
        context?.SetJobParameter("TenantName", tenantDisplay);
        context?.SetJobParameter("RepositoryId", args.RepositoryId.ToString("D"));
        context?.SetJobParameter("StageCount", stageIds.Count.ToString());

        _logger.LogInformation(
            "Bulk OCR job {JobId} started for tenant {TenantDisplay} ({TenantId}), repository {RepositoryId}, {Count} file(s)",
            jobId,
            tenantDisplay,
            args.TenantId,
            args.RepositoryId,
            stageIds.Count);

        if (stageIds.Count == 0)
            throw new InvalidOperationException("Bulk OCR job has no stage ids.");

        var connectionString = await _connectionStringResolver.GetConnectionStringAsync(args.TenantId);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Tenant connection string not found for {args.TenantId:D}.");

        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<ITenantConnectionProvider>().SetConnectionString(connectionString);
        var jobContext = services.GetRequiredService<JobExecutionContext>();
        jobContext.Set(args.TenantId, args.UserId ?? Guid.Empty);

        var uploadIndex = services.GetRequiredService<IRepositoryUploadIndexService>();
        var failures = 0;

        try
        {
            // One-by-one: UploadForOcr → merge fields → update stage table (same as single-file flow).
            foreach (var stageId in stageIds)
            {
                try
                {
                    await uploadIndex.ProcessBulkOcrForStageAsync(
                        args.RepositoryId,
                        args.TenantId,
                        stageId,
                        args.SharedFieldsJson,
                        args.PageNo,
                        args.OcrType,
                        args.ValidateType,
                        args.UserId,
                        CancellationToken.None);

                    _logger.LogInformation(
                        "Bulk OCR job {JobId}: stage {StageId} OCR completed and saved to stage table",
                        jobId,
                        stageId);
                }
                catch (Exception ex)
                {
                    failures++;
                    _logger.LogError(
                        ex,
                        "Bulk OCR job {JobId}: stage {StageId} OCR failed",
                        jobId,
                        stageId);
                }
            }

            if (failures > 0 && failures == stageIds.Count)
                throw new InvalidOperationException($"OCR failed for all {failures} file(s) in bulk upload job {jobId}.");

            _logger.LogInformation(
                "Bulk OCR job {JobId} finished. Succeeded={Succeeded}, Failed={Failed}",
                jobId,
                stageIds.Count - failures,
                failures);
        }
        finally
        {
            jobContext.Clear();
        }
    }

    private static List<Guid> ParseStageIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return new List<Guid>();

        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();
    }
}
