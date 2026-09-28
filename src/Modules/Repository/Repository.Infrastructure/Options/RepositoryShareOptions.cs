namespace SaaSApp.Repository.Infrastructure.Options;

public sealed class RepositoryShareOptions
{
    public const string SectionName = "RepositoryShare";

    /// <summary>App origin, e.g. https://cloud.ezofis.com</summary>
    public string FrontendBaseUrl { get; set; } = "https://cloud.ezofis.com";

    /// <summary>Login/signup route (share links land here with shareToken query).</summary>
    public string SignInPath { get; set; } = "/sign-in";

    public int DefaultExpiryDays { get; set; } = 30;
    public string EmailSubject { get; set; } = "A document was shared with you";
}
