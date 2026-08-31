using SaaSApp.SharedKernel.Domain;
using SaaSApp.SharedKernel.Domain.Interfaces;

namespace SaaSApp.Workflow.Domain.Entities;

/// <summary>Form data linked to a workflow instance or step.</summary>
public sealed class WorkflowForm : Entity<Guid>, ITenantEntity
{
    public Guid TenantId { get; private set; }
    public Guid WorkflowInstanceId { get; private set; }
    public Guid? StepInstanceId { get; private set; }
    public int WFormId { get; private set; } // Form template ID
    public Guid FormEntryId { get; private set; } // Form submission ID
    public string? FormData { get; private set; } // JSON: form field values
    public bool HasFormPdf { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ModifiedAtUtc { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid? ModifiedBy { get; private set; }
    public bool IsDeleted { get; private set; }

    private WorkflowForm() { } // EF

    /// <summary>Create a workflow form entry.</summary>
    public static WorkflowForm Create(Guid tenantId, Guid workflowInstanceId, int wFormId, Guid formEntryId, Guid createdBy, Guid? stepInstanceId = null, string? formData = null, bool hasFormPdf = false)
    {
        return new WorkflowForm
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            WorkflowInstanceId = workflowInstanceId,
            StepInstanceId = stepInstanceId,
            WFormId = wFormId,
            FormEntryId = formEntryId,
            FormData = formData?.Trim(),
            HasFormPdf = hasFormPdf,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = createdBy
        };
    }

    /// <summary>Update form data.</summary>
    public void Update(string? formData, bool? hasFormPdf, Guid modifiedBy)
    {
        if (formData != null)
            FormData = formData.Trim();
        if (hasFormPdf.HasValue)
            HasFormPdf = hasFormPdf.Value;
        ModifiedAtUtc = DateTime.UtcNow;
        ModifiedBy = modifiedBy;
    }

    /// <summary>Soft delete.</summary>
    public void SoftDelete()
    {
        IsDeleted = true;
        ModifiedAtUtc = DateTime.UtcNow;
    }
}
