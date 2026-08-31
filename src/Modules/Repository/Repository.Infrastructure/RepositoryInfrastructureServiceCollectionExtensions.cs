using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Options;
using SaaSApp.Repository.Infrastructure.Jobs;
using SaaSApp.Repository.Infrastructure.Services;
using SaaSApp.Repository.Infrastructure.Storage;
using SaaSApp.SharedKernel.Options;

namespace SaaSApp.Repository.Infrastructure;

public static class RepositoryInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddRepositoryInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentsChatOptions>(configuration.GetSection(AgentsChatOptions.SectionName));
        services.Configure<RepositoryFileStorageOptions>(configuration.GetSection(RepositoryFileStorageOptions.SectionName));
        services.Configure<RepositoryShareOptions>(configuration.GetSection(RepositoryShareOptions.SectionName));
        services.Configure<RepositorySignRequestOptions>(configuration.GetSection(RepositorySignRequestOptions.SectionName));
        services.AddHttpClient<IOcrExtractionService, OcrExtractionService>();
        services.AddHttpClient<IRepositoryAiSummaryService, RepositoryAiSummaryService>();
        services.AddScoped<IRepositorySchemaService, RepositorySchemaService>();
        services.AddScoped<IRepositoryStorageSeedService, RepositoryStorageSeedService>();
        services.AddScoped<IStaticRepositoryProvisioner, StaticRepositoryProvisioner>();
        services.AddScoped<IRepositoryBrowseService, RepositoryBrowseService>();
        services.AddScoped<IRepositoryFolderService, RepositoryFolderService>();
        services.AddScoped<IRepositoryItemQueryService, RepositoryItemQueryService>();
        services.AddScoped<IRepositoryRelatedDocumentsService, RepositoryRelatedDocumentsService>();
        services.AddScoped<IRepositoryItemActivityService, RepositoryItemActivityService>();
        services.AddScoped<LocalRepositoryFileStorage>();
        services.AddScoped<EzofisBlobRepositoryFileStorage>();
        services.AddScoped<IRepositoryFileStorage, RepositoryFileStorageRouter>();
        services.AddScoped<RepositoryWorkflowAttachService>();
        services.AddScoped<IRepositoryFileUploadService, RepositoryFileUploadService>();
        services.AddScoped<IRepositoryArchiveFileUploadService, RepositoryArchiveFileUploadService>();
        services.AddScoped<IRepositoryUploadIndexService, RepositoryUploadIndexService>();
        services.AddScoped<IRepositoryItemShareService, RepositoryItemShareService>();
        services.AddScoped<IShareGuestUserProvisioningService, ShareGuestUserProvisioningService>();
        services.AddScoped<IRepositorySecurityService, RepositorySecurityService>();
        services.AddScoped<IRepositorySignRequestService, RepositorySignRequestService>();
        services.AddScoped<ArchiveStageItemJob>();
        return services;
    }
}
