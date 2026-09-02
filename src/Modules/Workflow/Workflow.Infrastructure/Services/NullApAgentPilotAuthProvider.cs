using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>No pilot auth enrichment when API layer does not register a real provider.</summary>
public sealed class NullApAgentPilotAuthProvider : IApAgentPilotAuthProvider
{
    public Task<ApAgentPilotAuth?> GetAuthForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ApAgentPilotAuth?>(null);
}
