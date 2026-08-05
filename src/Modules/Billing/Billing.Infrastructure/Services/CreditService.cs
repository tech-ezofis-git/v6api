using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SaaSApp.Billing.Application.Contracts;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Billing.Infrastructure.Services;

public sealed class CreditService : ICreditService
{
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;
    private readonly string _catalogConnectionString;

    public CreditService(IDbContextFactory<CatalogDbContext> catalogFactory, IConfiguration configuration)
    {
        _catalogFactory = catalogFactory;
        _catalogConnectionString = configuration.GetConnectionString("CatalogConnection")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Catalog connection string is not configured.");
    }

    public async Task<CreditUpdateResult> UpdateCreditAsync(
        Guid tenantId,
        Guid? userId,
        CreditUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, IndiaTimeZone());
        var allocationMonth = nowIst.Month;
        var allocationYear = nowIst.Year;
        var creditToConsume = request.Credit <= 0 ? 1 : request.Credit;

        await using var db = await _catalogFactory.CreateDbContextAsync(cancellationToken);

        var isDocumentSummary = string.Equals(
            request.SubActivity?.Trim().Replace(" ", "", StringComparison.OrdinalIgnoreCase),
            "documentsummary",
            StringComparison.OrdinalIgnoreCase);

        var query = db.CreditMasters.AsQueryable()
            .Where(c => c.TenantId == tenantId
                        && c.AllocationMonth == allocationMonth
                        && c.AllocationYear == allocationYear
                        && !c.IsDeleted);

        if (isDocumentSummary)
            query = query.Where(c => c.CreditType == "DocumentSummary");

        var creditMaster = await query.SingleOrDefaultAsync(cancellationToken);
        if (creditMaster is null)
        {
            return new CreditUpdateResult(
                CreditUpdateStatus.Failed,
                "Your Credit Balance is not listed for this month.");
        }

        if (creditMaster.BalanceCredit < creditToConsume)
        {
            return new CreditUpdateResult(CreditUpdateStatus.LimitExceeded, "credit limit exceeded");
        }

        creditMaster.BalanceCredit -= creditToConsume;
        creditMaster.OverallConsumedCredit = (creditMaster.OverallConsumedCredit ?? 0) + creditToConsume;
        creditMaster.ModifiedAt = nowUtc;
        creditMaster.ModifiedBy = userId?.ToString("D");

        await db.SaveChangesAsync(cancellationToken);

        var tableSuffix = GetTransactionTableSuffix(nowUtc);
        await EnsureCreditTransactionTableAsync(tableSuffix, cancellationToken);
        await InsertCreditTransactionAsync(
            tableSuffix,
            tenantId,
            allocationMonth,
            allocationYear,
            request,
            creditToConsume,
            userId,
            nowUtc,
            cancellationToken);

        return new CreditUpdateResult(CreditUpdateStatus.Success, "credits updated");
    }

    public async Task<CreditMasterDto?> GetCreditMasterAsync(
        Guid tenantId,
        int? allocationMonth = null,
        int? allocationYear = null,
        string? creditType = null,
        CancellationToken cancellationToken = default)
    {
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IndiaTimeZone());
        var month = allocationMonth ?? nowIst.Month;
        var year = allocationYear ?? nowIst.Year;

        await using var db = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var query = db.CreditMasters.AsNoTracking()
            .Where(c => c.TenantId == tenantId
                        && c.AllocationMonth == month
                        && c.AllocationYear == year
                        && !c.IsDeleted);

        if (!string.IsNullOrWhiteSpace(creditType))
            query = query.Where(c => c.CreditType == creditType);

        var row = await query.OrderByDescending(c => c.Id).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : MapMaster(row);
    }

    public async Task<IReadOnlyList<CreditMonthlyBalanceDto>> GetCreditMonthlyBalancesAsync(
        Guid tenantId,
        int? year = null,
        string? creditType = null,
        CancellationToken cancellationToken = default)
    {
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IndiaTimeZone());
        var allocationYear = year ?? nowIst.Year;

        await using var db = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var query = db.CreditMasters.AsNoTracking()
            .Where(c => c.TenantId == tenantId
                        && c.AllocationYear == allocationYear
                        && !c.IsDeleted);

        if (!string.IsNullOrWhiteSpace(creditType))
            query = query.Where(c => c.CreditType == creditType);

        var rows = await query
            .OrderBy(c => c.AllocationMonth)
            .ThenByDescending(c => c.Id)
            .ToListAsync(cancellationToken);

        // One row per month (+ creditType): keep latest id when duplicates exist.
        return rows
            .GroupBy(r => new { r.AllocationMonth, CreditType = r.CreditType ?? string.Empty })
            .Select(g => g.First())
            .OrderBy(r => r.AllocationMonth)
            .Select(MapMonthlyBalance)
            .ToList();
    }

    public async Task<CreditUsageResult> GetCreditUsageAsync(
        Guid tenantId,
        CreditUsagePeriod period,
        CancellationToken cancellationToken = default)
    {
        var (startUtc, endUtc) = ResolveUsageRange(period, DateTime.UtcNow);
        var transactions = await LoadTransactionsAsync(tenantId, startUtc, endUtc, cancellationToken);

        transactions = transactions.OrderByDescending(t => t.CreatedAt).ToList();
        var totalCredits = transactions.Sum(t => t.Credit);

        return new CreditUsageResult(
            period,
            startUtc,
            endUtc,
            totalCredits,
            transactions.Count,
            transactions);
    }

    public async Task<CreditUsageDashboardResult> GetCreditUsageDashboardAsync(
        Guid tenantId,
        CreditUsageReportRequest request,
        CancellationToken cancellationToken = default)
    {
        var period = CreditUsagePeriodParser.Parse(request.Period);
        var nowUtc = DateTime.UtcNow;
        var (startUtc, endUtc) = ResolveUsageRange(period, nowUtc, request.Year, request.Month);
        var transactions = await LoadTransactionsAsync(tenantId, startUtc, endUtc, cancellationToken);
        return CreditUsageDashboardBuilder.Build(period, startUtc, endUtc, transactions);
    }

    private async Task<List<CreditTransactionItemDto>> LoadTransactionsAsync(
        Guid tenantId,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken)
    {
        var tableNames = GetTransactionTableNames(startUtc, endUtc);
        var transactions = new List<CreditTransactionItemDto>();

        await using var connection = new SqlConnection(_catalogConnectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var tableName in tableNames)
        {
            if (!await TableExistsAsync(connection, tableName, cancellationToken))
                continue;

            var batch = await ReadTransactionsAsync(connection, tableName, tenantId, startUtc, endUtc, cancellationToken);
            transactions.AddRange(batch);
        }

        return transactions;
    }

    private static CreditMasterDto MapMaster(CreditMaster row)
    {
        var monthlyBalance = CreditBalanceCalculator.CalculateMonthlyBalance(
            row.InitialCredit,
            row.CarryForwardCredit,
            row.TopUpBalanceCredit,
            row.OverallConsumedCredit);

        return new CreditMasterDto(
            row.Id,
            row.TenantId,
            row.AllocationMonth,
            row.AllocationYear,
            row.CreditType,
            row.InitialCredit,
            row.BalanceCredit,
            row.Remarks,
            row.Status,
            row.OverallConsumedCredit,
            row.ValidFromDate,
            row.ValidToDate,
            row.CarryForwardCredit,
            row.TopUpBalanceCredit,
            row.ExtraConsumedCredit,
            monthlyBalance);
    }

    private static CreditMonthlyBalanceDto MapMonthlyBalance(CreditMaster row)
    {
        var monthlyBalance = CreditBalanceCalculator.CalculateMonthlyBalance(
            row.InitialCredit,
            row.CarryForwardCredit,
            row.TopUpBalanceCredit,
            row.OverallConsumedCredit);
        var label = new DateTime(row.AllocationYear, row.AllocationMonth, 1)
            .ToString("MMM yyyy", CultureInfo.InvariantCulture);

        return new CreditMonthlyBalanceDto(
            row.Id,
            row.TenantId,
            row.AllocationMonth,
            row.AllocationYear,
            label,
            row.CreditType,
            row.InitialCredit,
            row.CarryForwardCredit,
            row.TopUpBalanceCredit,
            row.OverallConsumedCredit,
            row.ExtraConsumedCredit,
            row.BalanceCredit,
            monthlyBalance,
            row.Status);
    }

    private static string GetTransactionTableSuffix(DateTime utcNow)
    {
        var ist = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var istNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, ist);
        return istNow.ToString("MMyy", CultureInfo.InvariantCulture);
    }

    private async Task EnsureCreditTransactionTableAsync(string tableSuffix, CancellationToken cancellationToken)
    {
        var tableName = $"creditTransaction_{tableSuffix}";
        var sql = $"""
            IF OBJECT_ID(N'[dbo].[{tableName}]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[{tableName}] (
                    [id] INT IDENTITY(1,1) NOT NULL,
                    [tenantId] UNIQUEIDENTIFIER NOT NULL,
                    [allocationMonth] NVARCHAR(20) NULL,
                    [allocationYear] NVARCHAR(20) NULL,
                    [activityType] NVARCHAR(100) NULL,
                    [subActivityType] NVARCHAR(100) NULL,
                    [credit] INT NULL,
                    [identifyTable] NVARCHAR(100) NULL,
                    [identifyId] INT NULL,
                    [remarks] NVARCHAR(500) NULL,
                    [inputTokens] INT NULL,
                    [outputTokens] INT NULL,
                    [totalTokens] INT NULL,
                    [env] NVARCHAR(50) NULL,
                    [createdAt] DATETIME2 NULL,
                    [modifiedAt] DATETIME2 NULL,
                    [createdBy] NVARCHAR(50) NULL,
                    [modifiedBy] NVARCHAR(50) NULL,
                    [isDeleted] BIT NULL,
                    [ValidFrom] DATETIME2 NULL,
                    [ValidTo] DATETIME2 NULL,
                    CONSTRAINT [PK_{tableName}] PRIMARY KEY CLUSTERED ([id] ASC)
                );
                CREATE INDEX [IX_{tableName}_TenantId_CreatedAt] ON [dbo].[{tableName}] ([tenantId], [createdAt]);
            END
            """;

        await using var connection = new SqlConnection(_catalogConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertCreditTransactionAsync(
        string tableSuffix,
        Guid tenantId,
        int allocationMonth,
        int allocationYear,
        CreditUpdateRequest request,
        int credit,
        Guid? userId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var tableName = $"creditTransaction_{tableSuffix}";
        var sql = $"""
            INSERT INTO [dbo].[{tableName}]
                (tenantId, activityType, subActivityType, identifyTable, identifyId, remarks,
                 allocationMonth, allocationYear, credit, inputTokens, outputTokens, totalTokens,
                 env, createdAt, createdBy, isDeleted)
            VALUES
                (@TenantId, @ActivityType, @SubActivity, @IdentifyTable, @IdentifyId, @Remarks,
                 @AllocationMonth, @AllocationYear, @Credit, @InputTokens, @OutputTokens, @TotalTokens,
                 @Env, @CreatedAt, @CreatedBy, 0);
            """;

        await using var connection = new SqlConnection(_catalogConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@ActivityType", (object?)request.ActivityType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SubActivity", (object?)request.SubActivity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IdentifyTable", (object?)request.Identify ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IdentifyId", request.IdentifyId);
        cmd.Parameters.AddWithValue("@Remarks", (object?)request.Remarks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AllocationMonth", allocationMonth.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@AllocationYear", allocationYear.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@Credit", credit);
        cmd.Parameters.AddWithValue("@InputTokens", (object?)request.InputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OutputTokens", (object?)request.OutputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TotalTokens", (object?)request.TotalTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Env", (object?)request.Env ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", nowUtc);
        cmd.Parameters.AddWithValue("@CreatedBy", (object?)userId?.ToString("D") ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        SqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = N'dbo' AND TABLE_NAME = @TableName
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private static async Task<List<CreditTransactionItemDto>> ReadTransactionsAsync(
        SqlConnection connection,
        string tableName,
        Guid tenantId,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken)
    {
        var hasTenantColumn = await ColumnExistsAsync(connection, tableName, "tenantId", cancellationToken);
        var sql = hasTenantColumn
            ? $"""
                SELECT id, activityType, subActivityType, identifyTable, identifyId, remarks,
                       credit, inputTokens, outputTokens, totalTokens, createdAt
                FROM [dbo].[{tableName}]
                WHERE (isDeleted = 0 OR isDeleted IS NULL)
                  AND tenantId = @TenantId
                  AND createdAt >= @StartUtc AND createdAt < @EndUtc
                ORDER BY createdAt DESC
                """
            : $"""
                SELECT id, activityType, subActivityType, identifyTable, identifyId, remarks,
                       credit, inputTokens, outputTokens, totalTokens, createdAt
                FROM [dbo].[{tableName}]
                WHERE (isDeleted = 0 OR isDeleted IS NULL)
                  AND createdAt >= @StartUtc AND createdAt < @EndUtc
                ORDER BY createdAt DESC
                """;

        await using var cmd = new SqlCommand(sql, connection);
        if (hasTenantColumn)
            cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@StartUtc", startUtc);
        cmd.Parameters.AddWithValue("@EndUtc", endUtc);

        var list = new List<CreditTransactionItemDto>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new CreditTransactionItemDto(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetDateTime(10)));
        }

        return list;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqlConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = N'dbo' AND TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        cmd.Parameters.AddWithValue("@ColumnName", columnName);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static IReadOnlyList<string> GetTransactionTableNames(DateTime startUtc, DateTime endUtc)
    {
        var ist = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var startIst = TimeZoneInfo.ConvertTimeFromUtc(startUtc, ist);
        var endIst = TimeZoneInfo.ConvertTimeFromUtc(endUtc, ist);

        var cursor = new DateTime(startIst.Year, startIst.Month, 1);
        var endMonth = new DateTime(endIst.Year, endIst.Month, 1);
        var names = new List<string>();

        while (cursor <= endMonth)
        {
            names.Add($"creditTransaction_{cursor:MMyy}");
            cursor = cursor.AddMonths(1);
        }

        return names;
    }

    private static (DateTime StartUtc, DateTime EndUtc) ResolveUsageRange(
        CreditUsagePeriod period,
        DateTime nowUtc,
        int? year = null,
        int? month = null)
    {
        var ist = IndiaTimeZone();
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, ist);
        var todayIst = nowIst.Date;

        DateTime startIst;
        DateTime endIst;

        switch (period)
        {
            case CreditUsagePeriod.Today:
                startIst = todayIst;
                endIst = todayIst.AddDays(1);
                break;
            case CreditUsagePeriod.Yesterday:
                startIst = todayIst.AddDays(-1);
                endIst = todayIst;
                break;
            case CreditUsagePeriod.Monthly:
                var monthlyYear = year ?? nowIst.Year;
                var monthlyMonth = month ?? nowIst.Month;
                startIst = new DateTime(monthlyYear, monthlyMonth, 1);
                endIst = startIst.AddMonths(1);
                break;
            case CreditUsagePeriod.Quarterly:
                var qYear = year ?? nowIst.Year;
                var quarterStartMonth = month.HasValue
                    ? ((month.Value - 1) / 3) * 3 + 1
                    : ((nowIst.Month - 1) / 3) * 3 + 1;
                startIst = new DateTime(qYear, quarterStartMonth, 1);
                endIst = startIst.AddMonths(3);
                break;
            case CreditUsagePeriod.Yearly:
                startIst = new DateTime(year ?? nowIst.Year, 1, 1);
                endIst = startIst.AddYears(1);
                break;
            default:
                startIst = todayIst;
                endIst = todayIst.AddDays(1);
                break;
        }

        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startIst, ist);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endIst, ist);
        return (startUtc, endUtc);
    }

    private static TimeZoneInfo IndiaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
    }
}
