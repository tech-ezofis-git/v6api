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

    /// <summary>
    /// Full or partial product code. <c>SGV2(1S)_DP_MAC36_LH</c> also returns every product whose code contains <c>SGV2</c>.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("searchKey")]
    public string? SearchKey { get; set; }
}

public sealed class FtlCatalogQueryResult
{
    public string Mode { get; init; } = "codes";
    public IReadOnlyList<string> ProductCodes { get; init; } = [];
    public IReadOnlyList<FtlCatalogProductDto> Products { get; init; } = [];
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
        var search = request.SearchKey?.Trim();
        var selectedProduct = !string.IsNullOrWhiteSpace(productCode)
            && !string.Equals(productCode, "all", StringComparison.OrdinalIgnoreCase);

        await using var db = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        if (selectedProduct)
        {
            var exact = await LoadProductsAsync(connection, null, productCode, cancellationToken);
            if (exact.Count == 0)
                throw new InvalidOperationException($"Product '{productCode}' was not found in the FTL catalog.");

            return new FtlCatalogQueryResult { Mode = "details", Products = exact };
        }

        var match = search;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var exactCode = await LoadProductsAsync(connection, null, search, cancellationToken);
            if (exactCode.Count == 1)
                match = RelatedToken(search);
        }

        var codes = await LoadProductCodesAsync(connection, match, cancellationToken);
        return new FtlCatalogQueryResult
        {
            Mode = string.IsNullOrWhiteSpace(search) ? "codes" : "search",
            ProductCodes = codes
        };
    }

    private static async Task<IReadOnlyList<string>> LoadProductCodesAsync(
        NpgsqlConnection connection,
        string? contains,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT product_code
            FROM public.ftl_catalog
            WHERE product_code IS NOT NULL AND BTRIM(product_code) <> ''
            """;
        if (!string.IsNullOrWhiteSpace(contains))
            sql += """
                 AND product_code ILIKE @Pattern ESCAPE '\'
                """;
        sql += """
             ORDER BY product_code ASC
            """;

        var codes = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        if (!string.IsNullOrWhiteSpace(contains))
            cmd.Parameters.Add(new NpgsqlParameter("@Pattern", NpgsqlDbType.Text) { Value = "%" + EscapeLike(contains) + "%" });

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            codes.Add(reader.GetString(0));
        return codes;
    }

    /// <summary><c>SGV2(1S)_DP_MAC36_LH</c> and <c>SGV2_DOOR_TOOLS</c> both relate on <c>SGV2</c>.</summary>
    private static string RelatedToken(string search)
    {
        var trimmed = search.Trim();
        var cut = trimmed.IndexOfAny(['(', '_']);
        var token = cut > 0 ? trimmed[..cut] : trimmed;
        return token.Trim();
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static async Task<IReadOnlyList<FtlCatalogProductDto>> LoadProductsAsync(
        NpgsqlConnection connection,
        string? containsToken,
        string? exactCode,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT id, product_code, category, type, oem, door_hand, door_width,
                   description, unit_price, currency, search_text, raw_metadata,
                   created_at, updated_at
            FROM public.ftl_catalog
            WHERE product_code IS NOT NULL AND BTRIM(product_code) <> ''
            """;
        if (!string.IsNullOrWhiteSpace(exactCode))
            sql += """
                 AND LOWER(BTRIM(product_code)) = LOWER(BTRIM(@ProductCode))
                """;
        else if (!string.IsNullOrWhiteSpace(containsToken))
            sql += """
                 AND product_code ILIKE @Pattern ESCAPE '\'
                """;
        sql += """
             ORDER BY product_code ASC
            """;

        var products = new List<FtlCatalogProductDto>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        if (!string.IsNullOrWhiteSpace(exactCode))
            cmd.Parameters.Add(new NpgsqlParameter("@ProductCode", NpgsqlDbType.Text) { Value = exactCode });
        else if (!string.IsNullOrWhiteSpace(containsToken))
            cmd.Parameters.Add(new NpgsqlParameter("@Pattern", NpgsqlDbType.Text) { Value = "%" + EscapeLike(containsToken) + "%" });

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            products.Add(new FtlCatalogProductDto
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
            });
        }

        return products;
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
