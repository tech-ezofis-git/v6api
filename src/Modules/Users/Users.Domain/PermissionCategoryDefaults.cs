namespace SaaSApp.Users.Domain;

/// <summary>Default permission categories seeded into each tenant database.</summary>
public static class PermissionCategoryDefaults
{
    public static readonly Guid DashboardId = Guid.Parse("a1000001-0000-4000-8000-000000000006");
    public static readonly Guid WorkflowId = Guid.Parse("a1000001-0000-4000-8000-000000000001");
    public static readonly Guid FolderId = Guid.Parse("a1000001-0000-4000-8000-000000000002");
    public static readonly Guid TaskId = Guid.Parse("a1000001-0000-4000-8000-000000000003");
    public static readonly Guid WorkspaceId = Guid.Parse("a1000001-0000-4000-8000-000000000004");
    public static readonly Guid SettingsId = Guid.Parse("a1000001-0000-4000-8000-000000000005");
    public static readonly Guid FormId = Guid.Parse("a1000001-0000-4000-8000-000000000009");
    public static readonly Guid FolderCreateId = Guid.Parse("a1000001-0000-4000-8000-00000000000a");
    public static readonly Guid PortalId = Guid.Parse("a1000001-0000-4000-8000-00000000000b");
    public static readonly Guid ReportBuilderId = Guid.Parse("a1000001-0000-4000-8000-00000000000c");

    public static IReadOnlyList<(Guid Id, string Key, string Name, int SortOrder)> All =>
    [
        (DashboardId, "dashboard", "Dashboard", 1),
        (WorkflowId, "workflow", "Workflow", 2),
        (FolderId, "folder", "Folder", 3),
        (TaskId, "task", "Task", 4),
        (WorkspaceId, "workspace", "Workspace", 5),
        (SettingsId, "settings", "Settings", 6),
        (FormId, "form", "Form", 7),
        (FolderCreateId, "folder-create", "Folder Create", 8),
        (PortalId, "portal", "Portal", 9),
        (ReportBuilderId, "report-builder", "Report Builder", 10),
    ];
}
