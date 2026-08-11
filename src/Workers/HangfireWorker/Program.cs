using Hangfire;
using Hangfire.PostgreSql;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");

builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(Hangfire.CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(connectionString));

builder.Services.AddHangfireServer(options =>
    options.WorkerCount = builder.Configuration.GetValue<int?>("Hangfire:WorkerCount") ?? 5);

var host = builder.Build();

try
{
    Log.Information("Starting SaaSApp Hangfire worker");
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Hangfire worker terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
