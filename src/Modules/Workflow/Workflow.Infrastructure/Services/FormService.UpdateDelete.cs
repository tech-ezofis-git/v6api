using System.Text.Json;
using Npgsql;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Forms;
using SaaSApp.Workflow.Application.Workflows;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed partial class FormService
{
    public async Task<FormUpdateResult> UpdateFormAsync(
        string formId,
        FormJsonDto formJson,
        string rawJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formId))
            return new FormUpdateResult(FormUpdateStatus.NotFound, null, "Form not found.");

        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        var tenantGuid = _tenantContext.TenantId
            ?? throw new InvalidOperationException("Tenant context is required.");

        var general = formJson.Settings?.General
            ?? throw new InvalidOperationException("settings.general is required.");
        var name = general.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("settings.general.name is required.");

        var publish = formJson.Settings?.Publish;
        var publishOption = publish?.PublishOption?.Trim() ?? "DRAFT";
        var isPublished = publishOption.Equals("PUBLISHED", StringComparison.OrdinalIgnoreCase);

        var userId = _currentUserProvider.GetUserId() ?? throw new InvalidOperationException("User context is required.");
        var modifiedBy = userId.ToString("D");
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureFormSchemaAsync(connection, cancellationToken);

        var storedId = await ResolveStoredFormIdAsync(connection, tenantGuid, formId.Trim(), cancellationToken);
        if (storedId == null)
            return new FormUpdateResult(FormUpdateStatus.NotFound, null, "Form not found.");

        if (await FormNameExistsForOtherFormAsync(connection, tenantGuid, name, storedId, cancellationToken))
            return new FormUpdateResult(FormUpdateStatus.NameConflict, storedId, "form already Exist");

        var qrFields = general.QrFields is { Length: > 0 } ? string.Join(",", general.QrFields) : "";
        var uniqueColumns = general.UniqueColumns is { Length: > 0 } ? string.Join(",", general.UniqueColumns) : "";
        var superUser = general.SuperUser is { Length: > 0 } ? string.Join(",", general.SuperUser) : "";
        var entryUser = general.EntryUser is { Length: > 0 } ? string.Join(",", general.EntryUser) : "";

        await UpdateWFormRowAsync(
            connection,
            storedId,
            general,
            publishOption,
            qrFields,
            uniqueColumns,
            superUser,
            entryUser,
            modifiedBy,
            now,
            cancellationToken);

        var jsonToStore = !string.IsNullOrWhiteSpace(rawJson)
            ? rawJson
            : JsonSerializer.Serialize(formJson, WorkflowJsonSerializerOptions.Storage);
        await _formJsonStorage.SaveFormJsonAsync(storedId, jsonToStore, cancellationToken);

        var securityUserIds = CollectSecurityUserIds(modifiedBy, general.SuperUser, general.EntryUser);
        await RefreshFormSecurityAsync(connection, storedId, securityUserIds, modifiedBy, now, cancellationToken);

        if (isPublished)
        {
            var panels = formJson.Panels ?? new List<FormPanelDto>();
            var secondaryPanels = formJson.SecondaryPanels ?? new List<FormPanelDto>();
            var fields = CollectEntryFields(panels);

            if (fields.Count == 0)
                return new FormUpdateResult(FormUpdateStatus.NotFound, storedId, "Formfields not found");

            var fieldCols = BuildFieldColumnsFromLabels(fields);
            var existingEzfbColumns = await TryLoadEzfbColumnsAsync(connection, storedId, cancellationToken);
            await SyncFormControlsAsync(
                connection, storedId, panels, secondaryPanels, fields, fieldCols, existingEzfbColumns, modifiedBy, now, cancellationToken);
            await EnsureFormEntryTableAsync(connection, storedId, fields, fieldCols, cancellationToken);
        }

        _logger.LogInformation("Updated form {FormId} ({Name}), published={Published}", storedId, name, isPublished);
        return new FormUpdateResult(FormUpdateStatus.Updated, storedId, storedId);
    }

    public async Task<FormDeleteResult> DeleteFormAsync(string formId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formId))
            return new FormDeleteResult(FormDeleteStatus.NotFound, "Form not found.");

        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        var tenantGuid = _tenantContext.TenantId
            ?? throw new InvalidOperationException("Tenant context is required.");

        var userId = _currentUserProvider.GetUserId() ?? throw new InvalidOperationException("User context is required.");
        var modifiedBy = userId.ToString("D");
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, "wForm", cancellationToken))
            return new FormDeleteResult(FormDeleteStatus.NotFound, "Form not found.");

        var storedId = await ResolveStoredFormIdAsync(connection, tenantGuid, formId.Trim(), cancellationToken);
        if (storedId == null)
            return new FormDeleteResult(FormDeleteStatus.NotFound, "Form not found.");

        var rows = await SoftDeleteWFormAsync(connection, tenantGuid, storedId, modifiedBy, now, cancellationToken);
        if (rows == 0)
            return new FormDeleteResult(FormDeleteStatus.NotFound, "Form not found.");

        await using (var cmd = new NpgsqlCommand(
            """UPDATE dbo."wFormControl" SET "isDeleted" = true, "modifiedBy" = @ModifiedBy, "modifiedAt" = @ModifiedAt WHERE "wFormId" = @FormId AND "isDeleted" = false""",
            connection))
        {
            cmd.Parameters.AddWithValue("@FormId", storedId);
            cmd.Parameters.AddWithValue("@ModifiedBy", modifiedBy);
            cmd.Parameters.AddWithValue("@ModifiedAt", now);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = new NpgsqlCommand(
            """UPDATE dbo."wFormSecurity" SET "isDeleted" = true, "modifiedBy" = @ModifiedBy, "modifiedAt" = @ModifiedAt WHERE "wFormId" = @FormId AND "isDeleted" = false""",
            connection))
        {
            cmd.Parameters.AddWithValue("@FormId", storedId);
            cmd.Parameters.AddWithValue("@ModifiedBy", modifiedBy);
            cmd.Parameters.AddWithValue("@ModifiedAt", now);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogInformation("Soft-deleted form {FormId}", storedId);
        return new FormDeleteResult(FormDeleteStatus.Deleted, storedId);
    }

    private static async Task<string?> ResolveStoredFormIdAsync(
        NpgsqlConnection connection,
        Guid tenantGuid,
        string formId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id
            FROM dbo."wForm"
            WHERE id = @Id AND "tenantId" = @TenantId AND "isDeleted" = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", formId);
        cmd.Parameters.AddWithValue("@TenantId", tenantGuid);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result == null || result == DBNull.Value ? null : Convert.ToString(result)?.Trim();
    }

    private static async Task<bool> FormNameExistsForOtherFormAsync(
        NpgsqlConnection connection,
        Guid tenantGuid,
        string name,
        string excludeFormId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM dbo."wForm"
            WHERE "isDeleted" = false AND "tenantId" = @TenantId AND name = @Name AND id <> @ExcludeId;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantGuid);
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@ExcludeId", excludeFormId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task UpdateWFormRowAsync(
        NpgsqlConnection connection,
        string formId,
        FormGeneralDto general,
        string publishOption,
        string qrFields,
        string uniqueColumns,
        string superUser,
        string entryUser,
        string modifiedBy,
        string now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo."wForm"
            SET name = @Name,
                description = @Description,
                type = @Type,
                layout = @Layout,
                "publishOption" = @PublishOption,
                "qrFields" = @QrFields,
                "uniqueColumns" = @UniqueColumns,
                "superUser" = @SuperUser,
                "entryUser" = @EntryUser,
                "modifiedAt" = @ModifiedAt,
                "modifiedBy" = @ModifiedBy,
                "isEdit" = 1
            WHERE id = @Id AND "isDeleted" = false;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", formId);
        cmd.Parameters.AddWithValue("@Name", general.Name!.Trim());
        cmd.Parameters.AddWithValue("@Description", (object?)general.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Type", (object?)general.Type ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Layout", (object?)general.Layout ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PublishOption", publishOption);
        cmd.Parameters.AddWithValue("@QrFields", qrFields);
        cmd.Parameters.AddWithValue("@UniqueColumns", uniqueColumns);
        cmd.Parameters.AddWithValue("@SuperUser", superUser);
        cmd.Parameters.AddWithValue("@EntryUser", entryUser);
        cmd.Parameters.AddWithValue("@ModifiedAt", now);
        cmd.Parameters.AddWithValue("@ModifiedBy", modifiedBy);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> SoftDeleteWFormAsync(
        NpgsqlConnection connection,
        Guid tenantGuid,
        string formId,
        string modifiedBy,
        string now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo."wForm"
            SET "isDeleted" = true, "modifiedAt" = @ModifiedAt, "modifiedBy" = @ModifiedBy
            WHERE id = @Id AND "tenantId" = @TenantId AND "isDeleted" = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", formId);
        cmd.Parameters.AddWithValue("@TenantId", tenantGuid);
        cmd.Parameters.AddWithValue("@ModifiedAt", now);
        cmd.Parameters.AddWithValue("@ModifiedBy", modifiedBy);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RefreshFormSecurityAsync(
        NpgsqlConnection connection,
        string formId,
        List<string> userIds,
        string modifiedBy,
        string now,
        CancellationToken cancellationToken)
    {
        await using (var softDel = new NpgsqlCommand(
            """UPDATE dbo."wFormSecurity" SET "isDeleted" = true, "modifiedBy" = @ModifiedBy, "modifiedAt" = @ModifiedAt WHERE "wFormId" = @FormId AND "isDeleted" = false""",
            connection))
        {
            softDel.Parameters.AddWithValue("@FormId", formId);
            softDel.Parameters.AddWithValue("@ModifiedBy", modifiedBy);
            softDel.Parameters.AddWithValue("@ModifiedAt", now);
            await softDel.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insertSql = """
            INSERT INTO dbo."wFormSecurity"("wFormId", "userId", "createdAt", "createdBy", "isDeleted")
            VALUES(@FormId, @UserId, @CreatedAt, @CreatedBy, false);
            """;

        foreach (var userId in userIds)
        {
            await using var cmd = new NpgsqlCommand(insertSql, connection);
            cmd.Parameters.AddWithValue("@FormId", formId);
            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@CreatedAt", now);
            cmd.Parameters.AddWithValue("@CreatedBy", modifiedBy);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
