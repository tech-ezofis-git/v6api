using MediatR;
using SaaSApp.Users.Application.Contracts;
using SaaSApp.Users.Domain.Entities;

namespace SaaSApp.Users.Application.Roles.Commands.DeleteRole;

public sealed class DeleteRoleCommandHandler : IRequestHandler<DeleteRoleCommand, DeleteRoleCommandResult>
{
    private readonly IRoleRepository _roleRepository;

    public DeleteRoleCommandHandler(IRoleRepository roleRepository)
    {
        _roleRepository = roleRepository;
    }

    public async Task<DeleteRoleCommandResult> Handle(DeleteRoleCommand request, CancellationToken cancellationToken)
    {
        var role = await _roleRepository.GetByIdAsync(request.RoleId, cancellationToken);
        if (role == null)
            return new DeleteRoleCommandResult(Found: false);

        if (Role.IsReservedName(role.Name))
            return new DeleteRoleCommandResult(
                Found: true,
                Error: "Built-in roles cannot be deleted.",
                StatusCode: 400);

        if (role.UserRoles.Count > 0)
            return new DeleteRoleCommandResult(
                Found: true,
                Error: "Role is assigned to one or more users and cannot be deleted.",
                StatusCode: 400);

        role.SoftDelete();
        return new DeleteRoleCommandResult(Found: true, StatusCode: 204);
    }
}
