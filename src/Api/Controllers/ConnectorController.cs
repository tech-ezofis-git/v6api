using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Security;
using SaaSApp.Workflow.Application.Connectors;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Api.Controllers;

[ApiController]
[Route("api/connector")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class ConnectorController : ControllerBase
{
    private readonly IConnectorService _connectorService;
    private readonly IConnectorOAuthService _oauthService;
    private readonly ISapPurchaseOrderLookupService _sapPurchaseOrderLookup;

    public ConnectorController(
        IConnectorService connectorService,
        IConnectorOAuthService oauthService,
        ISapPurchaseOrderLookupService sapPurchaseOrderLookup)
    {
        _connectorService = connectorService;
        _oauthService = oauthService;
        _sapPurchaseOrderLookup = sapPurchaseOrderLookup;
    }

    /// <summary>Create a new connector (v5 POST /api/connector).</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ConnectorDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] ConnectorUpsertRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var created = await _connectorService.CreateAsync(request, cancellationToken);
            return Created($"/api/connector/{created.Id}", created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Update connector by id (v5 PUT /api/connector/{id}).</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ConnectorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(Guid id, [FromBody] ConnectorUpsertRequest request, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
            return NotFound(new { error = "ID mismatch" });

        try
        {
            var updated = await _connectorService.UpdateAsync(id, request, cancellationToken);
            if (updated == null)
                return NotFound(new { error = "Connector not found." });
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Get all active connectors (no body required).</summary>
    [HttpGet("all")]
    [ProducesResponseType(typeof(ConnectorListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        try
        {
            var items = await _connectorService.ListAsync(new ConnectorListRequest(), cancellationToken);
            return Ok(new ConnectorListResponse(items));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>List connectors with filters (v5 POST /api/connector/all).</summary>
    [HttpPost("all")]
    [ProducesResponseType(typeof(ConnectorListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListAll([FromBody] ConnectorListRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var items = await _connectorService.ListAsync(request, cancellationToken);
            return Ok(new ConnectorListResponse(items));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>List active OAuth providers from Catalog (no secrets).</summary>
    [HttpGet("providers")]
    [ProducesResponseType(typeof(IReadOnlyList<ConnectorProviderPublicDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListProviders(CancellationToken cancellationToken)
    {
        var items = await _oauthService.ListProvidersAsync(cancellationToken);
        return Ok(items);
    }

    /// <summary>Start OAuth: returns authorizationUrl to open in browser.</summary>
    [HttpPost("oauth/authorize")]
    [ProducesResponseType(typeof(ConnectorOAuthAuthorizeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Authorize([FromBody] ConnectorOAuthAuthorizeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _oauthService.BeginAuthorizeAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>OAuth callback from provider. Anonymous — validated via signed state.</summary>
    [HttpGet("oauth/callback")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> OAuthCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery] string? realmId,
        CancellationToken cancellationToken)
    {
        try
        {
            var redirectUrl = await _oauthService.CompleteCallbackAsync(code, state, error, realmId, cancellationToken);
            return Redirect(redirectUrl);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Force refresh access token.</summary>
    [HttpPost("{id:guid}/oauth/refresh")]
    [ProducesResponseType(typeof(ConnectorOAuthStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Refresh(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var status = await _oauthService.RefreshAsync(id, cancellationToken);
            return status == null ? NotFound() : Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Clear tokens and mark connector as Revoked.</summary>
    [HttpPost("{id:guid}/disconnect")]
    [ProducesResponseType(typeof(ConnectorOAuthStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken cancellationToken)
    {
        var status = await _oauthService.DisconnectAsync(id, cancellationToken);
        return status == null ? NotFound() : Ok(status);
    }

    /// <summary>OAuth connection status (no token values).</summary>
    [HttpGet("{id:guid}/status")]
    [ProducesResponseType(typeof(ConnectorOAuthStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Status(Guid id, CancellationToken cancellationToken)
    {
        var status = await _oauthService.GetStatusAsync(id, cancellationToken);
        return status == null ? NotFound() : Ok(status);
    }

    /// <summary>List files/folders for a connected storage connector.</summary>
    [HttpGet("{id:guid}/files")]
    [ProducesResponseType(typeof(ConnectorFileListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListFiles(Guid id, [FromQuery] string? path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _oauthService.ListFilesAsync(id, path, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Upload a file to the connected storage provider.</summary>
    [HttpPost("{id:guid}/files/upload")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 104_857_600)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UploadFile(
        Guid id,
        IFormFile? file,
        [FromForm] string? path,
        CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file received." });

        try
        {
            await using var stream = file.OpenReadStream();
            await _oauthService.UploadFileAsync(
                id,
                path ?? string.Empty,
                file.FileName,
                stream,
                file.ContentType,
                cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Download a file from the connected storage provider.</summary>
    [HttpGet("{id:guid}/files/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadFile(Guid id, [FromQuery] string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "path is required." });

        try
        {
            var (content, contentType, fileName) = await _oauthService.DownloadFileAsync(id, path, cancellationToken);
            return File(content, contentType, fileName);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>INBOX total and unread counts (GMAIL or OUTLOOK).</summary>
    [HttpGet("{id:guid}/mail/summary")]
    [HttpGet("{id:guid}/gmail/summary")]
    [ProducesResponseType(typeof(ConnectorMailSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMailSummary(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _oauthService.GetMailSummaryAsync(id, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>List mail messages (GMAIL or OUTLOOK connectors).</summary>
    [HttpGet("{id:guid}/gmail/messages")]
    [HttpGet("{id:guid}/mail/messages")]
    [ProducesResponseType(typeof(ConnectorGmailMessageListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListGmailMessages(
        Guid id,
        [FromQuery] int maxResults = 20,
        [FromQuery] string? query = null,
        [FromQuery] bool unreadOnly = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _oauthService.ListGmailMessagesAsync(id, maxResults, query, unreadOnly, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Newest inbox message (optionally unread only).</summary>
    [HttpGet("{id:guid}/mail/messages/top")]
    [HttpGet("{id:guid}/gmail/messages/top")]
    [ProducesResponseType(typeof(ConnectorGmailMessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> GetTopMailMessage(
        Guid id,
        [FromQuery] bool unreadOnly = true,
        [FromQuery] string? query = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _oauthService.GetTopMailMessageAsync(id, unreadOnly, query, cancellationToken);
            return result == null ? NoContent() : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Get one mail message with body and attachments (GMAIL or OUTLOOK).</summary>
    [HttpGet("{id:guid}/mail/messages/{messageId}")]
    [HttpGet("{id:guid}/gmail/messages/{messageId}")]
    [ProducesResponseType(typeof(ConnectorGmailMessageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMailMessage(Guid id, string messageId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _oauthService.GetMailMessageAsync(id, messageId, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Mark a mail message as read. Requires gmail.modify / Mail.ReadWrite (re-authorize after scope bump).</summary>
    [HttpPost("{id:guid}/mail/messages/{messageId}/read")]
    [HttpPost("{id:guid}/gmail/messages/{messageId}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkMailMessageRead(Guid id, string messageId, CancellationToken cancellationToken)
    {
        try
        {
            await _oauthService.MarkMailMessageReadAsync(id, messageId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Download a mail attachment (GMAIL or OUTLOOK).</summary>
    [HttpGet("{id:guid}/gmail/messages/{messageId}/attachments/{attachmentId}")]
    [HttpGet("{id:guid}/mail/messages/{messageId}/attachments/{attachmentId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadGmailAttachment(
        Guid id,
        string messageId,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var (content, contentType, fileName) = await _oauthService.DownloadGmailAttachmentAsync(
                id, messageId, attachmentId, cancellationToken);
            return File(content, contentType, fileName);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// List QuickBooks master records. masterType: Customer | Vendor | Item.
    /// </summary>
    [HttpGet("{id:guid}/quickbooks/masters")]
    [ProducesResponseType(typeof(ConnectorQuickBooksMasterListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListQuickBooksMasters(
        Guid id,
        [FromQuery] string masterType = "Customer",
        [FromQuery] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _oauthService.ListQuickBooksMastersAsync(id, masterType, maxResults, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// List QuickBooks documents. documentType: Invoice | Bill | PurchaseOrder | Estimate | SalesReceipt.
    /// </summary>
    [HttpGet("{id:guid}/quickbooks/documents")]
    [ProducesResponseType(typeof(ConnectorQuickBooksDocumentListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListQuickBooksDocuments(
        Guid id,
        [FromQuery] string documentType = "Invoice",
        [FromQuery] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _oauthService.ListQuickBooksDocumentsAsync(id, documentType, maxResults, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Look up an SAP Purchase Order by PO number for AP Agent invoice matching.
    /// Sample mode reads ConfigJson.samplePurchaseOrders (source=sap_sample).
    /// </summary>
    [HttpPost("{id:guid}/sap/purchase-orders/lookup")]
    [ProducesResponseType(typeof(ConnectorSapPoLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> LookupSapPurchaseOrder(
        Guid id,
        [FromBody] ConnectorSapPoLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.PoNumber))
                return BadRequest(new { error = "poNumber is required." });

            var result = await _sapPurchaseOrderLookup.LookupAsync(id, request.PoNumber, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Missing connector vs wrong provider — 404 for missing, 400 for non-SAP.
            if (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return NotFound(new { error = ex.Message });
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Look up a QuickBooks Purchase Order by PO Number (DocNumber) for AP Agent invoice matching.
    /// Returns header + line items + full QBO raw object.
    /// </summary>
    [HttpPost("{id:guid}/quickbooks/purchase-orders/lookup")]
    [ProducesResponseType(typeof(ConnectorQuickBooksPoLookupResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> LookupQuickBooksPurchaseOrder(
        Guid id,
        [FromBody] ConnectorQuickBooksPoLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.PoNumber))
                return BadRequest(new { error = "poNumber is required." });

            var result = await _oauthService.LookupQuickBooksPurchaseOrderAsync(id, request.PoNumber, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Download a QuickBooks document PDF (Invoice, Bill, PurchaseOrder, Estimate, SalesReceipt).</summary>
    [HttpGet("{id:guid}/quickbooks/documents/{documentId}/pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadQuickBooksDocumentPdf(
        Guid id,
        string documentId,
        [FromQuery] string documentType = "Invoice",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (content, contentType, fileName) = await _oauthService.DownloadQuickBooksDocumentPdfAsync(
                id, documentType, documentId, cancellationToken);
            return File(content, contentType, fileName);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Get connector by id (v5 GET /api/connector/{id}).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ConnectorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
            return NotFound();

        try
        {
            var item = await _connectorService.GetByIdAsync(id, cancellationToken);
            if (item == null)
                return NotFound();
            return Ok(item);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
