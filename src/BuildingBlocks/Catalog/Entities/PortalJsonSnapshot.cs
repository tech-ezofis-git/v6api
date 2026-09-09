namespace SaaSApp.Catalog.Entities;

/// <summary>Portal UI state saved in catalog DB per tenant + user.</summary>
public sealed class PortalJsonSnapshot
{
    public Guid Id { get; set; }
    public string Token { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string PortalJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}
