using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaaSApp.Reporting.Application.Contracts;

public sealed class ReportBuilderConfig
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Workflow name (optional). Form data comes from Source Form, not this.</summary>
    public string Domain { get; set; } = string.Empty;
    /// <summary>Workflow | Repository | Form</summary>
    public string? SourceType { get; set; } = "Workflow";
    /// <summary>Selected form name from the Source Form dropdown (e.g. Accounts Payable Form).</summary>
    public string? SourceForm { get; set; }
    /// <summary>dbo.wForm.id for the selected Source Form.</summary>
    public string? SourceFormId { get; set; }
    public Guid? WorkflowId { get; set; }
    /// <summary>Alias used by the UI for the workflow id. Copied into <see cref="WorkflowId"/> on save.</summary>
    public Guid? SourceId { get; set; }
    public List<string> Fields { get; set; } = [];
    public Dictionary<string, ReportFieldSetting> FieldSettings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<ReportFilter> Filters { get; set; } = [];
    public List<ReportCustomField> CustomFields { get; set; } = [];
    public ReportScheduleConfig? Schedule { get; set; }
    public bool Scheduled { get; set; }
    public List<string> SharedGroups { get; set; } = [];
    public List<string> SharedUsers { get; set; } = [];
    public string Status { get; set; } = ReportStatuses.Draft;
    public string? Visibility { get; set; } = "Private";
    public string? Owner { get; set; }
    public Guid? OwnerUserId { get; set; }
    public int Runs { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? Modified { get; set; }
    /// <summary>True for owner and Admin. Shared users can view/run only.</summary>
    public bool CanEdit { get; set; }
    /// <summary>owner | admin | shared</summary>
    public string? Access { get; set; }
}

public sealed class ReportFieldSetting
{
    public string? Label { get; set; }
    public int? Width { get; set; }
    /// <summary>None | Sum | Avg | Count | Min | Max</summary>
    public string? Calc { get; set; } = "None";
    /// <summary>value | calculated</summary>
    public string? ColType { get; set; } = "value";
    public string? Formula { get; set; }
    public string? Expression { get; set; }
}

public sealed class ReportFilter
{
    public string? Id { get; set; }
    public string? Field { get; set; }
    public string? FieldId { get; set; }
    public string? Operator { get; set; }
    [JsonPropertyName("op")]
    public string? Op { get; set; }
    public JsonElement Value { get; set; }
}

public sealed class ReportCustomField
{
    public string? Id { get; set; }
    public string? Label { get; set; }
    public string? Formula { get; set; }
    public string? Expression { get; set; }
    public string? Calc { get; set; } = "None";
    public string? ColType { get; set; } = "calculated";
    public int? Width { get; set; }
}

public sealed class ReportScheduleConfig
{
    public string? Recurrence { get; set; }
    public string? Day { get; set; }
    public string? Time { get; set; }
    public string? Timezone { get; set; } = "UTC";
    public string? Format { get; set; } = "PDF";
    public List<string> Recipients { get; set; } = [];
    public List<string> Cc { get; set; } = [];
    public string? Subject { get; set; }
    public string? Message { get; set; }
}

public static class ReportStatuses
{
    public const string Draft = "Draft";
    public const string Published = "Published";
}

public sealed record ReportListItemDto(
    Guid Id,
    string Name,
    string Domain,
    string Status,
    bool Scheduled,
    string Owner,
    int Runs,
    DateTime? Modified,
    string? SourceType,
    string? SourceFormId,
    Guid? WorkflowId,
    string? Visibility,
    bool CanEdit,
    string Access,
    IReadOnlyList<string> SharedUsers);

public sealed record ReportDomainDto(
    Guid WorkflowId,
    string Name,
    string? FormId,
    string? Description);

/// <summary>Source Form dropdown: dbo.wForm id + name. FormId builds dbo.ezfb_{first8}_items.</summary>
public sealed record ReportSourceFormDto(
    string FormId,
    string FormName,
    string? Type);

public sealed record ReportAvailableFieldDto(
    string Id,
    string Label,
    string? Type,
    bool IsMandatory);

public sealed record ReportColumnDto(
    string Key,
    string Label,
    string? Calc,
    string ColType,
    int Width,
    bool IsCalculated);

public sealed record ReportRunResult(
    Guid? ReportId,
    string Name,
    string Domain,
    IReadOnlyList<ReportColumnDto> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows,
    IReadOnlyDictionary<string, string?>? Totals,
    int RowCount,
    Guid? WorkflowId,
    string? SourceFormId);

public sealed record ReportFileResult(
    byte[] Content,
    string ContentType,
    string FileName);

public sealed class ReportListQuery
{
    public string? Domain { get; set; }
    public string? Search { get; set; }
    public string? Status { get; set; }
    public bool? Scheduled { get; set; }
}

public sealed class SaveReportRequest
{
    public ReportBuilderConfig Config { get; set; } = new();
}

public static class ReportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static ReportBuilderConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ReportBuilderConfig();
        return JsonSerializer.Deserialize<ReportBuilderConfig>(json, Options) ?? new ReportBuilderConfig();
    }

    public static string Serialize(ReportBuilderConfig config) =>
        JsonSerializer.Serialize(config, Options);
}
