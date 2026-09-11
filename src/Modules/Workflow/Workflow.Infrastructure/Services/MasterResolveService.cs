using System.Text.Json;
using SaaSApp.Workflow.Application.Connectors;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Forms;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class MasterResolveService : IMasterResolveService
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Vendor", "Customer", "Item"
    };

    private static readonly string[] DisplayNameKeys =
    [
        "DisplayName", "displayName", "Name", "name", "VendorName", "vendorName",
        "CustomerName", "customerName", "ItemName", "itemName", "Title", "title"
    ];

    private static readonly string[] EmailKeys = ["Email", "email", "PrimaryEmail", "primaryEmail"];

    private readonly IEmailIngestService _emailIngest;
    private readonly IFormEntryService _formEntryService;
    private readonly IConnectorOAuthService _oauthService;
    private readonly IConnectorService _connectorService;

    public MasterResolveService(
        IEmailIngestService emailIngest,
        IFormEntryService formEntryService,
        IConnectorOAuthService oauthService,
        IConnectorService connectorService)
    {
        _emailIngest = emailIngest;
        _formEntryService = formEntryService;
        _oauthService = oauthService;
        _connectorService = connectorService;
    }

    public async Task<MasterResolveResponse> ResolveAsync(
        string type,
        string? q,
        int maxResults,
        string? source,
        string? formId,
        Guid? connectorId,
        Guid? mailboxId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(type) || !AllowedTypes.Contains(type.Trim()))
            throw new InvalidOperationException("type must be Vendor, Customer, or Item.");

        maxResults = Math.Clamp(maxResults, 1, 100);
        var normalizedType = char.ToUpperInvariant(type.Trim()[0]) + type.Trim()[1..].ToLowerInvariant();

        string resolvedSource;
        string? resolvedFormId = formId;
        Guid? resolvedConnectorId = connectorId;

        if (mailboxId is { } mid && mid != Guid.Empty)
        {
            var mailbox = await _emailIngest.GetMailboxAsync(mid, cancellationToken)
                ?? throw new InvalidOperationException("Mailbox not found.");
            resolvedSource = mailbox.MasterSource;
            resolvedFormId ??= mailbox.MasterFormId;
            resolvedConnectorId ??= mailbox.MasterConnectorId;
        }
        else
        {
            resolvedSource = string.IsNullOrWhiteSpace(source)
                ? EmailIngestMasterSources.InternalForm
                : source.Trim();
        }

        if (string.Equals(resolvedSource, EmailIngestMasterSources.QuickBooks, StringComparison.OrdinalIgnoreCase))
        {
            if (resolvedConnectorId is not { } qbConnectorId || qbConnectorId == Guid.Empty)
                throw new InvalidOperationException("connectorId (QuickBooks) is required.");
            return await ResolveQuickBooksAsync(normalizedType, q, maxResults, qbConnectorId, cancellationToken);
        }

        if (string.Equals(resolvedSource, EmailIngestMasterSources.Sap, StringComparison.OrdinalIgnoreCase)
            || string.Equals(resolvedSource, "Sap", StringComparison.OrdinalIgnoreCase))
        {
            if (resolvedConnectorId is not { } sapConnectorId || sapConnectorId == Guid.Empty)
                throw new InvalidOperationException("connectorId (SAP) is required.");
            return await ResolveSapAsync(normalizedType, q, maxResults, sapConnectorId, cancellationToken);
        }

        if (string.Equals(resolvedSource, EmailIngestMasterSources.InternalForm, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(resolvedFormId))
                throw new InvalidOperationException("formId is required for InternalForm master source.");
            return await ResolveFormAsync(normalizedType, q, maxResults, resolvedFormId!, cancellationToken);
        }

        throw new InvalidOperationException("source must be InternalForm, QuickBooks, or SAP.");
    }

    private async Task<MasterResolveResponse> ResolveSapAsync(
        string type, string? q, int maxResults, Guid connectorId, CancellationToken cancellationToken)
    {
        var connector = await _connectorService.GetByIdAsync(connectorId, cancellationToken)
            ?? throw new InvalidOperationException("SAP connector not found.");
        if (!SapConnectorProviderCodes.IsSap(connector.ProviderCode))
            throw new InvalidOperationException("connectorId must be an SAP connector (SAP / SAP_XSUAA / SAP_*).");

        var items = SapSampleVendorResolver.Resolve(connector.ConfigJson, type, q, maxResults);
        return new MasterResolveResponse(type, EmailIngestMasterSources.Sap, items);
    }

    private async Task<MasterResolveResponse> ResolveQuickBooksAsync(
        string type, string? q, int maxResults, Guid connectorId, CancellationToken cancellationToken)
    {
        var qbType = type switch
        {
            "Vendor" => "Vendor",
            "Customer" => "Customer",
            _ => "Item"
        };
        var list = await _oauthService.ListQuickBooksMastersAsync(connectorId, qbType, maxResults, cancellationToken);
        IEnumerable<ConnectorQuickBooksMasterDto> items = list.Items;
        if (!string.IsNullOrWhiteSpace(q))
        {
            items = items.Where(i =>
                (i.DisplayName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i.Email?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                i.Id.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return new MasterResolveResponse(
            type,
            EmailIngestMasterSources.QuickBooks,
            items.Select(i => new MasterResolveItemDto(
                i.Id,
                type,
                i.DisplayName,
                i.Email,
                EmailIngestMasterSources.QuickBooks,
                i.Id,
                string.IsNullOrWhiteSpace(i.RawJson) ? null : TryParseJson(i.RawJson))).ToList());
    }

    private async Task<MasterResolveResponse> ResolveFormAsync(
        string type, string? q, int maxResults, string formId, CancellationToken cancellationToken)
    {
        List<FormAllFilterGroup>? filters = null;
        if (!string.IsNullOrWhiteSpace(q))
        {
            // Prefer common display columns; FormEntryService ignores unknown criteria.
            filters =
            [
                new FormAllFilterGroup("OR",
                [
                    new FormAllFilter("Name", "contains", q),
                    new FormAllFilter("DisplayName", "contains", q),
                    new FormAllFilter("VendorName", "contains", q),
                    new FormAllFilter("CustomerName", "contains", q),
                    new FormAllFilter("ItemName", "contains", q)
                ])
            ];
        }

        var result = await _formEntryService.ListEntriesAsync(
            formId,
            new FormEntryAllRequest(
                SortBy: null,
                FilterBy: filters,
                CurrentPage: 1,
                ItemsPerPage: maxResults,
                Mode: "browse",
                IncludeFormJson: false),
            cancellationToken);

        if (result.Status == FormEntryGetStatus.NotFound || result.Entries == null)
            return new MasterResolveResponse(type, EmailIngestMasterSources.InternalForm, Array.Empty<MasterResolveItemDto>());

        var items = new List<MasterResolveItemDto>();
        foreach (var row in result.Entries.Take(maxResults))
        {
            var id = GetString(row, "itemId") ?? GetString(row, "Id") ?? Guid.NewGuid().ToString("N");
            var display = DisplayNameKeys.Select(k => GetString(row, k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            var email = EmailKeys.Select(k => GetString(row, k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            items.Add(new MasterResolveItemDto(
                id,
                type,
                display,
                email,
                EmailIngestMasterSources.InternalForm,
                id,
                row));
        }

        return new MasterResolveResponse(type, EmailIngestMasterSources.InternalForm, items);
    }

    private static string? GetString(Dictionary<string, object?> row, string key)
    {
        foreach (var kv in row)
        {
            if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                continue;
            return kv.Value?.ToString();
        }
        return null;
    }

    private static object? TryParseJson(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch (JsonException)
        {
            return raw;
        }
    }
}
