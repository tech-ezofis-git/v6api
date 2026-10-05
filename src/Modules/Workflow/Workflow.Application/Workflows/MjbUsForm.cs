namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>MJB_US form control names and json ids. Names match wFormControl.name.</summary>
public static class MjbUsForm
{
    public const string EmailSubject = "Email Subject";
    public const string EmailSubjectId = "e9d67bb4-ede0-4cb8-947f-9064d8b59b00";
    public const string FromEmail = "From Email";
    public const string FromEmailId = "02b36e0e-9c2a-42fb-aaf8-4f1e5e099701";
    public const string EmailReceivedAt = "Email Recieved At";
    public const string EmailReceivedAtId = "c35d31f3-749d-41b6-90e0-f27994ca82b9";
    public const string ReceivedFilename = "Recieved Filename";
    public const string ReceivedFilenameId = "30d83830-50e7-46c0-8279-c7ab05ef9f05";
    public const string ClassificationCompleted = "Classification Completed";
    public const string ClassificationCompletedId = "2437b23f-513d-4386-b0bd-e820152bb6ce";
    public const string ClassificationStatus = "Classification Status";
    public const string ClassificationStatusId = "cc1d9905-fa88-46dc-98b6-68f0bb74a98f";
    public const string ExtractionCompleted = "Extraction Completed";
    public const string ExtractionCompletedId = "bb56396f-0dba-45fd-8fee-a9806d3b2d2e";
    public const string ExtractionStatus = "Extraction Status";
    public const string ExtractionStatusId = "91ca31c8-fde4-41cb-90f3-7ba6548cd69b";
    public const string FtpStatus = "FTP Status";
    public const string FtpStatusId = "e4a043c8-7602-425a-b40a-503d7715b6b7";
    public const string ErrorCode = "ERROR CODE";
    public const string ErrorCodeId = "89855529-35d3-4e5b-830d-5dbda8c33c84";
    public const string MessageId = "Message ID";
    public const string MessageIdId = "b81c703d-5e4a-4e31-a89d-b24b388e17f1";
    public const string RequestStatus = "Request Status";
    public const string RequestStatusId = "79258ce1-1186-4953-b207-b888ab06de16";

    public const string StatusSucceeded = "SUCCEEDED";
    public const string StatusFailed = "FAILED";
    public const string FtpSuccess = "Sucess";
    public const string FtpQueued = "Queued";
    public const string RequestSuccess = "Sucess";
    public const string RequestForceClose = "Force Close";

    public static void Set(IDictionary<string, string> fields, string name, string jsonId, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var text = value.Trim();
        fields[name] = text;
        fields[jsonId] = text;
    }
}
