using Microsoft.EntityFrameworkCore;
using SaaSApp.Catalog.Entities;

namespace SaaSApp.Catalog.Persistence;

public sealed class CatalogDbContext : DbContext
{
    public const string SchemaName = "catalog";

    public CatalogDbContext(DbContextOptions<CatalogDbContext> options)
        : base(options)
    {
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<UserTenant> UserTenants => Set<UserTenant>();
    public DbSet<MailSetting> MailSettings => Set<MailSetting>();
    public DbSet<OtpVerification> OtpVerifications => Set<OtpVerification>();
    public DbSet<RepositoryItemShare> RepositoryItemShares => Set<RepositoryItemShare>();
    public DbSet<CreditMaster> CreditMasters => Set<CreditMaster>();
    public DbSet<ConnectorProvider> ConnectorProviders => Set<ConnectorProvider>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("Tenants");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConnectionString).IsRequired();
            entity.Property(e => e.IsActive);
            entity.Property(e => e.CreatedAtUtc);
            entity.Property(e => e.ModifiedAtUtc);
            entity.Property(e => e.Email).HasMaxLength(256);
            entity.Property(e => e.SignupSource).HasMaxLength(128);
            entity.Property(e => e.Platform).HasMaxLength(64);
            entity.Property(e => e.AppVersion).HasMaxLength(64);
            entity.Property(e => e.LoginType).HasMaxLength(64);
        });

        modelBuilder.Entity<UserTenant>(entity =>
        {
            entity.ToTable("UserTenants");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Role).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CreatedAtUtc);
            entity.Property(e => e.UserId);
            entity.Property(e => e.PreQuestionsJson);
            entity.HasIndex(e => new { e.Email, e.TenantId }).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.TenantId });
        });

        modelBuilder.Entity<RepositoryItemShare>(entity =>
        {
            // Table is created by scripts/Create_RepositoryItemShares.sql (with defaults/PK
            // naming EF's Fluent API here doesn't replicate), never by an EF migration on
            // SQL Server either -- excluded from Migrations so EF only queries/writes it,
            // matching the existing architecture instead of introducing a second,
            // subtly-different definition of this table.
            entity.ToTable("RepositoryItemShares", t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ShareToken).HasMaxLength(128).IsRequired();
            entity.Property(e => e.RecipientEmail).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Message).HasMaxLength(2000);
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(e => e.ShareToken).IsUnique();
            entity.HasIndex(e => new { e.RecipientEmail, e.Status });
            entity.HasIndex(e => new { e.SourceTenantId, e.SourceRepositoryId, e.SourceItemId });
            entity.Property(e => e.AutoProvisionGuest);
            entity.Property(e => e.WorkflowInstanceId);
            entity.Property(e => e.Action);
        });

        modelBuilder.Entity<CreditMaster>(entity =>
        {
            // Table is created by scripts/AddCreditMaster.sql (legacy dbo schema, INT IDENTITY
            // PK), never by an EF migration on SQL Server either -- excluded from Migrations,
            // same reasoning as RepositoryItemShares above.
            entity.ToTable("creditMaster", "dbo", t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TenantId).HasColumnName("tenantId");
            entity.Property(e => e.AllocationMonth).HasColumnName("allocationMonth");
            entity.Property(e => e.AllocationYear).HasColumnName("allocationYear");
            entity.Property(e => e.CreditType).HasColumnName("creditType").HasMaxLength(100);
            entity.Property(e => e.InitialCredit).HasColumnName("initialCredit");
            entity.Property(e => e.BalanceCredit).HasColumnName("balanceCredit");
            entity.Property(e => e.Remarks).HasColumnName("remarks").HasMaxLength(500);
            entity.Property(e => e.CreatedAt).HasColumnName("createdAt");
            entity.Property(e => e.ModifiedAt).HasColumnName("modifiedAt");
            entity.Property(e => e.CreatedBy).HasColumnName("createdBy").HasMaxLength(50);
            entity.Property(e => e.ModifiedBy).HasColumnName("modifiedBy").HasMaxLength(50);
            entity.Property(e => e.IsDeleted).HasColumnName("isDeleted");
            entity.Property(e => e.ValidFrom).HasColumnName("ValidFrom");
            entity.Property(e => e.ValidTo).HasColumnName("ValidTo");
            entity.Property(e => e.ParentAllocationId).HasColumnName("parentAllocationId");
            entity.Property(e => e.SubscriptionType).HasColumnName("subscriptionType").HasMaxLength(100);
            entity.Property(e => e.ValidFromDate).HasColumnName("validFromDate");
            entity.Property(e => e.ValidToDate).HasColumnName("validToDate");
            entity.Property(e => e.IsCarryForward).HasColumnName("isCarryForward");
            entity.Property(e => e.Priority).HasColumnName("priority");
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(50);
            entity.Property(e => e.CarryForwardCredit).HasColumnName("carryForwardCredit");
            entity.Property(e => e.ExtraConsumedCredit).HasColumnName("extraConsumedCredit");
            entity.Property(e => e.TopUpBalanceCredit).HasColumnName("topUpBalanceCredit");
            entity.Property(e => e.OverallConsumedCredit).HasColumnName("overallConsumedCredit");
            entity.HasIndex(e => new { e.TenantId, e.AllocationYear, e.AllocationMonth, e.CreditType });
        });

        modelBuilder.Entity<MailSetting>(entity =>
        {
            entity.ToTable("mailsettings", "dbo");
            entity.HasKey(e => e.SettingId);
            entity.Property(e => e.SettingId).HasColumnName("settingId");
            entity.Property(e => e.EmailId).HasColumnName("EmailId");
            entity.Property(e => e.Password).HasColumnName("Password");
            entity.Property(e => e.OutgoingServer).HasColumnName("OutgoingServer");
            entity.Property(e => e.OutgoingPort).HasColumnName("OutgoingPort");
            entity.Property(e => e.Isdeleted).HasColumnName("Isdeleted");
            entity.Property(e => e.Preference).HasColumnName("Preference");
        });

        modelBuilder.Entity<OtpVerification>(entity =>
        {
            entity.ToTable("OTPVerification", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Email).HasColumnName("email");
            entity.Property(e => e.OTP).HasColumnName("OTP");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.ValidateAt).HasColumnName("validateAt");
            entity.Property(e => e.CreatedAt).HasColumnName("createdAt");
            entity.Property(e => e.CreatedBy).HasColumnName("createdBy");
            entity.Property(e => e.ModifiedAt).HasColumnName("modifiedAt");
            entity.Property(e => e.IsDeleted).HasColumnName("isDeleted");
        });

        modelBuilder.Entity<ConnectorProvider>(entity =>
        {
            // Table is created AND seeded (7 OAuth providers via MERGE) by
            // scripts/01a_CreateCatalogDatabase.sql, never by an EF migration on SQL Server
            // either -- excluded from Migrations, same reasoning as RepositoryItemShares
            // above. Letting EF's migration create this table instead would produce it
            // without the script's DEFAULT '' / DEFAULT (1) / DEFAULT SYSUTCDATETIME()
            // column defaults and with no seed rows.
            entity.ToTable("ConnectorProviders", t => t.ExcludeFromMigrations());
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProviderCode).HasMaxLength(64).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(128).IsRequired();
            entity.Property(e => e.ClientId).HasMaxLength(512).IsRequired();
            entity.Property(e => e.ClientSecret).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.AuthUrl).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.TokenUrl).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.Scopes).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.RedirectUri).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.ExtraConfigJson);
            entity.Property(e => e.IsActive);
            entity.Property(e => e.CreatedAtUtc);
            entity.Property(e => e.ModifiedAtUtc);
            entity.HasIndex(e => e.ProviderCode).IsUnique();
        });
    }
}
