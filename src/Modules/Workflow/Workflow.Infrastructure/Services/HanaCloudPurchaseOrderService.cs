using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Sap.Data.Hana;
using SaaSApp.Workflow.Application.Connectors;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class HanaCloudPurchaseOrderService : IHanaCloudPurchaseOrderService
{
    private static readonly Regex SafeIdent = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private readonly ITenantContext _tenantContext;

    public HanaCloudPurchaseOrderService(ITenantContext tenantContext)
    {
        _tenantContext = tenantContext;
    }

    public async Task<ConnectorHanaPoLookupResponse> ListAsync(
        Guid connectorId,
        string? poNumber,
        CancellationToken cancellationToken = default)
    {
        if (connectorId == Guid.Empty)
            throw new InvalidOperationException("Connector id is required.");

        var filter = string.IsNullOrWhiteSpace(poNumber) ? null : poNumber.Trim();
        var settings = await LoadSettingsAsync(connectorId, cancellationToken);
        var orders = QueryPurchaseOrders(settings, filter);

        return new ConnectorHanaPoLookupResponse(
            orders.Count > 0,
            connectorId,
            filter,
            orders.Count,
            orders);
    }

    public async Task<ConnectorHanaPoMatchResponse> MatchAsync(
        Guid connectorId,
        ConnectorHanaPoMatchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (connectorId == Guid.Empty)
            throw new InvalidOperationException("Connector id is required.");
        if (request is null || string.IsNullOrWhiteSpace(request.PoNumber))
            throw new ArgumentException("poNumber is required.", nameof(request));

        var poNumber = request.PoNumber.Trim();
        var instanceId = Clean(request.InstanceId, 128, "instanceId");
        var invoiceNumber = Clean(request.InvoiceNumber, 64, "invoiceNumber");
        var supplierName = Clean(request.SupplierName, 256, "supplierName");
        var invoiceDate = ParseInvoiceDate(request.InvoiceDate);
        var currency = Clean(request.Currency, 16, "currency");
        var status = Clean(request.Status, 64, "status");
        var invoiceStatus = Clean(request.InvoiceStatus, 64, "invoiceStatus");
        var itemsJson = HanaCloudPurchaseOrderMapper.SerializeItems(request.Items);
        if (instanceId is null)
            throw new ArgumentException("instanceId is required. One PO can match many instances.");
        if (invoiceNumber is null
            && supplierName is null
            && invoiceDate is null
            && currency is null
            && request.TotalAmount is null
            && status is null
            && invoiceStatus is null
            && itemsJson is null)
        {
            throw new ArgumentException(
                "invoiceNumber, supplierName, invoiceDate, currency, totalAmount, status, invoiceStatus, or items is required.");
        }

        var settings = await LoadSettingsAsync(connectorId, cancellationToken);
        var saved = SaveMatch(
            settings,
            poNumber,
            instanceId,
            invoiceNumber,
            supplierName,
            invoiceDate,
            currency,
            request.TotalAmount,
            status,
            invoiceStatus,
            itemsJson);
        return new ConnectorHanaPoMatchResponse(
            true,
            saved.Created,
            connectorId,
            saved.PoNumber,
            saved.InstanceId,
            saved.InvoiceNumber,
            saved.SupplierName,
            saved.InvoiceDate,
            saved.Currency,
            saved.TotalAmount,
            saved.Status,
            saved.InvoiceStatus,
            HanaCloudPurchaseOrderMapper.ParseItems(saved.ItemsJson));
    }

    public async Task<bool> TryMarkPaidByInstanceIdAsync(
        Guid connectorId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        if (connectorId == Guid.Empty || instanceId == Guid.Empty)
            return false;

        var settings = await LoadSettingsAsync(connectorId, cancellationToken);
        return MarkPaidByInstanceId(settings, instanceId.ToString("D"));
    }

    private static bool MarkPaidByInstanceId(HanaDatabaseSettings settings, string instanceId)
    {
        var matchTable = $"{QuoteIdent(settings.Schema)}.{QuoteIdent(MatchTable)}";
        try
        {
            using var conn = OpenConnection(settings);
            EnsureMatchTable(conn, settings);
            using var update = new HanaCommand(
                $"""
                UPDATE {matchTable}
                SET "MATCH_STATUS" = ?, "INVOICE_STATUS" = ?, "UPDATED_AT" = ?
                WHERE "INSTANCE_ID" = ?
                """,
                conn);
            update.CommandTimeout = 30;
            var now = DateTime.UtcNow;
            update.Parameters.Add(new HanaParameter { Value = "Paid" });
            update.Parameters.Add(new HanaParameter { Value = "Paid" });
            update.Parameters.Add(new HanaParameter { Value = now });
            update.Parameters.Add(new HanaParameter { Value = instanceId });
            return update.ExecuteNonQuery() > 0;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "HANA Cloud paid-status update failed: " + Truncate(ex.Message, 400));
        }
    }

    private async Task<HanaDatabaseSettings> LoadSettingsAsync(Guid connectorId, CancellationToken cancellationToken)
    {
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureColumnAsync(connection, cancellationToken);

        const string sql = """
            SELECT "HanaDatabaseJson"
            FROM dbo."connector"
            WHERE "Id" = @Id AND "IsDeleted" = false;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", connectorId);
        var raw = await cmd.ExecuteScalarAsync(cancellationToken);
        if (raw is null || raw is DBNull)
            throw new InvalidOperationException("Connector not found.");

        var json = raw as string;
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException(
                "HANA database details are not saved on this connector. Set dbo.connector.HanaDatabaseJson.");

        return HanaDatabaseSettings.Parse(json);
    }

    private static async Task EnsureColumnAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """ALTER TABLE dbo."connector" ADD COLUMN IF NOT EXISTS "HanaDatabaseJson" text NULL;""";
        await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 60 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<ConnectorHanaPurchaseOrderDto> QueryPurchaseOrders(
        HanaDatabaseSettings settings,
        string? poNumber)
    {
        var schema = QuoteIdent(settings.Schema);
        var table = QuoteIdent(settings.Table);
        var sql = $"""
            SELECT "PO_NUMBER", "PO_TYPE", "SUPPLIER_ID", "SUPPLIER_NAME", "COMPANY_CODE",
                   "PURCHASING_ORG", "PURCHASING_GROUP", "CURRENCY", "PO_DATE",
                   "APPROVAL_STATUS", "CREATED_BY", "ITEMS"
            FROM {schema}.{table}
            """;
        if (poNumber != null)
            sql += """ WHERE "PO_NUMBER" = ?""";
        sql += """ ORDER BY "PO_DATE" DESC, "PO_NUMBER" """;

        try
        {
            using var conn = OpenConnection(settings);
            EnsureMatchTable(conn, settings);
            using var cmd = new HanaCommand(sql, conn);
            if (poNumber != null)
                cmd.Parameters.Add(new HanaParameter { Value = poNumber });

            var pending = new List<(string? Po, string? Type, string? SupplierId, string? SupplierName, string? Company, string? Org, string? Group, string? Currency, string? Date, string? Approval, string? CreatedBy, IReadOnlyList<ConnectorHanaPoLineDto> Lines)>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var lines = HanaCloudPurchaseOrderMapper.ParseItems(ReadString(reader, 11));
                    pending.Add((
                        ReadString(reader, 0),
                        ReadString(reader, 1),
                        ReadString(reader, 2),
                        ReadString(reader, 3),
                        ReadString(reader, 4),
                        ReadString(reader, 5),
                        ReadString(reader, 6),
                        ReadString(reader, 7),
                        ReadDate(reader, 8),
                        ReadString(reader, 9),
                        ReadString(reader, 10),
                        lines));
                }
            }

            var orders = new List<ConnectorHanaPurchaseOrderDto>(pending.Count);
            foreach (var row in pending)
            {
                orders.Add(new ConnectorHanaPurchaseOrderDto(
                    row.Po,
                    row.Type,
                    row.SupplierId,
                    row.SupplierName,
                    row.Company,
                    row.Org,
                    row.Group,
                    row.Currency,
                    row.Date,
                    row.Approval,
                    row.CreatedBy,
                    HanaCloudPurchaseOrderMapper.SumNetValue(row.Lines),
                    row.Lines,
                    LoadMatches(conn, settings, row.Po)));
            }

            return orders;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("HANA Cloud purchase-order query failed: " + Truncate(ex.Message, 400));
        }
    }

    private const string MatchTable = "PO_INVOICE_MATCH";

    private static MatchSnapshot SaveMatch(
        HanaDatabaseSettings settings,
        string poNumber,
        string instanceId,
        string? invoiceNumber,
        string? supplierName,
        DateTime? invoiceDate,
        string? currency,
        decimal? totalAmount,
        string? status,
        string? invoiceStatus,
        string? itemsJson)
    {
        var poTable = $"{QuoteIdent(settings.Schema)}.{QuoteIdent(settings.Table)}";
        var matchTable = $"{QuoteIdent(settings.Schema)}.{QuoteIdent(MatchTable)}";

        try
        {
            using var conn = OpenConnection(settings);
            EnsureMatchTable(conn, settings);
            if (!PurchaseOrderExists(conn, poTable, poNumber))
                throw new InvalidOperationException("Purchase order not found.");

            var existing = FindMatch(conn, matchTable, poNumber, instanceId);
            var created = existing is null;
            var now = DateTime.UtcNow;
            if (created)
            {
                if (invoiceNumber is null)
                    throw new ArgumentException("invoiceNumber is required when creating a match.");
                status ??= "Matched";
                using var insert = new HanaCommand(
                    $"""
                    INSERT INTO {matchTable}
                    ("MATCH_ID", "PO_NUMBER", "INSTANCE_ID", "INVOICE_NUMBER", "SUPPLIER_NAME",
                     "INVOICE_DATE", "CURRENCY", "TOTAL_AMOUNT", "MATCH_STATUS", "INVOICE_STATUS",
                     "ITEMS", "CREATED_AT", "UPDATED_AT")
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    conn);
                insert.CommandTimeout = 30;
                insert.Parameters.Add(new HanaParameter { Value = NewMatchId() });
                insert.Parameters.Add(new HanaParameter { Value = poNumber });
                insert.Parameters.Add(new HanaParameter { Value = instanceId });
                insert.Parameters.Add(new HanaParameter { Value = invoiceNumber });
                insert.Parameters.Add(new HanaParameter { Value = (object?)supplierName ?? DBNull.Value });
                insert.Parameters.Add(new HanaParameter { Value = (object?)invoiceDate ?? DBNull.Value });
                insert.Parameters.Add(new HanaParameter { Value = (object?)currency ?? DBNull.Value });
                insert.Parameters.Add(new HanaParameter { Value = (object?)totalAmount ?? DBNull.Value });
                insert.Parameters.Add(new HanaParameter { Value = status });
                insert.Parameters.Add(new HanaParameter { Value = (object?)invoiceStatus ?? DBNull.Value });
                insert.Parameters.Add(new HanaParameter { Value = (object?)itemsJson ?? DBNull.Value });
                insert.Parameters.Add(new HanaParameter { Value = now });
                insert.Parameters.Add(new HanaParameter { Value = now });
                insert.ExecuteNonQuery();
                return new MatchSnapshot(
                    true,
                    poNumber,
                    instanceId,
                    invoiceNumber,
                    supplierName,
                    FormatDate(invoiceDate),
                    currency,
                    totalAmount,
                    status,
                    invoiceStatus,
                    itemsJson);
            }

            var sets = new List<string> { "\"UPDATED_AT\" = ?" };
            var values = new List<object> { now };
            void Set(string column, object? value)
            {
                if (value is null)
                    return;
                sets.Add($"\"{column}\" = ?");
                values.Add(value);
            }

            Set("INVOICE_NUMBER", invoiceNumber);
            Set("SUPPLIER_NAME", supplierName);
            Set("INVOICE_DATE", invoiceDate);
            Set("CURRENCY", currency);
            Set("TOTAL_AMOUNT", totalAmount);
            Set("MATCH_STATUS", status);
            Set("INVOICE_STATUS", invoiceStatus);
            Set("ITEMS", itemsJson);

            using var update = new HanaCommand(
                $"UPDATE {matchTable} SET {string.Join(", ", sets)} WHERE \"PO_NUMBER\" = ? AND \"INSTANCE_ID\" = ?",
                conn);
            update.CommandTimeout = 30;
            foreach (var value in values)
                update.Parameters.Add(new HanaParameter { Value = value });
            update.Parameters.Add(new HanaParameter { Value = poNumber });
            update.Parameters.Add(new HanaParameter { Value = instanceId });
            update.ExecuteNonQuery();
            return new MatchSnapshot(
                false,
                poNumber,
                instanceId,
                invoiceNumber ?? existing!.InvoiceNumber,
                supplierName ?? existing.SupplierName,
                FormatDate(invoiceDate) ?? existing.InvoiceDate,
                currency ?? existing.Currency,
                totalAmount ?? existing.TotalAmount,
                status ?? existing.Status,
                invoiceStatus ?? existing.InvoiceStatus,
                itemsJson ?? existing.ItemsJson);
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not ArgumentException)
        {
            throw new InvalidOperationException("HANA Cloud purchase-order match failed: " + Truncate(ex.Message, 400));
        }
    }

    private static IReadOnlyList<ConnectorHanaPoMatchLinkDto> LoadMatches(
        HanaConnection conn,
        HanaDatabaseSettings settings,
        string? poNumber)
    {
        if (string.IsNullOrWhiteSpace(poNumber))
            return Array.Empty<ConnectorHanaPoMatchLinkDto>();

        var matchTable = $"{QuoteIdent(settings.Schema)}.{QuoteIdent(MatchTable)}";
        using var cmd = new HanaCommand(
            $"""
            SELECT "INSTANCE_ID", "INVOICE_NUMBER", "SUPPLIER_NAME", "INVOICE_DATE",
                   "CURRENCY", "TOTAL_AMOUNT", "MATCH_STATUS", "INVOICE_STATUS", "ITEMS"
            FROM {matchTable}
            WHERE "PO_NUMBER" = ?
            ORDER BY "UPDATED_AT" DESC, "INSTANCE_ID"
            """,
            conn);
        cmd.Parameters.Add(new HanaParameter { Value = poNumber });
        var links = new List<ConnectorHanaPoMatchLinkDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            links.Add(new ConnectorHanaPoMatchLinkDto(
                ReadString(reader, 0),
                ReadString(reader, 1),
                ReadString(reader, 2),
                ReadDate(reader, 3),
                ReadString(reader, 4),
                ReadDecimal(reader, 5),
                ReadString(reader, 6),
                ReadString(reader, 7),
                HanaCloudPurchaseOrderMapper.ParseItems(ReadString(reader, 8))));
        }

        return links;
    }

    private static bool PurchaseOrderExists(HanaConnection conn, string poTable, string poNumber)
    {
        using var cmd = new HanaCommand($"SELECT COUNT(*) FROM {poTable} WHERE \"PO_NUMBER\" = ?", conn);
        cmd.Parameters.Add(new HanaParameter { Value = poNumber });
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static MatchSnapshot? FindMatch(HanaConnection conn, string matchTable, string poNumber, string instanceId)
    {
        using var cmd = new HanaCommand(
            $"""
            SELECT "PO_NUMBER", "INSTANCE_ID", "INVOICE_NUMBER", "SUPPLIER_NAME", "INVOICE_DATE",
                   "CURRENCY", "TOTAL_AMOUNT", "MATCH_STATUS", "INVOICE_STATUS", "ITEMS"
            FROM {matchTable}
            WHERE "PO_NUMBER" = ? AND "INSTANCE_ID" = ?
            """,
            conn);
        cmd.CommandTimeout = 30;
        cmd.Parameters.Add(new HanaParameter { Value = poNumber });
        cmd.Parameters.Add(new HanaParameter { Value = instanceId });
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new MatchSnapshot(
            false,
            ReadString(reader, 0) ?? poNumber,
            ReadString(reader, 1) ?? instanceId,
            ReadString(reader, 2),
            ReadString(reader, 3),
            ReadDate(reader, 4),
            ReadString(reader, 5),
            ReadDecimal(reader, 6),
            ReadString(reader, 7),
            ReadString(reader, 8),
            ReadString(reader, 9));
    }

    private static void EnsureMatchTable(HanaConnection conn, HanaDatabaseSettings settings)
    {
        using var exists = new HanaCommand(
            """
            SELECT COUNT(*)
            FROM SYS.TABLES
            WHERE SCHEMA_NAME = ? AND TABLE_NAME = ?
            """,
            conn);
        exists.Parameters.Add(new HanaParameter { Value = settings.Schema });
        exists.Parameters.Add(new HanaParameter { Value = MatchTable });
        var count = Convert.ToInt32(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        var table = $"{QuoteIdent(settings.Schema)}.{QuoteIdent(MatchTable)}";
        if (count == 0)
        {
            using var create = new HanaCommand(
                $"""
                CREATE COLUMN TABLE {table} (
                    "MATCH_ID" BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    "PO_NUMBER" NVARCHAR(40) NOT NULL,
                    "INSTANCE_ID" NVARCHAR(128) NOT NULL,
                    "INVOICE_NUMBER" NVARCHAR(64),
                    "SUPPLIER_NAME" NVARCHAR(256),
                    "INVOICE_DATE" DATE,
                    "CURRENCY" NVARCHAR(16),
                    "TOTAL_AMOUNT" DECIMAL(21, 6),
                    "MATCH_STATUS" NVARCHAR(64),
                    "INVOICE_STATUS" NVARCHAR(64),
                    "ITEMS" NCLOB,
                    "CREATED_AT" TIMESTAMP,
                    "UPDATED_AT" TIMESTAMP,
                    UNIQUE ("PO_NUMBER", "INSTANCE_ID")
                )
                """,
                conn);
            create.ExecuteNonQuery();
            return;
        }

        EnsureMatchColumn(conn, settings, "SUPPLIER_NAME", "NVARCHAR(256)");
        EnsureMatchColumn(conn, settings, "INVOICE_DATE", "DATE");
        EnsureMatchColumn(conn, settings, "CURRENCY", "NVARCHAR(16)");
        EnsureMatchColumn(conn, settings, "TOTAL_AMOUNT", "DECIMAL(21, 6)");
        EnsureMatchColumn(conn, settings, "INVOICE_STATUS", "NVARCHAR(64)");
        EnsureMatchColumn(conn, settings, "ITEMS", "NCLOB");
    }

    private static void EnsureMatchColumn(
        HanaConnection conn,
        HanaDatabaseSettings settings,
        string column,
        string sqlType)
    {
        using var exists = new HanaCommand(
            """
            SELECT COUNT(*)
            FROM SYS.TABLE_COLUMNS
            WHERE SCHEMA_NAME = ? AND TABLE_NAME = ? AND COLUMN_NAME = ?
            """,
            conn);
        exists.Parameters.Add(new HanaParameter { Value = settings.Schema });
        exists.Parameters.Add(new HanaParameter { Value = MatchTable });
        exists.Parameters.Add(new HanaParameter { Value = column });
        var count = Convert.ToInt32(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (count > 0)
            return;

        var alter =
            $"ALTER TABLE {QuoteIdent(settings.Schema)}.{QuoteIdent(MatchTable)} ADD (\"{column}\" {sqlType})";
        using var cmd = new HanaCommand(alter, conn);
        cmd.ExecuteNonQuery();
    }

    private static HanaConnection OpenConnection(HanaDatabaseSettings settings)
    {
        var builder = new HanaConnectionStringBuilder
        {
            Server = $"{settings.Host}:{settings.Port}",
            UserName = settings.User,
            Password = settings.Password,
            CurrentSchema = settings.Schema,
            Encrypt = true,
            SSLValidateCertificate = settings.SslValidateCertificate,
            // Milliseconds. 30 was 30ms and aborted every connect.
            CommunicationTimeout = 120000,
            Pooling = false
        };
        if (settings.SslValidateCertificate)
            builder.SSLUseDefaultTrustStore = true;

        var conn = new HanaConnection(builder.ConnectionString);
        conn.Open();
        return conn;
    }

    private static long NewMatchId()
    {
        Span<byte> bytes = stackalloc byte[8];
        Random.Shared.NextBytes(bytes);
        var id = BitConverter.ToInt64(bytes);
        return id == long.MinValue ? 1 : Math.Abs(id);
    }

    private static string? Clean(string? value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{field} must be {maxLength} characters or fewer.");
        return trimmed;
    }

    private static DateTime? ParseInvoiceDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateTime.TryParse(
                value.Trim(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            return dt.Date;
        }

        throw new ArgumentException("invoiceDate must be a valid date, e.g. 2026-09-14.");
    }

    private static string? FormatDate(DateTime? value) =>
        value?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static decimal? ReadDecimal(HanaDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        return Convert.ToDecimal(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string QuoteIdent(string name)
    {
        if (!SafeIdent.IsMatch(name))
            throw new InvalidOperationException("HANA schema or table name is invalid.");
        return "\"" + name + "\"";
    }

    private static string? ReadString(HanaDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        return Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? ReadDate(HanaDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        var value = reader.GetValue(ordinal);
        return value is DateTime dt
            ? dt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private sealed record MatchSnapshot(
        bool Created,
        string PoNumber,
        string? InstanceId,
        string? InvoiceNumber,
        string? SupplierName,
        string? InvoiceDate,
        string? Currency,
        decimal? TotalAmount,
        string? Status,
        string? InvoiceStatus,
        string? ItemsJson);

    private sealed record HanaDatabaseSettings(
        string Host,
        int Port,
        string User,
        string Password,
        string Schema,
        string Table,
        bool SslValidateCertificate)
    {
        public static HanaDatabaseSettings Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var host = Required(root, "host");
            var user = Required(root, "user", "username");
            var password = Required(root, "password");
            var portText = Optional(root, "port") ?? "443";
            if (!int.TryParse(portText, out var port) || port <= 0)
                throw new InvalidOperationException("HANA database port is invalid.");

            return new HanaDatabaseSettings(
                host,
                port,
                user,
                password,
                Optional(root, "schema") ?? "DBADMIN",
                Optional(root, "table") ?? "PURCHASE_ORDERS",
                ReadBool(root, "sslValidateCertificate", defaultValue: true)
                    && ReadBool(root, "validateCertificate", defaultValue: true));
        }

        private static string Required(JsonElement root, params string[] names)
        {
            var value = Optional(root, names);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"HANA database setting '{names[0]}' is required.");
            return value.Trim();
        }

        private static string? Optional(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                    continue;
                var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }

            return null;
        }

        private static bool ReadBool(JsonElement root, string name, bool defaultValue)
        {
            if (!root.TryGetProperty(name, out var value))
                return defaultValue;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => !string.Equals(value.GetString(), "false", StringComparison.OrdinalIgnoreCase),
                _ => defaultValue
            };
        }
    }
}
