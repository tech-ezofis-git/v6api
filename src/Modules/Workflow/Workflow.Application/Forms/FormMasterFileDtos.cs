namespace SaaSApp.Workflow.Application.Forms;

public sealed record FormMasterFileUploadRequest(
    string FormId,
    string? WorkflowId,
    string? InstanceId,
    Stream FileStream,
    string FileName,
    string? ContentType,
    long FileSize);

public sealed record FormMasterFileUploadResult(
    int MasterFileProcessId,
    string FilePath,
    int? NotificationId = null,
    string? HangfireJobId = null,
    /// <summary>Exact JSON body Hangfire POSTs to Python <c>ezDataImport</c>.</summary>
    object? PythonInput = null);
