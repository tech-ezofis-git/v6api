using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Security;
using SaaSApp.Workflow.Application.Connectors;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Api.Controllers;

[ApiController]
[Route("api/email-ingest")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class EmailIngestController : ControllerBase
{
    private readonly IServiceProvider _services;
    private readonly IGmailPushService _gmailPush;
    private readonly IWatchFoxWebhookService _watchFox;

    public EmailIngestController(
        IServiceProvider services,
        IGmailPushService gmailPush,
        IWatchFoxWebhookService watchFox)
    {
        _services = services;
        _gmailPush = gmailPush;
        _watchFox = watchFox;
    }

    /// <summary>
    /// Mailbox actions need the tenant database. The anonymous Gmail push does not,
    /// so this stays lazy and is not opened while Pub/Sub calls /gmail/push.
    /// </summary>
    private IEmailIngestService EmailIngest => _services.GetRequiredService<IEmailIngestService>();

    /// <summary>Google Pub/Sub push. When mail arrives, poll the linked mailbox and start the workflow.</summary>
    [AllowAnonymous]
    [HttpPost("gmail/push")]
    public async Task<IActionResult> GmailPush([FromQuery] string? token, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        var headerToken = Request.Headers["X-Gmail-Push-Token"].FirstOrDefault();
        try
        {
            await _gmailPush.HandlePushAsync(body, string.IsNullOrWhiteSpace(token) ? headerToken : token, cancellationToken);
            return Ok(new { accepted = true });
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// WatchFox invoice webhook. A match queues an MJB ticket. Other mail is ignored.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("watchfox")]
    public async Task<IActionResult> WatchFox(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        var signature = Request.Headers["X-WatchFox-Signature"].FirstOrDefault();
        try
        {
            await _watchFox.AcceptAsync(body, signature, cancellationToken);
            return Ok(new { accepted = true });
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    [HttpGet("mailboxes")]
    [ProducesResponseType(typeof(IReadOnlyList<EmailIngestMailboxDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var items = await EmailIngest.ListMailboxesAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("mailboxes/{id:guid}")]
    [ProducesResponseType(typeof(EmailIngestMailboxDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var item = await EmailIngest.GetMailboxAsync(id, cancellationToken);
        return item == null ? NotFound() : Ok(item);
    }

    [HttpPost("mailboxes")]
    [ProducesResponseType(typeof(EmailIngestMailboxDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] EmailIngestMailboxUpsertRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var created = await EmailIngest.CreateMailboxAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("mailboxes/{id:guid}")]
    [ProducesResponseType(typeof(EmailIngestMailboxDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] EmailIngestMailboxUpsertRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await EmailIngest.UpdateMailboxAsync(id, request, cancellationToken);
            return updated == null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("mailboxes/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var ok = await EmailIngest.DeleteMailboxAsync(id, cancellationToken);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Manual poll for one mailbox (Swagger / UI "Poll now").</summary>
    [HttpPost("mailboxes/{id:guid}/poll")]
    [ProducesResponseType(typeof(EmailIngestPollResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Poll(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await EmailIngest.PollMailboxAsync(id, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
