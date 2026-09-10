namespace SaaSApp.Catalog.Entities;

/// <summary>Saved dashboard KPI/chart schema in catalog DB per tenant + repository or workflow.</summary>
public sealed class DashboardSchemaSnapshot
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? RepositoryId { get; set; }
    public Guid? WorkflowId { get; set; }
    public string SchemaJson { get; set; } = "{}";
    public string? DashboardHtml { get; set; }
    public DateTime? HtmlModifiedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}
