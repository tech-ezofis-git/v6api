using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Api.Services;

public interface IFtlCatalogService
{
    Task<FtlCatalogQueryResult> QueryAsync(FtlCatalogQueryRequest request, CancellationToken cancellationToken);
}

public sealed class FtlCatalogQueryRequest
{
    public string? ProductCode { get; set; }
}

public sealed class FtlCatalogQueryResult
{
    public string Mode { get; init; } = "codes";
    public IReadOnlyList<string> ProductCodes { get; init; } = [];
    public FtlCatalogProductDto? Product { get; init; }
}

public sealed class FtlCatalogProductDto
{
    public Guid Id { get; init; }
    public string ProductCode { get; init; } = string.Empty;
    public string? Category { get; init; }
    public string? Type { get; init; }
    public string? Oem { get; init; }
    public string? DoorHand { get; init; }
    public decimal? DoorWidth { get; init; }
    public string? Description { get; init; }
    public decimal? UnitPrice { get; init; }
    public string? Currency { get; init; }
    public string? SearchText { get; init; }
    public JsonElement? RawMetadata { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

public sealed class FtlCatalogService : IFtlCatalogService
{
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;

    public FtlCatalogService(IDbContextFactory<CatalogDbContext> catalogFactory)
    {
        _catalogFactory = catalogFactory;
    }

    public async Task<FtlCatalogQueryResult> QueryAsync(FtlCatalogQueryRequest request, CancellationToken cancellationToken)
    {
        var productCode = request.ProductCode?.Trim();
        var listCodes = string.IsNullOrWhiteSpace(productCode)
            || string.Equals(productCode, "all", StringComparison.OrdinalIgnoreCase);

        await using var db = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        if (listCodes)
            return new FtlCatalogQueryResult { Mode = "codes", ProductCodes = await LoadProductCodesAsync(connection, cancellationToken) };

        var product = await LoadProductAsync(connection, productCode!, cancellationToken);
        if (product is null)
            throw new InvalidOperationException($"Product '{productCode}' was not found in the FTL catalog.");

        return new FtlCatalogQueryResult { Mode = "details", Product = product };
    }

    private static async Task<IReadOnlyList<string>> LoadProductCodesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT product_code
            FROM public.ftl_catalog
            WHERE product_code IS NOT NULL AND BTRIM(product_code) <> ''
            ORDER BY product_code ASC
            """;
        var codes = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            codes.Add(reader.GetString(0));
        return codes;
    }

    private static async Task<FtlCatalogProductDto?> LoadProductAsync(
        NpgsqlConnection connection,
        string productCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, product_code, category, type, oem, door_hand, door_width,
                   description, unit_price, currency, search_text, raw_metadata,
                   created_at, updated_at
            FROM public.ftl_catalog
            WHERE LOWER(BTRIM(product_code)) = LOWER(BTRIM(@ProductCode))
            LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add(new NpgsqlParameter("@ProductCode", NpgsqlDbType.Text) { Value = productCode });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new FtlCatalogProductDto
        {
            Id = reader.GetGuid(0),
            ProductCode = reader.GetString(1),
            Category = ReadString(reader, 2),
            Type = ReadString(reader, 3),
            Oem = ReadString(reader, 4),
            DoorHand = ReadString(reader, 5),
            DoorWidth = ReadDecimal(reader, 6),
            Description = ReadString(reader, 7),
            UnitPrice = ReadDecimal(reader, 8),
            Currency = ReadString(reader, 9),
            SearchText = ReadString(reader, 10),
            RawMetadata = ReadJson(reader, 11),
            CreatedAt = ReadDateTime(reader, 12),
            UpdatedAt = ReadDateTime(reader, 13)
        };
    }

    private static string? ReadString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static decimal? ReadDecimal(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static DateTime? ReadDateTime(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    private static JsonElement? ReadJson(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var raw = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
}
