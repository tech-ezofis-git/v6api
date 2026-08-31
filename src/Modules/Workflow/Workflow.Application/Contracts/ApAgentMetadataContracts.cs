namespace SaaSApp.Workflow.Application.Contracts;

public sealed record ApAgentMetadataApplyRequest(
    Guid WorkflowId,
    Guid InstanceId,
    string FormId,
    Guid FormEntryId,
    Guid RepositoryId,
    Guid ItemId,
    IReadOnlyDictionary<string, string> Fields,
    string? LineItemsJson = null);

public sealed record ApAgentMetadataApplyResult(
    Guid ItemId,
    Guid FormEntryId,
    int RepositoryFieldsUpdated,
    int EzfbFieldsUpdated,
    bool LineItemsUpdated,
    string? LineItemsEzfbColumn = null);
