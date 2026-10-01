namespace SaaSApp.Workflow.Application.Forms;

/// <summary>Designer form JSON (v5 fformJson parity).</summary>
public sealed class FormJsonDto
{
    public string? Uid { get; set; }
    public List<FormPanelDto>? Panels { get; set; }
    public List<FormPanelDto>? SecondaryPanels { get; set; }
    public FormSettingsDto? Settings { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed class FormSettingsDto
{
    public FormGeneralDto? General { get; set; }
    public FormPublishDto? Publish { get; set; }
}

public sealed class FormGeneralDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Layout { get; set; }
    public string? Type { get; set; }
    public string[]? QrFields { get; set; }
    public string[]? UniqueColumns { get; set; }
    public string[]? SuperUser { get; set; }
    public string[]? EntryUser { get; set; }
}

public sealed class FormPublishDto
{
    public string? PublishOption { get; set; }
    public string? PublishSchedule { get; set; }
}

public sealed class FormPanelDto
{
    public string? Id { get; set; }
    public List<FormFieldDto>? Fields { get; set; }
}

public sealed class FormFieldDto
{
    public string? Id { get; set; }
    public string? Label { get; set; }

    /// <summary>Table columns store the title here and leave <see cref="Label"/> empty.</summary>
    public string? Name { get; set; }

    public string? Type { get; set; }
    public FormFieldSettingsDto? Settings { get; set; }

    /// <summary>
    /// Title stored in wFormControl.name. Prefer a real label, then name.
    /// A value that is only the field id is skipped when the other property has a title.
    /// </summary>
    public string ControlName()
    {
        var id = Id?.Trim();
        var label = Label?.Trim();
        var name = Name?.Trim();

        if (IsDisplayTitle(label, id))
            return label!;
        if (IsDisplayTitle(name, id))
            return name!;
        if (!string.IsNullOrWhiteSpace(label))
            return label!;
        if (!string.IsNullOrWhiteSpace(name))
            return name!;
        return id ?? "";
    }

    private static bool IsDisplayTitle(string? value, string? id) =>
        !string.IsNullOrWhiteSpace(value)
        && (string.IsNullOrWhiteSpace(id) || !string.Equals(value, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class FormFieldSettingsDto
{
    public FormFieldSpecificDto? Specific { get; set; }
    public FormFieldValidationDto? Validation { get; set; }
}

public sealed class FormFieldValidationDto
{
    public string? FieldRule { get; set; }
    public string? DocumentExpiryField { get; set; }
    public string? ContentRule { get; set; }
    public string? Maximum { get; set; }
    public string? Minimum { get; set; }
}

public sealed class FormFieldSpecificDto
{
    public List<FormFieldDto>? TableColumns { get; set; }
    public int MappedPopupPanel { get; set; }
    public string? CustomOptions { get; set; }
}
