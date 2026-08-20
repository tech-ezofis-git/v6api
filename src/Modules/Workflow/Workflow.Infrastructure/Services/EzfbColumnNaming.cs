namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Maps designer fields to dbo.ezfb_* column names.
///
/// Two naming eras coexist by design (columns are never migrated between them):
///   - OLD forms (created before the Label-column change): column = sanitized jsonId
///     (designer field.Id, a nanoid). See <see cref="ToColumnName"/>.
///   - NEW forms (created after): column = sanitized field Label ("PO Number" -&gt; "PO_Number").
///     See <see cref="ToColumnNameFromLabel"/>. wFormControl.jsonId is still written for every
///     form (old and new) -- the designer needs it as a stable field id regardless of which
///     naming era the table itself uses.
///
/// <see cref="TryResolveEzfbColumn(string?, string?, IReadOnlySet{string}, out string)"/> is the
/// single shared lookup every read/write path (FormEntryService, WorkflowEzfbFormDataLoader,
/// WorkflowApAgentMoveNextService, WorkflowTicketSearchService) should call instead of hand-rolling
/// jsonId-only resolution: it tries the sanitized Name/Label first (matches new-form columns),
/// then falls back through the legacy jsonId-based chain (matches old-form columns) so both eras
/// resolve correctly against the same code path with no table migration required.
/// </summary>
public static class EzfbColumnNaming
{
    /// <summary>jsonId → SQL column name: letters, digits, underscore, hyphen (matches designer field id).</summary>
    public static string ToColumnName(string jsonId)
    {
        var safe = new string(jsonId.Where(static c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        if (string.IsNullOrEmpty(safe))
            throw new ArgumentException($"Invalid jsonId for ezfb column: {jsonId}");

        return safe;
    }

    public static bool TryToColumnName(string jsonId, out string column)
    {
        try
        {
            column = ToColumnName(jsonId);
            return true;
        }
        catch (ArgumentException)
        {
            column = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Label → SQL column name for NEW forms: letters/digits/underscore kept, any run of
    /// whitespace collapsed to a single underscore, everything else (/, -, unicode punctuation, ...)
    /// dropped. "PO Number" -&gt; "PO_Number", "G/L Account" -&gt; "GL_Account". Leading digit gets an
    /// "F_" prefix (same convention as the legacy jsonId path / repository custom columns) so the
    /// result is always a safe, quoted-or-unquoted-safe SQL identifier.
    /// </summary>
    public static string ToColumnNameFromLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Field label is required.");

        var chars = new List<char>(label.Length);
        var lastWasUnderscore = false;
        foreach (var c in label.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                chars.Add(c);
                lastWasUnderscore = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (chars.Count > 0 && !lastWasUnderscore)
                {
                    chars.Add('_');
                    lastWasUnderscore = true;
                }
            }
            // anything else (/, -, unicode punctuation, etc.) is dropped, not converted.
        }

        while (chars.Count > 0 && chars[^1] == '_')
            chars.RemoveAt(chars.Count - 1);

        if (chars.Count == 0)
            throw new ArgumentException($"Invalid label for ezfb column: {label}");

        var cleaned = new string(chars.ToArray());
        if (char.IsDigit(cleaned[0]))
            cleaned = "F_" + cleaned;

        return cleaned;
    }

    public static bool TryToColumnNameFromLabel(string label, out string column)
    {
        try
        {
            column = ToColumnNameFromLabel(label);
            return true;
        }
        catch (ArgumentException)
        {
            column = string.Empty;
            return false;
        }
    }

    /// <summary>Bracket-escaped column name for dynamic SQL.</summary>
    public static string ToSqlBracketIdentifier(string jsonId) =>
        ToColumnName(jsonId).Replace("]", "]]", StringComparison.Ordinal);

    /// <summary>How <see cref="TryResolveEzfbColumn(string?, string?, IReadOnlySet{string}, out string, out EzfbColumnMatchKind)"/> matched a column.</summary>
    public enum EzfbColumnMatchKind
    {
        None,
        ExactName,
        SanitizedName,
        ExactJsonId,
        SanitizedJsonId,
        LegacyPrefixedJsonId
    }

    /// <summary>
    /// Shared dual-era resolver. Tries, in order: exact Name, sanitized Name (new-form Label
    /// columns), exact jsonId, sanitized jsonId, legacy "F_"+jsonId (old-form columns). Callers
    /// that only have a jsonId (no control/Name in scope) can omit <paramref name="name"/>.
    /// </summary>
    public static bool TryResolveEzfbColumn(
        string? name,
        string? jsonId,
        IReadOnlySet<string> ezfbColumns,
        out string column,
        out EzfbColumnMatchKind matchKind)
    {
        column = string.Empty;
        matchKind = EzfbColumnMatchKind.None;

        if (!string.IsNullOrWhiteSpace(name))
        {
            var trimmedName = name.Trim();
            if (ezfbColumns.Contains(trimmedName))
            {
                column = trimmedName;
                matchKind = EzfbColumnMatchKind.ExactName;
                return true;
            }

            if (TryToColumnNameFromLabel(trimmedName, out var fromName) && ezfbColumns.Contains(fromName))
            {
                column = fromName;
                matchKind = EzfbColumnMatchKind.SanitizedName;
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(jsonId))
            return false;

        var trimmedJsonId = jsonId.Trim();
        if (ezfbColumns.Contains(trimmedJsonId))
        {
            column = trimmedJsonId;
            matchKind = EzfbColumnMatchKind.ExactJsonId;
            return true;
        }

        if (TryToColumnName(trimmedJsonId, out var fromJsonId) && ezfbColumns.Contains(fromJsonId))
        {
            column = fromJsonId;
            matchKind = EzfbColumnMatchKind.SanitizedJsonId;
            return true;
        }

        if (TryToColumnName(trimmedJsonId, out var baseName)
            && baseName.Length > 0
            && char.IsDigit(baseName[0]))
        {
            var legacy = "F_" + baseName;
            if (ezfbColumns.Contains(legacy))
            {
                column = legacy;
                matchKind = EzfbColumnMatchKind.LegacyPrefixedJsonId;
                return true;
            }
        }

        return false;
    }

    /// <summary>Convenience overload for callers that don't need the match-kind detail.</summary>
    public static bool TryResolveEzfbColumn(string? name, string? jsonId, IReadOnlySet<string> ezfbColumns, out string column) =>
        TryResolveEzfbColumn(name, jsonId, ezfbColumns, out column, out _);

    /// <summary>Backward-compatible overload for call sites that only have a jsonId (no Name/control in scope).</summary>
    public static bool TryResolveEzfbColumn(string jsonId, IReadOnlySet<string> ezfbColumns, out string column) =>
        TryResolveEzfbColumn(null, jsonId, ezfbColumns, out column, out _);
}
