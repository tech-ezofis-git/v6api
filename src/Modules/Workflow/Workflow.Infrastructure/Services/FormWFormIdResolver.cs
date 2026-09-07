using System.Globalization;
using Npgsql;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>Resolves dbo.wFormControl.wFormId values (GUID, 8-char prefix, or legacy INT).</summary>
internal static class FormWFormIdResolver
{
    internal static async Task<IReadOnlyList<object>> BuildCandidatesAsync(
        NpgsqlConnection connection,
        string normalizedFormId,
        CancellationToken cancellationToken)
    {
        var candidates = new List<object>();
        var primary = await ResolvePrimaryAsync(connection, normalizedFormId, cancellationToken);
        AddUnique(candidates, primary);

        var wFormIdType = await GetWFormIdColumnTypeAsync(connection, cancellationToken);
        if (!IsNumericSqlType(wFormIdType))
        {
            try
            {
                AddUnique(candidates, FormIdNaming.GetEzfbTableSuffix(normalizedFormId));
            }
            catch (InvalidOperationException)
            {
                // ignore invalid form id for suffix derivation
            }

            if (Guid.TryParse(normalizedFormId, out var guid))
            {
                AddUnique(candidates, guid.ToString("D").ToLowerInvariant());
                AddUnique(candidates, guid.ToString("D").ToUpperInvariant());
                AddUnique(candidates, guid.ToString("N"));
                AddUnique(candidates, guid.ToString("N")[..FormIdNaming.EzfbTableSuffixLength]);
            }
        }

        return candidates;
    }

    internal static async Task<object> ResolvePrimaryAsync(
        NpgsqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        var wFormIdType = await GetWFormIdColumnTypeAsync(connection, cancellationToken);
        if (IsNumericSqlType(wFormIdType))
        {
            if (int.TryParse(formId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n;

            var hex = new string(formId.Where(Uri.IsHexDigit).ToArray());
            if (hex.Length > FormIdNaming.EzfbTableSuffixLength)
                hex = hex[..FormIdNaming.EzfbTableSuffixLength];

            if (uint.TryParse(hex.PadLeft(FormIdNaming.EzfbTableSuffixLength, '0'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u))
                return unchecked((int)u);
        }

        return formId;
    }

    private static async Task<string?> GetWFormIdColumnTypeAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        // Postgres tenants use quoted dbo."wFormControl"."wFormId" (typically varchar).
        const string sql = """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = 'dbo' AND table_name = 'wFormControl' AND column_name = 'wFormId'
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        return (await cmd.ExecuteScalarAsync(cancellationToken))?.ToString()?.ToLowerInvariant();
    }

    private static bool IsNumericSqlType(string? type) =>
        type is "int" or "bigint" or "smallint" or "tinyint" or "integer";

    private static void AddUnique(List<object> candidates, object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (candidates.Any(existing =>
                string.Equals(
                    Convert.ToString(existing, CultureInfo.InvariantCulture),
                    text,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        candidates.Add(value);
    }
}
