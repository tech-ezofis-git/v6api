using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Api.Helpers;
using SaaSApp.Security;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Forms;

namespace SaaSApp.Api.Controllers;

/// <summary>v5-compatible form designer API (create, list all, get by id with formJson).</summary>
[ApiController]
[Route("api/form")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class FormController : ControllerBase
{
    private readonly IFormService _formService;
    private readonly IFormEntryService _formEntryService;
    private readonly IFormMasterFileUploadService _formMasterFileUpload;

    public FormController(
        IFormService formService,
        IFormEntryService formEntryService,
        IFormMasterFileUploadService formMasterFileUpload)
    {
        _formService = formService;
        _formEntryService = formEntryService;
        _formMasterFileUpload = formMasterFileUpload;
    }

    /// <summary>
    /// Upload master CSV/XLSX for a form (v5 POST /api/form/uploadMasterFile).
    /// Stores file in tenant blob, creates dbo.masterFileprocess + dbo.notification rows, enqueues Hangfire Python import.
    /// Response includes <c>pythonInput</c> — the JSON body posted to <c>ezDataImport</c>.
    /// </summary>
    [HttpPost("uploadMasterFile")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 104_857_600)]
    [ProducesResponseType(typeof(FormMasterFileUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadMasterFile(
        [FromForm] string? formId,
        [FromForm] string? workflowId,
        [FromForm] string? instanceId,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file received.");

        if (string.IsNullOrWhiteSpace(formId))
            return BadRequest(new { error = "formId is required." });

        var userId = GetCurrentUserId()
            ?? throw new InvalidOperationException("Authenticated user is required.");

        try
        {
            await using var stream = file.OpenReadStream();
            var request = new FormMasterFileUploadRequest(
                formId.Trim(),
                string.IsNullOrWhiteSpace(workflowId) ? null : workflowId.Trim(),
                string.IsNullOrWhiteSpace(instanceId) ? null : instanceId.Trim(),
                stream,
                file.FileName,
                file.ContentType,
                file.Length);

            var result = await _formMasterFileUpload.UploadMasterFileAsync(request, userId, cancellationToken);
            return Ok(new FormMasterFileUploadResponse(
                result.MasterFileProcessId,
                result.FilePath,
                result.NotificationId,
                result.HangfireJobId,
                result.PythonInput));
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

    /// <summary>Add new form from designer JSON (v5 POST /api/form).</summary>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(string), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status406NotAcceptable)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PostForm([FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return BadRequest(new { error = "Body must be a JSON object." });

        var designerRaw = FormJsonBodyHelper.ExtractDesignerJsonRaw(body);
        if (string.IsNullOrWhiteSpace(designerRaw))
            return BadRequest(new { error = "Invalid form JSON. Send designer payload with settings/panels or wrap in formJson." });

        FormJsonDto formJson;
        try
        {
            using var document = JsonDocument.Parse(designerRaw);
            formJson = FormJsonBodyHelper.NormalizeForCreate(document.RootElement);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid form JSON." });
        }

        try
        {
            var result = await _formService.CreateFormAsync(formJson, designerRaw, cancellationToken);

            return result.Status switch
            {
                FormCreateStatus.Created when !string.IsNullOrWhiteSpace(result.FormId) =>
                    Created($"/api/form/{result.FormId}", result.FormId),
                FormCreateStatus.NameConflict =>
                    StatusCode(StatusCodes.Status406NotAcceptable, result.Message ?? "Not Acceptable"),
                _ => NotFound(result.Message ?? "Not Found")
            };
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>List forms for the current tenant (formId and formName only).</summary>
    [HttpGet("all")]
    [ProducesResponseType(typeof(FormListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListForms(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _formService.ListAsync(cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Forms with filter, sort, group, pagination (same shape as POST /api/workflow/all).</summary>
    [HttpPost("all")]
    [ProducesResponseType(typeof(FormAllResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> QueryForms([FromBody] FormAllRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _formService.QueryAllAsync(
                request,
                GetCurrentUserId(),
                IsCurrentUserAdmin(),
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Update an existing form from designer JSON (v5 PUT /api/form/{id}).</summary>
    [HttpPut("{id}")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status406NotAcceptable)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PutForm(string id, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return NotFound();

        if (body.ValueKind != JsonValueKind.Object)
            return BadRequest(new { error = "Body must be a JSON object." });

        var designerRaw = FormJsonBodyHelper.ExtractDesignerJsonRaw(body);
        if (string.IsNullOrWhiteSpace(designerRaw))
            return BadRequest(new { error = "Invalid form JSON. Send designer payload with settings/panels or wrap in formJson." });

        FormJsonDto formJson;
        try
        {
            using var document = JsonDocument.Parse(designerRaw);
            formJson = FormJsonBodyHelper.NormalizeForCreate(document.RootElement);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid form JSON." });
        }

        try
        {
            var result = await _formService.UpdateFormAsync(id, formJson, designerRaw, cancellationToken);

            return result.Status switch
            {
                FormUpdateStatus.Updated when !string.IsNullOrWhiteSpace(result.FormId) =>
                    Ok(result.FormId),
                FormUpdateStatus.NameConflict =>
                    StatusCode(StatusCodes.Status406NotAcceptable, result.Message ?? "Not Acceptable"),
                _ => NotFound(result.Message ?? "Not Found")
            };
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Soft-delete a form by id.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteForm(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return NotFound();

        try
        {
            var result = await _formService.DeleteFormAsync(id, cancellationToken);
            return result.Status switch
            {
                FormDeleteStatus.Deleted => NoContent(),
                _ => NotFound(result.Message ?? "Not Found")
            };
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Add or update a form entry (v5 POST /api/form/{id}/entry/{entryId}).
    /// Use <c>entryId=00000000-0000-0000-0000-000000000000</c> to create; existing item id (GUID) to update.
    /// </summary>
    [HttpPost("{id}/entry/{entryId:guid}")]
    [ProducesResponseType(typeof(FormEntryResult), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(FormEntryResult), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(FormEntryResult), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpsertEntry(
        string id,
        Guid entryId,
        [FromBody] JsonElement body,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return NotFound("ID mismatch");

        try
        {
            var result = await _formEntryService.UpsertEntryAsync(id, entryId, body, cancellationToken);
            return result.Id switch
            {
                1 => StatusCode(StatusCodes.Status201Created, result),
                2 => StatusCode(StatusCodes.Status202Accepted, result),
                3 => StatusCode(StatusCodes.Status409Conflict, result),
                _ => StatusCode(StatusCodes.Status404NotFound, result)
            };
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Form entry schema: all wFormControl rows for this formId (jsonId, name, type, parentId, etc.).
    /// </summary>
    [HttpGet("{id}/entry/controls")]
    [HttpGet("{id}/controls")]
    [ProducesResponseType(typeof(FormControlsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetFormControls(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return NotFound();

        try
        {
            var result = await _formService.GetControlsAsync(id, cancellationToken);
            if (result == null)
                return NotFound();

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// List all form entries for a form (v5 POST /api/form/{id}/entry/all).
    /// Pass <paramref name="id"/> (formId) to get entry rows from dbo.ezfb_{form}_items.
    /// </summary>
    [HttpPost("{id}/entry/all")]
    [ProducesResponseType(typeof(FormEntryAllResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListEntries(
        string id,
        [FromBody] FormEntryAllRequest? request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return NotFound();

        try
        {
            var result = await _formEntryService.ListEntriesAsync(
                id,
                request ?? new FormEntryAllRequest(),
                cancellationToken);
            if (result.Status != FormEntryGetStatus.Found)
                return NotFound();

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Simple list of form entries by formId (GET shortcut for entry/all).</summary>
    [HttpGet("{id}/entry/all")]
    [ProducesResponseType(typeof(FormEntryAllResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListEntriesGet(
        string id,
        [FromQuery] int currentPage = 1,
        [FromQuery] int itemsPerPage = 20,
        [FromQuery] bool includeFormJson = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return NotFound();

        try
        {
            var result = await _formEntryService.ListEntriesAsync(
                id,
                new FormEntryAllRequest(
                    CurrentPage: currentPage,
                    ItemsPerPage: itemsPerPage,
                    IncludeFormJson: includeFormJson),
                cancellationToken);
            if (result.Status != FormEntryGetStatus.Found)
                return NotFound();

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Get form entry by itemId (v5 GET /api/form/{id}/entry/{entryId}).</summary>
    [HttpGet("{id}/entry/{entryId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<Dictionary<string, object?>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetEntry(string id, Guid entryId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || entryId == Guid.Empty)
            return NotFound();

        try
        {
            var result = await _formEntryService.GetEntriesAsync(
                id,
                entryId.ToString("D"),
                cancellationToken);
            if (result.Status != FormEntryGetStatus.Found || result.Entries == null || result.Entries.Count == 0)
                return NotFound();

            return Ok(result.Entries);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Get a form by id with designer JSON (<c>formJson</c>) from blob/file storage.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(FormByIdResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(string id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _formService.GetByIdAsync(id, cancellationToken);
            if (result == null)
                return NotFound();
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private Guid? GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? User.FindFirstValue("userId");
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private bool IsCurrentUserAdmin() =>
        User.Claims.Any(c =>
            (c.Type == ClaimTypes.Role || c.Type == "role") &&
            (string.Equals(c.Value, "Admin", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(c.Value, "Administrator", StringComparison.OrdinalIgnoreCase)));
}

public sealed record FormMasterFileUploadResponse(
    int MasterFileProcessId,
    string FilePath,
    int? NotificationId,
    string? HangfireJobId,
    /// <summary>Exact JSON body Hangfire POSTs to Python <c>ezDataImport</c>.</summary>
    object? PythonInput = null);
