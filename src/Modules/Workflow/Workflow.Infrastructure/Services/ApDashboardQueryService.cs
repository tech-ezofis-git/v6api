using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Domain.Enums;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// AP Command Center dashboard: aggregates invoice rows from workflow.agentDataValidation_* and ezfb form tables.
/// </summary>
public sealed class ApDashboardQueryService : IApDashboardQueryService
{
  private readonly ITenantContext _tenantContext;
  private readonly IWorkflowRepository _workflowRepository;
  private readonly IWorkflowEzfbFormDataLoader _formDataLoader;
  private readonly IApDashboardInsightsClient _insightsClient;
  private readonly ILogger<ApDashboardQueryService> _logger;

  public ApDashboardQueryService(
    ITenantContext tenantContext,
    IWorkflowRepository workflowRepository,
    IWorkflowEzfbFormDataLoader formDataLoader,
    IApDashboardInsightsClient insightsClient,
    ILogger<ApDashboardQueryService> logger)
  {
    _tenantContext = tenantContext;
    _workflowRepository = workflowRepository;
    _formDataLoader = formDataLoader;
    _insightsClient = insightsClient;
    _logger = logger;
  }

  public async Task<ApDashboardResult> GetDashboardAsync(
    Guid tenantId,
    ApDashboardRequest request,
    CancellationToken cancellationToken = default)
  {
    var connectionString = _tenantContext.ConnectionString
      ?? throw new InvalidOperationException("Tenant connection string not resolved.");

    var (rangeStart, rangeEnd) = ResolveRange(request);
    var (prevStart, prevEnd) = ResolvePreviousRange(request.Period, rangeStart, rangeEnd);

    var workflows = await _workflowRepository.ListAsync(cancellationToken);
    var workflowBySuffix = workflows.ToDictionary(
      w => w.Id.ToString("N")[..8],
      w => w,
      StringComparer.OrdinalIgnoreCase);

    var allInvoices = new List<ApDashboardInvoiceDto>();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var agentTables = await DiscoverAgentValidationTablesAsync(connection, cancellationToken);
    if (agentTables.Count == 0)
    {
      _logger.LogWarning(
        "AP dashboard: no workflow.agentDataValidation_* tables found for tenant {TenantId}.",
        tenantId);
    }

    foreach (var agentTable in agentTables)
    {
      var suffix = AgentTableSuffix(agentTable);
      if (request.WorkflowId is Guid wfId
          && !string.Equals(suffix, wfId.ToString("N")[..8], StringComparison.OrdinalIgnoreCase))
        continue;

      workflowBySuffix.TryGetValue(suffix, out var workflow);
      var workflowId = workflow?.Id ?? request.WorkflowId ?? Guid.Empty;
      if (workflowId == Guid.Empty)
      {
        workflowId = workflows.FirstOrDefault(w =>
            w.Id.ToString("N").StartsWith(suffix, StringComparison.OrdinalIgnoreCase))?.Id
          ?? Guid.Empty;
      }

      var workflowName = workflow?.Name ?? $"AP Workflow ({suffix})";

      try
      {
        var rows = await LoadAgentTableInvoicesAsync(
          connection,
          agentTable,
          suffix,
          workflowId,
          workflowName,
          cancellationToken);
        _logger.LogInformation(
          "AP dashboard loaded {RowCount} invoice row(s) from workflow.{AgentTable}.",
          rows.Count,
          agentTable);
        allInvoices.AddRange(rows);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "AP dashboard skipped agent table {AgentTable}.", agentTable);
      }
    }

    if (allInvoices.Count == 0)
      _logger.LogWarning(
        "AP dashboard loaded 0 invoices for tenant {TenantId} from {AgentTableCount} agent table(s).",
        tenantId,
        agentTables.Count);

    // Build dropdown options from the selected period (before extra filters),
    // so UI still shows available departments/suppliers for the day/month window.
    var inPeriod = allInvoices
      .Where(i => InRange(ResolvePeriodDate(i, request.Period), rangeStart, rangeEnd))
      .ToList();
    var filterOptions = ApDashboardFilterSupport.BuildFilterOptions(inPeriod);
    var current = ApDashboardFilterSupport.ApplyFilters(inPeriod, request);
    var previous = ApDashboardFilterSupport.ApplyFilters(
      allInvoices.Where(i => InRange(ResolvePeriodDate(i, request.Period), prevStart, prevEnd)).ToList(),
      request);

    var result = ApDashboardBuilder.Build(
      request,
      rangeStart,
      rangeEnd,
      prevStart,
      prevEnd,
      current,
      previous,
      filterOptions);

    var insights = await _insightsClient.GetInsightsAsync(result, cancellationToken);
    return result with { Insights = insights };
  }

  private async Task<List<ApDashboardInvoiceDto>> LoadAgentTableInvoicesAsync(
    NpgsqlConnection connection,
    string agentTable,
    string suffix,
    Guid workflowId,
    string workflowName,
    CancellationToken cancellationToken)
  {
    // Core source: latest AgentResponse per instance — no joins required.
    var sql = $"""
SELECT
    a.process_id AS instance_id,
    a.agent_response,
    a.created_at AS agent_created_at
FROM (
    SELECT process_id, MAX(id) AS max_id
    FROM workflow.{agentTable}
    WHERE is_deleted = false
    GROUP BY process_id
) latest
INNER JOIN workflow.{agentTable} a ON a.id = latest.max_id
WHERE a.is_deleted = false;
""";

    var list = new List<RawAgentRow>();
    await using (var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 60 })
    {
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken))
      {
        list.Add(new RawAgentRow(
          reader.GetGuid(0),
          reader.IsDBNull(1) ? null : reader.GetString(1),
          reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2)));
      }
    }

    var invoices = new List<ApDashboardInvoiceDto>(list.Count);
    foreach (var row in list)
    {
      var fields = ParseAgentFields(row.AgentJson);
      ApplyApFieldAliases(fields);

      if (NeedsFormFallback(fields))
      {
        try
        {
          await TryMergeFormFieldsFromProcessFormAsync(
            connection,
            suffix,
            row.InstanceId,
            fields,
            cancellationToken);
        }
        catch (Exception ex)
        {
          _logger.LogDebug(ex, "AP dashboard form fallback skipped for instance {InstanceId}.", row.InstanceId);
        }
      }

      InstanceEnrichment enrichment;
      try
      {
        enrichment = await TryLoadInstanceEnrichmentAsync(
          connection,
          suffix,
          row.InstanceId,
          cancellationToken);
      }
      catch (Exception ex)
      {
        _logger.LogDebug(ex, "AP dashboard enrichment skipped for instance {InstanceId}.", row.InstanceId);
        enrichment = new InstanceEnrichment(null, null, null, null, null, null);
      }

      invoices.Add(MapInvoice(
        workflowId,
        workflowName,
        row.InstanceId,
        enrichment.Reference,
        fields,
        enrichment.InstanceStatus,
        enrichment.Review,
        enrichment.StageType,
        enrichment.StartedAt,
        enrichment.CompletedAt,
        row.AgentCreatedAt));
    }

    return invoices;
  }

  private sealed record RawAgentRow(Guid InstanceId, string? AgentJson, DateTime? AgentCreatedAt);

  private sealed record InstanceEnrichment(
    string? Reference,
    string? InstanceStatus,
    string? Review,
    string? StageType,
    DateTime? StartedAt,
    DateTime? CompletedAt);

  private static bool NeedsFormFallback(IReadOnlyDictionary<string, string> fields) =>
    fields.Count == 0
    || (!fields.ContainsKey("Amount") && !fields.ContainsKey("InvoiceAmount") && !fields.ContainsKey("Total Due"))
    || (!fields.ContainsKey("Supplier") && !fields.ContainsKey("Vendor Name") && !fields.ContainsKey("Supplier Name"));

  private static void ApplyApFieldAliases(Dictionary<string, string> fields)
  {
    CopyAlias(fields, "Supplier Name", "Supplier");
    CopyAlias(fields, "Vendor Name", "Supplier");
    CopyAlias(fields, "Total Due", "Amount");
    CopyAlias(fields, "Invoice Amount", "Amount");
    CopyAlias(fields, "Invoice Date", "DocumentDate");
    CopyAlias(fields, "decision", "MatchedStatus");
  }

  private static void CopyAlias(Dictionary<string, string> fields, string sourceKey, string targetKey)
  {
    if (fields.ContainsKey(targetKey))
      return;
    if (fields.TryGetValue(sourceKey, out var value) && !string.IsNullOrWhiteSpace(value))
      fields[targetKey] = value;
  }

  private async Task TryMergeFormFieldsFromProcessFormAsync(
    NpgsqlConnection connection,
    string suffix,
    Guid instanceId,
    Dictionary<string, string> fields,
    CancellationToken cancellationToken)
  {
    var processFormTable = $"process_form_{suffix}";
    if (!await TableExistsAsync(connection, processFormTable, cancellationToken))
      return;

    // PHASE 4: workflow.process_form_{suffix} always has a fixed w_form_id column on Postgres
    // (see WorkflowTableCreator.cs) -- no WFormId/FormId column-name drift to detect.
    var sql = $"""
SELECT
    pf.w_form_id AS form_id,
    pf.form_entry_id
FROM workflow.{processFormTable} pf
WHERE pf.is_deleted = false AND pf.workflow_instance_id = @InstanceId
ORDER BY pf.created_at DESC, pf.id DESC
LIMIT 1;
""";

    await using var cmd = new NpgsqlCommand(sql, connection);
    cmd.Parameters.AddWithValue("@InstanceId", instanceId);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
      return;

    var formId = ReadScalarString(reader, 0);
    var formEntryId = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
    if (string.IsNullOrWhiteSpace(formId) || formEntryId <= 0)
      return;

    var formJson = await _formDataLoader.LoadFormDataJsonAsync(formId, formEntryId, cancellationToken);
    MergeFormFields(fields, formJson);
    ApplyApFieldAliases(fields);
  }

  private static async Task<InstanceEnrichment> TryLoadInstanceEnrichmentAsync(
    NpgsqlConnection connection,
    string suffix,
    Guid instanceId,
    CancellationToken cancellationToken)
  {
    var instancesTable = $"workflow_instances_{suffix}";
    var transactionTable = $"transaction_{suffix}";
    string? reference = null;
    string? instanceStatus = null;
    DateTime? startedAt = null;
    DateTime? completedAt = null;
    string? review = null;
    string? stageType = null;

    if (await TableExistsAsync(connection, instancesTable, cancellationToken))
    {
      var sql = $"""
SELECT i.reference_number, i.status, i.started_at_utc, i.completed_at_utc
FROM workflow.{instancesTable} i
WHERE i.id = @InstanceId
LIMIT 1;
""";
      await using var cmd = new NpgsqlCommand(sql, connection);
      cmd.Parameters.AddWithValue("@InstanceId", instanceId);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      if (await reader.ReadAsync(cancellationToken))
      {
        reference = ReadScalarString(reader, 0);
        instanceStatus = MapInstanceStatus(ReadScalarString(reader, 1));
        startedAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
        completedAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
      }
    }

    if (await TableExistsAsync(connection, transactionTable, cancellationToken))
    {
      var sql = $"""
SELECT t.review, t.stage_type
FROM workflow.{transactionTable} t
WHERE t.is_deleted = false AND t.workflow_instance_id = @InstanceId
ORDER BY t.created_at DESC, t.id DESC
LIMIT 1;
""";
      await using var cmd = new NpgsqlCommand(sql, connection);
      cmd.Parameters.AddWithValue("@InstanceId", instanceId);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      if (await reader.ReadAsync(cancellationToken))
      {
        review = ReadScalarString(reader, 0);
        stageType = ReadScalarString(reader, 1);
      }
    }

    return new InstanceEnrichment(reference, instanceStatus, review, stageType, startedAt, completedAt);
  }

  private static string? ReadScalarString(NpgsqlDataReader reader, int ordinal)
  {
    if (reader.IsDBNull(ordinal))
      return null;

    return Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
  }

  private static string? MapInstanceStatus(string? raw)
  {
    if (string.IsNullOrWhiteSpace(raw))
      return null;

    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
        && Enum.IsDefined(typeof(WorkflowInstanceStatus), code))
      return Enum.GetName(typeof(WorkflowInstanceStatus), code);

    return raw;
  }

  private static async Task<IReadOnlyList<string>> DiscoverAgentValidationTablesAsync(
    NpgsqlConnection connection,
    CancellationToken cancellationToken)
  {
    const string sql = """
      SELECT table_name
      FROM information_schema.tables
      WHERE table_schema = 'workflow'
        AND table_name LIKE 'agent\_data\_validation\_%' ESCAPE '\'
      ORDER BY table_name
      """;
    var tables = new List<string>();
    await using var cmd = new NpgsqlCommand(sql, connection);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
      tables.Add(reader.GetString(0));
    return tables;
  }

  private static string AgentTableSuffix(string tableName)
  {
    const string prefix = "agent_data_validation_";
    return tableName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
      ? tableName[prefix.Length..]
      : tableName;
  }

  private static Dictionary<string, string> ParseAgentFields(string? agentJson)
  {
    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(agentJson))
      return fields;

    try
    {
      using var doc = JsonDocument.Parse(agentJson);
      try
      {
        var (parsed, _) = ApAgentMetadataParser.ParseFieldsPayload(doc.RootElement);
        foreach (var (k, v) in parsed)
          fields[k] = v;
      }
      catch (JsonException)
      {
        FlattenScalarJsonProperties(doc.RootElement, fields);
      }

      MergeMatchingAgentDebugFields(doc.RootElement, fields);
      ApplyApFieldAliases(fields);
    }
    catch (JsonException)
    {
      // ignore invalid json
    }

    return fields;
  }

  private static void FlattenScalarJsonProperties(JsonElement root, Dictionary<string, string> fields)
  {
    if (root.ValueKind != JsonValueKind.Object)
      return;

    foreach (var prop in root.EnumerateObject())
    {
      if (prop.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
        continue;
      var val = prop.Value.ToString();
      if (!string.IsNullOrWhiteSpace(val))
        fields[prop.Name] = val;
    }
  }

  private static void MergeFormFields(Dictionary<string, string> target, string? formJson)
  {
    if (string.IsNullOrWhiteSpace(formJson))
      return;

    try
    {
      using var doc = JsonDocument.Parse(formJson);
      if (doc.RootElement.ValueKind != JsonValueKind.Object)
        return;

      foreach (var prop in doc.RootElement.EnumerateObject())
      {
        if (prop.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
          continue;
        var val = prop.Value.ToString();
        if (string.IsNullOrWhiteSpace(val))
          continue;
        if (!target.ContainsKey(prop.Name))
          target[prop.Name] = val;
      }
    }
    catch (JsonException)
    {
      // ignore
    }
  }

  private static ApDashboardInvoiceDto MapInvoice(
    Guid workflowId,
    string workflowName,
    Guid instanceId,
    string? reference,
    IReadOnlyDictionary<string, string> fields,
    string? instanceStatus,
    string? review,
    string? stageType,
    DateTime? startedAt,
    DateTime? completedAt,
    DateTime? agentCreatedAt)
  {
    var supplier = FirstField(fields, "Supplier", "Supplier Name", "Vendor Name", "VendorName", "Vendor");
    var amount = ParseDecimal(FirstField(fields, "Amount", "Invoice Amount", "InvoiceAmount", "Total Amount", "Total Due", "PO Amount"));
    var currency = FirstField(fields, "Currency") ?? "USD";
    var invoiceDate = ParseDate(FirstField(fields, "DocumentDate", "Invoice Date", "InvoiceDate", "PO Date"));
    var dueDate = ParseDate(FirstField(fields, "DueDate", "Due Date", "Payment Due Date"))
      ?? invoiceDate?.AddDays(ParseTermsDays(FirstField(fields, "Terms", "TERMS")));
    var department = FirstField(fields, "Department", "Cost Center", "CostCenter", "GL Category", "GlCategory") ?? "General";
    var matchedStatus = FirstField(fields, "MatchedStatus", "Matched Status", "decision", "AiStatus");
    var country = ResolveCountry(fields, currency);
    var paymentStatus = ResolvePaymentStatus(instanceStatus, review, stageType, dueDate);
    var approvalStatus = ApDashboardFilterSupport.ResolveApprovalStatus(paymentStatus, matchedStatus, review);
    var requestStatus = ApDashboardFilterSupport.ResolveRequestStatus(instanceStatus);
    var processingDays = ComputeProcessingDays(startedAt, completedAt, agentCreatedAt);
    var riskLevel = ResolveInvoiceRiskLevel(paymentStatus, matchedStatus, amount, dueDate);

    return new ApDashboardInvoiceDto(
      workflowId,
      workflowName,
      instanceId,
      reference,
      string.IsNullOrWhiteSpace(supplier) ? "Unknown" : supplier,
      amount,
      currency,
      invoiceDate,
      dueDate,
      department,
      country,
      paymentStatus,
      approvalStatus,
      requestStatus,
      matchedStatus,
      riskLevel,
      agentCreatedAt ?? startedAt,
      processingDays);
  }

  private static string ResolveInvoiceRiskLevel(
    string paymentStatus,
    string? matchedStatus,
    decimal amount,
    DateTime? dueDate)
  {
    var score = 0;
    var matched = matchedStatus ?? string.Empty;

    if (string.Equals(paymentStatus, "overdue", StringComparison.OrdinalIgnoreCase))
      score += 3;
    else if (string.Equals(paymentStatus, "due_today", StringComparison.OrdinalIgnoreCase))
      score += 2;
    else if (string.Equals(paymentStatus, "pending", StringComparison.OrdinalIgnoreCase))
      score += 1;

    if (matched.Contains("reject", StringComparison.OrdinalIgnoreCase)
        || matched.Contains("unmatch", StringComparison.OrdinalIgnoreCase)
        || matched.Contains("mismatch", StringComparison.OrdinalIgnoreCase))
      score += 3;
    else if (matched.Contains("partial", StringComparison.OrdinalIgnoreCase))
      score += 2;
    else if (matched.Contains("match", StringComparison.OrdinalIgnoreCase)
             || matched.Contains("approv", StringComparison.OrdinalIgnoreCase))
      score -= 1;

    if (amount >= 100_000m)
      score += 2;
    else if (amount >= 10_000m)
      score += 1;

    if (dueDate.HasValue)
    {
      var daysPastDue = (DateTime.UtcNow.Date - dueDate.Value.Date).TotalDays;
      if (daysPastDue > 30)
        score += 2;
      else if (daysPastDue > 0)
        score += 1;
    }

    if (score >= 4)
      return "high";
    if (score >= 2)
      return "medium";
    return "low";
  }

  private static string ResolvePaymentStatus(
    string? instanceStatus,
    string? review,
    string? stageType,
    DateTime? dueDate)
  {
    var reviewNorm = review?.Trim() ?? string.Empty;
    var stageNorm = stageType?.Trim() ?? string.Empty;
    var statusNorm = instanceStatus?.Trim() ?? string.Empty;

    if (reviewNorm.Contains("paid", StringComparison.OrdinalIgnoreCase)
        || statusNorm.Equals("Completed", StringComparison.OrdinalIgnoreCase)
        && stageNorm.Equals("END", StringComparison.OrdinalIgnoreCase))
      return "paid";

    if (dueDate.HasValue)
    {
      var today = DateTime.UtcNow.Date;
      if (dueDate.Value.Date < today)
        return "overdue";
      if (dueDate.Value.Date == today)
        return "due_today";
    }

    if (reviewNorm.Contains("approv", StringComparison.OrdinalIgnoreCase)
        || reviewNorm.Contains("verified", StringComparison.OrdinalIgnoreCase))
      return "approved";

    return "pending";
  }

  private static decimal? ComputeProcessingDays(
    DateTime? startedAt,
    DateTime? completedAt,
    DateTime? agentCreatedAt)
  {
    var start = startedAt ?? agentCreatedAt;
    var end = completedAt ?? agentCreatedAt;
    if (!start.HasValue || !end.HasValue)
      return null;
    return (decimal)Math.Max(0, (end.Value - start.Value).TotalDays);
  }

  private static string ResolveCountry(IReadOnlyDictionary<string, string> fields, string? currency)
  {
    var explicitCountry = FirstField(
      fields,
      "Country",
      "CountryCode",
      "Supplier Country",
      "SupplierCountry",
      "Vendor Country",
      "VendorCountry",
      "Nation",
      "Billing Country",
      "Ship Country");
    var mapped = MapCountryToken(explicitCountry);
    if (mapped != null)
      return mapped;

    foreach (var address in EnumerateAddressCandidates(fields))
    {
      mapped = InferCountryFromAddress(address);
      if (mapped != null)
        return mapped;
    }

    mapped = InferCountryFromCurrency(currency);
    return mapped ?? "UN";
  }

  private static IEnumerable<string> EnumerateAddressCandidates(IReadOnlyDictionary<string, string> fields)
  {
    foreach (var key in new[]
             {
               "SupplierAddress", "Supplier Address", "Vendor Address", "VendorAddress",
               "ShipToAddress", "Ship To Address", "Ship-To Address",
               "PayToAddress", "Pay To Address", "Buyer Address", "Billing Address",
               "Remit Address", "RemitToAddress"
             })
    {
      var value = FirstField(fields, key);
      if (!string.IsNullOrWhiteSpace(value))
        yield return value;
    }
  }

  private static string? InferCountryFromAddress(string address)
  {
    if (string.IsNullOrWhiteSpace(address))
      return null;

    var normalized = address.Trim();
    var mappedWhole = MapCountryToken(normalized);
    if (mappedWhole != null)
      return mappedWhole;

    // Canada postal: A1A 1A1 (optional space)
    if (Regex.IsMatch(normalized, @"\b[A-CEGHJ-NPR-TVXY]\d[A-Z]\s?\d[A-Z]\d\b", RegexOptions.IgnoreCase))
      return "CA";

    // US ZIP: 12345 or 12345-6789
    if (Regex.IsMatch(normalized, @"\b\d{5}(?:-\d{4})?\b"))
    {
      // Prefer US when a US state code also appears; otherwise still likely US for AP invoices.
      if (ContainsUsState(normalized) || ContainsUsStateCode(normalized))
        return "US";
    }

    if (ContainsCanadianProvince(normalized))
      return "CA";

    if (ContainsUsState(normalized) || ContainsUsStateCode(normalized))
      return "US";

    foreach (var part in normalized.Split(
                 new[] { ',', '|', ';', '/' },
                 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      mappedWhole = MapCountryToken(part);
      if (mappedWhole != null)
        return mappedWhole;

      // "ON M5V 2T6" / "Toronto ON"
      if (ContainsCanadianProvince(part) ||
          Regex.IsMatch(part, @"\b[A-CEGHJ-NPR-TVXY]\d[A-Z]\s?\d[A-Z]\d\b", RegexOptions.IgnoreCase))
        return "CA";
    }

    // Scan known country names embedded anywhere in the address (any ISO country via RegionInfo).
    foreach (var (token, code) in ApCountryCatalog.NameTokensLongestFirst)
    {
      if (normalized.Contains(token, StringComparison.OrdinalIgnoreCase))
        return code;
    }

    return null;
  }

  private static string? InferCountryFromCurrency(string? currency)
  {
    if (string.IsNullOrWhiteSpace(currency))
      return null;

    return currency.Trim().ToUpperInvariant() switch
    {
      "CAD" => "CA",
      "INR" or "RS" or "₹" => "IN",
      "GBP" or "£" => "GB",
      "AUD" => "AU",
      "NZD" => "NZ",
      "JPY" or "¥" => "JP",
      "CNY" or "RMB" => "CN",
      "MXN" => "MX",
      "BRL" => "BR",
      "SGD" => "SG",
      "AED" => "AE",
      "CHF" => "CH",
      "SEK" => "SE",
      "NOK" => "NO",
      "DKK" => "DK",
      "ZAR" => "ZA",
      "KRW" => "KR",
      "TRY" or "TL" => "TR",
      "PLN" => "PL",
      "THB" => "TH",
      "MYR" => "MY",
      "PHP" => "PH",
      "IDR" => "ID",
      "VND" => "VN",
      "HKD" => "HK",
      "TWD" => "TW",
      "SAR" => "SA",
      "QAR" => "QA",
      "KWD" => "KW",
      "EGP" => "EG",
      "NGN" => "NG",
      "KES" => "KE",
      "RUB" => "RU",
      // USD/EUR are too ambiguous globally — do not invent a country from them alone.
      _ => null
    };
  }

  private static string? MapCountryToken(string? raw) => ApCountryCatalog.MapToCode(raw);

  private static readonly HashSet<string> CanadianProvinces = new(StringComparer.OrdinalIgnoreCase)
  {
    "AB", "BC", "MB", "NB", "NL", "NS", "NT", "NU", "ON", "PE", "QC", "SK", "YT",
    "Alberta", "British Columbia", "Manitoba", "New Brunswick", "Newfoundland",
    "Nova Scotia", "Ontario", "Prince Edward Island", "Quebec", "Saskatchewan",
    "Northwest Territories", "Nunavut", "Yukon"
  };

  private static readonly HashSet<string> UsStateCodes = new(StringComparer.OrdinalIgnoreCase)
  {
    "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA",
    "KS", "KY", "LA", "ME", "MD", "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ",
    "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT", "VT",
    "VA", "WA", "WV", "WI", "WY", "DC"
  };

  private static readonly string[] UsStateNames =
  [
    "Alabama", "Alaska", "Arizona", "Arkansas", "California", "Colorado", "Connecticut", "Delaware",
    "Florida", "Georgia", "Hawaii", "Idaho", "Illinois", "Indiana", "Iowa", "Kansas", "Kentucky",
    "Louisiana", "Maine", "Maryland", "Massachusetts", "Michigan", "Minnesota", "Mississippi",
    "Missouri", "Montana", "Nebraska", "Nevada", "New Hampshire", "New Jersey", "New Mexico",
    "New York", "North Carolina", "North Dakota", "Ohio", "Oklahoma", "Oregon", "Pennsylvania",
    "Rhode Island", "South Carolina", "South Dakota", "Tennessee", "Texas", "Utah", "Vermont",
    "Virginia", "Washington", "West Virginia", "Wisconsin", "Wyoming", "District of Columbia"
  ];

  private static bool ContainsCanadianProvince(string text)
  {
    // Word-boundary check so "ON" in "Toronto ON M9L" matches, but not random mid-words.
    foreach (var province in CanadianProvinces)
    {
      if (province.Length == 2)
      {
        if (Regex.IsMatch(text, $@"\b{province}\b", RegexOptions.IgnoreCase))
          return true;
      }
      else if (text.Contains(province, StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }
    }

    return false;
  }

  private static bool ContainsUsStateCode(string text) =>
    UsStateCodes.Any(code => Regex.IsMatch(text, $@"\b{code}\b", RegexOptions.IgnoreCase));

  private static bool ContainsUsState(string text) =>
    UsStateNames.Any(name => text.Contains(name, StringComparison.OrdinalIgnoreCase));

  private static (DateTime Start, DateTime End) ResolveRange(ApDashboardRequest request)
  {
    if (request.Period == ApDashboardPeriod.Custom
        && request.FromUtc.HasValue
        && request.ToUtc.HasValue)
      return (request.FromUtc.Value, request.ToUtc.Value);

    var now = DateTime.UtcNow;
    return request.Period switch
    {
      ApDashboardPeriod.Today => DayRange(now),
      ApDashboardPeriod.Tomorrow => DayRange(now.AddDays(1)),
      ApDashboardPeriod.ThisWeek => WeekRange(now),
      ApDashboardPeriod.LastMonth => MonthRange(now.AddMonths(-1)),
      ApDashboardPeriod.ThisQuarter => QuarterRange(now),
      ApDashboardPeriod.ThisYear => (new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc), now),
      _ => MonthRange(now)
    };
  }

  private static (DateTime Start, DateTime End) ResolvePreviousRange(
    ApDashboardPeriod period,
    DateTime currentStart,
    DateTime currentEnd)
  {
    // Align previous window to the same calendar shape as the selected period.
    return period switch
    {
      ApDashboardPeriod.Today or ApDashboardPeriod.Tomorrow => DayRange(currentStart.AddDays(-1)),
      ApDashboardPeriod.ThisWeek => WeekRange(currentStart.AddDays(-7)),
      ApDashboardPeriod.ThisMonth or ApDashboardPeriod.LastMonth => MonthRange(currentStart.AddMonths(-1)),
      ApDashboardPeriod.ThisQuarter => PreviousQuarterRange(currentStart),
      ApDashboardPeriod.ThisYear => (
        new DateTime(currentStart.Year - 1, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(currentStart.Year - 1, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9999999)),
      _ => SameLengthPriorWindow(currentStart, currentEnd)
    };
  }

  private static (DateTime Start, DateTime End) SameLengthPriorWindow(DateTime currentStart, DateTime currentEnd)
  {
    var span = currentEnd - currentStart;
    return (currentStart - span, currentStart.AddTicks(-1));
  }

  private static (DateTime Start, DateTime End) PreviousQuarterRange(DateTime currentStart)
  {
    var prior = currentStart.AddMonths(-3);
    var quarter = (prior.Month - 1) / 3;
    var startMonth = quarter * 3 + 1;
    var start = new DateTime(prior.Year, startMonth, 1, 0, 0, 0, DateTimeKind.Utc);
    var end = start.AddMonths(3).AddTicks(-1);
    return (start, end);
  }

  private static (DateTime Start, DateTime End) MonthRange(DateTime anchor)
  {
    var start = new DateTime(anchor.Year, anchor.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    var end = start.AddMonths(1).AddTicks(-1);
    return (start, end);
  }

  private static (DateTime Start, DateTime End) DayRange(DateTime anchor)
  {
    var start = new DateTime(anchor.Year, anchor.Month, anchor.Day, 0, 0, 0, DateTimeKind.Utc);
    var end = start.AddDays(1).AddTicks(-1);
    return (start, end);
  }

  /// <summary>Monday 00:00 UTC through Sunday 23:59:59.999 UTC of the week containing <paramref name="anchor"/>.</summary>
  private static (DateTime Start, DateTime End) WeekRange(DateTime anchor)
  {
    var day = new DateTime(anchor.Year, anchor.Month, anchor.Day, 0, 0, 0, DateTimeKind.Utc);
    // Monday = start of week (ISO-style)
    var diff = ((int)day.DayOfWeek + 6) % 7; // Sunday=6, Monday=0, ...
    var start = day.AddDays(-diff);
    var end = start.AddDays(7).AddTicks(-1);
    return (start, end);
  }

  private static (DateTime Start, DateTime End) QuarterRange(DateTime anchor)
  {
    var quarter = (anchor.Month - 1) / 3;
    var startMonth = quarter * 3 + 1;
    var start = new DateTime(anchor.Year, startMonth, 1, 0, 0, 0, DateTimeKind.Utc);
    return (start, anchor);
  }

  /// <summary>
  /// Period date for filtering.
  /// Today / Tomorrow / ThisWeek prefer due date (cash / AP calendar); other periods prefer agent CreatedAt.
  /// </summary>
  private static DateTime? ResolvePeriodDate(ApDashboardInvoiceDto invoice, ApDashboardPeriod period) =>
    period is ApDashboardPeriod.Today or ApDashboardPeriod.Tomorrow or ApDashboardPeriod.ThisWeek
      ? invoice.DueDate ?? invoice.CreatedAtUtc ?? invoice.InvoiceDate
      : invoice.CreatedAtUtc ?? invoice.InvoiceDate ?? invoice.DueDate;

  private static bool InRange(DateTime? value, DateTime start, DateTime end)
  {
    if (!value.HasValue)
      return false;

    var normalized = value.Value.Kind == DateTimeKind.Unspecified
      ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
      : value.Value.ToUniversalTime();

    return normalized >= start && normalized <= end;
  }

  private static void MergeMatchingAgentDebugFields(JsonElement root, Dictionary<string, string> fields)
  {
    if (!TryGetPropertyIgnoreCase(root, "debug", out var debug) || debug.ValueKind != JsonValueKind.Object)
      return;

    foreach (var prop in debug.EnumerateObject())
    {
      if (prop.Value.ValueKind != JsonValueKind.Array
          || !prop.Name.Contains("Field Matching", StringComparison.OrdinalIgnoreCase))
        continue;

      foreach (var row in prop.Value.EnumerateArray())
      {
        if (row.ValueKind != JsonValueKind.Object)
          continue;

        var label = ReadJsonString(row, "Field");
        var invoiceValue = ReadJsonString(row, "Invoice Value");
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(invoiceValue))
          continue;

        if (!fields.ContainsKey(label))
          fields[label] = invoiceValue.Trim();
      }
    }
  }

  private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
  {
    foreach (var prop in obj.EnumerateObject())
    {
      if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
        continue;

      value = prop.Value;
      return true;
    }

    value = default;
    return false;
  }

  private static string? ReadJsonString(JsonElement obj, string name)
  {
    if (!TryGetPropertyIgnoreCase(obj, name, out var value))
      return null;

    return value.ValueKind switch
    {
      JsonValueKind.String => value.GetString(),
      JsonValueKind.Number => value.GetRawText(),
      JsonValueKind.True => "true",
      JsonValueKind.False => "false",
      _ => null
    };
  }

  private static string? FirstField(IReadOnlyDictionary<string, string> fields, params string[] keys)
  {
    foreach (var key in keys)
    {
      if (fields.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
        return val.Trim();
    }
    return null;
  }

  private static decimal ParseDecimal(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
      return 0;
    var cleaned = value.Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Trim();
    return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
  }

  private static DateTime? ParseDate(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
      return null;
    if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
      return d;
    if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeUniversal, out d))
      return d;
    return null;
  }

  private static int ParseTermsDays(string? terms)
  {
    if (string.IsNullOrWhiteSpace(terms))
      return 30;
    var digits = new string(terms.Where(char.IsDigit).ToArray());
    return int.TryParse(digits, out var days) && days > 0 ? days : 30;
  }

  private static async Task<bool> TableExistsAsync(
    NpgsqlConnection connection,
    string tableName,
    CancellationToken cancellationToken)
  {
    const string sql = """
      SELECT 1
      FROM information_schema.tables
      WHERE table_schema = 'workflow' AND table_name = @TableName
      """;
    await using var cmd = new NpgsqlCommand(sql, connection);
    cmd.Parameters.AddWithValue("@TableName", tableName);
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result != null && result != DBNull.Value;
  }
}
