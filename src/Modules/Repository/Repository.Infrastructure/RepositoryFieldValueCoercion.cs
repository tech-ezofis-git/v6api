using Npgsql;
using NpgsqlTypes;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure;

/// <summary>
/// Converts a repository custom field's raw string value (CreateRepositoryItemRequest.FieldValues /
/// UpdateItemMetadata's metadata dictionary are both IReadOnlyDictionary&lt;string,string&gt;) into a
/// CLR value matching the field's actual Postgres column type, before binding as an NpgsqlParameter.
/// Required because Npgsql infers a parameter's wire type from its CLR type: a plain C# string always
/// binds as `text`, and unlike SQL Server, Postgres will not implicitly cast a `text` parameter into a
/// `boolean` (or numeric/date) column on INSERT/UPDATE -- confirmed empirically (42804 "column is of
/// type boolean but expression is of type text") when a Boolean-typed custom field was inserted without
/// this conversion. Field DataType -> Postgres type mapping mirrors RepositorySqlHelper.MapDataTypeToSql
/// exactly, so a field created as e.g. "Boolean" here is parsed the same way its column was typed there.
/// </summary>
internal static class RepositoryFieldValueCoercion
{
    /// <summary>
    /// Looks up <paramref name="physicalColumn"/> in the repository's own field definitions (by
    /// SqlColumnName) and parses <paramref name="rawValue"/> accordingly. Returns null if the column
    /// isn't a known custom field (e.g. a core/reserved column) -- callers keep their own handling for
    /// those. A value that fails to parse for its declared type is passed through as the raw string
    /// (Postgres will then raise its own clear type-mismatch error rather than this silently guessing).
    /// </summary>
    public static object? TryCoerce(IReadOnlyList<RepositoryFieldDto> fields, string physicalColumn, string rawValue)
    {
        var field = fields.FirstOrDefault(f => string.Equals(f.SqlColumnName, physicalColumn, StringComparison.OrdinalIgnoreCase));
        if (field == null)
            return null;

        return (field.DataType ?? "text").Trim().ToLowerInvariant() switch
        {
            "number" or "decimal" or "amount" => decimal.TryParse(rawValue, out var dec) ? dec : (object)rawValue,
            "int" or "integer" => int.TryParse(rawValue, out var i) ? i : (object)rawValue,
            "date" or "datetime" => DateTime.TryParse(rawValue, out var dt) ? dt.Date : (object)rawValue,
            "bit" or "bool" or "boolean" => TryParseBoolean(rawValue, out var b) ? b : (object)rawValue,
            _ => rawValue
        };
    }

    /// <summary>Accepts "true"/"false" (any casing) plus the "1"/"0" convention some legacy clients send.</summary>
    private static bool TryParseBoolean(string rawValue, out bool value)
    {
        if (bool.TryParse(rawValue, out value))
            return true;
        if (rawValue == "1") { value = true; return true; }
        if (rawValue == "0") { value = false; return true; }
        value = default;
        return false;
    }

    /// <summary>Builds a typed NpgsqlParameter from a coerced value (falls back to plain string binding when uncoerced).</summary>
    public static NpgsqlParameter CreateParameter(string name, object? coercedOrRaw)
    {
        return coercedOrRaw switch
        {
            decimal d => new NpgsqlParameter(name, NpgsqlDbType.Numeric) { Value = d, Precision = 18, Scale = 2 },
            int i => new NpgsqlParameter(name, NpgsqlDbType.Integer) { Value = i },
            DateTime dt => new NpgsqlParameter(name, NpgsqlDbType.Date) { Value = dt },
            bool b => new NpgsqlParameter(name, NpgsqlDbType.Boolean) { Value = b },
            null => new NpgsqlParameter(name, DBNull.Value),
            _ => new NpgsqlParameter(name, coercedOrRaw)
        };
    }
}
