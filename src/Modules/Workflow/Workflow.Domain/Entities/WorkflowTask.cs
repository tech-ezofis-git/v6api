using SaaSApp.SharedKernel.Domain;
using SaaSApp.SharedKernel.Domain.Interfaces;

namespace SaaSApp.Workflow.Domain.Entities;

/// <summary>Task linked to a workflow instance.</summary>
public sealed class WorkflowTask : Entity<Guid>, ITenantEntity
{
    public Guid TenantId { get; private set; }
    public Guid WorkflowInstanceId { get; private set; }
    public Guid? StepInstanceId { get; private set; }
    public int WFormId { get; private set; }
    public Guid FormEntryId { get; private set; }
    public string? TaskName { get; private set; }
    public string? TaskDescription { get; private set; }
    public Guid? AssignedToUserId { get; private set; }
    public DateTime? DueDate { get; private set; }
    public bool IsCompleted { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ModifiedAtUtc { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid? ModifiedBy { get; private set; }
    public bool IsDeleted { get; private set; }

    private WorkflowTask() { } // EF

    /// <summary>Create a workflow task.</summary>
    public static WorkflowTask Create(Guid tenantId, Guid workflowInstanceId, int wFormId, Guid formEntryId, Guid createdBy, Guid? stepInstanceId = null, string? taskName = null, string? taskDescription = null, Guid? assignedToUserId = null, DateTime? dueDate = null)
    {
        return new WorkflowTask
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            WorkflowInstanceId = workflowInstanceId,
            StepInstanceId = stepInstanceId,
            WFormId = wFormId,
            FormEntryId = formEntryId,
            TaskName = taskName?.Trim(),
            TaskDescription = taskDescription?.Trim(),
            AssignedToUserId = assignedToUserId,
            DueDate = dueDate,
            IsCompleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = createdBy
        };
    }

    /// <summary>Mark task as completed.</summary>
    public void Complete()
    {
        IsCompleted = true;
        CompletedAt = DateTime.UtcNow;
        ModifiedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Update task.</summary>
    public void Update(string? taskName, string? taskDescription, Guid? assignedToUserId, DateTime? dueDate, Guid modifiedBy)
    {
        if (taskName != null)
            TaskName = taskName.Trim();
        if (taskDescription != null)
            TaskDescription = taskDescription.Trim();
        if (assignedToUserId.HasValue)
            AssignedToUserId = assignedToUserId;
        if (dueDate.HasValue)
            DueDate = dueDate;
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
