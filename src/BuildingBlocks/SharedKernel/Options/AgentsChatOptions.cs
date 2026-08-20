namespace SaaSApp.SharedKernel.Options;

/// <summary>
/// Shared Python agents <c>/chat</c> endpoint. All intents (<c>ap</c>, <c>ocr</c>, <c>summary</c>, <c>insight</c>)
/// POST to the same URL; the <c>intent</c> field selects the handler.
/// </summary>
public sealed class AgentsChatOptions
{
    public const string SectionName = "Agents";

    public string ChatUrl { get; set; } = string.Empty;

    /// <summary>
    /// Returns <see cref="ChatUrl"/> when set, otherwise falls back to a legacy per-feature URL.
    /// </summary>
    public string ResolveChatUrl(string? legacyUrl = null)
    {
        var url = ChatUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(url))
            return url;

        return legacyUrl?.Trim() ?? string.Empty;
    }
}
