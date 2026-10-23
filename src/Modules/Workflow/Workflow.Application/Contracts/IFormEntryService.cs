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
    /// Load form entries whose given column equals the given value, instead of requiring the
    /// caller to already know the item_id GUID. <paramref name="columnName"/> may be the physical
    /// ezfb column name, the control's label, or (legacy forms) its jsonId -- resolved the same
    /// way as <see cref="UpsertEntryAsync(string, Guid, FormEntryUpsertRequest, CancellationToken)"/>.
    /// </summary>
    Task<FormEntryByColumnResult> GetEntriesByColumnAsync(
        string formId,
        string columnName,
        string columnValue,
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
}
