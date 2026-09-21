namespace SaaSApp.Repository.Application.Contracts;

/// <summary>Validated share grant — use source tenant + repo/item ids with existing repository APIs.</summary>
public sealed record RepositoryShareAccess(
    Guid SourceTenantId,
    Guid SourceRepositoryId,
    Guid? SourceItemId,
    string ShareToken,
    bool ReadOnly = true,
    /// <summary>When set, this is a live filter share (folder + filters only).</summary>
    string? FiltersJson = null,
    string ShareKind = "Item",
    Guid? SourceDashboardId = null,
    Guid? SourceWorkflowId = null)
{
    public bool IsFilterShare =>
        string.Equals(ShareKind, "Filter", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(FiltersJson)
            && !IsDashboardShare
            && (SourceItemId is null || SourceItemId == Guid.Empty));

    public bool IsDashboardShare =>
        string.Equals(ShareKind, "Dashboard", StringComparison.OrdinalIgnoreCase)
        || SourceDashboardId is Guid id && id != Guid.Empty;

    public bool IsItemShare => !IsFilterShare && !IsDashboardShare;
}
