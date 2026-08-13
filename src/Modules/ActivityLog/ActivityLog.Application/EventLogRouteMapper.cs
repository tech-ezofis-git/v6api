using System.Globalization;
using System.Text.RegularExpressions;

namespace SaaSApp.ActivityLog.Application;

public sealed record EventLogMappedEvent(
    string EventTitle,
    string EventType,
    string Category,
    string Severity);

/// <summary>Maps HTTP method + path (+ subject) to Event Log title/type/category/severity.</summary>
public static partial class EventLogRouteMapper
{
    private static readonly Regex GuidSegment = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static EventLogMappedEvent Map(
        string method,
        string path,
        int statusCode,
        EventLogSubject? subject = null)
    {
        subject ??= EventLogSubject.Empty;
        var normalizedPath = NormalizePath(path);
        var success = statusCode is >= 200 and < 400;
        var isAuthFail = statusCode is 401 or 403;

        if (TryMapAuth(method, normalizedPath, success, isAuthFail, out var auth))
            return auth;

        if (TryMapUsersRoles(method, normalizedPath, success, subject, out var users))
            return users;

        if (TryMapWorkflow(method, normalizedPath, success, subject, out var workflow, out var listedWorkflow))
            return listedWorkflow ? workflow : AdjustForFailure(workflow, normalizedPath, statusCode, success);

        if (TryMapForm(method, normalizedPath, success, subject, out var form, out var listedForm))
            return listedForm ? form : AdjustForFailure(form, normalizedPath, statusCode, success);

        if (TryMapRepository(method, normalizedPath, success, subject, out var repo, out var listedRepo))
            return listedRepo ? repo : AdjustForFailure(repo, normalizedPath, statusCode, success);

        if (TryMapDms(method, normalizedPath, success, out var dms))
            return dms;

        return MapGeneric(normalizedPath, statusCode);
    }

    /// <summary>
    /// Mutation endpoints that are operational, view-like, or otherwise intentionally omitted from Event Log.
    /// Route parameters are normalized to <c>{}</c> so names and constraints do not affect matching.
    /// </summary>
    private static readonly HashSet<string> ExcludedRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST api/reports/ap-dashboard",
        "POST api/billing/credits/update",
        "POST api/billing/credits/usage",

        "POST api/connector",
        "PUT api/connector/{}",
        "POST api/connector/all",
        "POST api/connector/{}/oauth/refresh",
        "POST api/connector/{}/disconnect",
        "POST api/connector/{}/files/upload",
        "POST api/connector/{}/mail/messages/{}/read",
        "POST api/connector/{}/gmail/messages/{}/read",

        "POST api/email-ingest/mailboxes",
        "PUT api/email-ingest/mailboxes/{}",
        "DELETE api/email-ingest/mailboxes/{}",
        "POST api/email-ingest/mailboxes/{}/poll",

        "POST api/form/all",
        "POST api/form/{}/entry/{}",
        "POST api/form/{}/entry/all",

        "POST api/playground/api-keys",
        "POST api/playground/api-usage",

        "POST api/repositories/storage-providers/seed",
        "POST api/repositories/{}/provision-tables",
        "POST api/repositories/{}/items",
        "POST api/repositories/{}/items/query",
        "POST api/repositories/{}/items/{}/ai-summary",
        "POST api/repositories/{}/items/{}/share",
        "DELETE api/repositories/share/{}",
        "POST api/repositories/{}/items/{}/timeline",
        "PATCH api/repositories/{}/items/{}/metadata",
        "POST api/repositories/{}/items/upload-archive",
        "POST api/repositories/{}/items/upload",

        "POST api/tenant/checkauthenticate",
        "POST api/tenant/validateotp",
        "POST api/admin/tenants",

        "POST api/uploadandindex/upload",
        "POST api/uploadandindex/uploadforocr",
        "POST api/uploadandindex/load/{}",
        "PUT api/uploadandindex/index/{}",
        "POST api/uploadandindex/index/all",

        "POST api/users/{}/configuration",

        "POST api/workflows/{}/filter/search",
        "POST api/workflow/all",
        "POST api/workflow/inboxlist/{}",
        "POST api/workflows/instances/{}/share-file",
        "POST api/workflows/{}/sync-steps",
        "POST api/workflows/{}/steps",
        "POST api/workflows/{}/publish",
        "POST api/workflows/{}/start",
        "POST api/workflows/{}/start/json",
        "POST api/workflows/{}/sla",
        "POST api/workflows/{}/instances/{}/comments",
        "POST api/workflows/{}/instances/{}/attachments",
        "POST api/workflows/{}/instances/{}/attachments/link",
        "POST api/workflows/instances/{}/steps/{}/reject",
        "PATCH api/workflows/{}/instances/{}/ap-agent/metadata",
        "PATCH api/workflows/ap-agent/jobs/{}/progress",
        "POST api/workflows/{}/instances/{}/ap-agent/run",
        "POST api/workflows/instances/bulk-move-next",
        "POST api/workflows/instances/{}/actions"
    };

    /// <summary>
    /// Whether this request should be written to Event Log.
    /// Skips GET/HEAD, known view/list-only routes, and search endpoints; keeps mutations and meaningful actions.
    /// </summary>
    public static bool ShouldLog(string method, string path, string? routeTemplate = null)
    {
        if (HttpMethodsEqual(method, "GET") || HttpMethodsEqual(method, "HEAD"))
            return false;

        if (!string.IsNullOrWhiteSpace(routeTemplate))
        {
            var routeKey = $"{method.Trim().ToUpperInvariant()} {NormalizeRouteTemplate(routeTemplate)}";
            if (ExcludedRoutes.Contains(routeKey))
                return false;
        }

        var normalizedPath = NormalizePath(path);
        if (IsViewOnlyRoute(method, normalizedPath))
            return false;

        return true;
    }

    private static bool IsViewOnlyRoute(string method, string path)
    {
        // List / filter endpoints ending with /all (e.g. POST /api/workflow/all, /api/form/all).
        // Match by path so exclusion works even when route template is unavailable in OnCompleted.
        if (path.EndsWith("/all", StringComparison.OrdinalIgnoreCase))
            return true;

        // Search / filter-search style endpoints (e.g. POST .../filter/search).
        if (path.Contains("/search", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }


    public static bool IsAuthLoginRoute(string path)
    {
        var p = NormalizePath(path);
        return p.Equals("/api/auth/ezofis/login", StringComparison.OrdinalIgnoreCase)
               || p.Equals("/api/auth/social/login", StringComparison.OrdinalIgnoreCase)
               || p.Equals("/api/auth/2fa/complete", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetUserIdFromPath(string path, out Guid userId)
    {
        userId = Guid.Empty;
        var match = UserById().Match(NormalizePath(path));
        return match.Success && Guid.TryParse(match.Groups[1].Value, out userId);
    }

    public static bool TryGetRoleIdFromPath(string path, out Guid roleId)
    {
        roleId = Guid.Empty;
        var normalized = NormalizePath(path);
        var match = RoleById().Match(normalized);
        if (!match.Success)
            match = RoleMenus().Match(normalized);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out roleId);
    }

    public static bool TryGetGroupIdFromPath(string path, out Guid groupId)
    {
        groupId = Guid.Empty;
        var match = GroupById().Match(NormalizePath(path));
        return match.Success && Guid.TryParse(match.Groups[1].Value, out groupId);
    }

    public static bool TryGetWorkflowIdFromPath(string path, out Guid workflowId)
    {
        workflowId = Guid.Empty;
        var canonical = ToCanonicalWorkflowPath(NormalizePath(path));
        var match = WorkflowById().Match(canonical);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out workflowId);
    }

    public static bool TryGetWorkflowInstanceIdFromPath(string path, out Guid instanceId)
    {
        instanceId = Guid.Empty;
        var canonical = ToCanonicalWorkflowPath(NormalizePath(path));
        var match = WorkflowApprove().Match(canonical);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out instanceId);
    }

    public static bool TryGetRepositoryIdFromPath(string path, out Guid repositoryId)
    {
        repositoryId = Guid.Empty;
        var match = RepoById().Match(NormalizePath(path));
        return match.Success && Guid.TryParse(match.Groups[1].Value, out repositoryId);
    }

    public static bool TryGetFormIdFromPath(string path, out string formId)
    {
        formId = string.Empty;
        var match = FormById().Match(NormalizePath(path));
        if (!match.Success)
            return false;

        var id = match.Groups[1].Value;
        if (id.Equals("all", StringComparison.OrdinalIgnoreCase)
            || id.Equals("uploadMasterFile", StringComparison.OrdinalIgnoreCase))
            return false;

        formId = id;
        return true;
    }

    private static EventLogMappedEvent AdjustForFailure(
        EventLogMappedEvent mapped,
        string path,
        int statusCode,
        bool success)
    {
        if (success)
            return mapped;

        if (IsAuthLoginRoute(path))
            return mapped;

        var severity = statusCode switch
        {
            >= 500 => "Critical",
            401 or 403 => "Critical",
            _ => "Warning"
        };

        var title = mapped.EventTitle;
        if (!title.StartsWith("Failed ", StringComparison.OrdinalIgnoreCase)
            && !title.StartsWith("Request failed", StringComparison.OrdinalIgnoreCase))
        {
            title = title.EndsWith(" completed", StringComparison.OrdinalIgnoreCase)
                ? title[..^" completed".Length] + " failed"
                : $"Failed: {title}";
        }

        return mapped with { EventTitle = title, Severity = severity };
    }

    private static bool TryMapAuth(
        string method,
        string path,
        bool success,
        bool isAuthFail,
        out EventLogMappedEvent mapped)
    {
        mapped = default!;
        if (!HttpMethodsEqual(method, "POST") || !IsAuthLoginRoute(path))
            return false;

        if (success)
        {
            mapped = new EventLogMappedEvent(
                "User logged in successfully",
                "Login",
                "Authentication",
                "Info");
            return true;
        }

        mapped = new EventLogMappedEvent(
            "Failed login attempt",
            "Security Event",
            "Security",
            "Critical");
        return true;
    }

    private static bool TryMapUsersRoles(
        string method,
        string path,
        bool success,
        EventLogSubject subject,
        out EventLogMappedEvent mapped)
    {
        mapped = default!;
        var person = PersonLabel(subject);

        if (HttpMethodsEqual(method, "POST") && path.Equals("/api/users", StringComparison.OrdinalIgnoreCase))
        {
            var title = success
                ? WithName("New user account created", person)
                : WithName("Failed to create new user account", person);
            mapped = new EventLogMappedEvent(title, "User Created", "User Management", success ? "Info" : "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "PUT") && UserById().IsMatch(path))
        {
            if (!success)
            {
                mapped = new EventLogMappedEvent(
                    $"Failed to Update user account - {UserLabelOrUnknown(person)}",
                    "User Updated",
                    "User Management",
                    "Warning");
                return true;
            }

            if (!string.IsNullOrWhiteSpace(subject.Role))
            {
                var roleName = subject.Role.Trim();
                var label = person is { } s
                    ? $"User account {s} updated for role- {roleName}"
                    : $"User account updated for role- {roleName}";
                mapped = new EventLogMappedEvent(label, "User Updated", "User Management", "Info");
                return true;
            }

            mapped = new EventLogMappedEvent(
                WithName("User account updated", person),
                "User Updated",
                "User Management",
                "Info");
            return true;
        }

        if (HttpMethodsEqual(method, "DELETE") && UserById().IsMatch(path))
        {
            var title = success
                ? WithName("User account deleted", person)
                : $"Failed to Delete the user account - {UserLabelOrUnknown(person)}";
            mapped = new EventLogMappedEvent(title, "User Deleted", "User Management", "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "POST") && path.Equals("/api/users/roles", StringComparison.OrdinalIgnoreCase))
        {
            var roleLabel = FirstNonEmpty(subject.RoleName, subject.Name);
            var title = success
                ? WithName("New role created", roleLabel)
                : "Failed role creation";
            mapped = new EventLogMappedEvent(title, "Role Created", "User Management", success ? "Info" : "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "PUT") && RoleMenus().IsMatch(path))
        {
            var roleLabel = FirstNonEmpty(subject.RoleName, subject.Name);
            var title = success
                ? "Role permissions updated"
                : WithName("Failed to update role permissions", roleLabel);
            mapped = new EventLogMappedEvent(
                title,
                "Permission Changed",
                "Security",
                success ? "Warning" : "Critical");
            return true;
        }

        if (HttpMethodsEqual(method, "PUT") && RoleById().IsMatch(path))
        {
            var roleLabel = FirstNonEmpty(subject.RoleName, subject.Name);
            var title = success
                ? WithName("Role updated", roleLabel)
                : WithName("Failed to Update user role", roleLabel);
            mapped = new EventLogMappedEvent(
                title,
                "Permission Changed",
                "Security",
                success ? "Warning" : "Critical");
            return true;
        }

        if (HttpMethodsEqual(method, "POST") && path.Equals("/api/users/groups", StringComparison.OrdinalIgnoreCase))
        {
            var group = FirstNonEmpty(subject.GroupName, subject.Name);
            var title = success
                ? WithName("New group created", group)
                : WithName("Failed to create new group", group);
            mapped = new EventLogMappedEvent(title, "Group Created", "User Management", success ? "Info" : "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "PUT") && GroupById().IsMatch(path))
        {
            var group = FirstNonEmpty(subject.GroupName, subject.Name);
            var title = success
                ? WithName("Group updated", group)
                : WithName("Failed to update group", group);
            mapped = new EventLogMappedEvent(title, "Group Updated", "User Management", success ? "Info" : "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "DELETE") && GroupById().IsMatch(path))
        {
            var group = FirstNonEmpty(subject.GroupName, subject.Name);
            var title = success
                ? WithName("Group deleted", group)
                : WithName("Failed to delete the group", group);
            mapped = new EventLogMappedEvent(title, "Group Deleted", "User Management", "Warning");
            return true;
        }

        return false;
    }

    private static bool TryMapWorkflow(
        string method,
        string path,
        bool success,
        EventLogSubject subject,
        out EventLogMappedEvent mapped,
        out bool fullySpecified)
    {
        mapped = default!;
        fullySpecified = false;
        if (!IsWorkflowPath(path))
            return false;

        var canonical = ToCanonicalWorkflowPath(path);
        var sevInfo = success ? "Info" : "Warning";

        if (HttpMethodsEqual(method, "POST")
            && canonical.Equals("/api/workflows/setup-schema", StringComparison.OrdinalIgnoreCase))
        {
            fullySpecified = true;
            mapped = new EventLogMappedEvent(
                success ? "Workflow setup schema completed" : "Workflow setup schema failed",
                "Schema Setup",
                "Workflow",
                success ? "Info" : "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "POST")
            && (canonical.Equals("/api/workflows", StringComparison.OrdinalIgnoreCase)
                || canonical.Equals("/api/workflow", StringComparison.OrdinalIgnoreCase)))
        {
            fullySpecified = true;
            var name = FirstNonEmpty(subject.Name);
            var title = success
                ? WithName("New workflow created", name)
                : WithName("Failed to create new workflow", name);
            mapped = new EventLogMappedEvent(title, "Workflow Created", "Workflow", success ? "Info" : "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "GET")
            && (canonical.Equals("/api/workflows", StringComparison.OrdinalIgnoreCase)
                || canonical.Equals("/api/workflow", StringComparison.OrdinalIgnoreCase)
                || canonical.Equals("/api/workflow/getall", StringComparison.OrdinalIgnoreCase)))
        {
            mapped = new EventLogMappedEvent("Workflows list viewed", "Workflows Listed", "Workflow", "Info");
            return true;
        }

        if (canonical.EndsWith("/inbox", StringComparison.OrdinalIgnoreCase) && HttpMethodsEqual(method, "GET"))
        {
            mapped = new EventLogMappedEvent("Workflow inbox viewed", "Inbox Viewed", "Workflow", "Info");
            return true;
        }

        if (canonical.EndsWith("/sent", StringComparison.OrdinalIgnoreCase) && HttpMethodsEqual(method, "GET"))
        {
            mapped = new EventLogMappedEvent("Workflow sent list viewed", "Sent Viewed", "Workflow", "Info");
            return true;
        }

        if (canonical.EndsWith("/completed", StringComparison.OrdinalIgnoreCase) && HttpMethodsEqual(method, "GET"))
        {
            mapped = new EventLogMappedEvent("Workflow completed list viewed", "Completed Viewed", "Workflow", "Info");
            return true;
        }

        if (HttpMethodsEqual(method, "POST") && WorkflowPublish().IsMatch(canonical))
        {
            mapped = new EventLogMappedEvent("Workflow published", "Workflow Published", "Workflow", sevInfo);
            return true;
        }

        if (HttpMethodsEqual(method, "POST")
            && (WorkflowStart().IsMatch(canonical) || WorkflowStartJson().IsMatch(canonical)))
        {
            mapped = new EventLogMappedEvent("Workflow started", "Workflow Started", "Workflow", sevInfo);
            return true;
        }

        if (HttpMethodsEqual(method, "POST") && WorkflowApprove().IsMatch(canonical))
        {
            fullySpecified = true;
            var name = FirstNonEmpty(subject.Name);
            var title = success
                ? "Workflow step approved"
                : WithName("Failed to approve workflow steps", name);
            mapped = new EventLogMappedEvent(title, "Step Approved", "Workflow", success ? "Info" : "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "POST") && WorkflowReject().IsMatch(canonical))
        {
            mapped = new EventLogMappedEvent("Workflow step rejected", "Step Rejected", "Workflow", "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "POST") && WorkflowMoveNext().IsMatch(canonical))
        {
            mapped = new EventLogMappedEvent("Workflow moved to next step", "Step Advanced", "Workflow", sevInfo);
            return true;
        }

        if (HttpMethodsEqual(method, "POST") && WorkflowActions().IsMatch(canonical))
        {
            mapped = new EventLogMappedEvent("Workflow action performed", "Workflow Action", "Workflow", sevInfo);
            return true;
        }

        if (HttpMethodsEqual(method, "POST") && WorkflowShareFile().IsMatch(canonical))
        {
            mapped = new EventLogMappedEvent("Workflow file shared", "File Shared", "Workflow", sevInfo);
            return true;
        }

        if (HttpMethodsEqual(method, "PUT") && WorkflowById().IsMatch(canonical))
        {
            fullySpecified = true;
            var name = FirstNonEmpty(subject.Name);
            var title = success
                ? WithName("Workflow updated", name)
                : WithName("Failed to update workflow", name);
            mapped = new EventLogMappedEvent(title, "Workflow Updated", "Workflow", success ? "Info" : "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "DELETE") && WorkflowById().IsMatch(canonical))
        {
            fullySpecified = true;
            var name = FirstNonEmpty(subject.Name);
            var title = success
                ? WithName("Workflow deleted", name)
                : WithName("Failed to delete workflow", name);
            mapped = new EventLogMappedEvent(title, "Workflow Deleted", "Workflow", "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "GET") && WorkflowById().IsMatch(canonical))
        {
            mapped = new EventLogMappedEvent("Workflow details viewed", "Workflow Viewed", "Workflow", "Info");
            return true;
        }

        return false;
    }

    private static bool TryMapForm(
        string method,
        string path,
        bool success,
        EventLogSubject subject,
        out EventLogMappedEvent mapped,
        out bool fullySpecified)
    {
        mapped = default!;
        fullySpecified = false;
        if (!path.StartsWith("/api/form", StringComparison.OrdinalIgnoreCase))
            return false;

        if ((HttpMethodsEqual(method, "GET") || HttpMethodsEqual(method, "POST"))
            && path.Equals("/api/form/all", StringComparison.OrdinalIgnoreCase))
        {
            mapped = new EventLogMappedEvent("Forms list viewed", "Forms Listed", "Forms", "Info");
            return true;
        }

        if (HttpMethodsEqual(method, "POST") && path.Equals("/api/form", StringComparison.OrdinalIgnoreCase))
        {
            fullySpecified = true;
            var name = FirstNonEmpty(subject.Name);
            var title = success
                ? WithName("New Form Created", name)
                : WithName("Failed to create new form", name);
            mapped = new EventLogMappedEvent(title, "Form Created", "Form", success ? "Info" : "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "PUT") && FormById().IsMatch(path) && !path.EndsWith("/all", StringComparison.OrdinalIgnoreCase))
        {
            fullySpecified = true;
            var name = FirstNonEmpty(subject.Name);
            var title = success
                ? WithName("Form updated", name)
                : WithName("Failed to update form", name);
            mapped = new EventLogMappedEvent(title, "Form Updated", "Form", success ? "Info" : "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "DELETE") && FormById().IsMatch(path))
        {
            fullySpecified = true;
            var name = FirstNonEmpty(subject.Name);
            var title = success
                ? WithName("Form deleted", name)
                : WithName("Failed to delete form", name);
            mapped = new EventLogMappedEvent(title, "Form Deleted", "Form", "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "GET") && FormById().IsMatch(path))
        {
            mapped = new EventLogMappedEvent("Form details viewed", "Form Viewed", "Forms", "Info");
            return true;
        }

        return false;
    }

    private static bool TryMapRepository(
        string method,
        string path,
        bool success,
        EventLogSubject subject,
        out EventLogMappedEvent mapped,
        out bool fullySpecified)
    {
        mapped = default!;
        fullySpecified = false;
        if (!path.StartsWith("/api/repositories", StringComparison.OrdinalIgnoreCase))
            return false;

        var sevInfo = success ? "Info" : "Warning";

        if (HttpMethodsEqual(method, "GET")
            && path.Equals("/api/repositories/shared-with-me", StringComparison.OrdinalIgnoreCase))
        {
            mapped = new EventLogMappedEvent("Shared repositories viewed", "Shared Viewed", "Repository", "Info");
            return true;
        }

        if (HttpMethodsEqual(method, "DELETE") && RepoShareById().IsMatch(path))
        {
            mapped = new EventLogMappedEvent("Repository share revoked", "Share Revoked", "Repository", "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "POST") && RepoItemShare().IsMatch(path))
        {
            var label = FirstNonEmpty(subject.Email) is { } s
                ? $"Repository item shared: {s}"
                : "Repository item shared";
            mapped = new EventLogMappedEvent(label, "Item Shared", "Repository", sevInfo);
            return true;
        }

        if (HttpMethodsEqual(method, "POST")
            && (RepoItemUpload().IsMatch(path) || RepoItemUploadArchive().IsMatch(path)))
        {
            var label = FirstNonEmpty(subject.FileName) is { } s
                ? $"Repository file uploaded: {s}"
                : "Repository file uploaded";
            mapped = new EventLogMappedEvent(label, "File Uploaded", "Repository", sevInfo);
            return true;
        }

        if (HttpMethodsEqual(method, "POST") && RepoItems().IsMatch(path))
        {
            var label = FirstNonEmpty(subject.FileName, subject.Name) is { } s
                ? $"Repository item created: {s}"
                : "Repository item created";
            mapped = new EventLogMappedEvent(label, "Item Created", "Repository", sevInfo);
            return true;
        }

        if (HttpMethodsEqual(method, "POST") && path.Equals("/api/repositories", StringComparison.OrdinalIgnoreCase))
        {
            fullySpecified = true;
            var name = FirstNonEmpty(subject.Name);
            var title = success
                ? WithName("New repository created", name)
                : WithName("Failed to create new repository", name);
            mapped = new EventLogMappedEvent(title, "Repository Created", "Repository", success ? "Info" : "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "GET") && path.Equals("/api/repositories", StringComparison.OrdinalIgnoreCase))
        {
            mapped = new EventLogMappedEvent("Repositories list viewed", "Repositories Listed", "Repository", "Info");
            return true;
        }

        if (HttpMethodsEqual(method, "PUT") && RepoById().IsMatch(path))
        {
            fullySpecified = true;
            var name = FirstNonEmpty(subject.Name);
            var title = success
                ? WithName("Repository updated", name)
                : WithName("Failed to update repository", name);
            mapped = new EventLogMappedEvent(title, "Repository Updated", "Repository", success ? "Info" : "Warning");
            return true;
        }

        if (HttpMethodsEqual(method, "GET") && RepoById().IsMatch(path))
        {
            mapped = new EventLogMappedEvent("Repository details viewed", "Repository Viewed", "Repository", "Info");
            return true;
        }

        return false;
    }

    private static bool TryMapDms(
        string method,
        string path,
        bool success,
        out EventLogMappedEvent mapped)
    {
        mapped = default!;
        if (!HttpMethodsEqual(method, "POST"))
            return false;

        if (path.Equals("/api/dms/setup-schema", StringComparison.OrdinalIgnoreCase))
        {
            mapped = new EventLogMappedEvent(
                success ? "DMS setup schema completed" : "DMS setup schema failed",
                "Schema Setup",
                "System",
                success ? "Info" : "Warning");
            return true;
        }

        return false;
    }

    private static EventLogMappedEvent MapGeneric(string path, int statusCode)
    {
        var success = statusCode is >= 200 and < 400;
        var severity = statusCode switch
        {
            >= 500 => "Critical",
            >= 400 => "Warning",
            _ => "Info"
        };

        var action = HumanizeLastSegment(path);
        var title = success ? $"{action} completed" : $"{action} failed";
        return new EventLogMappedEvent(title, "Api Request", "System", severity);
    }

    private static string HumanizeLastSegment(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !s.Equals("api", StringComparison.OrdinalIgnoreCase))
            .Where(s => !GuidSegment.IsMatch(s))
            .ToList();

        var segment = segments.Count > 0 ? segments[^1] : "Request";
        var spaced = segment.Replace('-', ' ').Replace('_', ' ').Trim();
        if (string.IsNullOrWhiteSpace(spaced))
            spaced = "Request";

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
    }

    private static string? PersonLabel(EventLogSubject subject) =>
        FirstNonEmpty(subject.Name, subject.Email);

    private static string WithName(string title, string? name) =>
        name is { } s ? $"{title} - {s}" : title;

    private static string UserLabelOrUnknown(string? person) =>
        person ?? "username unknown";

    private static string NormalizePath(string path)
    {
        var value = (path ?? string.Empty).Trim();
        var q = value.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
            value = value[..q];
        if (value.Length > 1 && value.EndsWith('/'))
            value = value.TrimEnd('/');
        return value;
    }

    private static string NormalizeRouteTemplate(string routeTemplate)
    {
        var value = routeTemplate.Trim();
        if (value.StartsWith("~/", StringComparison.Ordinal))
            value = value[2..];
        else
            value = value.TrimStart('/');

        value = value.TrimEnd('/');
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith('{') && segments[i].EndsWith('}'))
                segments[i] = "{}";
        }

        return string.Join('/', segments);
    }

    private static bool IsWorkflowPath(string path) =>
        path.StartsWith("/api/workflows", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/workflow/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/workflow", StringComparison.OrdinalIgnoreCase);

    private static string ToCanonicalWorkflowPath(string path)
    {
        if (path.StartsWith("/api/workflow/", StringComparison.OrdinalIgnoreCase))
            return "/api/workflows/" + path["/api/workflow/".Length..];
        if (path.Equals("/api/workflow", StringComparison.OrdinalIgnoreCase))
            return "/api/workflows";
        return path;
    }

    private static bool HttpMethodsEqual(string method, string expected) =>
        string.Equals(method, expected, StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    [GeneratedRegex(@"^/api/users/([0-9a-fA-F\-]{36})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserById();

    [GeneratedRegex(@"^/api/users/roles/([0-9a-fA-F\-]{36})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoleById();

    [GeneratedRegex(@"^/api/users/roles/([0-9a-fA-F\-]{36})/menus$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoleMenus();

    [GeneratedRegex(@"^/api/users/groups/([0-9a-fA-F\-]{36})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GroupById();

    [GeneratedRegex(@"^/api/workflows/([0-9a-fA-F\-]{36})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowById();

    [GeneratedRegex(@"^/api/workflows/([0-9a-fA-F\-]{36})/publish$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowPublish();

    [GeneratedRegex(@"^/api/workflows/([0-9a-fA-F\-]{36})/start$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowStart();

    [GeneratedRegex(@"^/api/workflows/([0-9a-fA-F\-]{36})/start/json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowStartJson();

    [GeneratedRegex(@"^/api/workflows/instances/([0-9a-fA-F\-]{36})/steps/.+/approve$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowApprove();

    [GeneratedRegex(@"^/api/workflows/instances/([0-9a-fA-F\-]{36})/steps/.+/reject$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowReject();

    [GeneratedRegex(@"^/api/workflows/instances/([0-9a-fA-F\-]{36})/.+/move-next$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowMoveNext();

    [GeneratedRegex(@"^/api/workflows/instances/([0-9a-fA-F\-]{36})/.+/actions$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowActions();

    [GeneratedRegex(@"^/api/workflows/instances/([0-9a-fA-F\-]{36})/.+/share-file$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowShareFile();

    [GeneratedRegex(@"^/api/form/([^/]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FormById();

    [GeneratedRegex(@"^/api/repositories/([0-9a-fA-F\-]{36})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepoById();

    [GeneratedRegex(@"^/api/repositories/([0-9a-fA-F\-]{36})/items$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepoItems();

    [GeneratedRegex(@"^/api/repositories/([0-9a-fA-F\-]{36})/items/upload$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepoItemUpload();

    [GeneratedRegex(@"^/api/repositories/([0-9a-fA-F\-]{36})/items/upload-archive$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepoItemUploadArchive();

    [GeneratedRegex(@"^/api/repositories/([0-9a-fA-F\-]{36})/items/([0-9a-fA-F\-]{36})/share$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepoItemShare();

    [GeneratedRegex(@"^/api/repositories/share/([0-9a-fA-F\-]{36})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepoShareById();
}
