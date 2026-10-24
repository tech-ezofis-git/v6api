using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaaSApp.Reporting.Application.Contracts;

public sealed class ReportAiPromptRequest
{
    public Guid? Id { get; set; }
    public string? SessionId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string ReportType { get; set; } = "all_workflows";
    public string? WorkflowName { get; set; }
    public string? RepositoryName { get; set; }
    public string? Name { get; set; }
    public string? Model { get; set; }
}

public sealed class ReportAiRunRequest
{
    /// <summary>1-based page. 0 or omitted becomes 1.</summary>
    public int Page { get; set; } = 1;
    /// <summary>Rows per page. 0 or omitted becomes 25.</summary>
    public int PageSize { get; set; } = 25;
    /// <summary>Optional edited JSON prompt. Omit to use the saved prompt. Do not send the Swagger example "string".</summary>
    public string? ReportPrompt { get; set; }
    /// <summary>Optional edited spec object. Omit to use the saved spec. Do not send the Swagger example "string".</summary>
    public JsonElement ReportSpec { get; set; }
    /// <summary>Optional model override. Omit unless you intend to change the catalog model.</summary>
    public string? Model { get; set; }
}

public sealed class ReportAiUpdateRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? SessionId { get; set; }
    public string? ReportPrompt { get; set; }
    public JsonElement ReportSpec { get; set; }
}

public sealed class ReportAiDefinitionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ReportType { get; set; } = "all_workflows";
    public string? WorkflowName { get; set; }
    public string? RepositoryName { get; set; }
    public string ReportPrompt { get; set; } = string.Empty;
    public string ReportSpecJson { get; set; } = "{}";
    public string Status { get; set; } = "Draft";
    public int Runs { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
}

public sealed class ReportAiDefinitionDto
{
    public Guid Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ReportType { get; set; } = string.Empty;
    public string? WorkflowName { get; set; }
    public string? RepositoryName { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ReportPrompt { get; set; } = string.Empty;
    public JsonElement ReportSpec { get; set; }
    public IReadOnlyList<string> Warnings { get; set; } = [];
    public IReadOnlyList<string>? SchemaTables { get; set; }
    public double? DurationMs { get; set; }
    public string Status { get; set; } = "Draft";
    public int Runs { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
}

public sealed class ReportAiListItemDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ReportType { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public int Runs { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
}

public sealed class ReportAiHttpResult
{
    public int StatusCode { get; init; } = 200;
    public object? Payload { get; init; }

    public static ReportAiHttpResult Ok(object payload) =>
        new() { StatusCode = 200, Payload = payload };

    public static ReportAiHttpResult Error(int statusCode, string error) =>
        new() { StatusCode = statusCode, Payload = new { error } };
}

public sealed class ReportAgentChatRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string Phase { get; set; } = "prompt";
    public Guid TenantId { get; set; }
    public string? Message { get; set; }
    public string? Description { get; set; }
    public string? ReportType { get; set; }
    public string? WorkflowName { get; set; }
    public string? RepositoryName { get; set; }
    public string? ReportPrompt { get; set; }
    public JsonElement ReportSpec { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? Model { get; set; }
}

public sealed class ReportAgentProxyResult
{
    public int StatusCode { get; init; }
    public string RawBody { get; init; } = string.Empty;
    public string? Reply { get; init; }
    public JsonElement ReportResult { get; init; }
    public bool TimedOut { get; init; }

    [JsonIgnore]
    public bool HasReportResult =>
        ReportResult.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
}
