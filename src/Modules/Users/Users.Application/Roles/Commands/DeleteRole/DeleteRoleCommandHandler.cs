using MediatR;
using SaaSApp.Users.Application.Contracts;
using SaaSApp.Users.Domain.Entities;

namespace SaaSApp.Users.Application.Roles.Commands.DeleteRole;

public sealed class DeleteRoleCommandHandler : IRequestHandler<DeleteRoleCommand, DeleteRoleCommandResult>
{
    private readonly IRoleRepository _roleRepository;
    private readonly IUserRepository _userRepository;
    private readonly IUserTenantRoleSync _userTenantRoleSync;
    private readonly ITenantContext _tenantContext;

    public DeleteRoleCommandHandler(
        IRoleRepository roleRepository,
        IUserRepository userRepository,
        IUserTenantRoleSync userTenantRoleSync,
        ITenantContext tenantContext)
    {
        _roleRepository = roleRepository;
        _userRepository = userRepository;
        _userTenantRoleSync = userTenantRoleSync;
        _tenantContext = tenantContext;
    }

    public async Task<DeleteRoleCommandResult> Handle(DeleteRoleCommand request, CancellationToken cancellationToken)
    {
        var role = await _roleRepository.GetByIdAsync(request.RoleId, cancellationToken);
        if (role == null)
            return new DeleteRoleCommandResult(Found: false);

        if (Role.IsReservedName(role.Name))
            return new DeleteRoleCommandResult(
                Found: true,
                Error: "Built-in roles (admin, tenantuser) cannot be deleted.",
                StatusCode: 400);

        // Unassign all users from this role (hard-delete users.UserRoles rows).
        role.ReplaceUsers([]);

        // Remove this role name from comma-separated users.Users.Role (empty when none remain).
        var affectedUsers = await _userRepository.RemoveRoleNameFromUsersAsync(role.Name, cancellationToken);

        role.SoftDelete();

        if (affectedUsers.Count > 0)
        {
            var tenantId = _tenantContext.TenantId
                ?? throw new InvalidOperationException("TenantId is required to sync role deletions.");

            foreach (var (email, newRole) in affectedUsers)
                await _userTenantRoleSync.SyncRoleForUserAsync(email, tenantId, newRole, cancellationToken);
        }

        return new DeleteRoleCommandResult(Found: true, StatusCode: 204);
    }
}
