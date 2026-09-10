namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>
/// Issues tenant-scoped JWT for the AP Agent pilot service account (pilot@ezofis.com).
/// Used to enrich start payloads so Python can call V6 APIs as the agent user.
/// </summary>
public interface IApAgentPilotAuthProvider
{
    Task<ApAgentPilotAuth?> GetAuthForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Pilot service-account JWT for one tenant.</summary>
public sealed record ApAgentPilotAuth(
    Guid UserId,
    string Email,
    string AccessToken,
    int ExpiresInSeconds);
