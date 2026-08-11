using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure;

internal static class RepositoryItemCursorHelper
{
    public const int MaxPageSize = 500;

    private sealed record CursorPayload(string S, string D, string? V, Guid I);

    public static string Encode(string sortCol, bool ascending, object? sortValue, Guid id)
    {
        var payload = new CursorPayload(
            sortCol,
            ascending ? "asc" : "desc",
            sortValue?.ToString(),
            id);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    }

    public static (string SortCol, bool Ascending, string? SortValue, Guid Id) Decode(string cursor)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var payload = JsonSerializer.Deserialize<CursorPayload>(json)
                ?? throw new ArgumentException("Invalid cursor.");
            if (string.IsNullOrWhiteSpace(payload.S) || payload.I == Guid.Empty)
                throw new ArgumentException("Invalid cursor.");
            var ascending = string.Equals(payload.D, "asc", StringComparison.OrdinalIgnoreCase);
            return (RepositorySqlHelper.SanitizeColumnName(payload.S), ascending, payload.V, payload.I);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("cursor is invalid or corrupted.", ex);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("cursor is invalid or corrupted.", ex);
        }
    }

    public static void ApplyKeysetFilter(
        ICollection<string> where,
        IList<NpgsqlParameter> parameters,
        string sortCol,
        bool ascending,
        string? sortValueRaw,
        Guid lastId)
    {
        var col = $"i.{RepositorySqlHelper.ColumnRef(sortCol)}";
        parameters.Add(new NpgsqlParameter("@CursorId", lastId));

        if (string.IsNullOrEmpty(sortValueRaw))
        {
            where.Add(ascending ? $"i.id > @CursorId" : $"i.id < @CursorId");
            return;
        }

        if (DateTime.TryParse(sortValueRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
        {
            parameters.Add(new NpgsqlParameter("@CursorVal", dt));
            where.Add(ascending
                ? $"({col} > @CursorVal OR ({col} = @CursorVal AND i.id > @CursorId))"
                : $"({col} < @CursorVal OR ({col} = @CursorVal AND i.id < @CursorId))");
            return;
        }

        if (decimal.TryParse(sortValueRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var dec))
        {
            parameters.Add(new NpgsqlParameter("@CursorVal", dec));
            where.Add(ascending
                ? $"({col} > @CursorVal OR ({col} = @CursorVal AND i.id > @CursorId))"
                : $"({col} < @CursorVal OR ({col} = @CursorVal AND i.id < @CursorId))");
            return;
        }

        if (byte.TryParse(sortValueRaw, out var b))
        {
            parameters.Add(new NpgsqlParameter("@CursorVal", (short)b));
            where.Add(ascending
                ? $"({col} > @CursorVal OR ({col} = @CursorVal AND i.id > @CursorId))"
                : $"({col} < @CursorVal OR ({col} = @CursorVal AND i.id < @CursorId))");
            return;
        }

        parameters.Add(new NpgsqlParameter("@CursorVal", sortValueRaw));
        where.Add(ascending
            ? $"({col} > @CursorVal OR ({col} = @CursorVal AND i.id > @CursorId))"
            : $"({col} < @CursorVal OR ({col} = @CursorVal AND i.id < @CursorId))");
    }

    public static object? GetSortValueFromRow(RepositoryItemListDto row, string sortCol) =>
        sortCol switch
        {
            "DocumentDate" => row.DocumentDate,
            "Amount" => row.Amount,
            "FileName" => row.FileName,
            "DocumentType" => row.DocumentType,
            "Supplier" => row.Supplier,
            "InvoiceNumber" => row.InvoiceNumber,
            "PoNumber" => row.PoNumber,
            "Currency" => row.Currency,
            "Status" => row.Status,
            "OcrScore" or "OcrPercent" => row.OcrPercent,
            "AiStatus" => row.AiStatus,
            "RiskLevel" => row.RiskLevel,
            "Source" => row.Source,
            "Department" => row.Department,
            "InvoiceDate" => row.InvoiceDate,
            "InvoiceAmount" => row.InvoiceAmount,
            "InvoiceTaxAmount" => row.InvoiceTaxAmount,
            "PODate" => row.PoDate,
            "POAmount" => row.PoAmount,
            "Buyer" => row.Buyer,
            "Terms" => row.Terms,
            "SupplierAddress" => row.SupplierAddress,
            "ShipToAddress" => row.ShipToAddress,
            "PayToAddress" => row.PayToAddress,
            _ => null
        };
}
