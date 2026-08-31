namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>Migrates legacy integer ezfb item_id PKs and workflow form_entry_id columns to uuid.</summary>
public interface IEzfbEntryIdMigrationService
{
    Task<EzfbEntryIdMigrationResult> MigrateTenantAsync(
        string tenantConnectionString,
        CancellationToken cancellationToken = default);
}

public sealed record EzfbEntryIdMigrationResult(
    int TablesMigrated,
    int RowsMapped,
    int ProcessFormRowsUpdated,
    int WorkflowFormRowsUpdated,
    IReadOnlyList<string> Messages);
