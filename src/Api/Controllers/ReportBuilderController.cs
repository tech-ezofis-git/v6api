using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.MultiTenancy;
using SaaSApp.Reporting.Application.Contracts;
using SaaSApp.Security;

namespace SaaSApp.Api.Controllers;

/// <summary>
/// Configurable workflow reports: fields, filters, calculated columns, sharing, and Hangfire email delivery.
/// Domain is the workflow name; data is read from the linked ezfb form table.
/// </summary>
[ApiController]
[Route("api/report-builder")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class ReportBuilderController : ControllerBase
{
    private readonly IReportDefinitionService _definitions;
    private readonly IReportQueryService _query;
    private readonly IReportExportService _export;
    private readonly IReportMailService _mail;
    private readonly ITenantProvider _tenantProvider;

    public ReportBuilderController(
        IReportDefinitionService definitions,
        IReportQueryService query,
        IReportExportService export,
        IReportMailService mail,
        ITenantProvider tenantProvider)
    {
        _definitions = definitions;
        _query = query;
        _export = export;
        _mail = mail;
        _tenantProvider = tenantProvider;
    }

    /// <summary>List reports for the dashboard (name, domain, status, scheduled, owner, runs, modified).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ReportListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? domain,
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] bool? scheduled,
        CancellationToken cancellationToken)
    {
        var items = await _definitions.ListAsync(
            RequireTenantId(),
            RequireUserId(),
            IsAdmin(),
            new ReportListQuery
            {
                Domain = domain,
                Search = search,
                Status = status,
                Scheduled = scheduled
            },
            cancellationToken);
        return Ok(items);
    }

    /// <summary>Workflows available as report domains.</summary>
    [HttpGet("domains")]
    [ProducesResponseType(typeof(IReadOnlyList<ReportDomainDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Domains(CancellationToken cancellationToken)
    {
        var domains = await _query.ListDomainsAsync(cancellationToken);
        return Ok(domains);
    }

    /// <summary>Forms for the Source Form dropdown. FormId is dbo.wForm.id (used to build ezfb_{id8}_items).</summary>
    [HttpGet("forms")]
    [ProducesResponseType(typeof(IReadOnlyList<ReportSourceFormDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Forms(
        [FromQuery] string? sourceType,
        CancellationToken cancellationToken)
    {
        var forms = await _query.ListSourceFormsAsync(sourceType, cancellationToken);
        return Ok(forms);
    }

    /// <summary>Form fields for the Fields step. Pass sourceType + sourceForm or sourceFormId.</summary>
    [HttpGet("fields")]
    [ProducesResponseType(typeof(IReadOnlyList<ReportAvailableFieldDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Fields(
        [FromQuery] string? domain,
        [FromQuery] string? sourceFormId,
        [FromQuery] string? sourceForm,
        [FromQuery] string? sourceType,
        CancellationToken cancellationToken)
    {
        try
        {
            var fields = await _query.ListFieldsAsync(
                RequireTenantId(),
                domain,
                sourceFormId,
                sourceForm,
                sourceType,
                cancellationToken);
            return Ok(fields);
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

    /// <summary>Preview report data from a config body without saving (live preview).</summary>
    [HttpPost("preview")]
    [ProducesResponseType(typeof(ReportRunResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(
        [FromBody] ReportBuilderConfig config,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _query.ExecuteAsync(RequireTenantId(), config, cancellationToken);
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

    /// <summary>Saved report JSON (fields, filters, schedule). Use GET {id}/data to show rows.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ReportBuilderConfig), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var config = await _definitions.GetAsync(RequireTenantId(), RequireUserId(), IsAdmin(), id, cancellationToken);
        if (config is null)
            return NotFound(new { error = "Report not found." });
        return Ok(config);
    }

    /// <summary>
    /// Show report data: load the saved JSON by report id, query the workflow form table, return columns + rows.
    /// </summary>
    [HttpGet("{id:guid}/data")]
    [ProducesResponseType(typeof(ReportRunResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetData(Guid id, CancellationToken cancellationToken) =>
        RunSavedReportAsync(id, download: false, sendEmail: false, format: null, incrementRuns: false, cancellationToken);

    /// <summary>Create a report (status Draft unless you send Published). Hangfire is registered only when published + scheduled.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ReportBuilderConfig), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create(
        [FromBody] ReportBuilderConfig config,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(config.Status))
                config.Status = ReportStatuses.Draft;
            var saved = await _definitions.SaveAsync(RequireTenantId(), RequireUserId(), IsAdmin(), config, cancellationToken);
            return Ok(saved);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ReportBuilderConfig), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] ReportBuilderConfig config,
        CancellationToken cancellationToken)
    {
        try
        {
            config.Id = id;
            var saved = await _definitions.SaveAsync(RequireTenantId(), RequireUserId(), IsAdmin(), config, cancellationToken);
            return Ok(saved);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Report not found." });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id:guid}/publish")]
    [ProducesResponseType(typeof(ReportBuilderConfig), StatusCodes.Status200OK)]
    public async Task<IActionResult> Publish(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var saved = await _definitions.PublishAsync(RequireTenantId(), RequireUserId(), IsAdmin(), id, cancellationToken);
            return Ok(saved);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Report not found." });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var ok = await _definitions.DeleteAsync(RequireTenantId(), RequireUserId(), IsAdmin(), id, cancellationToken);
            if (!ok)
                return NotFound(new { error = "Report not found." });
            return Ok(new { deleted = true, id });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    /// <summary>Run now: returns JSON data, or set download=true / sendEmail=true.</summary>
    [HttpPost("{id:guid}/run")]
    public Task<IActionResult> Run(
        Guid id,
        [FromQuery] bool download = false,
        [FromQuery] bool sendEmail = false,
        [FromQuery] string? format = null,
        CancellationToken cancellationToken = default) =>
        RunSavedReportAsync(id, download, sendEmail, format, incrementRuns: true, cancellationToken);

    private async Task<IActionResult> RunSavedReportAsync(
        Guid id,
        bool download,
        bool sendEmail,
        string? format,
        bool incrementRuns,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var config = await _definitions.GetAsync(tenantId, RequireUserId(), IsAdmin(), id, cancellationToken);
        if (config is null)
            return NotFound(new { error = "Report not found." });

        try
        {
            var data = await _query.ExecuteAsync(tenantId, config, cancellationToken);
            if (incrementRuns)
                await _definitions.IncrementRunsAsync(tenantId, id, cancellationToken);

            if (sendEmail)
                await _mail.SendAsync(tenantId, config, data, cancellationToken);

            if (download)
            {
                var file = _export.Export(data, format ?? config.Schedule?.Format);
                return File(file.Content, file.ContentType, file.FileName);
            }

            return Ok(data);
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

    private Guid RequireTenantId() =>
        _tenantProvider.GetTenantId()
        ?? throw new InvalidOperationException("X-Tenant-Id header is required.");

    private Guid RequireUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue("sub")
                  ?? User.FindFirstValue("oid")
                  ?? User.FindFirstValue("userId");
        if (!Guid.TryParse(raw, out var id))
            throw new InvalidOperationException("User id is required.");
        return id;
    }

    private bool IsAdmin() =>
        User.IsInRole("Admin")
        || User.HasClaim(ClaimTypes.Role, "Admin")
        || User.HasClaim("role", "Admin");
}
