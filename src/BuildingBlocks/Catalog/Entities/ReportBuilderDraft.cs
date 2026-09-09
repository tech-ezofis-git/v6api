namespace SaaSApp.Catalog.Entities;

/// <summary>
/// In-progress Report Builder wizard draft (resume after network drop).
/// Stored in the shared catalog database.
/// </summary>
public sealed class ReportBuilderDraft
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>1-based wizard step (1=AI ΓÇª 5=Schedule).</summary>
    public int CurrentStep { get; set; }
    /// <summary>ai | details | fields | filters | schedule</summary>
    public string CurrentStepKey { get; set; } = "details";
    /// <summary>Full wizard state JSON (all steps so far).</summary>
    public string DraftJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsDeleted { get; set; }
}
