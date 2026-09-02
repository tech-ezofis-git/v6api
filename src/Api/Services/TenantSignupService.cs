using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SaaSApp.Api.Options;
using SaaSApp.Catalog;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;
using SaaSApp.MultiTenancy;
using SaaSApp.Users.Domain.Entities;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.ActivityLog.Application.Contracts;
using SaaSApp.ActivityLog.Infrastructure.Options;
using SaaSApp.Users.Infrastructure.Persistence;

namespace SaaSApp.Api.Services;

/// <summary>
/// Creates a new tenant: provisions dedicated DB, applies migrations, registers in catalog.
/// Optionally creates admin user with password for Ezofis login.
/// </summary>
public interface ITenantSignupService
{
    Task<TenantSignupResult> SignupAsync(TenantSignupRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Signup request. Name or OrganizationName required. Email + Password create admin with Ezofis login.</summary>
public record TenantSignupRequest(
    Guid? TenantId,
    string Name,
    string? OrganizationName,
    string? Email,
    string? Password,
    string? LoginType,
    int? LicenseType,
    string? FirstName,
    string? LastName,
    string? DatabaseName,
    string? SignupSource,
    string? Platform,
    string? AppVersion);
/// <summary>Result of tenant signup. Save TenantId for X-Tenant-Id header and catalog linkage.</summary>
public record TenantSignupResult(Guid TenantId, string Name, string DatabaseName, string ConnectionString);

public sealed class TenantSignupService : ITenantSignupService
{
    private readonly ITenantDatabaseCreator _dbCreator;
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;
    private readonly IConfiguration _configuration;
    private readonly IUserTenantRegistry _userTenantRegistry;
    private readonly IRepositorySchemaService _repositorySchema;
    private readonly IRepositoryStorageSeedService _repositoryStorageSeed;
    private readonly IActivityLogSchemaService _activityLogSchema;
    private readonly ActivityLogOptions _activityLogOptions;
    private readonly EventLogOptions _eventLogOptions;
    private readonly ITenantPilotUserProvisioningService _pilotUserProvisioning;
    private readonly TenantDefaultCreditOptions _defaultCreditOptions;

    public TenantSignupService(
        ITenantDatabaseCreator dbCreator,
        IDbContextFactory<CatalogDbContext> catalogFactory,
        IConfiguration configuration,
        IUserTenantRegistry userTenantRegistry,
        IRepositorySchemaService repositorySchema,
        IRepositoryStorageSeedService repositoryStorageSeed,
        IActivityLogSchemaService activityLogSchema,
        IOptions<ActivityLogOptions> activityLogOptions,
        IOptions<EventLogOptions> eventLogOptions,
        ITenantPilotUserProvisioningService pilotUserProvisioning,
        IOptions<TenantDefaultCreditOptions> defaultCreditOptions)
    {
        _dbCreator = dbCreator;
        _catalogFactory = catalogFactory;
        _configuration = configuration;
        _userTenantRegistry = userTenantRegistry;
        _repositorySchema = repositorySchema;
        _repositoryStorageSeed = repositoryStorageSeed;
        _activityLogSchema = activityLogSchema;
        _activityLogOptions = activityLogOptions.Value;
        _eventLogOptions = eventLogOptions.Value;
        _pilotUserProvisioning = pilotUserProvisioning;
        _defaultCreditOptions = defaultCreditOptions.Value;
    }

    public async Task<TenantSignupResult> SignupAsync(TenantSignupRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = request.TenantId ?? Guid.NewGuid();
        var licenseType = request.LicenseType ?? 3; // Old behavior default: DMS + Workflow
        if (licenseType is < 1 or > 3)
            throw new ArgumentException("LicenseType must be 1 (DMS), 2 (Workflow), or 3 (DMS+Workflow).", nameof(request));

        var displayName = !string.IsNullOrWhiteSpace(request.OrganizationName)
            ? request.OrganizationName!.Trim()
            : request.Name.Trim();

        var prefix = _configuration["TenantDatabase:NamePrefix"]?.Trim() ?? "ezofis_Tenant";
        prefix = new string(prefix.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (string.IsNullOrEmpty(prefix))
            prefix = "ezofis_Tenant";

        string dbName;
        string tenantConnectionString;
        await using (var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken))
        {
            var exists = await catalog.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken);
            if (exists)
                throw new InvalidOperationException("Tenant already registered with this Id.");

            dbName = request.DatabaseName?.Trim() ?? "";
            if (string.IsNullOrEmpty(dbName))
            {
                dbName = BuildDefaultTenantDatabaseName(prefix, tenantId);
                if (await _dbCreator.DatabaseExistsAsync(dbName, cancellationToken)
                    && await IsTenantDatabaseOwnedByAnotherTenantAsync(catalog, dbName, tenantId, cancellationToken))
                {
                    dbName = $"{prefix}_{tenantId:N}".ToLowerInvariant();
                }
            }

            dbName = new string(dbName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (string.IsNullOrEmpty(dbName))
                throw new ArgumentException("Invalid database name.", nameof(request));

            if (!await _dbCreator.DatabaseExistsAsync(dbName, cancellationToken))
                await _dbCreator.CreateDatabaseAsync(dbName, cancellationToken);

            tenantConnectionString = BuildTenantConnectionString(dbName);

            await ApplyUsersMigrationsAsync(tenantConnectionString, tenantId, cancellationToken);
            await ApplyWorkflowSchemaAsync(tenantConnectionString, cancellationToken);
            await ApplyConnectorSchemaAsync(tenantConnectionString, cancellationToken);
            await _repositorySchema.ApplyBaseSchemaAsync(tenantConnectionString, cancellationToken);
            if (_activityLogOptions.Enabled || _eventLogOptions.Enabled)
                await _activityLogSchema.ApplyBaseSchemaAsync(tenantConnectionString, cancellationToken);
            await _repositoryStorageSeed.EnsureDefaultProvidersAsync(tenantConnectionString, tenantId, null, cancellationToken);

            // Old licenseType intent:
            // 1 = DMS, 2 = Workflow, 3 = DMS + Workflow.
            if (licenseType is 2 or 3)
            {
                await ApplyWorkflowSchemaAsync(tenantConnectionString, cancellationToken);
                await ApplyConnectorSchemaAsync(tenantConnectionString, cancellationToken);
            }

            if (licenseType is 1 or 3)
            {
                await ApplyDmsSchemaAsync(tenantConnectionString, cancellationToken);
            }

            // Add tenant to catalog first (UserTenants FK requires Tenant to exist)
            catalog.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = displayName,
                ConnectionString = tenantConnectionString,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                Email = request.Email?.Trim(),
                SignupSource = request.SignupSource?.Trim(),
                Platform = request.Platform?.Trim(),
                AppVersion = request.AppVersion?.Trim(),
                LoginType = request.LoginType?.Trim() ?? "EZOFIS"
            });
            try
            {
                await catalog.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_Tenants_Name", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Unique org name was removed; if an old catalog DB still has IX_Tenants_Name, guide operator to drop it.
                throw new InvalidOperationException(
                    "Organization name unique constraint still exists on catalog.Tenants (IX_Tenants_Name). " +
                    "Run scripts/DropTenantsNameUniqueConstraint.sql, then retry signup. Same organization name is allowed on multiple tenants.");
            }

            await SeedDefaultCreditMasterAsync(catalog, tenantId, cancellationToken);

            var adminEmail = request.Email?.Trim();
            if (!string.IsNullOrEmpty(adminEmail))
            {
                var adminUserId = await CreateTenantUserAsync(
                    tenantConnectionString,
                    tenantId,
                    adminEmail,
                    displayName,
                    request.Password,
                    User.RoleAdmin,
                    request.LoginType,
                    request.FirstName,
                    request.LastName,
                    cancellationToken);
                await _userTenantRegistry.AddOrUpdateAsync(
                    adminEmail,
                    tenantId,
                    User.RoleAdmin,
                    adminUserId,
                    cancellationToken);
            }

            await CreatePilotUserIfConfiguredAsync(
                tenantConnectionString,
                tenantId,
                adminEmail,
                cancellationToken);

            await EnsureBuiltinRolesAsync(tenantConnectionString, tenantId, cancellationToken);
        }

        return new TenantSignupResult(tenantId, displayName, dbName, tenantConnectionString);
    }

    /// <summary>
    /// Seeds the default credit allocation into catalog dbo.creditMaster for the tenant's signup month.
    /// Failures here never block tenant creation (e.g. when the creditMaster table has not been provisioned).
    /// </summary>
    private async Task SeedDefaultCreditMasterAsync(
        CatalogDbContext catalog,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (!_defaultCreditOptions.Enabled)
            return;

        try
        {
            var nowUtc = DateTime.UtcNow;
            var ist = GetIndiaTimeZone();
            var nowIst = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, ist);
            var month = nowIst.Month;
            var year = nowIst.Year;

            var alreadyExists = await catalog.CreditMasters.AnyAsync(
                c => c.TenantId == tenantId
                     && c.AllocationMonth == month
                     && c.AllocationYear == year
                     && c.CreditType == _defaultCreditOptions.CreditType
                     && !c.IsDeleted,
                cancellationToken);
            if (alreadyExists)
                return;

            var initial = Math.Max(0, _defaultCreditOptions.InitialCredit);
            DateTime? validTo = _defaultCreditOptions.ValidDays > 0
                ? nowUtc.AddDays(_defaultCreditOptions.ValidDays)
                : null;

            catalog.CreditMasters.Add(new CreditMaster
            {
                TenantId = tenantId,
                AllocationMonth = month,
                AllocationYear = year,
                CreditType = _defaultCreditOptions.CreditType,
                SubscriptionType = _defaultCreditOptions.SubscriptionType,
                Status = _defaultCreditOptions.Status,
                InitialCredit = initial,
                BalanceCredit = initial,
                OverallConsumedCredit = 0,
                Remarks = _defaultCreditOptions.Remarks,
                CreatedAt = nowUtc,
                CreatedBy = "system",
                IsDeleted = false,
                ValidFromDate = nowUtc,
                ValidToDate = validTo
            });

            await catalog.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: default creditMaster seed failed for tenant {tenantId}: {ex.Message}");
        }
    }

    private static TimeZoneInfo GetIndiaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
    }

    private static string BuildDefaultTenantDatabaseName(string prefix, Guid tenantId)
    {
        // Same convention as workflow tables: first 8 hex chars of tenant GUID (no dashes).
        var suffix = tenantId.ToString("N")[..8].ToLowerInvariant();
        return $"{prefix}_{suffix}";
    }

    private static async Task<bool> IsTenantDatabaseOwnedByAnotherTenantAsync(
        CatalogDbContext catalog,
        string databaseName,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var tenants = await catalog.Tenants
            .AsNoTracking()
            .Select(t => new { t.Id, t.ConnectionString })
            .ToListAsync(cancellationToken);

        foreach (var tenant in tenants)
        {
            if (tenant.Id == tenantId)
                continue;
            if (ConnectionStringUsesDatabase(tenant.ConnectionString, databaseName))
                return true;
        }

        return false;
    }

    private static bool ConnectionStringUsesDatabase(string? connectionString, string databaseName)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        try
        {
            // Catalog.dbo.Tenants.ConnectionString is DATA, not config -- existing rows still
            // hold SQL-Server-format strings until Phase 7 rewrites them per tenant. Parse as
            // Postgres first (the format all newly-created tenants get); the catch below
            // falls back to a plain substring check for any still-SQL-Server-format legacy
            // row, so this keeps working correctly during that mixed-format transition period.
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return string.Equals(builder.Database, databaseName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return connectionString.Contains(databaseName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private string BuildTenantConnectionString(string databaseName)
    {
        var catalogConnection = _configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection not found.");
        // SQL-Server-only options (Encrypt, TrustServerCertificate, MultipleActiveResultSets)
        // have no Postgres analog and are dropped rather than mistranslated -- NpgsqlConnectionStringBuilder
        // simply won't parse them if they were ever present, and DefaultConnection no longer
        // carries them (rewritten to Postgres format in Phase 1).
        var builder = new NpgsqlConnectionStringBuilder(catalogConnection) { Database = databaseName };
        return builder.ConnectionString;
    }

    private static async Task ApplyUsersMigrationsAsync(string tenantConnectionString, Guid tenantId, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UsersDbContext>();
        optionsBuilder.UseNpgsql(tenantConnectionString, npgsql =>
        {
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", UsersDbContext.SchemaName);
            npgsql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorCodesToAdd: null);
        });
        var tenantProvider = new StaticTenantProvider(tenantId);
        await using var context = new UsersDbContext(optionsBuilder.Options, tenantProvider);
        await context.Database.MigrateAsync(cancellationToken);
        await UsersSchemaEnsurer.EnsureExtendedUserColumnsAsync(context, cancellationToken);
        await UsersSchemaEnsurer.EnsureGroupsTablesAsync(context, cancellationToken);
        await UsersSchemaEnsurer.EnsurePermissionCategoriesAsync(context, cancellationToken);
        await UsersSchemaEnsurer.EnsureRoleMenusTablesAsync(context, cancellationToken);
    }

    private static async Task EnsureBuiltinRolesAsync(
        string tenantConnectionString,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UsersDbContext>();
        optionsBuilder.UseNpgsql(tenantConnectionString, npgsql =>
        {
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", UsersDbContext.SchemaName);
            npgsql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorCodesToAdd: null);
        });
        await using var context = new UsersDbContext(optionsBuilder.Options, new StaticTenantProvider(tenantId));
        await BuiltinRoleProvisioning.EnsureAsync(context, tenantId, cancellationToken);
    }

    private async Task CreatePilotUserIfConfiguredAsync(
        string tenantConnectionString,
        Guid tenantId,
        string? adminEmail,
        CancellationToken cancellationToken)
    {
        await _pilotUserProvisioning.EnsurePilotUserAsync(
            tenantId,
            tenantConnectionString,
            skipIfSameEmailAs: adminEmail,
            cancellationToken);
    }

    private static async Task<Guid?> CreateTenantUserAsync(
        string tenantConnectionString,
        Guid tenantId,
        string email,
        string displayName,
        string? password,
        string role,
        string? loginType,
        string? firstName,
        string? lastName,
        CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UsersDbContext>();
        optionsBuilder.UseNpgsql(tenantConnectionString, npgsql =>
        {
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", UsersDbContext.SchemaName);
            npgsql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorCodesToAdd: null);
        });
        var tenantProvider = new StaticTenantProvider(tenantId);
        await using var context = new UsersDbContext(optionsBuilder.Options, tenantProvider);
        await UsersSchemaEnsurer.EnsureExtendedUserColumnsAsync(context, cancellationToken);

        var normalizedEmail = email.Trim();
        var existing = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail && !u.IsDeleted, cancellationToken);
        if (existing != null)
            return existing.Id;

        var user = User.Create(
            tenantId,
            normalizedEmail,
            displayName,
            role,
            firstName?.Trim(),
            lastName?.Trim(),
            User.AuthStrategyEzofis);
        user.SetLoginType(loginType ?? "EZOFIS");
        if (!string.IsNullOrWhiteSpace(password))
            user.SetPasswordHash(BCrypt.Net.BCrypt.HashPassword(password.Trim()));

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user.Id;
    }

    private static async Task ApplyWorkflowSchemaAsync(string tenantConnectionString, CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "postgres", "CreateWorkflowSchemaComplete.sql");
        if (!File.Exists(scriptPath))
        {
            // Fallback: try relative to project root
            scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "scripts", "postgres", "CreateWorkflowSchemaComplete.sql");
        }

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Workflow schema script not found at: {scriptPath}");
        }

        var script = await File.ReadAllTextAsync(scriptPath, cancellationToken);

        await using var connection = new NpgsqlConnection(tenantConnectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            // Ported Postgres script uses CREATE SCHEMA/TABLE/INDEX IF NOT EXISTS throughout
            // (no SQL Server "GO" batch separators), so it runs as a single multi-statement batch.
            await using var command = new NpgsqlCommand(script, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex)
        {
            // Log the error but don't throw - objects may already exist (idempotent re-run).
            Console.WriteLine($"Warning: Workflow schema batch failed: {ex.Message}");
        }
    }

    private static async Task ApplyDmsSchemaAsync(string tenantConnectionString, CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "postgres", "CreateDmsSchema.sql");
        if (!File.Exists(scriptPath))
        {
            scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "scripts", "postgres", "CreateDmsSchema.sql");
        }

        if (!File.Exists(scriptPath))
        {
            Console.WriteLine("Warning: DMS schema script not found. Skipping DMS setup.");
            return;
        }

        var script = await File.ReadAllTextAsync(scriptPath, cancellationToken);

        await using var connection = new NpgsqlConnection(tenantConnectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = new NpgsqlCommand(script, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex)
        {
            Console.WriteLine($"Warning: DMS schema batch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates dbo.connector (OAuth) and email-ingest tables on the new tenant DB.
    /// </summary>
    private static async Task ApplyConnectorSchemaAsync(string tenantConnectionString, CancellationToken cancellationToken)
    {
        await ApplySqlScriptAsync(
            tenantConnectionString,
            "Create-Connector-Table.sql",
            required: true,
            cancellationToken);
        await ApplySqlScriptAsync(
            tenantConnectionString,
            "Create-EmailIngest-Tables.sql",
            required: false,
            cancellationToken);
    }

    private static async Task ApplySqlScriptAsync(
        string tenantConnectionString,
        string scriptFileName,
        bool required,
        CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "postgres", scriptFileName);
        if (!File.Exists(scriptPath))
            scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "scripts", "postgres", scriptFileName);
        if (!File.Exists(scriptPath))
            scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "src", "Api", "scripts", "postgres", scriptFileName);
        if (!File.Exists(scriptPath))
        {
            var message = $"Schema script not found: {scriptFileName}";
            if (required)
                throw new FileNotFoundException(message);
            Console.WriteLine($"Warning: {message}. Skipping.");
            return;
        }

        var script = await File.ReadAllTextAsync(scriptPath, cancellationToken);

        await using var connection = new NpgsqlConnection(tenantConnectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            // Ported Postgres scripts use CREATE SCHEMA/TABLE/INDEX IF NOT EXISTS throughout
            // (no SQL Server "GO" batch separators), so they run as a single multi-statement batch.
            await using var command = new NpgsqlCommand(script, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex)
        {
            Console.WriteLine($"Warning: {scriptFileName} failed: {ex.Message}");
        }
    }
}
