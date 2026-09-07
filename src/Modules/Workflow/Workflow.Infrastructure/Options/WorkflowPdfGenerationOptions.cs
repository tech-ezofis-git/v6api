namespace SaaSApp.Workflow.Infrastructure.Options;

public sealed class WorkflowPdfGenerationOptions
{
    public const string SectionName = "Workflow:PdfGeneration";

    public bool Enabled { get; set; } = true;

    /// <summary>Python PDF service URL (e.g. http://localhost:8000/api/pdf/generate).</summary>
    public string? ServiceUrl { get; set; }

    public int TimeoutSeconds { get; set; } = 120;
}
