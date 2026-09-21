using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Api.Middleware;

/// <summary>Request-scoped validated share grant (set by <see cref="RepositoryShareMiddleware"/>).</summary>
public sealed class RepositoryShareContext
{
    public const string HttpContextItemKey = "RepositoryShareContext";

    public RepositoryShareAccess Access { get; }

    public RepositoryShareContext(RepositoryShareAccess access) => Access = access;

    public Guid SourceTenantId => Access.SourceTenantId;
    public Guid SourceRepositoryId => Access.SourceRepositoryId;
    public Guid? SourceItemId => Access.SourceItemId;
    public bool ReadOnly => Access.ReadOnly;
    public string? FiltersJson => Access.FiltersJson;
    public bool IsFilterShare => Access.IsFilterShare;
    public bool IsDashboardShare => Access.IsDashboardShare;
    public Guid? SourceDashboardId => Access.SourceDashboardId;

    public static bool TryGet(HttpContext httpContext, out RepositoryShareContext? context)
    {
        if (httpContext.Items.TryGetValue(HttpContextItemKey, out var value)
            && value is RepositoryShareContext ctx)
        {
            context = ctx;
            return true;
        }

        context = null;
        return false;
    }
}
