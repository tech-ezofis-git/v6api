using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaaSApp.Workflow.Application.Workflows.Commands.CreateWorkflow;

/// <summary>Complete workflow JSON structure from source API.</summary>
public record WorkflowJsonDto(
    WorkflowSettingsDto? Settings = null,
    List<WorkflowBlockDto>? Blocks = null,
    List<WorkflowConnectionDto>? Rules = null,
    string[]? MlPredictions = null,
    int? HasSLASettings = null,  // 0 or 1 (number in source)
    string? InitiateRights = null,
    string[]? InitiateUserDomain = null,
    string[]? ModifiedBlockIds = null,
    int? BlockStatus = null,  // Number in source
    WorkflowOcrDto? Ocr = null,
    int? WorkspaceId = null,
    string[]? MasterFormIds = null  // Array of master form IDs
);

public record WorkflowSettingsDto(
    WorkflowGeneralDto? General = null,
    WorkflowPublishDto? Publish = null,
    /// <summary>PO Master for AP Agent (works for DOCUMENT / DOCUMENT_FORM without email mailbox).</summary>
    WorkflowPoMasterDto? PoMaster = null
);

/// <summary>PO / vendor master binding mirrored from mailbox masterSource fields.</summary>
public record WorkflowPoMasterDto(
    string? MasterSource = null,
    string? MasterConnectorId = null,
    string? MasterFormId = null
);

public record WorkflowGeneralDto(
    string? Name = null,
    string? Description = null,
    WorkflowInitiateUsingDto? InitiateUsing = null,
    string[]? Coordinator = null,
    string[]? Superuser = null,
    WorkflowSlaSettingsDto? SlaSettings = null,
    List<WorkflowKanbanSettingsDto>? KanbanSettings = null,
    WorkflowScheduleReportDto? ScheduleReport = null,
    string? ProcessNumberPrefix = null,
    List<WorkflowSlaRuleDto>? SlaRules = null,
    WorkflowOcrDto? Ocr = null  // OCR can be at general level too
);

public record WorkflowInitiateUsingDto(
    string Type,  // "FORM", "DOCUMENT", "DOCUMENT_FORM", "EMAIL"
    [property: JsonConverter(typeof(FlexibleRepositoryIdJsonConverter))]
    FlexibleRepositoryId? RepositoryId = null,
    [property: JsonConverter(typeof(FlexibleWorkflowIdJsonConverter))]
    FlexibleWorkflowId? FormId = null
);

public record WorkflowPublishDto(
    string PublishOption,  // "PUBLISHED", "DRAFT"
    string? PublishSchedule = null,
    string? UnpublishSchedule = null
);

public record WorkflowSlaSettingsDto(
    string[]? WorkDays = null,
    WorkflowWorkHoursDto? WorkHours = null,
    string? TimeZone = null
);

public record WorkflowWorkHoursDto(
    string? From = null,
    string? To = null
);

public record WorkflowKanbanSettingsDto(
    string? Name = null,
    string? Color = null
);

public record WorkflowScheduleReportDto(
    bool? Enabled = null,
    string? Schedule = null
);

public record WorkflowSlaRuleDto(
    int Id = 0,
    string? Name = null,
    int? Duration = null,
    string? DurationType = null,
    int? Level = null,
    string[]? Users = null,
    string? Action = null,
    string? MasterFormId = null,
    string? FieldId = null,
    string? SettingsJson = null
);

public record WorkflowBlockDto(
    string Id,
    string Type,  // "START", "END", "APPROVAL", "RULES", "WORKFLOW_CONNECTOR", etc.
    WorkflowBlockSettingsDto Settings,
    // UI positioning fields (optional, for frontend)
    double? Left = null,
    double? Top = null,
    double? Width = null,
    double? Height = null,
    string? Color = null,
    string? Icon = null
);

public record WorkflowBlockSettingsDto(
    string? Label = null,
    string? InitiateMode = null,
    string[]? InitiateBy = null,
    int? MasterFormId = null,
    string[]? Users = null,
    string[]? Groups = null,
    List<WorkflowSlaRuleDto>? SlaRules = null,
    WorkflowFileSettingsDto? FileSettings = null,
    WorkflowMailInitiateDto? MailInitiate = null,
    int? SubWorkflowId = null,
    WorkflowOcrAgentDto? OcrAgent = null,
    WorkflowAiPredictionDto? AiPrediction = null,
    WorkflowRuleConditionsDto? RuleConditions = null,
    WorkflowConditionsDto? Conditions = null,
    bool? MlCompareMaster = null,
    int? MlMasterFormId = null,
    List<WorkflowMlCompareFieldDto>? MlCompareFields = null,
    string? MlPredictionField = null,
    [property: JsonPropertyName("generatePDF")]
    bool? GeneratePDF = null,
    [property: JsonPropertyName("pdfTemplate")]
    JsonElement? PdfTemplate = null,
    [property: JsonPropertyName("generatePDFFields")]
    string[]? GeneratePDFFields = null
);

public record WorkflowFileSettingsDto(
    List<WorkflowFileChecklistDto> FileCheckList
);

public record WorkflowFileChecklistDto(
    string? Name = null,
    string? Type = null
);

public record WorkflowMailInitiateDto(
    [property: JsonConverter(typeof(FlexibleWorkflowIdJsonConverter))]
    FlexibleWorkflowId? ConnectorId = null,
    string? Review = null
);

public record WorkflowOcrAgentDto(
    int? OcrPromptId = null,
    string? OcrSpecificInstruction = null,
    string? PromptSchema = null
);

public record WorkflowAiPredictionDto(
    string[]? PredictionFields = null
);

public record WorkflowRuleConditionsDto(
    string? Name = null,
    string? GroupLogic = null,  // "ALL", "ANY"
    List<WorkflowRuleDto>? Conditions = null
);

public record WorkflowRuleDto(
    string? Name = null,
    string? Logic = null,  // "IS_EQUALS_TO", "IS_GREATER_THAN", etc.
    string? Value = null
);

public record WorkflowConditionsDto(
    string? GroupLogic = null,
    List<WorkflowConditionDto>? Condition = null
);

public record WorkflowConditionDto(
    string? Name = null,
    string? Logic = null,
    string? Value = null
);

public record WorkflowMlCompareFieldDto(
    string? Id = null,
    string? MasterField = null,
    string? WorkflowFormField = null
);

public record WorkflowConnectionDto(
    string Id,
    string FromBlockId,
    string ToBlockId,
    string? ProceedAction = null,
    // UI positioning fields (optional, for frontend)
    double? Left = null,
    double? Top = null
);

public record WorkflowOcrDto(
    bool? Required = null,
    int? Credit = null,  // Matches source API field name
    int? CreditLimit = null  // Alternative name
);

