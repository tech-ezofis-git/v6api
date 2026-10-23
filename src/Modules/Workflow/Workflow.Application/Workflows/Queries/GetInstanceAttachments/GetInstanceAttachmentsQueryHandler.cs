using MediatR;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Application.Workflows.Queries.GetInstanceAttachments;

public sealed class GetInstanceAttachmentsQueryHandler : IRequestHandler<GetInstanceAttachmentsQuery, GetInstanceAttachmentsQueryResult>
{
    private readonly IWorkflowRepository _workflowRepository;
    private readonly IDynamicTableRepository _dynamicTableRepository;
    private readonly IWorkflowProcessAddonService _processAddon;
    private readonly IUserEmailLookup _userEmails;

    public GetInstanceAttachmentsQueryHandler(
        IWorkflowRepository workflowRepository,
        IDynamicTableRepository dynamicTableRepository,
        IWorkflowProcessAddonService processAddon,
        IUserEmailLookup userEmails)
    {
        _workflowRepository = workflowRepository;
        _dynamicTableRepository = dynamicTableRepository;
        _processAddon = processAddon;
        _userEmails = userEmails;
    }

    public async Task<GetInstanceAttachmentsQueryResult> Handle(GetInstanceAttachmentsQuery request, CancellationToken cancellationToken)
    {
        var instance = await _workflowRepository.GetInstanceByIdAsync(request.InstanceId, cancellationToken);
        WorkflowInstanceScopeValidator.EnsureInstanceBelongsToWorkflow(instance, request.WorkflowId, request.InstanceId);

        var workflow = await _workflowRepository.GetByIdAsync(request.WorkflowId, cancellationToken);
        var attachments = await _dynamicTableRepository.GetAttachmentsAsync(request.WorkflowId, request.InstanceId, cancellationToken);
        var workflowRepositoryGuid = TryParseRepositoryGuid(workflow?.RepositoryId);
        var uploaderEmails = await _userEmails.GetEmailsAsync(
            attachments.Select(a => a.CreatedBy).Concat(
                (await _processAddon.ListByProcessAsync(request.WorkflowId, request.InstanceId, cancellationToken))
                .Select(a => a.CreatedBy)),
            cancellationToken);

        var attachmentItems = attachments.Select(a =>
        {
            var fileName = a.FileName ?? string.Empty;
            return new AttachmentItem(
                a.Id,
                a.WorkflowInstanceId,
                request.WorkflowId,
                fileName,
                a.FilePath ?? string.Empty,
                a.FileSize,
                a.ContentType,
                a.CreatedAtUtc,
                a.CreatedBy,
                uploaderEmails.GetValueOrDefault(a.CreatedBy),
                a.ModifiedBy,
                a.ModifiedAtUtc,
                RepositoryId: a.RepositoryId ?? workflowRepositoryGuid,
                ItemId: a.ItemId,
                IsOriginal: WorkflowAttachmentOriginality.IsOriginal(fileName, a.ContentType));
        }).ToList();

        var knownItemIds = attachmentItems
            .Where(a => a.ItemId is { } id && id != Guid.Empty)
            .Select(a => a.ItemId!.Value)
            .ToHashSet();

        var addons = await _processAddon.ListByProcessAsync(request.WorkflowId, request.InstanceId, cancellationToken);
        foreach (var addon in addons)
        {
            if (knownItemIds.Contains(addon.ItemId))
                continue;

            var fileName = addon.FileName ?? string.Empty;
            attachmentItems.Add(new AttachmentItem(
                Id: Guid.Empty,
                WorkflowInstanceId: request.InstanceId,
                WorkflowId: request.WorkflowId,
                FileName: fileName,
                FilePath: string.Empty,
                FileSize: null,
                ContentType: null,
                CreatedAtUtc: addon.CreatedAt,
                CreatedBy: addon.CreatedBy,
                UploadedBy: uploaderEmails.GetValueOrDefault(addon.CreatedBy),
                ModifiedBy: null,
                ModifiedAtUtc: null,
                RepositoryId: addon.RepositoryId,
                ItemId: addon.ItemId,
                IsOriginal: WorkflowAttachmentOriginality.IsOriginal(fileName, contentType: null)));

            knownItemIds.Add(addon.ItemId);
        }

        // Original .eml first, then other originals (quote PDFs), then supporting files by recency.
        attachmentItems = attachmentItems
            .OrderByDescending(a => WorkflowAttachmentOriginality.LooksLikeEml(a.FileName, a.ContentType))
            .ThenByDescending(a => a.IsOriginal)
            .ThenByDescending(a => a.CreatedAtUtc)
            .ToList();

        var tableName = _dynamicTableRepository.GetTableName(request.WorkflowId, "WorkflowAttachments");

        return new GetInstanceAttachmentsQueryResult(request.WorkflowId, request.InstanceId, attachmentItems, tableName);
    }

    private static Guid? TryParseRepositoryGuid(string? repositoryIdLink)
    {
        if (string.IsNullOrWhiteSpace(repositoryIdLink))
            return null;

        var trimmed = repositoryIdLink.Trim();
        if (Guid.TryParse(trimmed, out var guid))
            return guid;

        return trimmed.Length == 32 && Guid.TryParseExact(trimmed, "N", out guid) ? guid : null;
    }
}
