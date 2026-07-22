using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SaaSApp.ActivityLog.Application.Contracts;
using SaaSApp.ActivityLog.Infrastructure.Options;
using SaaSApp.Catalog;
using SaaSApp.MultiTenancy;
using SaaSApp.Security;
using SaaSApp.Users.Application.Contracts;
using SaaSApp.Users.Application.Users.Commands.CompleteUserConfiguration;
using SaaSApp.Users.Application.Users.Commands.CreateUser;
using SaaSApp.Users.Application.Users.Commands.DeleteUser;
using SaaSApp.Users.Application.Users.Commands.UpdateUser;
using SaaSApp.Users.Application.Roles.Commands.CreateRole;
using SaaSApp.Users.Application.Roles.Commands.DeleteRole;
using SaaSApp.Users.Application.Roles.Commands.UpdateRole;
using SaaSApp.Users.Application.Roles.Queries.ListPermissionCatalog;
using SaaSApp.Users.Application.Roles.Queries.ListRoles;
using SaaSApp.Users.Application.Roles.Queries.GetRoleById;
using SaaSApp.Users.Application.Groups.Commands.CreateGroup;
using SaaSApp.Users.Application.Groups.Commands.UpdateGroup;
using SaaSApp.Users.Application.Groups.Commands.DeleteGroup;
using SaaSApp.Users.Application.Groups.Queries.ListGroups;
using SaaSApp.Users.Application.Groups.Queries.GetGroupById;
using SaaSApp.Users.Application.Menus.Commands.CreateMenu;
using SaaSApp.Users.Application.Menus.Commands.UpdateMenu;
using SaaSApp.Users.Application.Menus.Commands.DeleteMenu;
using SaaSApp.Users.Application.Menus.Queries.ListMenus;
using SaaSApp.Users.Application.Menus.Queries.GetMenuById;
using SaaSApp.Users.Application.Roles.Commands.SetRoleMenus;
using SaaSApp.Users.Application.Roles.Queries.GetRoleMenus;
using SaaSApp.Users.Application.Users;
using SaaSApp.Users.Application.Users.Queries.GetCurrentUser;
using SaaSApp.Users.Application.Users.Queries.GetUserById;
using SaaSApp.Users.Application.Users.Queries.ListUsers;

namespace SaaSApp.Api.Controllers;

/// <summary>User CRUD for the current tenant. Requires JWT and X-Tenant-Id.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class UsersController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;
    private readonly IUserTenantRegistry _userTenantRegistry;
    private readonly IActivityLogQueryService _activityLogQuery;
    private readonly ActivityLogOptions _activityLogOptions;

    public UsersController(
        IMediator mediator,
        ITenantContext tenantContext,
        IUserTenantRegistry userTenantRegistry,
        IActivityLogQueryService activityLogQuery,
        IOptions<ActivityLogOptions> activityLogOptions)
    {
        _mediator = mediator;
        _tenantContext = tenantContext;
        _userTenantRegistry = userTenantRegistry;
        _activityLogQuery = activityLogQuery;
        _activityLogOptions = activityLogOptions.Value;
    }

    /// <summary>Create a new user in the current tenant. Admin only. Optionally set password for Ezofis login.</summary>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(CreateUserCommandResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");
        var createdBy = GetCurrentUserId();
        var command = new CreateUserCommand(
            request.Email,
            request.DisplayName,
            request.Password,
            request.Role,
            request.FirstName,
            request.LastName,
            request.AuthStrategy,
            request.UserName,
            request.LoginType,
            request.PasswordExpiryDays,
            request.AccountExpiryDate,
            request.ForcePasswordResetOnLogin,
            request.JobTitle,
            request.EmployeeId,
            request.Department,
            request.BusinessUnit,
            request.Manager,
            request.Location,
            request.Group,
            request.MfAuthentication,
            request.MfaMethods,
            request.PhoneNo,
            request.Language,
            request.CountryCode,
            request.AvatarPath,
            request.UiPreference,
            request.SecondaryEmail,
            request.UserType,
            request.IdCardPath,
            request.SignaturePath,
            createdBy);
        var result = await _mediator.Send(command, cancellationToken);
        if (!result.Success)
            return StatusCode(result.StatusCode, new { error = result.Error });

        await _userTenantRegistry.AddOrUpdateAsync(
            request.Email.Trim(),
            tenantId,
            result.RoleName ?? SaaSApp.Users.Domain.Entities.User.RoleTenantUser,
            result.UserId,
            cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.UserId }, new { userId = result.UserId });
    }

    /// <summary>Current user profile and custom-role permissions (path is /api/usersession). Includes permissionCount and permissionKeys as full catalog items with visible flags.</summary>
    [HttpGet("/api/usersession")]
    [ProducesResponseType(typeof(UserExtendedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserSession(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        if (_tenantContext.TenantId == null)
            return BadRequest(new { error = "Tenant not resolved. Send X-Tenant-Id header or ensure JWT includes tid." });

        var result = await _mediator.Send(new GetCurrentUserQuery(userId.Value), cancellationToken);
        if (result == null)
            return NotFound(new { error = "User not found in this tenant." });

        return Ok(result);
    }

    /// <summary>List all users in the current tenant (excluding soft-deleted).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ListUsersQueryResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListUsersQuery(), cancellationToken);
        return Ok(result);
    }

    /// <summary>List permission categories (key + name) for the role Permissions tab. Admin only.</summary>
    [HttpGet("roles/permissions")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(ListPermissionCatalogQueryResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListRolePermissions(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListPermissionCatalogQuery(), cancellationToken);
        return Ok(result);
    }

    /// <summary>List custom roles in the current tenant. Admin only.</summary>
    [HttpGet("roles")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(ListRolesQueryResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListRoles(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListRolesQuery(), cancellationToken);
        return Ok(result);
    }

    /// <summary>Create a custom role with assigned users and category-level permissions. Admin only.</summary>
    [HttpPost("roles")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(CreateRoleResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest request, CancellationToken cancellationToken)
    {
        var command = new CreateRoleCommand(
            request.RoleName,
            request.Description,
            request.Users ?? [],
            request.Permissions ?? []);
        var result = await _mediator.Send(command, cancellationToken);
        if (!result.Success)
            return StatusCode(result.StatusCode, new { error = result.Error });

        return CreatedAtAction(
            nameof(GetRoleById),
            new { roleId = result.RoleId },
            new CreateRoleResponse(result.RoleId!.Value, result.RoleName!, result.UserCount, result.PermissionCount));
    }

    /// <summary>Get a custom role by ID with assigned users and permissionKeys (full catalog with visible flags). Admin only.</summary>
    [HttpGet("roles/{roleId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(GetRoleByIdQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRoleById(Guid roleId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetRoleByIdQuery(roleId), cancellationToken);
        if (result == null)
            return NotFound();
        return Ok(result);
    }

    /// <summary>Update a custom role name, description, assigned users, and category-level permissions. Admin only.</summary>
    [HttpPut("roles/{roleId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateRole(Guid roleId, [FromBody] UpdateRoleRequest request, CancellationToken cancellationToken)
    {
        var command = new UpdateRoleCommand(
            roleId,
            request.RoleName,
            request.Description,
            request.Users,
            request.Permissions);
        var result = await _mediator.Send(command, cancellationToken);
        if (!result.Success)
            return StatusCode(result.StatusCode, new { error = result.Error });
        return NoContent();
    }

    /// <summary>Soft-delete a custom role. Fails if the role is assigned to any user. Admin only.</summary>
    [HttpDelete("roles/{roleId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRole(Guid roleId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new DeleteRoleCommand(roleId), cancellationToken);
        if (!result.Found)
            return NotFound();
        if (!string.IsNullOrWhiteSpace(result.Error))
            return StatusCode(result.StatusCode, new { error = result.Error });
        return NoContent();
    }

    /// <summary>List user groups in the current tenant. Admin only.</summary>
    [HttpGet("groups")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(ListGroupsQueryResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListGroups(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListGroupsQuery(), cancellationToken);
        return Ok(result);
    }

    /// <summary>Get a user group by ID with member users. Admin only.</summary>
    [HttpGet("groups/{groupId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(GetGroupByIdQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGroupById(Guid groupId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetGroupByIdQuery(groupId), cancellationToken);
        if (result == null)
            return NotFound();
        return Ok(result);
    }

    /// <summary>Create a user group with assigned members. Admin only.</summary>
    [HttpPost("groups")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(CreateGroupResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupRequest request, CancellationToken cancellationToken)
    {
        var command = new CreateGroupCommand(
            request.GroupName,
            request.Description,
            request.Users ?? []);
        var result = await _mediator.Send(command, cancellationToken);
        if (!result.Success)
            return StatusCode(result.StatusCode, new { error = result.Error });

        return CreatedAtAction(
            nameof(GetGroupById),
            new { groupId = result.GroupId },
            new CreateGroupResponse(result.GroupId!.Value, result.GroupName!, result.UserCount));
    }

    /// <summary>Update a user group. Admin only. Only provided fields are updated. Users replaces member list when included (may be empty to clear).</summary>
    [HttpPut("groups/{groupId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateGroup(Guid groupId, [FromBody] UpdateGroupRequest request, CancellationToken cancellationToken)
    {
        var command = new UpdateGroupCommand(
            groupId,
            request.GroupName,
            request.Description,
            request.Users);
        var result = await _mediator.Send(command, cancellationToken);
        if (!result.Success)
            return StatusCode(result.StatusCode, new { error = result.Error });
        return NoContent();
    }

    /// <summary>Soft-delete a user group. Admin only.</summary>
    [HttpDelete("groups/{groupId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteGroup(Guid groupId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new DeleteGroupCommand(groupId), cancellationToken);
        if (!result.Found)
            return NotFound();
        return NoContent();
    }

    /// <summary>List navigation menus. Admin only.</summary>
    [HttpGet("menus")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(ListMenusQueryResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMenus(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListMenusQuery(), cancellationToken);
        return Ok(result);
    }

    /// <summary>Get a navigation menu by ID. Admin only.</summary>
    [HttpGet("menus/{menuId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(GetMenuByIdQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMenuById(Guid menuId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetMenuByIdQuery(menuId), cancellationToken);
        if (result == null)
            return NotFound();
        return Ok(result);
    }

    /// <summary>Create a custom navigation menu. Admin only.</summary>
    [HttpPost("menus")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(CreateMenuResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateMenu([FromBody] CreateMenuRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new CreateMenuCommand(request.Key, request.Label, request.RoutePath, request.SortOrder),
            cancellationToken);
        if (!result.Success)
            return StatusCode(result.StatusCode, new { error = result.Error });

        return CreatedAtAction(
            nameof(GetMenuById),
            new { menuId = result.MenuId },
            new CreateMenuResponse(result.MenuId!.Value, result.Key!, result.Label!));
    }

    /// <summary>Update a navigation menu label, route, and sort order. Admin only.</summary>
    [HttpPut("menus/{menuId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMenu(Guid menuId, [FromBody] UpdateMenuRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new UpdateMenuCommand(menuId, request.Label, request.RoutePath, request.SortOrder),
            cancellationToken);
        if (!result.Success)
            return StatusCode(result.StatusCode, new { error = result.Error });
        return NoContent();
    }

    /// <summary>Soft-delete a custom navigation menu. System menus cannot be deleted. Admin only.</summary>
    [HttpDelete("menus/{menuId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteMenu(Guid menuId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new DeleteMenuCommand(menuId), cancellationToken);
        if (!result.Found)
            return NotFound();
        if (result.Error != null)
            return StatusCode(result.StatusCode, new { error = result.Error });
        return NoContent();
    }

    /// <summary>Get menus assigned to a role with default landing page. Admin only.</summary>
    [HttpGet("roles/{roleId:guid}/menus")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(GetRoleMenusQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRoleMenus(Guid roleId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetRoleMenusQuery(roleId), cancellationToken);
        if (result == null)
            return NotFound();
        return Ok(result);
    }

    /// <summary>Assign menus to a role and set the default landing page. Admin only.</summary>
    [HttpPut("roles/{roleId:guid}/menus")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetRoleMenus(Guid roleId, [FromBody] SetRoleMenusRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new SetRoleMenusCommand(roleId, request.Menus ?? [], request.DefaultLandingMenuId),
            cancellationToken);
        if (!result.Success)
            return StatusCode(result.StatusCode, new { error = result.Error });
        return NoContent();
    }

    /// <summary>Get onboarding pre-questions saved for a user in catalog.UserTenants.</summary>
    [HttpGet("{userId:guid}/pre-questions")]
    [ProducesResponseType(typeof(UserPreQuestionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPreQuestions(Guid userId, CancellationToken cancellationToken)
    {
        if (!CanAccessUserProfile(userId))
            return Forbid();

        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");
        var user = await _mediator.Send(new GetUserByIdQuery(userId), cancellationToken);
        if (user == null)
            return NotFound(new { error = "User not found in this tenant." });

        var result = await _userTenantRegistry.GetPreQuestionsAsync(userId, tenantId, user.Email, cancellationToken);
        if (result == null)
            return NotFound(new { error = "User tenant membership not found in catalog." });

        return Ok(result);
    }

    /// <summary>Mark user onboarding configuration as completed (Configuration = 1).</summary>
    [HttpPost("{userId:guid}/configuration")]
    [ProducesResponseType(typeof(UserConfigurationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CompleteConfiguration(
        Guid userId,
        [FromBody] CompleteUserConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        if (!CanAccessUserProfile(userId))
            return Forbid();

        var result = await _mediator.Send(
            new CompleteUserConfigurationCommand(userId, request.Message ?? string.Empty),
            cancellationToken);

        if (!string.IsNullOrEmpty(result.Error))
            return BadRequest(new { error = result.Error });

        if (!result.Found)
            return NotFound(new { error = "User not found in this tenant." });

        return Ok(new UserConfigurationResponse(userId, result.Configuration, request.Message));
    }

    /// <summary>Save onboarding pre-questions and answers as JSON in catalog.UserTenants for the user.</summary>
    [HttpPut("{userId:guid}/pre-questions")]
    [ProducesResponseType(typeof(UserPreQuestionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdatePreQuestions(
        Guid userId,
        [FromBody] UpdateUserPreQuestionsRequest request,
        CancellationToken cancellationToken)
    {
        if (!CanAccessUserProfile(userId))
            return Forbid();

        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");
        var user = await _mediator.Send(new GetUserByIdQuery(userId), cancellationToken);
        if (user == null)
            return NotFound(new { error = "User not found in this tenant." });

        try
        {
            var updated = await _userTenantRegistry.UpdatePreQuestionsAsync(
                userId,
                tenantId,
                user.Email,
                request.Questions ?? [],
                cancellationToken);

            if (!updated)
                return NotFound(new { error = "User tenant membership could not be updated in catalog." });

            var result = await _userTenantRegistry.GetPreQuestionsAsync(userId, tenantId, user.Email, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Get a user by ID in the current tenant. Includes permissionCount and permissionKeys as full catalog items with visible flags.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(UserExtendedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetUserByIdQuery(id), cancellationToken);
        if (result == null)
            return NotFound();
        return Ok(result);
    }

    /// <summary>Update a user's profile. Admin only. Only provided fields are updated.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var modifiedBy = GetCurrentUserId();
        var result = await _mediator.Send(new UpdateUserCommand(
            id,
            request.Email,
            request.DisplayName,
            request.Password,
            request.Role,
            request.FirstName,
            request.LastName,
            request.AuthStrategy,
            request.UserName,
            request.LoginType,
            request.PasswordExpiryDays,
            request.AccountExpiryDate,
            request.ForcePasswordResetOnLogin,
            request.JobTitle,
            request.EmployeeId,
            request.Department,
            request.BusinessUnit,
            request.Manager,
            request.Location,
            request.Group,
            request.MfAuthentication,
            request.MfaMethods,
            request.PhoneNo,
            request.Language,
            request.CountryCode,
            request.AvatarPath,
            request.UiPreference,
            request.SecondaryEmail,
            request.UserType,
            request.IdCardPath,
            request.SignaturePath,
            modifiedBy), cancellationToken);

        if (!result.Success)
            return StatusCode(result.StatusCode, new { error = result.Error });
        if (!result.Found)
            return NotFound();

        if (result.RegistryEmail != null && result.RegistryRole != null)
        {
            var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");
            await _userTenantRegistry.AddOrUpdateAsync(result.RegistryEmail, tenantId, result.RegistryRole, id, cancellationToken);
        }

        return NoContent();
    }

    /// <summary>Soft-delete a user. Admin only.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new DeleteUserCommand(id), cancellationToken);
        if (!result.Found)
            return NotFound();
        return NoContent();
    }

    /// <summary>Paginated API access history for a user. Admin only. Disabled while ActivityLog:Enabled is false.</summary>
    [HttpGet("{id:guid}/activity-logs")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ApiExplorerSettings(IgnoreApi = true)]
    [ProducesResponseType(typeof(PagedResult<ActivityLogEntryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ActivityLogEntryDto>>> GetActivityLogs(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? method = null,
        [FromQuery] string? path = null,
        [FromQuery] int? statusCode = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        CancellationToken cancellationToken = default)
    {
        if (!_activityLogOptions.Enabled)
            return NotFound();

        var tenantId = _tenantContext.TenantId;
        if (tenantId == null)
            return BadRequest(new { error = "Tenant not resolved." });

        var query = new ListActivityLogsQuery(page, pageSize, id, method, path, statusCode, dateFrom, dateTo);
        var result = await _activityLogQuery.ListAsync(tenantId.Value, query, cancellationToken);
        return Ok(result);
    }

    private Guid? GetCurrentUserId()
    {
        var user = HttpContext.User;
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? user.FindFirstValue("sub")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("userId");
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private bool CanAccessUserProfile(Guid userId)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId == userId)
            return true;

        return User.IsInRole(SaaSApp.Users.Domain.Entities.User.RoleAdmin)
            || User.HasClaim(ClaimTypes.Role, SaaSApp.Users.Domain.Entities.User.RoleAdmin);
    }
}

/// <summary>Request to create a user with extended profile and auth settings.</summary>
public sealed class CreateUserRequest
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Password { get; set; }
    public string? Role { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? AuthStrategy { get; set; }
    public string? UserName { get; set; }

    [JsonPropertyName("LoginType")]
    public string? LoginType { get; set; }

    public int? PasswordExpiryDays { get; set; }
    public DateTime? AccountExpiryDate { get; set; }
    public string? ForcePasswordResetOnLogin { get; set; }

    [JsonPropertyName("Job Title")]
    public string? JobTitle { get; set; }

    [JsonPropertyName("Employee Id")]
    public string? EmployeeId { get; set; }

    public string? Department { get; set; }

    [JsonPropertyName("Bussiness Unit")]
    public string? BusinessUnit { get; set; }

    [JsonPropertyName("Manager")]
    public string? Manager { get; set; }

    public string? Location { get; set; }
    public string[]? Group { get; set; }

    [JsonPropertyName("MFAuthentication")]
    public string? MfAuthentication { get; set; }

    [JsonPropertyName("MFA Methods")]
    public string? MfaMethods { get; set; }

    public string? PhoneNo { get; set; }
    public string? Language { get; set; }
    public string? CountryCode { get; set; }
    public string? AvatarPath { get; set; }
    public string? UiPreference { get; set; }
    public string? SecondaryEmail { get; set; }
    public string? UserType { get; set; }
    public string? IdCardPath { get; set; }
    public string? SignaturePath { get; set; }
}

/// <summary>Request to update a user. Only non-null fields are updated.</summary>
public sealed class UpdateUserRequest
{
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Password { get; set; }
    public string? Role { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? AuthStrategy { get; set; }
    public string? UserName { get; set; }

    [JsonPropertyName("LoginType")]
    public string? LoginType { get; set; }

    public int? PasswordExpiryDays { get; set; }
    public DateTime? AccountExpiryDate { get; set; }
    public string? ForcePasswordResetOnLogin { get; set; }

    [JsonPropertyName("Job Title")]
    public string? JobTitle { get; set; }

    [JsonPropertyName("Employee Id")]
    public string? EmployeeId { get; set; }

    public string? Department { get; set; }

    [JsonPropertyName("Bussiness Unit")]
    public string? BusinessUnit { get; set; }

    [JsonPropertyName("Manager")]
    public string? Manager { get; set; }

    public string? Location { get; set; }
    public string[]? Group { get; set; }

    [JsonPropertyName("MFAuthentication")]
    public string? MfAuthentication { get; set; }

    [JsonPropertyName("MFA Methods")]
    public string? MfaMethods { get; set; }

    public string? PhoneNo { get; set; }
    public string? Language { get; set; }
    public string? CountryCode { get; set; }
    public string? AvatarPath { get; set; }
    public string? UiPreference { get; set; }
    public string? SecondaryEmail { get; set; }
    public string? UserType { get; set; }
    public string? IdCardPath { get; set; }
    public string? SignaturePath { get; set; }
}

/// <summary>
/// Request to create a custom role with assigned users and category-level permissions.
/// <paramref name="Permissions"/> accepts module category names or keys (e.g. ["Dashboard","workflow","Folder"]), not dotted keys like dashboard.view.
/// </summary>
public record CreateRoleRequest(string RoleName, IReadOnlyList<Guid> Users, IReadOnlyList<string> Permissions, string? Description = null);

/// <summary>
/// Request to update a custom role and replace its users and category-level permissions.
/// When provided, <paramref name="Permissions"/> is category names/keys only (e.g. ["Dashboard","workflow"]).
/// </summary>
public record UpdateRoleRequest(
    string? RoleName = null,
    string? Description = null,
    IReadOnlyList<Guid>? Users = null,
    IReadOnlyList<string>? Permissions = null);

/// <summary>Response after creating a custom role.</summary>
public record CreateRoleResponse(Guid RoleId, string RoleName, int UserCount, int PermissionCount);

/// <summary>Request to create a user group with assigned members.</summary>
public record CreateGroupRequest(string GroupName, IReadOnlyList<Guid> Users, string? Description = null);

/// <summary>Request to update a user group. Only provided fields are updated.</summary>
public record UpdateGroupRequest(string? GroupName = null, IReadOnlyList<Guid>? Users = null, string? Description = null);

/// <summary>Response after creating a user group.</summary>
public record CreateGroupResponse(Guid GroupId, string GroupName, int UserCount);

/// <summary>Request to create a navigation menu.</summary>
public record CreateMenuRequest(string Key, string Label, string RoutePath, int SortOrder);

/// <summary>Request to update a navigation menu.</summary>
public record UpdateMenuRequest(string Label, string RoutePath, int SortOrder);

/// <summary>Response after creating a navigation menu.</summary>
public record CreateMenuResponse(Guid MenuId, string Key, string Label);

/// <summary>Request to assign menus to a role and set default landing page.</summary>
public record SetRoleMenusRequest(IReadOnlyList<Guid> Menus, Guid? DefaultLandingMenuId = null);

/// <summary>Onboarding pre-questions to store in catalog.UserTenants.PreQuestionsJson.</summary>
public record UpdateUserPreQuestionsRequest(IReadOnlyList<PreQuestionAnswerDto> Questions);

/// <summary>Mark onboarding configuration complete. Message must be <c>configuration:completed</c>.</summary>
public record CompleteUserConfigurationRequest(string Message);

/// <summary>User configuration flag after update (0 = pending, 1 = completed).</summary>
public record UserConfigurationResponse(Guid UserId, int Configuration, string? Message);
