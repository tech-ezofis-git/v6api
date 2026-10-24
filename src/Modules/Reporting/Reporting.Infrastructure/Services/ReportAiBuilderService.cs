using System.Text.Json;
using SaaSApp.Reporting.Application.Contracts;

namespace SaaSApp.Reporting.Infrastructure.Services;

public sealed class ReportAiBuilderService : IReportAiBuilderService
{
    private readonly IReportAgentPythonClient _client;
    private readonly IReportAiDefinitionService _store;

    public ReportAiBuilderService(
        IReportAgentPythonClient client,
        IReportAiDefinitionService store)
    {
        _client = client;
        _store = store;
    }

    public async Task<ReportAiHttpResult> GenerateAndSaveAsync(
        Guid tenantId,
        Guid userId,
        ReportAiPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Description))
            return ReportAiHttpResult.Error(400, "description is required.");
        if (string.IsNullOrWhiteSpace(request.ReportType))
            return ReportAiHttpResult.Error(400, "reportType is required.");

        var reportType = request.ReportType.Trim();
        if (reportType.Equals("specific_workflow", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.WorkflowName))
            return ReportAiHttpResult.Error(400, "workflowName is required for specific_workflow.");
        if (reportType.Equals("specific_repository", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.RepositoryName))
            return ReportAiHttpResult.Error(400, "repositoryName is required for specific_repository.");

        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? $"report-{Guid.NewGuid():N}"
            : request.SessionId.Trim();

        ReportAgentProxyResult proxy;
        try
        {
            proxy = await _client.GeneratePromptAsync(
                new ReportAgentChatRequest
                {
                    SessionId = sessionId,
                    Phase = "prompt",
                    TenantId = tenantId,
                    Message = request.Description,
                    Description = request.Description,
                    ReportType = reportType,
                    WorkflowName = request.WorkflowName,
                    RepositoryName = request.RepositoryName,
                    Model = request.Model
                },
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return ReportAiHttpResult.Error(502, ex.Message);
        }

        var mapped = MapProxy(proxy);
        if (mapped is not null)
            return mapped;
        if (!proxy.HasReportResult)
            return ReportAiHttpResult.Error(422, proxy.Reply ?? "Report prompt was not generated.");

        var title = ReportAgentChatMapper.ReadString(proxy.ReportResult, "title")
                    ?? (string.IsNullOrWhiteSpace(request.Name) ? request.Description.Trim() : request.Name.Trim());
        var prompt = ReportAgentChatMapper.ReadString(proxy.ReportResult, "reportPrompt", "report_prompt") ?? "";
        var spec = ReportAgentChatMapper.ReadProperty(proxy.ReportResult, "reportSpec", "report_spec");

        var record = request.Id is { } existingId && existingId != Guid.Empty
            ? await _store.GetAsync(tenantId, existingId, cancellationToken)
            : null;
        record ??= new ReportAiDefinitionRecord
        {
            Id = request.Id is { } reuseId && reuseId != Guid.Empty ? reuseId : Guid.Empty,
            TenantId = tenantId,
            OwnerUserId = userId,
            Status = "Draft"
        };

        record.TenantId = tenantId;
        record.OwnerUserId = record.OwnerUserId == Guid.Empty ? userId : record.OwnerUserId;
        record.SessionId = sessionId;
        record.Name = string.IsNullOrWhiteSpace(request.Name) ? title : request.Name.Trim();
        record.Description = request.Description.Trim();
        record.ReportType = reportType;
        record.WorkflowName = request.WorkflowName?.Trim();
        record.RepositoryName = request.RepositoryName?.Trim();
        record.ReportPrompt = prompt;
        record.ReportSpecJson = ReportAgentChatMapper.SpecJson(spec);

        var saved = await _store.SaveAsync(record, cancellationToken);
        return ReportAiHttpResult.Ok(
            ReportAgentChatMapper.MergePromptResponse(saved.Id, saved.SessionId, proxy.ReportResult));
    }

    public async Task<ReportAiHttpResult> RunSavedAsync(
        Guid tenantId,
        Guid reportId,
        ReportAiRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var record = await _store.GetAsync(tenantId, reportId, cancellationToken);
        if (record is null)
            return ReportAiHttpResult.Error(404, "Report not found.");

        var overlayPrompt = request.ReportPrompt;
        var overlaySpec = request.ReportSpec;
        if (!string.IsNullOrWhiteSpace(overlayPrompt)
            || overlaySpec.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            record = await _store.UpdateAsync(
                tenantId,
                reportId,
                new ReportAiUpdateRequest
                {
                    ReportPrompt = overlayPrompt,
                    ReportSpec = overlaySpec
                },
                cancellationToken) ?? record;
        }

        var spec = ReportAgentChatMapper.ParseObjectOrEmpty(record.ReportSpecJson);
        ReportAgentProxyResult proxy;
        try
        {
            proxy = await _client.RunReportAsync(
                new ReportAgentChatRequest
                {
                    SessionId = string.IsNullOrWhiteSpace(record.SessionId)
                        ? $"report-{record.Id:N}"
                        : record.SessionId,
                    Phase = "run",
                    TenantId = tenantId,
                    ReportType = record.ReportType,
                    WorkflowName = record.WorkflowName,
                    RepositoryName = record.RepositoryName,
                    ReportPrompt = record.ReportPrompt,
                    ReportSpec = spec,
                    Page = request.Page < 1 ? 1 : request.Page,
                    PageSize = request.PageSize is < 1 or > 500 ? 25 : request.PageSize,
                    Model = request.Model
                },
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return ReportAiHttpResult.Error(502, ex.Message);
        }

        var mapped = MapProxy(proxy);
        if (mapped is not null)
            return mapped;
        if (!proxy.HasReportResult)
            return ReportAiHttpResult.Error(422, proxy.Reply ?? "Report was not generated.");

        var warnings = ReportAgentChatMapper.ReadWarnings(proxy.ReportResult);
        if (ReportAgentChatMapper.IsLockFailure(warnings))
            return ReportAiHttpResult.Error(422, warnings[0]);

        await _store.IncrementRunsAsync(tenantId, reportId, cancellationToken);
        return ReportAiHttpResult.Ok(ReportAgentChatMapper.MergeRunResponse(record.Id, proxy.ReportResult));
    }

    public async Task<ReportAiHttpResult> GetAsync(
        Guid tenantId,
        Guid reportId,
        CancellationToken cancellationToken = default)
    {
        var record = await _store.GetAsync(tenantId, reportId, cancellationToken);
        return record is null
            ? ReportAiHttpResult.Error(404, "Report not found.")
            : ReportAiHttpResult.Ok(ReportAgentChatMapper.ToDto(record));
    }

    public async Task<ReportAiHttpResult> ListAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _store.ListAsync(tenantId, userId, cancellationToken);
        var items = rows.Select(row => new ReportAiListItemDto
        {
            Id = row.Id,
            Name = row.Name,
            Description = row.Description,
            ReportType = row.ReportType,
            Status = row.Status,
            Runs = row.Runs,
            CreatedAtUtc = row.CreatedAtUtc,
            ModifiedAtUtc = row.ModifiedAtUtc
        }).ToList();
        return ReportAiHttpResult.Ok(items);
    }

    public async Task<ReportAiHttpResult> UpdateAsync(
        Guid tenantId,
        Guid reportId,
        ReportAiUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var updated = await _store.UpdateAsync(tenantId, reportId, request, cancellationToken);
            return updated is null
                ? ReportAiHttpResult.Error(404, "Report not found.")
                : ReportAiHttpResult.Ok(ReportAgentChatMapper.ToDto(updated));
        }
        catch (KeyNotFoundException)
        {
            return ReportAiHttpResult.Error(404, "Report not found.");
        }
    }

    public async Task<ReportAiHttpResult> ListTypesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var proxy = await _client.GetTypesAsync(cancellationToken);
            return FromRawJson(proxy, "Failed to list report types.");
        }
        catch (InvalidOperationException ex)
        {
            return ReportAiHttpResult.Error(502, ex.Message);
        }
    }

    public async Task<ReportAiHttpResult> ListScopeOptionsAsync(
        string scope,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scope)
            || (!scope.Equals("workflows", StringComparison.OrdinalIgnoreCase)
                && !scope.Equals("repositories", StringComparison.OrdinalIgnoreCase)))
            return ReportAiHttpResult.Error(400, "scope must be workflows or repositories.");

        try
        {
            var proxy = await _client.GetScopeOptionsAsync(scope, tenantId, cancellationToken);
            return FromRawJson(proxy, "Failed to list scope options.");
        }
        catch (InvalidOperationException ex)
        {
            return ReportAiHttpResult.Error(502, ex.Message);
        }
    }

    private static ReportAiHttpResult? MapProxy(ReportAgentProxyResult proxy)
    {
        if (proxy.TimedOut)
            return ReportAiHttpResult.Error(504, "Report Agent timed out.");
        if (proxy.StatusCode is >= 500)
            return ReportAiHttpResult.Error(502, proxy.Reply ?? "Report Agent is unavailable.");
        if (proxy.StatusCode is >= 400 and < 500)
            return ReportAiHttpResult.Error(proxy.StatusCode, proxy.Reply ?? "Report Agent rejected the request.");
        return null;
    }

    private static ReportAiHttpResult FromRawJson(ReportAgentProxyResult proxy, string fallback)
    {
        if (proxy.TimedOut)
            return ReportAiHttpResult.Error(504, "Report Agent timed out.");
        if (proxy.StatusCode is >= 500)
            return ReportAiHttpResult.Error(502, fallback);
        if (string.IsNullOrWhiteSpace(proxy.RawBody))
            return new ReportAiHttpResult { StatusCode = proxy.StatusCode, Payload = null };

        try
        {
            using var doc = JsonDocument.Parse(proxy.RawBody);
            return new ReportAiHttpResult
            {
                StatusCode = proxy.StatusCode,
                Payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText())
            };
        }
        catch (JsonException)
        {
            return ReportAiHttpResult.Error(proxy.StatusCode >= 400 ? proxy.StatusCode : 502, fallback);
        }
    }
}
