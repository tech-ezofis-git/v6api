namespace SaaSApp.Repository.Infrastructure.Options;

public sealed class RepositorySignRequestOptions
{
    public const string SectionName = "RepositorySignRequest";

    /// <summary>App origin, e.g. https://demoapp.ezofis.com</summary>
    public string FrontendBaseUrl { get; set; } = "https://demoapp.ezofis.com";

    /// <summary>Signer landing path; token is appended as /{inviteToken}.</summary>
    public string SignRequestPath { get; set; } = "/sign-request";

    public int DefaultExpiryDays { get; set; } = 14;

    public string EmailSubjectPrefix { get; set; } = "Please review and sign";

    /// <summary>Optional absolute path to logo PNG. Default: Assets/ezofis-logo-mark.png next to the assembly.</summary>
    public string? EmailLogoPath { get; set; }

    /// <summary>Shown in email security footer (mailto).</summary>
    public string SupportEmail { get; set; } = "support@ezofis.com";
}
