using System.Globalization;
using Npgsql;
using SaaSApp.Workflow.Application.Forms;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed partial class FormService
{
    public async Task<FormControlsResult?> GetControlsAsync(string formId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formId))
            return null;

        var normalizedFormId = FormIdNaming.NormalizeFormId(formId);
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, "wFormControl", cancellationToken))
            return null;

        if (!await FormExistsAsync(connection, normalizedFormId, cancellationToken))
            return null;

        var controls = await LoadFormControlsAsync(connection, normalizedFormId, cancellationToken);

        return new FormControlsResult(normalizedFormId, controls.Count, controls);
    }

    private static async Task<bool> FormExistsAsync(
        NpgsqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "wForm", cancellationToken))
            return true;

        const string sql = """
            SELECT 1
            FROM dbo."wForm"
            WHERE id = @FormId AND "isDeleted" = false
            LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", formId);
        return await cmd.ExecuteScalarAsync(cancellationToken) != null;
    }

    private static async Task<List<FormControlItem>> LoadFormControlsAsync(
        NpgsqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                id,
                "wFormId",
                "jsonId",
                name,
                type,
                "isMandatory",
                "parentId",
                "createdAt",
                "modifiedAt",
                "createdBy",
                "modifiedBy",
                "isDeleted",
                "activityBy",
                "activityOn",
                "activityId",
                "validationJson"
            FROM dbo."wFormControl"
            WHERE "wFormId" = @FormId AND "isDeleted" = false
            ORDER BY "parentId", id
            """;

        var controls = new List<FormControlItem>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", formId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            controls.Add(new FormControlItem(
                Id: reader.GetInt32(0),
                WFormId: reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty,
                JsonId: reader.IsDBNull(2) ? null : reader.GetString(2),
                Name: reader.IsDBNull(3) ? null : reader.GetString(3),
                Type: reader.IsDBNull(4) ? null : reader.GetString(4),
                IsMandatory: !reader.IsDBNull(5) && reader.GetBoolean(5),
                ParentId: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                CreatedAt: reader.IsDBNull(7) ? null : Convert.ToString(reader.GetValue(7), CultureInfo.InvariantCulture),
                ModifiedAt: reader.IsDBNull(8) ? null : Convert.ToString(reader.GetValue(8), CultureInfo.InvariantCulture),
                CreatedBy: reader.IsDBNull(9) ? null : Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture),
                ModifiedBy: reader.IsDBNull(10) ? null : Convert.ToString(reader.GetValue(10), CultureInfo.InvariantCulture),
                IsDeleted: !reader.IsDBNull(11) && reader.GetBoolean(11),
                ActivityBy: reader.IsDBNull(12) ? null : Convert.ToString(reader.GetValue(12), CultureInfo.InvariantCulture),
                ActivityOn: reader.IsDBNull(13) ? null : Convert.ToString(reader.GetValue(13), CultureInfo.InvariantCulture),
                ActivityId: reader.IsDBNull(14) ? null : reader.GetInt32(14),
                ValidationJson: reader.IsDBNull(15) ? null : reader.GetString(15)));
        }

        return controls;
    }
}
