namespace SaaSApp.Catalog.Entities;

/// <summary>
/// In-progress Create Folder wizard draft (resume after network drop).
/// Stored in the shared catalog database.
/// </summary>
public sealed class FolderCreationDraft
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>1-based wizard step last completed / current (1=Folder Details … 5=Integrations).</summary>
    public int CurrentStep { get; set; }
    /// <summary>folderDetails | fields | storage | versioning | integrations</summary>
    public string CurrentStepKey { get; set; } = "folderDetails";
    /// <summary>Full wizard state JSON (all steps so far).</summary>
    public string DraftJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsDeleted { get; set; }
}
