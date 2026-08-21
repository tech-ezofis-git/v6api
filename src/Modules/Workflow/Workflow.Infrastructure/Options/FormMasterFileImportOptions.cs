namespace SaaSApp.Workflow.Infrastructure.Options;

/// <summary>Only the Python data-import URL is configured. Other settings are hardcoded defaults.</summary>
public sealed class FormMasterFileImportOptions
{
    public const string SectionName = "FormMasterFileImport";

    /// <summary>POST target for Hangfire master-file import (e.g. https://cloud.ezofis.com/api/ezDataImport).</summary>
    public string? PythonServiceUrl { get; set; }
}

/// <summary>Hardcoded master-file import defaults. URL comes from <see cref="FormMasterFileImportOptions"/>.</summary>
public static class FormMasterFileImportDefaults
{
    public const bool Enabled = true;
    public const bool UseHangfirePython = true;
    public const bool QueueBlobEnabled = false;
    public const string QueueBlobPathPrefix = "ezPackages/MasterExcel";
    public const int TimeoutMinutes = 30;
    public const string NotificationCategory = "WORKFLOW";
}
