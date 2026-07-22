using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Jobs;
using SaaSApp.Workflow.Infrastructure.Options;
using SaaSApp.Workflow.Infrastructure.Persistence;
using SaaSApp.Workflow.Infrastructure.Services;
using SaaSApp.Workflow.Infrastructure.Services.ConnectorAdapters;

namespace SaaSApp.Workflow.Infrastructure;

public static class WorkflowInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var slowQueryThresholdMs = configuration.GetValue<int?>("Performance:SlowSqlThresholdMs") ?? 500;
        // Database-per-tenant: WorkflowDbContext is created per request with the tenant's connection string (set by middleware).
        services.AddScoped<WorkflowDbContext>(sp =>
        {
            var tenantConnection = sp.GetRequiredService<ITenantConnectionProvider>();
            var connectionString = tenantConnection.ConnectionString
                ?? throw new InvalidOperationException("Tenant connection string has not been set for this request. Ensure tenant resolution middleware runs and the tenant exists in the catalog.");
            var optionsBuilder = new DbContextOptionsBuilder<WorkflowDbContext>();
            optionsBuilder.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", WorkflowDbContext.SchemaName);
                sql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(30),
                    errorNumbersToAdd: null);
            });
            optionsBuilder.LogTo(
                action: message =>
                {
                    if (!message.Contains("Executed DbCommand", StringComparison.Ordinal) ||
                        !TryGetCommandDurationMs(message, out var durationMs) ||
                        durationMs < slowQueryThresholdMs)
                    {
                        return;
                    }

                    Console.WriteLine($"SLOW SQL ({durationMs:F0}ms): {message}");
                },
                minimumLevel: LogLevel.Information);
            var tenantProvider = sp.GetRequiredService<ITenantProvider>();
            return new WorkflowDbContext(optionsBuilder.Options, tenantProvider);
        });

        services.AddScoped<IWorkflowInstanceStore, WorkflowInstanceStore>();
        services.AddScoped<IWorkflowRepository, WorkflowRepository>();
        services.AddScoped<IUnitOfWork, WorkflowUnitOfWork>();
        services.AddScoped<CurrentUserProvider>();
        services.AddScoped<ICurrentUserProvider, AmbientCurrentUserProvider>();
        services.AddScoped<ITenantContext, WorkflowTenantContext>();
        services.AddScoped<IWorkflowTableCreator, WorkflowTableCreator>();
        services.AddScoped<IDynamicTableRepository, DynamicTableRepository>();
        
        // New services for full workflow creation
        services.AddScoped<IWorkflowJsonStorageService, WorkflowJsonStorageService>();
        services.AddScoped<IFormJsonStorageService, FormJsonStorageService>();
        services.AddScoped<IFormService, FormService>();
        services.AddScoped<IFormEntryService, FormEntryService>();
        services.AddScoped<IWorkflowTicketSearchService, WorkflowTicketSearchService>();
        services.AddScoped<IFormMasterFileUploadService, FormMasterFileUploadService>();
        services.AddScoped<IConnectorService, ConnectorService>();
        services.Configure<ConnectorOAuthOptions>(configuration.GetSection(ConnectorOAuthOptions.SectionName));
        services.AddHttpClient(nameof(IConnectorProviderAdapter));
        services.AddScoped<IConnectorProviderAdapter, GcpConnectorAdapter>();
        services.AddScoped<IConnectorProviderAdapter, GmailConnectorAdapter>();
        services.AddScoped<IConnectorProviderAdapter, OutlookConnectorAdapter>();
        services.AddScoped<IConnectorProviderAdapter, OneDriveConnectorAdapter>();
        services.AddScoped<IConnectorProviderAdapter, TeamsConnectorAdapter>();
        services.AddScoped<IConnectorProviderAdapter, DropboxConnectorAdapter>();
        services.AddScoped<IConnectorProviderAdapter, QuickBooksConnectorAdapter>();
        services.AddScoped<IConnectorOAuthService, ConnectorOAuthService>();
        services.AddScoped<IEmailIngestService, EmailIngestService>();
        services.AddScoped<IWorkflowEmailIngestLinker, WorkflowEmailIngestLinker>();
        services.AddScoped<IMasterResolveService, MasterResolveService>();
        services.AddScoped<RunEmailIngestPollJob>();
        services.AddScoped<IWorkflowSecurityService, WorkflowSecurityService>();
        services.AddScoped<IWorkflowInitiationService, WorkflowInitiationService>();
        services.AddScoped<IWorkflowSlaService, WorkflowSlaService>();
        services.AddScoped<IWorkflowMlService, WorkflowMlService>();
        services.AddScoped<WorkflowLegacyMailboxSyncService>();
        services.AddScoped<IWorkflowLegacyMailboxSyncService>(sp => sp.GetRequiredService<WorkflowLegacyMailboxSyncService>());
        services.AddScoped<IUserEmailLookup, UserEmailLookup>();
        services.AddScoped<IWorkflowLegacyMailboxQueryService, WorkflowLegacyMailboxQueryService>();
        services.AddScoped<IWorkflowInstanceHistoryService, WorkflowInstanceHistoryService>();
        services.AddScoped<IWorkflowProcessAddonService, WorkflowProcessAddonService>();
        services.AddScoped<IWorkflowLegacyTransactionSyncService, WorkflowLegacyTransactionSyncService>();
        services.AddScoped<IWorkflowInboxShareAssignmentService, WorkflowInboxShareAssignmentService>();
        services.AddScoped<IApDashboardQueryService, ApDashboardQueryService>();
        services.AddScoped<IWorkflowStepSyncService, WorkflowStepSyncService>();
        services.AddScoped<IWorkflowStartBootstrapService, WorkflowStartBootstrapService>();
        services.AddScoped<IWorkflowApAgentMoveNextService, WorkflowApAgentMoveNextService>();
        services.AddScoped<IWorkflowEzfbFormDataLoader, WorkflowEzfbFormDataLoader>();
        services.AddScoped<IApAgentJobProgressService, ApAgentJobProgressService>();
        services.AddScoped<IApAgentJobStatusService, ApAgentJobStatusService>();
        services.Configure<FormMasterFileImportOptions>(configuration.GetSection(FormMasterFileImportOptions.SectionName));
        services.AddHttpClient(nameof(MasterFileImportPythonPipelineService), client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddScoped<IMasterFileImportPythonPipelineService, MasterFileImportPythonPipelineService>();
        services.AddScoped<IMasterFileImportPythonJobClient, MasterFileImportPythonJobClient>();
        services.AddScoped<RunMasterFileImportPythonJob>();
        services.Configure<ApAgentOptions>(configuration.GetSection(ApAgentOptions.SectionName));
        services.Configure<EmailIngestOptions>(configuration.GetSection(EmailIngestOptions.SectionName));
        services.AddHttpClient(nameof(ApAgentPythonPipelineService), client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddScoped<IApAgentPythonPipelineService, ApAgentPythonPipelineService>();
        services.AddScoped<IApAgentPythonJobClient, ApAgentPythonJobClient>();
        services.AddScoped<RunApAgentPythonJob>();

        services.AddHttpClient(nameof(FieldMappingService), client =>
        {
            client.Timeout = TimeSpan.FromMinutes(2);
        });
        services.AddScoped<IFieldMappingService, FieldMappingService>();

        return services;
    }

    private static bool TryGetCommandDurationMs(string message, out double durationMs)
    {
        durationMs = 0;
        var marker = "Executed DbCommand (";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += marker.Length;
        var end = message.IndexOf("ms)", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        var raw = message[start..end].Trim();
        return double.TryParse(raw, out durationMs);
    }
}
