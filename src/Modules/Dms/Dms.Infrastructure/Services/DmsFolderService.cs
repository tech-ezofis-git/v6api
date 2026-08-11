using Npgsql;
using SaaSApp.Dms.Domain.Models;
using SaaSApp.MultiTenancy;

namespace SaaSApp.Dms.Infrastructure.Services;

/// <summary>Service for DMS folder tree and document listing. Uses Year/InvoiceType/VendorName folder structure.</summary>
public sealed class DmsFolderService : IDmsFolderService
{
    private readonly ITenantConnectionProvider _connectionProvider;

    public DmsFolderService(ITenantConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider;
    }

    public async Task<FolderChildrenResponse> GetFolderChildrenAsync(Guid repositoryId, string tableName, string path, CancellationToken ct = default)
    {
        var connStr = _connectionProvider.ConnectionString ?? throw new InvalidOperationException("Tenant connection not set.");
        var parts = ParsePath(path);
        var year = parts.Length > 0 ? parts[0] : null;
        var invoiceType = parts.Length > 1 ? parts[1] : null;
        var vendorName = parts.Length > 2 ? parts[2] : null;

        var table = EscapeTableName(tableName);

        string sql;
        if (year == null)
        {
            sql = $"""
                SELECT CAST("Year" AS varchar(4)) AS "Name", COUNT(*) AS "DocumentCount",
                       CAST("Year" AS varchar(4)) AS "Path"
                FROM dms."{table}"
                WHERE "IsDeleted" = false AND "RepositoryId" = @RepoId
                GROUP BY "Year" ORDER BY "Year" DESC
                """;
        }
        else if (invoiceType == null)
        {
            sql = $"""
                SELECT "InvoiceType" AS "Name", COUNT(*) AS "DocumentCount",
                       CAST("Year" AS varchar(4)) || '/' || "InvoiceType" AS "Path"
                FROM dms."{table}"
                WHERE "IsDeleted" = false AND "RepositoryId" = @RepoId AND "Year" = @Year
                GROUP BY "Year", "InvoiceType" ORDER BY "InvoiceType"
                """;
        }
        else if (vendorName == null)
        {
            sql = $"""
                SELECT "VendorName" AS "Name", COUNT(*) AS "DocumentCount",
                       CAST("Year" AS varchar(4)) || '/' || "InvoiceType" || '/' || "VendorName" AS "Path"
                FROM dms."{table}"
                WHERE "IsDeleted" = false AND "RepositoryId" = @RepoId AND "Year" = @Year AND "InvoiceType" = @InvoiceType
                GROUP BY "Year", "InvoiceType", "VendorName" ORDER BY "VendorName"
                """;
        }
        else
        {
            return new FolderChildrenResponse(path, new List<FolderNode>());
        }

        var children = new List<FolderNode>();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@RepoId", repositoryId);
        if (year != null) cmd.Parameters.AddWithValue("@Year", short.Parse(year));
        if (invoiceType != null) cmd.Parameters.AddWithValue("@InvoiceType", invoiceType);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            children.Add(new FolderNode(
                r.GetString(0),
                r.GetString(2),
                r.GetInt32(1)));
        }

        return new FolderChildrenResponse(path, children);
    }

    public async Task<DocumentListResponse> GetDocumentsInFolderAsync(Guid repositoryId, string tableName, string path, int page, int pageSize, CancellationToken ct = default)
    {
        var parts = ParsePath(path);
        if (parts.Length < 3)
            return new DocumentListResponse(new List<DocumentListItem>(), 0);

        var (year, invoiceType, vendorName) = (parts[0], parts[1], parts[2]);
        var table = EscapeTableName(tableName);
        var connStr = _connectionProvider.ConnectionString ?? throw new InvalidOperationException("Tenant connection not set.");

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);

        var countSql = $"""
            SELECT COUNT(*) FROM dms."{table}"
            WHERE "IsDeleted" = false AND "RepositoryId" = @RepoId AND "Year" = @Year AND "InvoiceType" = @InvoiceType AND "VendorName" = @VendorName
            """;

        int total;
        await using (var countCmd = new NpgsqlCommand(countSql, conn))
        {
            countCmd.Parameters.AddWithValue("@RepoId", repositoryId);
            countCmd.Parameters.AddWithValue("@Year", short.Parse(year));
            countCmd.Parameters.AddWithValue("@InvoiceType", invoiceType);
            countCmd.Parameters.AddWithValue("@VendorName", vendorName);
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct) ?? 0);
        }

        var dataSql = $"""
            SELECT "Id", "FileName", "Status", "SignStatus", "CreatedAt", "WorkflowInstanceId", "ReportNo", "ReferenceNo"
            FROM dms."{table}"
            WHERE "IsDeleted" = false AND "RepositoryId" = @RepoId AND "Year" = @Year AND "InvoiceType" = @InvoiceType AND "VendorName" = @VendorName
            ORDER BY "CreatedAt" DESC
            OFFSET @Skip ROWS FETCH NEXT @PageSize ROWS ONLY
            """;

        var items = new List<DocumentListItem>();
        await using var dataCmd = new NpgsqlCommand(dataSql, conn);
        dataCmd.Parameters.AddWithValue("@RepoId", repositoryId);
        dataCmd.Parameters.AddWithValue("@Year", short.Parse(year));
        dataCmd.Parameters.AddWithValue("@InvoiceType", invoiceType);
        dataCmd.Parameters.AddWithValue("@VendorName", vendorName);
        dataCmd.Parameters.AddWithValue("@Skip", (page - 1) * pageSize);
        dataCmd.Parameters.AddWithValue("@PageSize", pageSize);

        await using var r = await dataCmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            items.Add(new DocumentListItem(
                r.GetGuid(0),
                r.GetString(1),
                r.GetInt16(2),
                r.GetInt16(3),
                r.GetDateTime(4),
                r.IsDBNull(5) ? null : r.GetGuid(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7)));
        }

        return new DocumentListResponse(items, total);
    }

    private static string[] ParsePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string EscapeTableName(string name) => name.Replace("\"", "\"\"", StringComparison.Ordinal);
}
