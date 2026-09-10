using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SaaSApp.Reporting.Application.Contracts;
using SaaSApp.Reporting.Infrastructure.Jobs;
using SaaSApp.Reporting.Infrastructure.Services;

namespace SaaSApp.Reporting.Infrastructure;

public static class ReportingInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddReportingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IReportDefinitionService, ReportDefinitionService>();
        services.AddScoped<IReportQueryService, ReportQueryService>();
        services.AddScoped<IReportExportService, ReportExportService>();
        services.AddScoped<IReportMailService, ReportMailService>();
        services.AddScoped<IReportScheduleService, ReportScheduleService>();
        services.AddScoped<RunScheduledReportJob>();
        return services;
    }
}
