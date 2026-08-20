using Hangfire;
using Hangfire.PostgreSql;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using SaaSApp.Api.Middleware;
using SaaSApp.Api.Options;
using SaaSApp.Api.Services;
using SaaSApp.Api.Services.Jira;
using SaaSApp.Api.Swagger;
using SaaSApp.Billing.Application;
using SaaSApp.Billing.Infrastructure;
using SaaSApp.Catalog;
using SaaSApp.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Reporting.Application;
using SaaSApp.Reporting.Infrastructure;
using SaaSApp.Security;
using SaaSApp.Users.Application;
using SaaSApp.Users.Infrastructure;
using SaaSApp.Workflow.Application;
using SaaSApp.Workflow.Infrastructure;
using SaaSApp.Workflow.Infrastructure.Jobs;
using SaaSApp.Dms.Infrastructure;
using Serilog;
using System.Reflection;
using SaaSApp.BlobStorage;
using SaaSApp.Repository.Application;
using SaaSApp.Repository.Infrastructure;
using SaaSApp.ActivityLog.Application;
using SaaSApp.ActivityLog.Infrastructure;
using SaaSApp.SharedKernel.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.ActivityLog.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.EventLog.json", optional: true, reloadOnChange: true);


// Serilog + Application Insights (clear default providers to avoid duplicate log lines)
builder.Logging.ClearProviders();
builder.AddSaaSAppLogging();

// HTTPS enforcement (use 5001 in dev so redirect lands on the right port)
builder.Services.AddHttpsRedirection(options =>
{
    options.RedirectStatusCode = StatusCodes.Status307TemporaryRedirect;
    options.HttpsPort = builder.Environment.IsDevelopment() ? 5001 : 443;
});

// Multi-tenancy (database-per-tenant: catalog + tenant connection resolution)
builder.Services.AddMultiTenancy();
builder.Services.AddCatalog(builder.Configuration);
builder.Services.AddScoped<IPlaygroundApiKeyService, PlaygroundApiKeyService>();
builder.Services.AddScoped<ITenantSignupService, TenantSignupService>();
builder.Services.Configure<TenantPilotUserOptions>(
    builder.Configuration.GetSection(TenantPilotUserOptions.SectionName));
builder.Services.Configure<TenantDefaultCreditOptions>(
    builder.Configuration.GetSection(TenantDefaultCreditOptions.SectionName));
builder.Services.Configure<JiraOptions>(
    builder.Configuration.GetSection(JiraOptions.SectionName));
builder.Services.Configure<AgentsChatOptions>(
    builder.Configuration.GetSection(AgentsChatOptions.SectionName));
builder.Services.AddHttpClient<JiraIssueClient>();
builder.Services.AddScoped<SupportTicketStore>();
builder.Services.AddScoped<SupportTicketEmailService>();
builder.Services.AddScoped<IWorkflowSchemaService, WorkflowSchemaService>();
builder.Services.AddScoped<IDmsSchemaService, DmsSchemaService>();
builder.Services.AddHttpClient(nameof(LegacyWorkflowTransactionService));
builder.Services.AddScoped<ILegacyWorkflowTransactionService, LegacyWorkflowTransactionService>();
builder.Services.AddScoped<SaaSApp.Workflow.Application.Contracts.IWorkflowStartAttachmentUploader, WorkflowStartAttachmentUploader>();
builder.Services.AddScoped<SaaSApp.Workflow.Application.Contracts.IWorkflowAttachmentArchiveService, WorkflowAttachmentArchiveService>();
builder.Services.AddMemoryCache();
var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString;
        options.InstanceName = builder.Configuration["Redis:InstanceName"] ?? "SaaSApp:";
    });
}
else
{
    // Redis optional: use in-memory distributed cache when no Redis connection is configured.
    builder.Services.AddDistributedMemoryCache();
}
builder.Services.AddScoped<ITwoFactorService, TwoFactorService>();
builder.Services.AddScoped<IEzofisAuthService, EzofisAuthService>();
builder.Services.AddScoped<SaaSApp.Users.Application.Contracts.IUserTenantRoleSync, UserTenantRoleSync>();

// JWT Bearer: Microsoft Entra ID (Azure AD), Auth0, and Ezofis
var azureAdClientId = builder.Configuration["AzureAd:ClientId"];
var auth0Domain = builder.Configuration["Auth0:Domain"];
var ezofisKey = builder.Configuration["EzofisAuth:SigningKey"];
var hasAzureAd = !string.IsNullOrWhiteSpace(azureAdClientId);
var hasAuth0 = !string.IsNullOrEmpty(auth0Domain);
var hasEzofis = !string.IsNullOrEmpty(ezofisKey);

var authenticationSchemes = new List<string>();
string? defaultScheme = null;

if (hasAzureAd)
{
    authenticationSchemes.Add(JwtBearerDefaults.AuthenticationScheme);
    defaultScheme = JwtBearerDefaults.AuthenticationScheme;
}

if (hasAuth0)
{
    authenticationSchemes.Add("Auth0");
    defaultScheme ??= "Auth0";
}

string? ezofisScheme = null;
if (hasEzofis)
{
    // Swagger and policies use "Bearer"; register Ezofis as Bearer when it is the only JWT handler.
    ezofisScheme = authenticationSchemes.Contains(JwtBearerDefaults.AuthenticationScheme)
        ? "Ezofis"
        : JwtBearerDefaults.AuthenticationScheme;
    authenticationSchemes.Add(ezofisScheme);
    defaultScheme ??= ezofisScheme;
}

var authBuilder = builder.Services.AddAuthentication(options =>
{
    if (!string.IsNullOrEmpty(defaultScheme))
    {
        options.DefaultAuthenticateScheme = defaultScheme;
        options.DefaultChallengeScheme = defaultScheme;
    }
});

if (hasAzureAd)
{
    authBuilder.AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
}

if (hasAuth0)
{
    authBuilder.AddJwtBearer("Auth0", options =>
    {
        options.Authority = $"https://{auth0Domain}/";
        options.Audience = builder.Configuration["Auth0:Audience"];
    });
}

if (hasEzofis)
{
    authBuilder.AddJwtBearer(ezofisScheme!, options =>
    {
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["EzofisAuth:Issuer"] ?? "Ezofis",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["EzofisAuth:Audience"] ?? "Ezofis",
            ValidateLifetime = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(ezofisKey!)),
            ValidateIssuerSigningKey = true,
            RoleClaimType = "role"
        };
        options.MapInboundClaims = false;
    });
}

builder.Services.AddAuthorization();
builder.Services.AddSaaSAppAuthorizationPolicies(authenticationSchemes.Count > 0 ? authenticationSchemes.ToArray() : null);

// Modules
builder.Services.AddUsersApplication();
builder.Services.AddUsersInfrastructure(builder.Configuration);
builder.Services.AddBillingApplication();
builder.Services.AddBillingInfrastructure(builder.Configuration);
builder.Services.AddReportingApplication();
builder.Services.AddReportingInfrastructure(builder.Configuration);
builder.Services.AddWorkflowApplication();
builder.Services.AddWorkflowInfrastructure(builder.Configuration);
builder.Services.AddDmsInfrastructure();
builder.Services.AddEzofisBlobStorage(builder.Configuration);
builder.Services.AddRepositoryApplication();
builder.Services.AddRepositoryInfrastructure(builder.Configuration);
builder.Services.AddActivityLogApplication();
builder.Services.AddActivityLogInfrastructure(builder.Configuration);

// Hangfire (background jobs)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var hangfireEnabled = !string.IsNullOrWhiteSpace(connectionString);
if (hangfireEnabled)
{
    var hangfireStorageOptions = new PostgreSqlStorageOptions
    {
        // Reduce catalog DB polling so HTTP requests are not competing with Hangfire every few seconds.
        QueuePollInterval = TimeSpan.FromSeconds(15),
        JobExpirationCheckInterval = TimeSpan.FromHours(1),
    };

    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(Hangfire.CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(connectionString, hangfireStorageOptions));

    if (builder.Configuration.GetValue<bool?>("Hangfire:RunServerInApi") ?? true)
    {
        // API host: keep workers low — each job holds SQL + HTTP to Python (minutes). High WorkerCount
        // starves IIS/Kestrel threads and makes every API call feel slow.
        var apiWorkers = builder.Configuration.GetValue<int?>("Hangfire:ApiWorkerCount")
            ?? builder.Configuration.GetValue<int?>("Hangfire:WorkerCount")
            ?? 5;
        apiWorkers = Math.Clamp(apiWorkers, 1, 10);

        builder.Services.AddHangfireServer(options =>
        {
            options.WorkerCount = apiWorkers;
            options.ServerName = $"{Environment.MachineName}:V6Api";
            options.Queues = ["default"];
            options.SchedulePollingInterval = TimeSpan.FromSeconds(15);
        });

        Log.Information("Hangfire server in API process: {WorkerCount} worker(s)", apiWorkers);
    }
}
else
{
    Log.Warning("Hangfire is disabled because ConnectionStrings:DefaultConnection is missing.");
}

// API controllers — keep default numeric enum serialization (workflow status = 1, not "active").
// String enums (e.g. AP dashboard period) use [JsonConverter] on those types only.
builder.Services.AddControllers();
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly()));

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSaaSAppSwaggerDoc();
});

// Health checks
builder.Services.AddHealthChecks();

// CORS (configure for Azure API Management / SPA as needed)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();

var showDetailedErrors = app.Configuration.GetValue<bool>("Diagnostics:ShowDetailedErrors")
    || app.Environment.IsDevelopment();
if (showDetailedErrors)
{
    app.UseDeveloperExceptionPage();
}

app.UseSerilogRequestLogging();

// IIS virtual directory support (example: /V6API). Also reads ASPNETCORE_PATHBASE env var.
var pathBase = builder.Configuration["PathBase"]
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_PATHBASE");
if (!string.IsNullOrWhiteSpace(pathBase))
{
    app.UsePathBase(pathBase);
}

// HTTPS redirection (configurable; keep off when hosting IIS on HTTP-only localhost)
var httpsRedirectionEnabled = builder.Configuration.GetValue<bool?>("HttpsRedirection:Enabled")
    ?? !app.Environment.IsDevelopment();
if (httpsRedirectionEnabled)
{
    app.UseHttpsRedirection();
}

// Secure headers
app.UseSecureHeaders();

// Correlation ID (must run early)
app.UseCorrelationId();
app.UseMiddleware<RequestPerformanceLoggingMiddleware>();

app.UseCors();

var swaggerEnabled = app.Environment.IsDevelopment() || (builder.Configuration.GetValue<bool?>("Swagger:Enabled") ?? false);
if (swaggerEnabled)
{
    app.UseSwagger(options =>
    {
        options.RouteTemplate = "swagger/{documentName}/swagger.json";
    });
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "swagger";
        // Relative endpoint works correctly under IIS virtual directories/path base.
        options.SwaggerEndpoint("v1/swagger.json", "SaaSApp API v1");
    });
}

app.UseAuthentication();
app.UseAuthorization();

// X-Tenant-Id may be an email: resolve to Guid via catalog.UserTenants before tenant DB connection
app.UseMiddleware<EmailTenantResolutionMiddleware>();
// Resolve tenant DB connection from catalog (must run after auth so JWT/tid is available)
app.UseMiddleware<TenantConnectionMiddleware>();
app.UseMiddleware<RepositoryShareMiddleware>();
app.UseMiddleware<UsersPermissionSchemaEnsuringMiddleware>();
// Ensure workflow schema exists in tenant DB before workflow operations
app.UseMiddleware<WorkflowSchemaEnsuringMiddleware>();
app.UseMiddleware<DmsSchemaEnsuringMiddleware>();
app.UseMiddleware<RepositorySchemaEnsuringMiddleware>();

var activityLogEnabled = builder.Configuration.GetValue("ActivityLog:Enabled", false);
var eventLogEnabled = builder.Configuration.GetValue("EventLog:Enabled", false);
if (activityLogEnabled || eventLogEnabled)
    app.UseMiddleware<ActivityLogSchemaEnsuringMiddleware>();
if (activityLogEnabled)
    app.UseMiddleware<ApiActivityLoggingMiddleware>();
if (eventLogEnabled)
    app.UseMiddleware<ApiEventLoggingMiddleware>();

app.MapControllers();
app.MapHealthChecks("/health");

// Hangfire dashboard (protect in production with auth)
if (hangfireEnabled)
{
    app.MapHangfireDashboard("/hangfire");

    var emailIngestHangfire = app.Configuration.GetValue("EmailIngest:HangfireEnabled", true);
    if (emailIngestHangfire)
    {
        var cron = app.Configuration.GetValue("EmailIngest:HangfireCron", "*/5 * * * *")
                   ?? "*/5 * * * *";
        RecurringJob.AddOrUpdate<RunEmailIngestPollJob>(
            "email-ingest-poll",
            job => job.Execute(null),
            cron);
        Log.Information(
            "Registered Hangfire email-ingest-poll cron={Cron} (only tenants with enabled mailboxes)",
            cron);
    }
    else
    {
        RecurringJob.RemoveIfExists("email-ingest-poll");
        Log.Information("Email ingest Hangfire job disabled (EmailIngest:HangfireEnabled=false)");
    }
}

try
{
    Log.Information("Starting SaaSApp API");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
