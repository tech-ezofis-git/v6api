namespace SaaSApp.Catalog.Entities;

/// <summary>Cross-tenant read-only grant for a repository item (stored in catalog DB).</summary>
public sealed class RepositoryItemShare
{
    public Guid Id { get; set; }
    public string ShareToken { get; set; } = null!;
    public Guid SourceTenantId { get; set; }
    public Guid SourceRepositoryId { get; set; }
    public Guid SourceItemId { get; set; }
    public Guid SharedByUserId { get; set; }
    public string RecipientEmail { get; set; } = null!;
    public string? Message { get; set; }
    public string Status { get; set; } = ShareStatuses.Active;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastAccessedAtUtc { get; set; }

    /// <summary>When true, recipient is auto-added to the source tenant (no password) for workflow inbox guest shares.</summary>
    public bool AutoProvisionGuest { get; set; }

    /// <summary>Optional workflow instance that originated this share (workflow inbox).</summary>
    public Guid? WorkflowInstanceId { get; set; }

    /// <summary>0 = Can View, 1 = Can Edit (upload). Used for repository invite shares.</summary>
    public int Action { get; set; }
}

public static class ShareStatuses
{
    public const string Active = "Active";
    public const string Revoked = "Revoked";
}
