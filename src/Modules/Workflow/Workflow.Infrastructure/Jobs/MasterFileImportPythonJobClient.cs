using System.Diagnostics;
using Hangfire;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Jobs;

public sealed class MasterFileImportPythonJobClient : IMasterFileImportPythonJobClient
{
    /// <summary>
    /// Hangfire <c>BackgroundJob.Enqueue</c> is synchronous and can block forever when Postgres
    /// storage / connection pool is stuck. Fail fast so uploadMasterFile returns an error instead of nginx 504.
    /// </summary>
    private static readonly TimeSpan EnqueueTimeout = TimeSpan.FromSeconds(15);

    private readonly ITenantDisplayResolver _tenantDisplay;
    private readonly ILogger<MasterFileImportPythonJobClient> _logger;

    public MasterFileImportPythonJobClient(
        ITenantDisplayResolver tenantDisplay,
        ILogger<MasterFileImportPythonJobClient> logger)
    {
        _tenantDisplay = tenantDisplay;
        _logger = logger;
    }

    public async Task<string> EnqueueAsync(MasterFileImportPythonJobArgs args, CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();
        var payloadBytes = args.PayloadJson?.Length ?? 0;

        _logger.LogInformation(
            "MasterFileImport enqueue starting. Tenant={TenantId}, Process={ProcessId}, Notification={NotificationId}, PayloadChars={PayloadChars}",
            args.TenantId,
            args.MasterFileProcessId,
            args.NotificationId,
            payloadBytes);

        var step = Stopwatch.StartNew();
        string tenantDisplay;
        try
        {
            tenantDisplay = await _tenantDisplay.ResolveAsync(args.TenantId, cancellationToken);
            _logger.LogInformation(
                "MasterFileImport tenant display resolved in {ElapsedMs}ms ({TenantDisplay})",
                step.ElapsedMilliseconds,
                tenantDisplay);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "MasterFileImport tenant display resolve FAILED after {ElapsedMs}ms for tenant {TenantId}",
                step.ElapsedMilliseconds,
                args.TenantId);
            throw;
        }

        step.Restart();
        string jobId;
        try
        {
            // Enqueue blocks on Hangfire PostgreSQL storage. Run with timeout so we can tell
            // "storage hang" apart from a healthy but slow write.
            jobId = await Task.Run(
                    () => BackgroundJob.Enqueue<RunMasterFileImportPythonJob>(j =>
                        j.Execute(tenantDisplay, args, null)),
                    cancellationToken)
                .WaitAsync(EnqueueTimeout, cancellationToken);

            _logger.LogInformation(
                "MasterFileImport Hangfire Enqueue OK in {ElapsedMs}ms. JobId={JobId}, TotalMs={TotalMs}",
                step.ElapsedMilliseconds,
                jobId,
                total.ElapsedMilliseconds);
        }
        catch (TimeoutException)
        {
            _logger.LogError(
                "MasterFileImport Hangfire Enqueue TIMED OUT after {TimeoutSec}s (step {ElapsedMs}ms). " +
                "Hangfire dashboard may still open; this usually means Hangfire Postgres storage write is blocked " +
                "(connection pool / lock). Check catalog DB connections and hangfire.job / hangfire.lock tables.",
                EnqueueTimeout.TotalSeconds,
                step.ElapsedMilliseconds);
            throw new InvalidOperationException(
                $"Hangfire enqueue timed out after {EnqueueTimeout.TotalSeconds:0}s. " +
                "Hangfire Postgres storage write is blocked (check catalog DB connections / locks).");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "MasterFileImport Hangfire Enqueue FAILED after {ElapsedMs}ms for process {ProcessId}",
                step.ElapsedMilliseconds,
                args.MasterFileProcessId);
            throw;
        }

        return jobId;
    }
}
