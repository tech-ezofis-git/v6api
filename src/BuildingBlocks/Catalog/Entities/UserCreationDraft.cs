namespace SaaSApp.Catalog.Entities;

/// <summary>
/// In-progress Create User wizard draft (resume after network drop).
/// Stored in the shared catalog database.
/// </summary>
public sealed class UserCreationDraft
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>1-based wizard step (1=Login Details … 5=Review).</summary>
    public int CurrentStep { get; set; }
    /// <summary>loginDetails | businessDetail | groupAssignment | authentication | review</summary>
    public string CurrentStepKey { get; set; } = "loginDetails";
    /// <summary>Full wizard state JSON (password fields should be omitted by FE).</summary>
    public string DraftJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsDeleted { get; set; }
}
