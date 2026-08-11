using System.Text.Json.Serialization;

namespace SaaSApp.Workflow.Application.Connectors;

public sealed record ConnectorProviderPublicDto(
    string ProviderCode,
    string DisplayName,
    bool IsConfigured,
    bool SupportsFiles,
    bool SupportsGmail,
    bool SupportsQuickBooks = false);

public sealed record ConnectorOAuthAuthorizeRequest(
    string ProviderCode,
    Guid? ConnectorId = null,
    string? Name = null,
    string? ConfigJson = null,
    string? SuccessRedirectUrl = null);

public sealed record ConnectorOAuthAuthorizeResponse(
    Guid ConnectorId,
    string AuthorizationUrl,
    string State);

public sealed record ConnectorOAuthStatusDto(
    Guid ConnectorId,
    string? ProviderCode,
    string OAuthStatus,
    string? ExternalAccountEmail,
    DateTime? TokenExpiresAtUtc,
    bool IsConnected);

public sealed record ConnectorFileEntryDto(
    string Path,
    string Name,
    bool IsFolder,
    long? SizeBytes,
    DateTime? ModifiedAtUtc);

public sealed record ConnectorFileListResponse(IReadOnlyList<ConnectorFileEntryDto> Items);

public sealed record ConnectorGmailMessageDto(
    string Id,
    string? ThreadId,
    string? Subject,
    string? From,
    string? Snippet,
    DateTime? ReceivedAtUtc,
    IReadOnlyList<ConnectorGmailAttachmentDto> Attachments,
    bool IsUnread = false,
    string? BodyText = null,
    string? BodyHtml = null);

public sealed record ConnectorGmailAttachmentDto(
    string Id,
    string? FileName,
    string? MimeType,
    long? SizeBytes);

public sealed record ConnectorGmailMessageListResponse(IReadOnlyList<ConnectorGmailMessageDto> Items);

public sealed record ConnectorMailSummaryDto(int TotalCount, int UnreadCount);

public sealed record ConnectorQuickBooksMasterDto(
    string Id,
    string Type,
    string? DisplayName,
    string? Email,
    bool Active,
    string? RawJson);

public sealed record ConnectorQuickBooksMasterListResponse(string Type, IReadOnlyList<ConnectorQuickBooksMasterDto> Items);

public sealed record ConnectorQuickBooksDocumentDto(
    string Id,
    string Type,
    string? DocNumber,
    string? TxnDate,
    decimal? TotalAmount,
    string? CustomerVendorName,
    string? Status,
    string? RawJson);

public sealed record ConnectorQuickBooksDocumentListResponse(string Type, IReadOnlyList<ConnectorQuickBooksDocumentDto> Items);

/// <summary>AP Agent payload: look up a QuickBooks Purchase Order by DocNumber (PO Number).</summary>
public sealed record ConnectorQuickBooksPoLookupRequest(string PoNumber);

/// <summary>AP Agent PO line shape (snake_case field names).</summary>
public sealed class ConnectorQuickBooksPoLineDto
{
    [JsonPropertyName("line_no")]
    public int? LineNo { get; init; }

    [JsonPropertyName("item_no")]
    public string? ItemNo { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("uom")]
    public string? Uom { get; init; }

    [JsonPropertyName("rate")]
    public decimal? Rate { get; init; }

    [JsonPropertyName("line_amount")]
    public decimal? LineAmount { get; init; }
}

/// <summary>AP Agent PO header shape (display field names with spaces).</summary>
public sealed class ConnectorQuickBooksPurchaseOrderDto
{
    [JsonPropertyName("PO Number")]
    public string? PoNumber { get; init; }

    [JsonPropertyName("Vendor Name")]
    public string? VendorName { get; init; }

    [JsonPropertyName("Vendor")]
    public string? Vendor { get; init; }

    [JsonPropertyName("PO Date")]
    public string? PoDate { get; init; }

    [JsonPropertyName("PO Amount")]
    public decimal? PoAmount { get; init; }

    [JsonPropertyName("Currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("Terms")]
    public string? Terms { get; init; }

    [JsonPropertyName("Buyer")]
    public string? Buyer { get; init; }

    [JsonPropertyName("Vendor Address")]
    public string? VendorAddress { get; init; }

    [JsonPropertyName("Ship To Address")]
    public string? ShipToAddress { get; init; }

    [JsonPropertyName("Notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("PO Status")]
    public string? PoStatus { get; init; }

    [JsonPropertyName("PO Line Item")]
    public IReadOnlyList<ConnectorQuickBooksPoLineDto> PoLineItem { get; init; } = Array.Empty<ConnectorQuickBooksPoLineDto>();
}

public sealed record ConnectorQuickBooksPoLookupResponse(
    bool Found,
    string PoNumber,
    ConnectorQuickBooksPurchaseOrderDto? PurchaseOrder);

