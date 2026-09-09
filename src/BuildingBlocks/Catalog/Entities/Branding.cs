namespace SaaSApp.Catalog.Entities;

/// <summary>Tenant branding config stored in the shared catalog database.</summary>
public sealed class Branding
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string BrandingName { get; set; } = string.Empty;
    public string BrandingJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}
