using MediatR;

namespace SaaSApp.Users.Application.Roles.Commands.DeleteRole;

public record DeleteRoleCommand(Guid RoleId) : IRequest<DeleteRoleCommandResult>;

public record DeleteRoleCommandResult(bool Found, string? Error = null, int StatusCode = 404);
