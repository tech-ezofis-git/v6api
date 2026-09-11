using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Security;

namespace SaaSApp.Api.Controllers;

/// <summary>
/// v5-compatible upload/index routes backed by V6 repository stage tables and archive upload (Hangfire).
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class UploadAndIndexController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ITenantProvider _tenantProvider;
    private readonly IRepositoryUploadIndexService _uploadIndex;

    public UploadAndIndexController(ITenantProvider tenantProvider, IRepositoryUploadIndexService uploadIndex)
    {
        _tenantProvider = tenantProvider;
        _uploadIndex = uploadIndex;
    }

    /// <summary>v5: POST api/uploadAndIndex/upload — stage file to monitor + stage table.</summary>
    [HttpPost("/api/uploadAndIndex/upload")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 104_857_600)]
    [ProducesResponseType(typeof(UploadIndexUploadResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Upload(
        IFormFile? file,
        [FromForm] string? repositoryId,
        [FromForm] string? filename,
        [FromForm] List<string>? fields,
        CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file received." });

        if (!TryParseRepositoryId(repositoryId, out var repoId))
            return BadRequest(new { error = "repositoryId is required (GUID)." });

        var tenantId = RequireTenantId();
        var uploadName = ResolveUploadFileName(filename, file.FileName);

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _uploadIndex.UploadAsync(
                repoId,
                tenantId,
                stream,
                uploadName!,
                file.ContentType,
                file.Length,
                ResolveFieldsFormInput(fields),
                GetUserId(),
                cancellationToken);

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
    }

    /// <summary>
    /// Send file to Python OCR API (base64 + parameters from fields) and return OCR output. No staging.
    /// </summary>
    [HttpPost("/api/uploadAndIndex/uploadForOcr")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 104_857_600)]
    [ProducesResponseType(typeof(UploadForOcrResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadForOcr(
        IFormFile? file,
        [FromForm] string? repositoryId,
        [FromForm] List<string>? fields,
        [FromForm] string? pageNo,
        [FromForm] string? ocrType,
        [FromForm] string? validateType,
        CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file received." });

        if (!TryParseRepositoryId(repositoryId, out var repoId))
            return BadRequest(new { error = "repositoryId is required (GUID)." });

        var tenantId = RequireTenantId();

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _uploadIndex.UploadForOcrAsync(
                repoId,
                tenantId,
                stream,
                ResolveFieldsFormInput(fields),
                pageNo,
                ocrType,
                validateType,
                file.FileName,
                cancellationToken);

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
    }

    /// <summary>
    /// Stage file to monitor storage using pre-extracted OCR metadata (from uploadForOcr). Does not call OCR.
    /// Form: file, repositoryId, metadata (JSON), optional ocrJson / ocrText.
    /// </summary>
    [HttpPost("/api/uploadAndIndex/uploadWithOcr")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 104_857_600)]
    [ProducesResponseType(typeof(UploadWithOcrResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadWithOcr(
        IFormFile? file,
        [FromForm] string? repositoryId,
        [FromForm] string? filename,
        [FromForm] string? metadata,
        [FromForm] List<string>? fields,
        [FromForm] string? ocrJson,
        [FromForm] string? ocrText,
        CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file received." });

        if (!TryParseRepositoryId(repositoryId, out var repoId))
            return BadRequest(new { error = "repositoryId is required (GUID)." });

        var tenantId = RequireTenantId();
        var uploadName = ResolveUploadFileName(filename, file.FileName);
        // Prefer flat metadata object; otherwise merge Swagger/FE "fields" array JSON.
        var metadataJson = !string.IsNullOrWhiteSpace(metadata)
            ? metadata
            : ResolveFieldsFormInput(fields);
        // When both are sent, keep metadata as the stage values; fields still flow via ocrJson/parser.

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _uploadIndex.UploadWithOcrAsync(
                repoId,
                tenantId,
                stream,
                uploadName!,
                file.ContentType,
                file.Length,
                metadataJson,
                ocrJson,
                ocrText,
                GetUserId(),
                cancellationToken);

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
    }

    /// <summary>v5: POST api/uploadAndIndex/load/{id} — load staged file for indexing UI.</summary>
    [HttpPost("/api/uploadAndIndex/load/{id}")]
    [ProducesResponseType(typeof(UploadIndexLoadResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Load(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var stageId) || stageId == Guid.Empty)
            return BadRequest(new { error = "id must be a valid GUID." });

        var tenantId = RequireTenantId();
        var result = await _uploadIndex.LoadAsync(stageId, tenantId, cancellationToken);
        return result == null ? NotFound(new { error = "File not found." }) : Ok(result);
    }

    /// <summary>v5: PUT api/uploadAndIndex/index/{id} — save fields and queue V6 archive (Hangfire).</summary>
    [HttpPut("/api/uploadAndIndex/index/{id}")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(UploadIndexArchiveQueuedResult), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Index(string id, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var stageId) || stageId == Guid.Empty)
            return BadRequest(new { error = "id must be a valid GUID." });

        UploadIndexSaveRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<UploadIndexSaveRequest>(body.GetRawText(), JsonOptions);
        }
        catch (JsonException ex)
        {
            return BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
        }

        if (request == null || request.RepositoryId == Guid.Empty)
            return BadRequest(new { error = "repositoryId is required." });

        var tenantId = RequireTenantId();
        try
        {
            var result = await _uploadIndex.QueueArchiveAsync(stageId, tenantId, request, GetUserId(), cancellationToken);
            if (result == null)
                return NotFound(new { error = "Index record not found." });

            return Accepted(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>v5: POST api/uploadAndIndex/index/all — list staged/index rows.</summary>
    [HttpPost("/api/uploadAndIndex/index/all")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(UploadIndexListResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> IndexAll([FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        UploadIndexListRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<UploadIndexListRequest>(body.GetRawText(), JsonOptions)
                ?? new UploadIndexListRequest();
        }
        catch (JsonException ex)
        {
            return BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
        }

        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("repositoryId", out var repoProp)
            && request.RepositoryId is null)
        {
            if (repoProp.ValueKind == JsonValueKind.String && Guid.TryParse(repoProp.GetString(), out var g))
                request = request with { RepositoryId = g };
            else if (repoProp.ValueKind == JsonValueKind.Number && repoProp.TryGetInt32(out var legacyId))
            {
                return BadRequest(new { error = "repositoryId must be a repository GUID in V6 (legacy int id is not supported)." });
            }
        }

        var tenantId = RequireTenantId();
        try
        {
            var result = await _uploadIndex.ListIndexAsync(tenantId, request, cancellationToken);
            return Ok(new
            {
                data = new[]
                {
                    new { key = string.Empty, value = result.Items }
                },
                meta = new
                {
                    currentPage = result.CurrentPage,
                    itemsPerPage = result.ItemsPerPage,
                    totalItems = result.TotalItems
                }
            });
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

    /// <summary>
    /// Swagger form default is <c>filename=string</c>. That must not become archive <c>string.pdf</c>
    /// when the last naming column is Filename — use the uploaded file's original name.
    /// </summary>
    private static string ResolveUploadFileName(string? filename, string? uploadedFileName)
    {
        var formName = string.IsNullOrWhiteSpace(filename) ? null : filename.Trim();
        var fileName = string.IsNullOrWhiteSpace(uploadedFileName) ? null : uploadedFileName.Trim();

        if (IsPlaceholderFileName(formName))
            formName = null;
        if (IsPlaceholderFileName(fileName))
            fileName = null;

        return formName ?? fileName ?? "upload.bin";
    }

    private static bool IsPlaceholderFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        var stem = Path.GetFileNameWithoutExtension(name.Trim());
        return stem.Equals("string", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Swagger/multipart often sends each OCR parameter as a separate <c>fields</c> form value.
    /// <see cref="string"/> binding only keeps the first; merge all values here.
    /// </summary>
    private static string? ResolveFieldsFormInput(List<string>? fields)
    {
        if (fields == null || fields.Count == 0)
            return null;

        var values = fields
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();

        if (values.Count == 0)
            return null;

        if (values.Count == 1)
            return values[0];

        // Each "fields" form value is often one JSON object. Keep them as objects, not quoted strings,
        // so name/value pairs survive into stage columns.
        var objects = new List<JsonElement>();
        foreach (var value in values)
        {
            if (!value.StartsWith('{') && !value.StartsWith('['))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                        objects.Add(item.Clone());
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    objects.Add(doc.RootElement.Clone());
                }
            }
            catch (JsonException)
            {
                // Leave non-JSON lines for the service parser.
            }
        }

        return objects.Count > 0
            ? JsonSerializer.Serialize(objects)
            : JsonSerializer.Serialize(values);
    }

    private static bool TryParseRepositoryId(string? raw, out Guid repositoryId)
    {
        repositoryId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        return Guid.TryParse(raw.Trim(), out repositoryId) && repositoryId != Guid.Empty;
    }

    private Guid RequireTenantId() =>
        _tenantProvider.GetTenantId() ?? throw new InvalidOperationException("Tenant context is required (X-Tenant-Id).");

    private Guid? GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? User.FindFirstValue("oid");
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
