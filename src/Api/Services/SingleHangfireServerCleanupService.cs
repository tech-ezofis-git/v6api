using Hangfire;
using Hangfire.Storage;
using Serilog;

namespace SaaSApp.Api.Services;

/// <summary>
/// One Hangfire server for the deployed API. Each process id is unique, so a publish
/// used to leave the previous container Aborted, and a local API showed up beside it.
/// </summary>
public sealed class SingleHangfireServerCleanupService : IHostedService
{
    private readonly JobStorage _storage;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _serverPrefix;
    private CancellationTokenSource? _cts;

    public SingleHangfireServerCleanupService(
        JobStorage storage,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration)
    {
        _storage = storage;
        _lifetime = lifetime;
        var name = configuration.GetValue<string>("Hangfire:ServerName");
        if (string.IsNullOrWhiteSpace(name))
            name = "v6-api";
        _serverPrefix = name.Trim().ToLowerInvariant() + ":";
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        _lifetime.ApplicationStarted.Register(() =>
        {
            _ = RunAsync(token);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            RemoveExtraServers();
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            RemoveExtraServers();
        }
        catch (OperationCanceledException)
        {
            // Host is stopping.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Hangfire server cleanup failed.");
        }
    }

    private void RemoveExtraServers()
    {
        var servers = _storage.GetMonitoringApi().Servers();
        var freshCutoff = DateTime.UtcNow.AddSeconds(-45);
        using var connection = _storage.GetConnection();
        foreach (var server in servers)
        {
            var isThisApp = server.Name.StartsWith(_serverPrefix, StringComparison.OrdinalIgnoreCase);
            var heartbeat = server.Heartbeat ?? DateTime.MinValue;
            if (isThisApp && heartbeat >= freshCutoff)
                continue;

            connection.RemoveServer(server.Name);
            Log.Information("Removed Hangfire server {Server}.", server.Name);
        }
    }
}
