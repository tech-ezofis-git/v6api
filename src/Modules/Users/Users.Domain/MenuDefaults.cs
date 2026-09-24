namespace SaaSApp.Users.Domain;

/// <summary>Default navigation menus seeded into each tenant database.</summary>
public static class MenuDefaults
{
    public static readonly Guid DashboardId = Guid.Parse("b2000001-0000-4000-8000-000000000001");
    public static readonly Guid InboxId = Guid.Parse("b2000001-0000-4000-8000-000000000002");
    public static readonly Guid OcrReviewId = Guid.Parse("b2000001-0000-4000-8000-000000000003");
    public static readonly Guid ProcessedInvoicesId = Guid.Parse("b2000001-0000-4000-8000-000000000004");
    public static readonly Guid ApprovalQueueId = Guid.Parse("b2000001-0000-4000-8000-000000000005");
    public static readonly Guid VendorsId = Guid.Parse("b2000001-0000-4000-8000-000000000006");
    public static readonly Guid FolderCreateId = Guid.Parse("b2000001-0000-4000-8000-000000000007");
    public static readonly Guid ReportBuilderId = Guid.Parse("b2000001-0000-4000-8000-000000000008");
    public static readonly Guid FormId = Guid.Parse("b2000001-0000-4000-8000-000000000009");
    public static readonly Guid PortalId = Guid.Parse("b2000001-0000-4000-8000-00000000000a");

    public static IReadOnlyList<(Guid Id, string Key, string Label, string RoutePath, int SortOrder)> All =>
    [
        (DashboardId, "dashboard", "Dashboard", "/dashboard", 1),
        (InboxId, "inbox", "Inbox", "/inbox", 2),
        (OcrReviewId, "ocr-review", "OCR.Review", "/ocr-review", 3),
        (ProcessedInvoicesId, "processed-invoices", "Processed Invoices", "/processed-invoices", 4),
        (ApprovalQueueId, "approval-queue", "Approval Queue", "/approval-queue", 5),
        (VendorsId, "vendors", "Vendors", "/vendors", 6),
        (FolderCreateId, "folder-create", "Folder Create", "/folder-creation", 7),
        (ReportBuilderId, "report-builder", "Report Builder", "/report-builder", 8),
        (FormId, "form", "Form", "/form", 9),
        (PortalId, "portal", "Portal", "/portal", 10),
    ];
}
