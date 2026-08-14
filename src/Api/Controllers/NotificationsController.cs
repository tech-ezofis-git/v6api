using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Security;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Api.Controllers;

/// <summary>In-app notifications for the current tenant user. Requires JWT and X-Tenant-Id.</summary>
[ApiController]
[Route("api/notifications")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class NotificationsController : ControllerBase
{
    private readonly IWorkflowNotificationQueryService _notifications;

    public NotificationsController(IWorkflowNotificationQueryService notifications) =>
        _notifications = notifications;

    /// <summary>Notifications for the current user. Optional category filter (workflow, form, upload, …).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<WorkflowNotificationItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? category = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var items = await _notifications.ListForCurrentUserAsync(category, cancellationToken);
            return Ok(items);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("User context", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    /// <summary>Mark a notification as read for the current user.</summary>
    [HttpPatch("{id:int}/read")]
    [ProducesResponseType(typeof(WorkflowNotificationReadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(int id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _notifications.MarkReadAsync(id, cancellationToken);
            if (result == null)
                return NotFound(new { error = "Notification not found." });
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("User context", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    /// <summary>Soft-delete a notification for the current user.</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await _notifications.DeleteAsync(id, cancellationToken);
            if (!deleted)
                return NotFound(new { error = "Notification not found." });
            return NoContent();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("User context", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(new { error = ex.Message });
        }
    }
}
