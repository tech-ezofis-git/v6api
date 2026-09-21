namespace SaaSApp.Catalog.Entities;

/// <summary>Cross-tenant grant for a repository item or live filter share (stored in catalog DB).</summary>
public sealed class RepositoryItemShare
{
    public Guid Id { get; set; }
    public string ShareToken { get; set; } = null!;
    public Guid SourceTenantId { get; set; }
    public Guid SourceRepositoryId { get; set; }

    /// <summary>Single-file share target. Null for filter shares (<see cref="ShareKinds.Filter"/>).</summary>
    public Guid? SourceItemId { get; set; }

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

    /// <summary><see cref="ShareKinds.Item"/> (default), <see cref="ShareKinds.Filter"/>, or <see cref="ShareKinds.Dashboard"/>.</summary>
    public string ShareKind { get; set; } = ShareKinds.Item;

    /// <summary>
    /// Live browse filters for filter shares, same JSON shape as items list
    /// (e.g. <c>{"Supplier":"APC-T001"}</c>). Null for single-item and typical dashboard shares.
    /// </summary>
    public string? FiltersJson { get; set; }

    /// <summary>Catalog <c>DashboardSchemaSnapshots.Id</c> when <see cref="ShareKinds.Dashboard"/>.</summary>
    public Guid? SourceDashboardId { get; set; }
}

public static class ShareStatuses
{
    public const string Active = "Active";
    public const string Revoked = "Revoked";
}

public static class ShareKinds
{
    public const string Item = "Item";
    public const string Filter = "Filter";
    public const string Dashboard = "Dashboard";
}
