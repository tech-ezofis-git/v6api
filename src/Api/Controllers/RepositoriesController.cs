using System.Security.Claims;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Api.Middleware;
using SaaSApp.Billing.Application.Contracts;
using SaaSApp.Billing.Application.Credits.Commands.UpdateCredit;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Options;
using SaaSApp.Repository.Infrastructure.Services;
using SaaSApp.Security;

namespace SaaSApp.Api.Controllers;

/// <summary>STATIC document repositories (GUID, repository schema, paged items).</summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class RepositoriesController : ControllerBase
{
    private readonly ITenantProvider _tenantProvider;
    private readonly IStaticRepositoryProvisioner _provisioner;
    private readonly IRepositoryBrowseService _browse;
    private readonly IRepositoryItemQueryService _items;
    private readonly IRepositoryRelatedDocumentsService _relatedDocuments;
    private readonly IRepositoryFileUploadService _fileUpload;
    private readonly IRepositoryArchiveFileUploadService _archiveUpload;
    private readonly IRepositoryStorageSeedService _storageSeed;
    private readonly IRepositoryItemActivityService _itemActivity;
    private readonly IRepositoryItemShareService _itemShares;
    private readonly IRepositorySecurityService _security;
    private readonly ITenantConnectionStringResolver _connectionResolver;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IRepositoryAiSummaryService _aiSummary;
    private readonly IMediator _mediator;
    private readonly ILogger<RepositoriesController> _logger;

    public RepositoriesController(
        ITenantProvider tenantProvider,
        IStaticRepositoryProvisioner provisioner,
        IRepositoryBrowseService browse,
        IRepositoryItemQueryService items,
        IRepositoryRelatedDocumentsService relatedDocuments,
        IRepositoryFileUploadService fileUpload,
        IRepositoryArchiveFileUploadService archiveUpload,
        IRepositoryStorageSeedService storageSeed,
        IRepositoryItemActivityService itemActivity,
        IRepositoryItemShareService itemShares,
        IRepositorySecurityService security,
        ITenantConnectionStringResolver connectionResolver,
        ITenantConnectionProvider connectionProvider,
        IRepositoryAiSummaryService aiSummary,
        IMediator mediator,
        ILogger<RepositoriesController> logger)
    {
        _tenantProvider = tenantProvider;
        _provisioner = provisioner;
        _browse = browse;
        _items = items;
        _relatedDocuments = relatedDocuments;
        _fileUpload = fileUpload;
        _archiveUpload = archiveUpload;
        _storageSeed = storageSeed;
        _itemActivity = itemActivity;
        _itemShares = itemShares;
        _security = security;
        _connectionResolver = connectionResolver;
        _connectionProvider = connectionProvider;
        _aiSummary = aiSummary;
        _mediator = mediator;
        _logger = logger;
    }

    /// <summary>Seed default storage providers (EZOFIS, GCP, ONEDRIVE) for current tenant.</summary>
    [HttpPost("/api/repositories/storage-providers/seed")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<IActionResult> SeedStorageProviders(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        await _storageSeed.EnsureDefaultProvidersAsync(tenantId, GetUserId(), cancellationToken);
        var providers = await _storageSeed.ListProvidersAsync(tenantId, cancellationToken);
        return Ok(new { message = "Storage providers seeded.", providers });
    }

    /// <summary>List storage providers and their GUIDs for create-repository body.</summary>
    [HttpGet("/api/repositories/storage-providers")]
    public async Task<IActionResult> ListStorageProviders(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var providers = await _storageSeed.ListProvidersAsync(tenantId, cancellationToken);
        return Ok(providers);
    }

    [HttpPost("/api/repositories")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<IActionResult> Create([FromBody] CreateRepositoryRequest body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { error = "name is required." });

        var tenantId = RequireTenantId();
        try
        {
            var result = await _provisioner.CreateRepositoryAsync(body, tenantId, GetUserId(), cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = result.RepositoryId }, result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("/api/repositories")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var list = await _provisioner.ListRepositoriesAsync(tenantId, cancellationToken);
        if (IsCurrentUserAdmin())
            return Ok(list);

        var userId = GetUserId();
        if (userId is null)
            return Ok(Array.Empty<RepositorySummaryDto>());

        var allowedIds = await _security.FilterAccessibleRepositoryIdsAsync(
            list.Select(r => r.Id).ToList(),
            tenantId,
            userId.Value,
            isAdmin: false,
            cancellationToken);
        var allowed = allowedIds.ToHashSet();
        return Ok(list.Where(r => allowed.Contains(r.Id)).ToList());
    }

    [HttpGet("/api/repositories/{id:guid}")]
    public async Task<IActionResult> GetById(
        Guid id,
        [FromQuery] string? shareToken,
        [FromQuery] string? sharedtoken,
        CancellationToken cancellationToken)
    {
        if (await EnsureShareContextAsync(cancellationToken) is { } shareError)
            return shareError;

        var (repoId, _, tenantId) = ResolveItemAccess(id, Guid.Empty);
        if (await EnsureRepositoryAccessAsync(repoId, tenantId, RepositorySecurityPermissions.View, cancellationToken) is { } denied)
            return denied;

        var repo = await _provisioner.GetRepositoryAsync(repoId, tenantId, cancellationToken);
        return repo == null ? NotFound() : Ok(repo);
    }

    /// <summary>Get folder security policies for a repository (Admin configures; TenantUser may read own effective policies).</summary>
    [HttpGet("/api/repositories/{id:guid}/security/folder")]
    public async Task<IActionResult> GetFolderSecurity(
        Guid id,
        [FromQuery] Guid? folderId,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        try
        {
            var result = await _security.GetFolderSecurityAsync(id, tenantId, folderId, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Replace folder security policies (Users + Permissions wizard). Admin only.</summary>
    [HttpPut("/api/repositories/{id:guid}/security/folder")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<IActionResult> SaveFolderSecurity(
        Guid id,
        [FromBody] RepositoryFolderSecurityUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        try
        {
            var result = await _security.SaveFolderSecurityAsync(id, tenantId, request, GetUserId(), cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Get document security rules (metadata grant/hide).</summary>
    [HttpGet("/api/repositories/{id:guid}/security/documents")]
    public async Task<IActionResult> GetDocumentSecurity(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        try
        {
            var result = await _security.GetDocumentSecurityAsync(id, tenantId, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Replace document security rules. Admin only.</summary>
    [HttpPut("/api/repositories/{id:guid}/security/documents")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<IActionResult> SaveDocumentSecurity(
        Guid id,
        [FromBody] RepositoryDocumentSecurityUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        try
        {
            var result = await _security.SaveDocumentSecurityAsync(id, tenantId, request, GetUserId(), cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Creates missing per-repo tables (e.g. stage table) for an existing repository.</summary>
    [HttpPost("/api/repositories/{id:guid}/provision-tables")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<IActionResult> ProvisionTables(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        try
        {
            await _provisioner.EnsureRepositoryTablesAsync(id, tenantId, cancellationToken);
            var repo = await _provisioner.GetRepositoryAsync(id, tenantId, cancellationToken);
            return Ok(new
            {
                message = "Repository tables verified.",
                itemsTableName = repo?.ItemsTableName,
                stageTableName = repo?.StageTableName
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Update repository name, storage, and/or field definitions (same field shape as create).</summary>
    [HttpPut("/api/repositories/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRepositoryRequest request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        try
        {
            var repo = await _provisioner.UpdateRepositoryAsync(id, tenantId, request, GetUserId(), cancellationToken);
            return repo == null ? NotFound() : Ok(repo);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Folder fields and browse paths for this repository (driven by RepositoryFields, not hardcoded).</summary>
    [HttpGet("/api/repositories/{id:guid}/browse/structure")]
    public async Task<IActionResult> BrowseStructure(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        if (await EnsureRepositoryAccessAsync(id, tenantId, RepositorySecurityPermissions.View, cancellationToken) is { } denied)
            return denied;
        return await Browse(async () => await _browse.GetBrowseStructureAsync(id, tenantId, cancellationToken));
    }

    /// <summary>Next tree level. parentFilters JSON keys from GET .../browse/structure (not fixed field names).</summary>
    [HttpGet("/api/repositories/{id:guid}/browse/children")]
    public async Task<IActionResult> BrowseChildren(
        Guid id,
        [FromQuery] BrowseFolderQuery query,
        CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenantId();
        if (await EnsureRepositoryAccessAsync(id, tenantId, RepositorySecurityPermissions.View, cancellationToken) is { } denied)
            return denied;
        var filters = ParseParentFilters(query.ParentFilters);
        return await Browse(async () =>
            await _browse.GetBrowseChildrenAsync(
                id, tenantId, query.PathId, filters, query.Page, query.PageSize, query.Search, cancellationToken));
    }

    /// <summary>Group items by any folder field name; parentFilters JSON for drill-down context.</summary>
    [HttpGet("/api/repositories/{id:guid}/browse/groups/{fieldName}")]
    public async Task<IActionResult> BrowseGroups(
        Guid id,
        string fieldName,
        [FromQuery] BrowseFolderQuery query,
        CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenantId();
        if (await EnsureRepositoryAccessAsync(id, tenantId, RepositorySecurityPermissions.View, cancellationToken) is { } denied)
            return denied;
        var filters = ParseParentFilters(query.ParentFilters);
        return await Browse(async () =>
            await _browse.GetBrowseGroupsAsync(
                id, tenantId, fieldName, filters, query.Page, query.PageSize, query.Search, cancellationToken));
    }

    /// <summary>Allowed filter keys for GET .../items (per repository fields + standard columns).</summary>
    [HttpGet("/api/repositories/{id:guid}/items/filter-fields")]
    public async Task<IActionResult> GetItemFilterFields(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        if (await EnsureRepositoryAccessAsync(id, tenantId, RepositorySecurityPermissions.View, cancellationToken) is { } denied)
            return denied;
        try
        {
            var schema = await _items.GetItemListFilterSchemaAsync(id, tenantId, cancellationToken);
            return Ok(schema);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Bottom file list (paged). Prefer <c>POST .../items/query</c> for multi-value filters
    /// (JSON arrays in GET query strings are often corrupted by spaces/encoding).
    /// </summary>
    [HttpGet("/api/repositories/{id:guid}/items")]
    public async Task<IActionResult> ListItems(Guid id, [FromQuery] RepositoryItemListQuery query, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        if (await EnsureRepositoryAccessAsync(id, tenantId, RepositorySecurityPermissions.View, cancellationToken) is { } denied)
            return denied;
        try
        {
            var normalized = NormalizeItemListQuery(query);
            var result = await _items.ListItemsAsync(id, tenantId, normalized, cancellationToken);
            result = await ApplyItemListSecurityAsync(id, tenantId, result, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Same as GET items, but filters are in the JSON body (recommended for multi-select).
    /// Sample body:
    /// <code>
    /// {
    ///   "filters": { "Supplier": ["CHAMPION INDUSTRIAL SUPPLY", "Gerrie Logistics Services Ltd"] },
    ///   "page": 1,
    ///   "pageSize": 50
    /// }
    /// </code>
    /// </summary>
    [HttpPost("/api/repositories/{id:guid}/items/query")]
    public async Task<IActionResult> QueryItems(
        Guid id,
        [FromBody] RepositoryItemQueryRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        if (await EnsureRepositoryAccessAsync(id, tenantId, RepositorySecurityPermissions.View, cancellationToken) is { } denied)
            return denied;
        try
        {
            var query = ToItemListQuery(request);
            var result = await _items.ListItemsAsync(id, tenantId, query, cancellationToken);
            result = await ApplyItemListSecurityAsync(id, tenantId, result, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("/api/repositories/{id:guid}/items/facets/{fieldName}")]
    public async Task<IActionResult> Facets(
        Guid id,
        string fieldName,
        [FromQuery] string? scopeFilters = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenantId();
        if (await EnsureRepositoryAccessAsync(id, tenantId, RepositorySecurityPermissions.View, cancellationToken) is { } denied)
            return denied;
        try
        {
            var facets = await _items.GetFacetsAsync(id, tenantId, fieldName, scopeFilters, limit, cancellationToken);
            return Ok(facets);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("/api/repositories/{id:guid}/items/{itemId:guid}")]
    public async Task<IActionResult> GetItem(
        Guid id,
        Guid itemId,
        [FromQuery] string? shareToken,
        [FromQuery] string? sharedtoken,
        CancellationToken cancellationToken)
    {
        if (await EnsureShareContextAsync(cancellationToken) is { } shareError)
            return shareError;

        var (repoId, resolvedItemId, tenantId) = ResolveItemAccess(id, itemId);
        try
        {
            var item = await _items.GetItemAsync(repoId, tenantId, resolvedItemId, cancellationToken);
            if (item == null)
                return NotFound();
            if (await EnsureItemAccessAsync(repoId, tenantId, RepositorySecurityFieldMap.FromDetail(item), RepositorySecurityPermissions.View, cancellationToken) is { } denied)
                return denied;
            return Ok(item);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Related documents across all tenant repositories for the open file.
    /// FE sends only repositoryId + itemId. Backend matches folder-structure fields
    /// (<c>IncludeInFolderStructure</c>): with 3 levels, any 2 matching fields is enough.
    /// </summary>
    [HttpGet("/api/repositories/{id:guid}/items/{itemId:guid}/related")]
    [ProducesResponseType(typeof(RepositoryRelatedDocumentsResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRelatedDocuments(
        Guid id,
        Guid itemId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenantId();
        if (await EnsureRepositoryAccessAsync(id, tenantId, RepositorySecurityPermissions.View, cancellationToken) is { } deniedRepo)
            return deniedRepo;

        try
        {
            var source = await _items.GetItemAsync(id, tenantId, itemId, cancellationToken);
            if (source == null)
                return NotFound();
            if (await EnsureItemAccessAsync(id, tenantId, RepositorySecurityFieldMap.FromDetail(source), RepositorySecurityPermissions.View, cancellationToken) is { } deniedItem)
                return deniedItem;

            var result = await _relatedDocuments.GetRelatedAsync(id, tenantId, itemId, page, pageSize, cancellationToken);
            if (result == null)
                return NotFound();

            result = await ApplyRelatedDocumentsSecurityAsync(tenantId, result, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Exact related documents: scores every file against all repository fields.
    /// Score = matchedFields / totalFields × 100 (e.g. 10/14 → 71, 14/14 → 100).
    /// </summary>
    [HttpGet("/api/repositories/{id:guid}/items/{itemId:guid}/related-exact")]
    [ProducesResponseType(typeof(RepositoryRelatedDocumentsResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRelatedDocumentsExact(
        Guid id,
        Guid itemId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenantId();
        if (await EnsureRepositoryAccessAsync(id, tenantId, RepositorySecurityPermissions.View, cancellationToken) is { } deniedRepo)
            return deniedRepo;

        try
        {
            var source = await _items.GetItemAsync(id, tenantId, itemId, cancellationToken);
            if (source == null)
                return NotFound();
            if (await EnsureItemAccessAsync(id, tenantId, RepositorySecurityFieldMap.FromDetail(source), RepositorySecurityPermissions.View, cancellationToken) is { } deniedItem)
                return deniedItem;

            var result = await _relatedDocuments.GetRelatedExactAsync(id, tenantId, itemId, page, pageSize, cancellationToken);
            if (result == null)
                return NotFound();

            result = await ApplyRelatedDocumentsSecurityAsync(tenantId, result, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Return the cached AI summary, or generate and cache it via agents <c>/chat</c>
    /// (<c>intent=summary</c>). Uses item <c>ocr_text</c> when present; otherwise sends blob <c>filepath</c>.
    /// </summary>
    [HttpPost("/api/repositories/{repositoryId:guid}/items/{itemId:guid}/ai-summary")]
    public async Task<IActionResult> GetAiSummary(
        Guid repositoryId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        try
        {
            var summary = await _aiSummary.GetOrGenerateAsync(
                repositoryId,
                tenantId,
                itemId,
                cancellationToken);

            var creditConsumed = false;
            if (summary.WasGenerated)
            {
                try
                {
                    var creditResult = await _mediator.Send(
                        new UpdateCreditCommand(
                            tenantId,
                            GetUserId(),
                            new CreditUpdateRequest(
                                "Document Summary",
                                "DocumentSummary",
                                "repository.items",
                                0,
                                $"AI summary for item {itemId}",
                                RepositoryAiSummaryDefaults.Credit)),
                        cancellationToken);
                    creditConsumed = creditResult.Status == CreditUpdateStatus.Success;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "AI summary was saved for item {ItemId}, but Document Summary credit could not be consumed",
                        itemId);
                }
            }

            return Ok(new { output = summary.Output, creditConsumed });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = "AI summary service timed out." });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = ex.Message });
        }
    }

    /// <summary>Structured document workspace (panels + line items) for filename click detail view.</summary>
    [HttpGet("/api/repositories/{id:guid}/items/{itemId:guid}/workspace")]
    public async Task<IActionResult> GetItemWorkspace(
        Guid id,
        Guid itemId,
        [FromQuery] string? shareToken,
        [FromQuery] string? sharedtoken,
        CancellationToken cancellationToken)
    {
        if (await EnsureShareContextAsync(cancellationToken) is { } shareError)
            return shareError;

        var (repoId, resolvedItemId, tenantId) = ResolveItemAccess(id, itemId);
        try
        {
            var workspace = await _items.GetItemWorkspaceAsync(repoId, tenantId, resolvedItemId, cancellationToken);
            if (workspace == null)
                return NotFound();
            if (await EnsureItemAccessAsync(repoId, tenantId, RepositorySecurityFieldMap.FromWorkspace(workspace), RepositorySecurityPermissions.View, cancellationToken) is { } denied)
                return denied;
            return Ok(workspace);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Invite a person to a repository file — same shareToken flow as workflow share.
    /// Returns <c>shareUrl</c> (emailed) with <c>shareToken</c>, <c>email</c>, <c>isnew</c>.
    /// Guest: preview → set-password / social-login / login, then open file with shareToken.
    /// <c>action</c>: 0 = Can View, 1 = Can Edit (upload).
    /// </summary>
    [HttpPost("/api/repositories/{id:guid}/items/{itemId:guid}/share")]
    [ProducesResponseType(typeof(CreateRepositoryItemShareResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ShareItem(
        Guid id,
        Guid itemId,
        [FromBody] CreateRepositoryItemShareRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var userId = GetUserId();
        if (userId == null || userId == Guid.Empty)
            return Unauthorized(new { error = "User id is required." });

        try
        {
            var result = await _itemShares.CreateShareAsync(
                tenantId, id, itemId, userId.Value, request, cancellationToken);
            return CreatedAtAction(nameof(GetItem), new { id, itemId }, result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Files shared with the logged-in user (their email). Returns share tokens to reopen without the email link.</summary>
    [HttpGet("/api/repositories/shared-with-me")]
    [ProducesResponseType(typeof(IReadOnlyList<SharedWithMeItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSharedWithMe(CancellationToken cancellationToken)
    {
        var email = GetUserEmail();
        if (string.IsNullOrWhiteSpace(email))
            return Unauthorized(new { error = "Logged-in user email is required to list shared files." });

        var shares = await _itemShares.ListSharesForRecipientAsync(email, cancellationToken);
        return Ok(shares);
    }

    /// <summary>Anonymous share link preview (before login).</summary>
    [HttpGet("/api/repositories/share/{shareToken}/preview")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(RepositoryItemSharePreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSharePreview(string shareToken, CancellationToken cancellationToken)
    {
        var preview = await _itemShares.GetPreviewAsync(shareToken, cancellationToken);
        return preview == null ? NotFound(new { error = "Share link not found or expired." }) : Ok(preview);
    }

    /// <summary>Revoke an active share (sharer only).</summary>
    [HttpDelete("/api/repositories/share/{shareId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.TenantUser)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeShare(Guid shareId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var userId = GetUserId();
        if (userId == null || userId == Guid.Empty)
            return Unauthorized();

        var revoked = await _itemShares.RevokeShareAsync(shareId, tenantId, userId.Value, cancellationToken);
        return revoked ? NoContent() : NotFound(new { error = "Share not found or already revoked." });
    }

    [HttpGet("/api/repositories/{id:guid}/items/{itemId:guid}/timeline")]
    public async Task<IActionResult> GetItemTimeline(
        Guid id,
        Guid itemId,
        [FromQuery] string? shareToken,
        [FromQuery] string? sharedtoken,
        CancellationToken cancellationToken)
    {
        if (await EnsureShareContextAsync(cancellationToken) is { } shareError)
            return shareError;

        var (repoId, resolvedItemId, tenantId) = ResolveItemAccess(id, itemId);
        var timeline = await _itemActivity.GetTimelineAsync(repoId, tenantId, resolvedItemId, cancellationToken);
        return timeline == null ? NotFound() : Ok(timeline);
    }

    [HttpPost("/api/repositories/{id:guid}/items/{itemId:guid}/timeline")]
    public async Task<IActionResult> AddItemTimelineEvent(
        Guid id,
        Guid itemId,
        [FromBody] AddRepositoryItemTimelineEventRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        try
        {
            var evt = await _itemActivity.AddTimelineEventAsync(id, tenantId, itemId, request, GetUserId(), cancellationToken);
            return evt == null ? NotFound() : Ok(evt);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("/api/repositories/{id:guid}/items/{itemId:guid}/comments")]
    public async Task<IActionResult> GetItemComments(
        Guid id,
        Guid itemId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? shareToken = null,
        [FromQuery] string? sharedtoken = null,
        CancellationToken cancellationToken = default)
    {
        if (await EnsureShareContextAsync(cancellationToken) is { } shareError)
            return shareError;

        var (repoId, resolvedItemId, tenantId) = ResolveItemAccess(id, itemId);
        var comments = await _itemActivity.GetCommentsAsync(repoId, tenantId, resolvedItemId, page, pageSize, cancellationToken);
        return comments == null ? NotFound() : Ok(comments);
    }

    [HttpPost("/api/repositories/{id:guid}/items/{itemId:guid}/comments")]
    public async Task<IActionResult> AddItemComment(
        Guid id,
        Guid itemId,
        [FromBody] AddRepositoryItemCommentRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User id is required to post a comment." });

        var tenantId = RequireTenantId();
        try
        {
            var result = await _itemActivity.AddCommentAsync(id, tenantId, itemId, request, userId.Value, cancellationToken);
            if (result == null)
                return NotFound();

            return Created($"/api/repositories/{id:D}/items/{itemId:D}/comments/{result.CommentId:D}", result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("/api/repositories/{id:guid}/items")]
    public async Task<IActionResult> CreateItem(Guid id, [FromBody] CreateRepositoryItemRequest request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        if (await EnsureRepositoryAccessAsync(id, tenantId, RepositorySecurityPermissions.Upload, cancellationToken) is { } denied)
            return denied;
        var itemId = await _items.CreateItemAsync(id, tenantId, request, GetUserId(), cancellationToken);
        return CreatedAtAction(nameof(GetItem), new { id, itemId }, new { itemId });
    }

    /// <summary>Update metadata on an existing item (after upload). Body = JSON object, same keys as upload metadata.</summary>
    [HttpPatch("/api/repositories/{id:guid}/items/{itemId:guid}/metadata")]
    public async Task<IActionResult> UpdateItemMetadata(
        Guid id,
        Guid itemId,
        [FromBody] JsonElement body,
        CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenantId();
        if (await EnsureRepositoryAccessAsync(id, tenantId, RepositorySecurityPermissions.EditMetadata, cancellationToken) is { } denied)
            return denied;
        try
        {
            var metadata = ParseMetadataBody(body);
            if (metadata.Count == 0)
                return BadRequest(new { error = "metadata JSON object with at least one field is required." });

            var result = await _items.UpdateItemMetadataAsync(id, tenantId, itemId, metadata, GetUserId(), cancellationToken);
            if (result == null)
                return NotFound();

            var item = await _items.GetItemAsync(id, tenantId, itemId, cancellationToken);
            return Ok(new
            {
                result.ItemId,
                result.UpdatedFieldCount,
                item
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Upload with archive folder layout (multipart): ezts{tenantId}/archive/{repositoryName}/{folder fields}/{uploadedFileName}.ext
    /// Folder levels from fields with IncludeInFolderStructure; file name from highest-level metadata above folders (e.g. PoNumber).
    /// Requires repository fields with IncludeInFolderStructure; metadata JSON required for mandatory levels.
    /// </summary>
    [HttpPost("/api/repositories/{id:guid}/items/upload-archive")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 104_857_600)]
    public async Task<IActionResult> UploadItemArchive(
        Guid id,
        IFormFile? file,
        [FromForm] Guid? workflowId,
        [FromForm] int? processId,
        [FromForm] Guid? instanceId,
        [FromForm] int? transactionId,
        [FromForm] string? storageProviderCode,
        [FromForm] string? metadata,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "file is required." });

        var tenantId = RequireTenantId();
        if (await EnsureRepositoryAccessAsync(id, tenantId, RepositorySecurityPermissions.Upload, cancellationToken) is { } denied)
            return denied;
        try
        {
            var mergedMetadata = RepositoryFormMetadataCollector.ToMetadataJson(
                RepositoryFormMetadataCollector.Collect(metadata, EnumerateExtraFormFields()));

            await using var stream = file.OpenReadStream();
            var request = new RepositoryUploadItemRequest(
                stream,
                file.FileName,
                file.ContentType,
                workflowId,
                processId,
                instanceId,
                transactionId,
                storageProviderCode,
                file.Length,
                mergedMetadata);

            var result = await _archiveUpload.UploadItemAsync(id, tenantId, request, GetUserId(), cancellationToken);
            return CreatedAtAction(nameof(GetItem), new { id, itemId = result.ItemId }, result);
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

    /// <summary>Upload a file into the repository (multipart, flat path). Optionally link to workflow via workflowId + processId and/or instanceId.</summary>
    [HttpPost("/api/repositories/{id:guid}/items/upload")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 104_857_600)]
    public async Task<IActionResult> UploadItem(
        Guid id,
        IFormFile? file,
        [FromForm] Guid? workflowId,
        [FromForm] int? processId,
        [FromForm] Guid? instanceId,
        [FromForm] int? transactionId,
        [FromForm] string? storageProviderCode,
        [FromForm] string? metadata,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "file is required." });

        var tenantId = RequireTenantId();
        if (await EnsureRepositoryAccessAsync(id, tenantId, RepositorySecurityPermissions.Upload, cancellationToken) is { } denied)
            return denied;
        try
        {
            var mergedMetadata = RepositoryFormMetadataCollector.ToMetadataJson(
                RepositoryFormMetadataCollector.Collect(metadata, EnumerateExtraFormFields()));

            await using var stream = file.OpenReadStream();
            var request = new RepositoryUploadItemRequest(
                stream,
                file.FileName,
                file.ContentType,
                workflowId,
                processId,
                instanceId,
                transactionId,
                storageProviderCode,
                file.Length,
                mergedMetadata);

            var result = await _fileUpload.UploadItemAsync(id, tenantId, request, GetUserId(), cancellationToken);
            return CreatedAtAction(nameof(GetItem), new { id, itemId = result.ItemId }, result);
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

    /// <summary>Download or inline-view the item file from storage (EZOFIS blob or local fallback).</summary>
    [HttpGet("/api/repositories/{id:guid}/items/{itemId:guid}/file")]
    public async Task<IActionResult> GetItemFile(
        Guid id,
        Guid itemId,
        [FromQuery] string disposition = "inline",
        [FromQuery] string? shareToken = null,
        [FromQuery] string? sharedtoken = null,
        CancellationToken cancellationToken = default)
    {
        if (await EnsureShareContextAsync(cancellationToken) is { } shareError)
            return shareError;

        var (repoId, resolvedItemId, tenantId) = ResolveItemAccess(id, itemId);
        try
        {
            var item = await _items.GetItemAsync(repoId, tenantId, resolvedItemId, cancellationToken);
            if (item == null)
                return NotFound();
            if (await EnsureItemAccessAsync(
                    repoId,
                    tenantId,
                    RepositorySecurityFieldMap.FromDetail(item),
                    RepositorySecurityPermissions.Download,
                    cancellationToken) is { } denied)
                return denied;

            var content = await _items.OpenItemFileAsync(repoId, tenantId, resolvedItemId, cancellationToken);
            if (content == null)
                return NotFound();

            var inline = string.Equals(disposition, "inline", StringComparison.OrdinalIgnoreCase);
            return new FileStreamResult(content.Stream, content.ContentType)
            {
                FileDownloadName = inline ? null : content.FileName,
                EnableRangeProcessing = true
            };
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private static IReadOnlyDictionary<string, string> ParseMetadataBody(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("metadata", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            return RepositoryMetadataParser.Parse(nested.GetRawText());
        }

        if (body.ValueKind == JsonValueKind.Object)
            return RepositoryMetadataParser.Parse(body.GetRawText());

        throw new ArgumentException("Request body must be a JSON object, e.g. {\"Supplier\":\"Acme\",\"InvoiceNumber\":\"INV-1\"}.");
    }

    private static RepositoryItemListQuery NormalizeItemListQuery(RepositoryItemListQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Filters))
            return query;

        var filters = query.Filters.Trim();
        // Query-string clients sometimes send still-encoded JSON.
        if (filters.Contains('%', StringComparison.Ordinal))
        {
            try
            {
                filters = Uri.UnescapeDataString(filters);
            }
            catch (UriFormatException)
            {
                // keep original
            }
        }

        return new RepositoryItemListQuery
        {
            Filters = filters,
            Search = query.Search,
            DateFrom = query.DateFrom,
            DateTo = query.DateTo,
            SortBy = query.SortBy,
            SortOrder = query.SortOrder,
            Page = query.Page,
            PageSize = query.PageSize,
            SkipTotal = query.SkipTotal,
            Cursor = query.Cursor
        };
    }

    private static RepositoryItemListQuery ToItemListQuery(RepositoryItemQueryRequest request)
    {
        string? filtersJson = null;
        if (request.Filters is { } filters && filters.ValueKind == JsonValueKind.Object)
            filtersJson = filters.GetRawText();
        else if (!string.IsNullOrWhiteSpace(request.FiltersJson))
            filtersJson = request.FiltersJson;

        return new RepositoryItemListQuery
        {
            Filters = filtersJson,
            Search = request.Search,
            DateFrom = request.DateFrom,
            DateTo = request.DateTo,
            SortBy = request.SortBy ?? "documentDate",
            SortOrder = request.SortOrder ?? "desc",
            Page = request.Page <= 0 ? 1 : request.Page,
            PageSize = request.PageSize <= 0 ? 50 : request.PageSize,
            SkipTotal = request.SkipTotal,
            Cursor = request.Cursor
        };
    }

    private static IReadOnlyDictionary<string, string> ParseParentFilters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict == null)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            return dict
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"parentFilters must be a JSON object, e.g. {{\"Supplier\":\"Acme Supplies\"}}. {ex.Message}");
        }
    }

    private IEnumerable<KeyValuePair<string, string?>> EnumerateExtraFormFields()
    {
        if (!Request.HasFormContentType)
            yield break;

        foreach (var key in Request.Form.Keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            yield return new KeyValuePair<string, string?>(key, Request.Form[key].ToString());
        }
    }

    private async Task<IActionResult> Browse<T>(Func<Task<T>> task)
    {
        try
        {
            return Ok(await task());
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }

    private Guid RequireTenantId() =>
        _tenantProvider.GetTenantId() ?? throw new InvalidOperationException("Tenant context is required (X-Tenant-Id).");

    /// <summary>Applies share context from optional shareToken / sharedtoken query or X-Share-Token header.</summary>
    private async Task<IActionResult?> EnsureShareContextAsync(CancellationToken cancellationToken) =>
        await RepositoryShareContextApplicator.TryApplyAsync(
            HttpContext,
            _itemShares,
            _connectionResolver,
            _connectionProvider,
            cancellationToken);

    private (Guid RepositoryId, Guid ItemId, Guid TenantId) ResolveItemAccess(Guid repositoryId, Guid itemId)
    {
        if (RepositoryShareContext.TryGet(HttpContext, out var share) && share != null)
            return (share.SourceRepositoryId, share.SourceItemId, share.SourceTenantId);

        return (repositoryId, itemId, RequireTenantId());
    }

    private bool IsCurrentUserAdmin() =>
        User.Claims.Any(c =>
            (c.Type == ClaimTypes.Role || c.Type == "role") &&
            (string.Equals(c.Value, "Admin", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(c.Value, "Administrator", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Share-token viewers skip ACL. Admin skips. TenantUser must pass permission.</summary>
    private async Task<IActionResult?> EnsureRepositoryAccessAsync(
        Guid repositoryId,
        Guid tenantId,
        string permission,
        CancellationToken cancellationToken)
    {
        if (RepositoryShareContext.TryGet(HttpContext, out var share) && share != null)
            return null;
        if (IsCurrentUserAdmin())
            return null;

        var userId = GetUserId();
        if (userId is null)
            return Unauthorized(new { error = "User id is required." });

        var allowed = await _security.CanAccessRepositoryAsync(
            repositoryId, tenantId, userId.Value, isAdmin: false, permission, cancellationToken);
        return allowed
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, new { error = "You do not have access to this repository." });
    }

    private async Task<IActionResult?> EnsureItemAccessAsync(
        Guid repositoryId,
        Guid tenantId,
        IReadOnlyDictionary<string, string?> fields,
        string permission,
        CancellationToken cancellationToken)
    {
        if (RepositoryShareContext.TryGet(HttpContext, out var share) && share != null)
            return null;
        if (IsCurrentUserAdmin())
            return null;

        var userId = GetUserId();
        if (userId is null)
            return Unauthorized(new { error = "User id is required." });

        var allowed = await _security.CanAccessItemAsync(
            repositoryId, tenantId, userId.Value, isAdmin: false, fields, permission, cancellationToken);
        return allowed
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, new { error = "You do not have access to this document." });
    }

    private async Task<RepositoryRelatedDocumentsResultDto> ApplyRelatedDocumentsSecurityAsync(
        Guid tenantId,
        RepositoryRelatedDocumentsResultDto result,
        CancellationToken cancellationToken)
    {
        if (IsCurrentUserAdmin() || result.Data.Count == 0)
            return result;

        var userId = GetUserId();
        if (userId is null)
            return result with { Data = Array.Empty<RepositoryRelatedDocumentDto>(), TotalCount = 0 };

        var allowed = new List<RepositoryRelatedDocumentDto>();
        foreach (var group in result.Data.GroupBy(x => x.RepositoryId))
        {
            var filtered = await _security.FilterAccessibleItemsAsync(
                group.Key,
                tenantId,
                userId.Value,
                isAdmin: false,
                group.ToList(),
                FromRelatedDocument,
                RepositorySecurityPermissions.View,
                cancellationToken);
            allowed.AddRange(filtered);
        }

        return result with
        {
            Data = allowed
                .OrderByDescending(x => x.MatchScore)
                .ThenByDescending(x => x.CreatedAtUtc)
                .ToList(),
            TotalCount = allowed.Count
        };
    }

    private static IReadOnlyDictionary<string, string?> FromRelatedDocument(RepositoryRelatedDocumentDto item) =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ItemId"] = item.Id.ToString("D"),
            ["Id"] = item.Id.ToString("D"),
            ["FileName"] = item.FileName,
            ["FileType"] = item.FileType,
            ["DocumentType"] = item.DocumentType,
            ["Supplier"] = item.Supplier,
            ["PoNumber"] = item.PoNumber,
            ["PONumber"] = item.PoNumber,
            ["InvoiceNumber"] = item.InvoiceNumber,
            ["InvoiceNo"] = item.InvoiceNumber
        };

    private async Task<PagedResult<RepositoryItemListDto>> ApplyItemListSecurityAsync(
        Guid repositoryId,
        Guid tenantId,
        PagedResult<RepositoryItemListDto> result,
        CancellationToken cancellationToken)
    {
        if (RepositoryShareContext.TryGet(HttpContext, out var share) && share != null)
        {
            var sharedOnly = result.Data.Where(i => i.Id == share.SourceItemId).ToList();
            return result with { Data = sharedOnly, TotalCount = sharedOnly.Count };
        }

        if (IsCurrentUserAdmin())
            return result;

        var userId = GetUserId();
        if (userId is null)
            return result with { Data = Array.Empty<RepositoryItemListDto>(), TotalCount = 0 };

        var filtered = await _security.FilterAccessibleItemsAsync(
            repositoryId,
            tenantId,
            userId.Value,
            isAdmin: false,
            result.Data,
            RepositorySecurityFieldMap.FromListItem,
            RepositorySecurityPermissions.View,
            cancellationToken);

        return result with
        {
            Data = filtered,
            TotalCount = result.TotalSkipped ? result.TotalCount : filtered.Count
        };
    }

    private Guid? GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? User.FindFirstValue("oid");
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private string? GetUserEmail() =>
        User.FindFirstValue("email")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress");
}

/// <summary>POST body for <c>/api/repositories/{id}/items/query</c> (multi-value filters safe).</summary>
public sealed class RepositoryItemQueryRequest
{
    /// <summary>JSON object, e.g. <c>{ "Supplier": ["A", "B"], "Status": "Verifier" }</c>.</summary>
    public JsonElement? Filters { get; init; }

    /// <summary>Optional alternate to <see cref="Filters"/> when sending filters as a JSON string.</summary>
    public string? FiltersJson { get; init; }

    public string? Search { get; init; }
    public DateTime? DateFrom { get; init; }
    public DateTime? DateTo { get; init; }
    public string? SortBy { get; init; }
    public string? SortOrder { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public bool SkipTotal { get; init; }
    public string? Cursor { get; init; }
}
