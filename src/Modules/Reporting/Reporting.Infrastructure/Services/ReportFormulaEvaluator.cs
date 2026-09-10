using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SaaSApp.Reporting.Infrastructure.Services;

internal static class ReportFormulaEvaluator
{
    private static readonly Regex Unsafe = new(@"[^0-9+\-*/().\s]", RegexOptions.Compiled);

    public static decimal? TryEvaluate(
        string? formula,
        IReadOnlyDictionary<string, string?> row,
        IReadOnlyList<string> fieldLabels)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return null;

        var expr = formula.Trim();
        foreach (var label in fieldLabels.OrderByDescending(l => l.Length))
        {
            if (string.IsNullOrWhiteSpace(label))
                continue;
            row.TryGetValue(label, out var raw);
            var token = ToNumberToken(raw);
            expr = expr.Replace($"[{label}]", token, StringComparison.OrdinalIgnoreCase);
            expr = expr.Replace($"{{{label}}}", token, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var kv in row.OrderByDescending(x => x.Key.Length))
        {
            var token = ToNumberToken(kv.Value);
            expr = expr.Replace($"[{kv.Key}]", token, StringComparison.OrdinalIgnoreCase);
            expr = expr.Replace($"{{{kv.Key}}}", token, StringComparison.OrdinalIgnoreCase);
        }

        if (Unsafe.IsMatch(expr))
            return null;

        try
        {
            var result = new DataTable().Compute(expr, null);
            if (result is null or DBNull)
                return null;
            return Convert.ToDecimal(result, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    public static decimal? TryParseNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var trimmed = raw.Trim().Replace(",", "", StringComparison.Ordinal);
        if (decimal.TryParse(trimmed, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var n))
            return n;
        if (decimal.TryParse(trimmed, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.CurrentCulture, out n))
            return n;
        return null;
    }

    private static string ToNumberToken(string? raw)
    {
        var n = TryParseNumber(raw);
        return n.HasValue ? n.Value.ToString(CultureInfo.InvariantCulture) : "0";
    }
}
