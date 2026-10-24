using System.Text.Json;
using SaaSApp.Workflow.Application.Forms;

namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>v5-compatible form entry CRUD on dbo.ezfb_{form}_items.</summary>
public interface IFormEntryService
{
    /// <summary>
    /// Add (<paramref name="entryId"/> = empty Guid) or update an ezfb form entry row.
    /// Returns v5 result codes: 1=created, 2=updated, 3=duplicate, 0=failed.
    /// </summary>
    Task<FormEntryResult> UpsertEntryAsync(
        string formId,
        Guid entryId,
        FormEntryUpsertRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Parse v5-style body and upsert.</summary>
    Task<FormEntryResult> UpsertEntryAsync(
        string formId,
        Guid entryId,
        JsonElement body,
        CancellationToken cancellationToken = default);

    /// <summary>Get one or more entries by itemId (comma-separated supported like v5).</summary>
    Task<FormEntryGetResult> GetEntriesAsync(
        string formId,
        string entryIds,
        CancellationToken cancellationToken = default);

    /// <summary>List form entries for a form (v5 POST /api/form/{id}/entry/all).</summary>
    Task<FormEntryAllResult> ListEntriesAsync(
        string formId,
        FormEntryAllRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Get distinct non-empty values for a form control from ezfb_{form}_items.</summary>
    Task<FormControlDistinctValuesResult> GetDistinctControlValuesAsync(
        string wFormId,
        string wFormControlName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Load form entries by column. Target key is <paramref name="columnName"/> and/or
    /// <paramref name="columnId"/> (physical ezfb column, control label, or jsonId).
    /// When <paramref name="columnValue"/> is omitted/blank, returns distinct non-empty values for
    /// that column in <see cref="FormEntryByColumnResult.Values"/> (AND-narrowed by
    /// <paramref name="filters"/> when provided). When a value is supplied, returns matching entry
    /// rows in <see cref="FormEntryByColumnResult.Entries"/> (also AND-narrowed by filters).
    /// </summary>
    Task<FormEntryByColumnResult> GetEntriesByColumnAsync(
        string formId,
        string? columnName,
        string? columnValue = null,
        IReadOnlyList<FormEntryColumnFilter>? filters = null,
        string? columnId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the form entry whose given column equals the given value. Returns
    /// <see cref="FormEntryByColumnStatus.InvalidRequest"/> when zero or more than one entry
    /// matches, so an ambiguous lookup never silently updates the wrong (or several) records.
    /// </summary>
    Task<FormEntryByColumnResult> UpdateEntriesByColumnAsync(
        string formId,
        string columnName,
        string columnValue,
        FormEntryUpsertRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Master-form insert/update by column names and values. <paramref name="request"/>.<c>Fields</c>
    /// keys may be control label, jsonId, or physical ezfb column. Optional match column/filters:
    /// one matching row → update; none → insert; more than one → invalid. No match key → insert.
    /// </summary>
    Task<FormEntryByColumnResult> UpsertEntryByColumnsAsync(
        string formId,
        FormEntryUpsertByColumnsRequest request,
        CancellationToken cancellationToken = default);
}
