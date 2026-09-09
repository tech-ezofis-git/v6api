using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Reporting.Application.Contracts;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Reporting.Infrastructure.Services;

public sealed class ReportQueryService : IReportQueryService
{
    private const int MaxRows = 10_000;

    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IWorkflowRepository _workflows;

    public ReportQueryService(
        ITenantConnectionProvider connectionProvider,
        IWorkflowRepository workflows)
    {
        _connectionProvider = connectionProvider;
        _workflows = workflows;
    }

    public async Task<IReadOnlyList<ReportDomainDto>> ListDomainsAsync(CancellationToken cancellationToken = default)
    {
        var workflows = await _workflows.ListAsync(cancellationToken);
        return workflows
            .Select(w => new ReportDomainDto(w.Id, w.Name, w.FormId, w.Description))
            .ToList();
    }

    public async Task<IReadOnlyList<ReportSourceFormDto>> ListSourceFormsAsync(
        string? sourceType,
        CancellationToken cancellationToken = default)
    {
        _ = sourceType;
        var connectionString = RequireConnection();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var forms = await ReportEzfbNaming.ListWFormsAsync(connection, cancellationToken);
        return forms
            .Select(f => new ReportSourceFormDto(f.FormId, f.FormName, f.Type))
            .ToList();
    }

    public async Task<IReadOnlyList<ReportAvailableFieldDto>> ListFieldsAsync(
        Guid tenantId,
        string? domain,
        string? sourceFormId,
        string? sourceForm = null,
        string? sourceType = null,
        CancellationToken cancellationToken = default)
    {
        var connectionString = RequireConnection();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var stub = new ReportBuilderConfig
        {
            Domain = domain ?? string.Empty,
            SourceFormId = sourceFormId,
            SourceForm = sourceForm,
            SourceType = sourceType
        };
        var workflow = await ResolveWorkflowAsync(tenantId, stub, cancellationToken);
        var (formId, _) = await ResolveFormTableAsync(connection, workflow, stub, cancellationToken);

        var controls = await LoadControlsAsync(connection, formId, cancellationToken);
        return controls
            .Where(c => !string.IsNullOrWhiteSpace(c.JsonId) || !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new ReportAvailableFieldDto(
                c.JsonId ?? c.Name!,
                string.IsNullOrWhiteSpace(c.Name) ? c.JsonId! : c.Name!,
                c.Type,
                c.IsMandatory))
            .ToList();
    }

    public async Task<ReportRunResult> ExecuteAsync(
        Guid tenantId,
        ReportBuilderConfig config,
        CancellationToken cancellationToken = default)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        var workflow = await ResolveWorkflowAsync(tenantId, config, cancellationToken);
        var connectionString = RequireConnection();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await ReportEzfbNaming.EnsureTryCastFunctionsAsync(connection, cancellationToken);

        var (formId, tableName) = await ResolveFormTableAsync(connection, workflow, config, cancellationToken);

        config.SourceFormId = formId;
        if (workflow != null)
            config.WorkflowId = workflow.Id;

        var ezfbColumns = await ReportEzfbNaming.LoadTableColumnsAsync(connection, tableName, cancellationToken);
        var controls = await LoadControlsAsync(connection, formId, cancellationToken);
        var columns = BuildColumns(config, controls, ezfbColumns);

        string? processFormTable = null;
        if (workflow != null)
        {
            var pfName = $"process_form_{ReportEzfbNaming.WorkflowTableSuffix(workflow.Id)}";
            if (await ReportEzfbNaming.TableExistsAsync(connection, "workflow", pfName, cancellationToken))
                processFormTable = pfName;
        }

        var rows = await TryReadRowsAsync(
            connection, tableName, processFormTable, applyDeletedFilter: true,
            columns, config.Filters, ezfbColumns, controls, cancellationToken);
        if (rows.Count == 0)
        {
            rows = await TryReadRowsAsync(
                connection, tableName, processFormTable: null, applyDeletedFilter: true,
                columns, config.Filters, ezfbColumns, controls, cancellationToken);
        }

        if (rows.Count == 0)
        {
            rows = await TryReadRowsAsync(
                connection, tableName, processFormTable: null, applyDeletedFilter: false,
                columns, config.Filters, ezfbColumns, controls, cancellationToken);
        }

        if (columns.All(c => string.IsNullOrWhiteSpace(c.SqlColumn) && !c.IsCalculated))
            throw new InvalidOperationException("None of the selected fields exist on the workflow form table.");

        ApplyCalculatedColumns(config, columns, rows);
        var totals = BuildTotals(columns, rows);

        return new ReportRunResult(
            config.Id == Guid.Empty ? null : config.Id,
            string.IsNullOrWhiteSpace(config.Name) ? "Untitled report" : config.Name.Trim(),
            config.Domain,
            columns.Select(c => new ReportColumnDto(c.Label, c.Label, c.Calc, c.ColType, c.Width, c.IsCalculated)).ToList(),
            rows,
            totals,
            rows.Count,
            workflow?.Id,
            formId);
    }

    private string RequireConnection() =>
        _connectionProvider.ConnectionString
        ?? throw new InvalidOperationException("Tenant connection string not resolved.");

    private async Task<ResolvedWorkflow?> ResolveWorkflowAsync(
        Guid tenantId,
        ReportBuilderConfig config,
        CancellationToken cancellationToken)
    {
        // Domain (workflow name) is the UI key — prefer it over a stale saved workflowId.
        if (!string.IsNullOrWhiteSpace(config.Domain))
        {
            var byName = await _workflows.GetByNameAsync(config.Domain.Trim(), tenantId, cancellationToken);
            if (byName != null)
                return new ResolvedWorkflow(byName.Id, byName.Name, byName.FormId);

            var all = await _workflows.ListAsync(cancellationToken);
            var match = all.FirstOrDefault(w =>
                string.Equals(w.Name, config.Domain.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return new ResolvedWorkflow(match.Id, match.Name, match.FormId);
        }

        if (config.WorkflowId is Guid wfId && wfId != Guid.Empty)
        {
            var byId = await _workflows.GetByIdAsync(wfId, cancellationToken);
            if (byId != null)
                return new ResolvedWorkflow(byId.Id, byId.Name, byId.FormId);
        }

        return null;
    }

    private async Task<(string FormId, string TableName)> ResolveFormTableAsync(
        NpgsqlConnection connection,
        ResolvedWorkflow? workflow,
        ReportBuilderConfig config,
        CancellationToken cancellationToken)
    {
        var wform = await FindSourceWFormAsync(connection, config, cancellationToken);
        if (wform != null)
        {
            var formId = ReportEzfbNaming.NormalizeFormId(wform.Value.FormId);
            config.SourceFormId = formId;
            if (!string.IsNullOrWhiteSpace(wform.Value.FormName))
                config.SourceForm = wform.Value.FormName;
            var tableName = ReportEzfbNaming.ItemsTable(formId);
            if (await ReportEzfbNaming.TableExistsAsync(connection, tableName, cancellationToken))
                return (formId, tableName);

            throw new InvalidOperationException(
                $"Source Form '{wform.Value.FormName}' (formId={formId}) has no data table dbo.\"{tableName}\". " +
                "Publish/save the form so ezfb_{formId first 8}_items is created.");
        }

        return await ResolveFormTableFromWorkflowAsync(
            connection, workflow, config.SourceFormId, config.Domain, cancellationToken);
    }

    private static async Task<(string FormId, string FormName, string? Type)?> FindSourceWFormAsync(
        NpgsqlConnection connection,
        ReportBuilderConfig config,
        CancellationToken cancellationToken)
    {
        foreach (var key in new[] { config.SourceFormId, config.SourceForm, config.Domain })
        {
            var match = await ReportEzfbNaming.FindWFormAsync(connection, key, cancellationToken);
            if (match != null)
                return match;
        }

        return null;
    }

    private async Task<(string FormId, string TableName)> ResolveFormTableFromWorkflowAsync(
        NpgsqlConnection connection,
        ResolvedWorkflow? workflow,
        string? sourceFormId,
        string? domain,
        CancellationToken cancellationToken)
    {
        var tried = new List<string>();
        string? processFormId = null;
        if (workflow != null)
        {
            try
            {
                processFormId = await ReportEzfbNaming.TryReadProcessFormWFormIdAsync(
                    connection, workflow.Id, cancellationToken);
            }
            catch
            {
                processFormId = null;
            }
        }

        var candidates = FormIdCandidates(workflow, sourceFormId, processFormId).ToList();
        if (candidates.Count == 0)
            throw new ArgumentException(
                "No form is linked to this workflow. Set the workflow FormId (initiateUsing.formId). " +
                $"workflowId={workflow?.Id.ToString("D") ?? "(unresolved)"}, domain={domain ?? "(none)"}.");

        foreach (var raw in candidates)
        {
            string formId;
            try
            {
                formId = ReportEzfbNaming.NormalizeFormId(raw);
            }
            catch
            {
                continue;
            }

            var tableName = ReportEzfbNaming.ItemsTable(formId);
            if (tried.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                continue;
            tried.Add(tableName);

            if (await ReportEzfbNaming.TableExistsAsync(connection, tableName, cancellationToken))
                return (formId, tableName);
        }

        var workflowIdText = workflow?.Id.ToString("D") ?? "(unresolved)";
        var workflowSuffix = workflow == null ? null : ReportEzfbNaming.WorkflowTableSuffix(workflow.Id);
        var linkedForm = string.IsNullOrWhiteSpace(workflow?.FormId) ? "(none)" : workflow!.FormId!;
        var expectedFromWorkflowForm = string.IsNullOrWhiteSpace(workflow?.FormId)
            ? "(set FormId on the workflow first)"
            : $"dbo.\"{ReportEzfbNaming.ItemsTable(ReportEzfbNaming.NormalizeFormId(workflow.FormId!))}\"";

        throw new InvalidOperationException(
            "Form data table was not found. " +
            "Workflow id locates the workflow; the ezfb table is named from the linked form id " +
            $"(first 8 characters of the form GUID), not from the workflow id. " +
            $"workflowId={workflowIdText}, workflow.FormId={linkedForm}, " +
            $"expected={expectedFromWorkflowForm}" +
            (workflowSuffix == null ? "" : $", processForm=workflow.process_form_{workflowSuffix}") +
            (tried.Count == 0 ? "" : $", tried={string.Join(", ", tried.Select(t => "dbo.\"" + t + "\""))}.") +
            $" Domain={domain ?? "(none)"}.");
    }

    private static IEnumerable<string> FormIdCandidates(
        ResolvedWorkflow? workflow,
        string? sourceFormId,
        string? processFormId)
    {
        // workflow.FormId on workflow.Workflows is the source of truth (e.g. 8aaadcde-f2f9-...).
        if (!string.IsNullOrWhiteSpace(workflow?.FormId))
            yield return workflow.FormId!;
        if (!string.IsNullOrWhiteSpace(processFormId)
            && !string.Equals(processFormId, workflow?.FormId, StringComparison.OrdinalIgnoreCase))
            yield return processFormId!;

        if (string.IsNullOrWhiteSpace(workflow?.FormId)
            && !string.IsNullOrWhiteSpace(sourceFormId)
            && (workflow == null || !ReportEzfbNaming.LooksLikeWorkflowId(sourceFormId, workflow.Id)))
            yield return sourceFormId!;
    }

    private async Task<List<ControlRow>> LoadControlsAsync(
        NpgsqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        if (!await ReportEzfbNaming.TableExistsAsync(connection, "wFormControl", cancellationToken))
            return [];

        await using (var alterCmd = new NpgsqlCommand(
            """ALTER TABLE dbo."wFormControl" ADD COLUMN IF NOT EXISTS "columnName" varchar(200) NULL;""",
            connection))
            await alterCmd.ExecuteNonQueryAsync(cancellationToken);

        var wFormId = await ReportEzfbNaming.ResolveWFormIdParameterAsync(connection, formId, cancellationToken);
        const string sql = """
            SELECT "jsonId", name, type, "isMandatory", "columnName"
            FROM dbo."wFormControl"
            WHERE "wFormId" = @FormId AND "isDeleted" = false
            ORDER BY "parentId", id;
            """;
        var rows = new List<ControlRow>();
        await using (var cmd = new NpgsqlCommand(sql, connection))
        {
            cmd.Parameters.AddWithValue("@FormId", wFormId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(ReadControl(reader));
            }
        }

        if (rows.Count == 0 && !Equals(wFormId, formId))
        {
            await using var retry = new NpgsqlCommand(sql, connection);
            retry.Parameters.AddWithValue("@FormId", formId);
            await using var retryReader = await retry.ExecuteReaderAsync(cancellationToken);
            while (await retryReader.ReadAsync(cancellationToken))
                rows.Add(ReadControl(retryReader));
        }

        return rows;
    }

    private static ControlRow ReadControl(NpgsqlDataReader reader) =>
        new(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            !reader.IsDBNull(3) && Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null : reader.GetString(4));

    private static List<ResolvedColumn> BuildColumns(
        ReportBuilderConfig config,
        IReadOnlyList<ControlRow> controls,
        IReadOnlySet<string> ezfbColumns)
    {
        var columns = new List<ResolvedColumn>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in config.Fields ?? [])
        {
            if (string.IsNullOrWhiteSpace(field))
                continue;

            string? jsonId = null;
            ReportFieldSetting? setting = null;
            if (config.FieldSettings != null)
            {
                foreach (var kv in config.FieldSettings)
                {
                    if (string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(kv.Value.Label, field, StringComparison.OrdinalIgnoreCase))
                    {
                        jsonId = kv.Key;
                        setting = kv.Value;
                        break;
                    }
                }
            }

            var control = FindControl(controls, jsonId, field);
            jsonId ??= control?.JsonId ?? field;
            var label = FirstNonEmpty(setting?.Label, control?.Name, field) ?? field;
            var key = label;
            if (!usedKeys.Add(key))
                key = $"{label}_{columns.Count}";

            var isCalculated = IsCalculated(setting?.ColType, setting?.Calc, setting?.Formula ?? setting?.Expression);
            string? sqlColumn = null;
            if (!isCalculated
                && ReportEzfbNaming.TryResolveColumn(control?.ColumnName, control?.Name ?? jsonId, jsonId, ezfbColumns, out var byStored)
                && !string.IsNullOrWhiteSpace(byStored))
                sqlColumn = byStored;
            if (sqlColumn is null && !isCalculated && ReportEzfbNaming.TryResolveColumn(jsonId, ezfbColumns, out var byJson))
                sqlColumn = byJson;
            if (sqlColumn is null && !isCalculated && control?.Name != null
                && ReportEzfbNaming.TryResolveColumn(control.Name, ezfbColumns, out var byName))
                sqlColumn = byName;
            if (sqlColumn is null && !isCalculated)
                ReportEzfbNaming.TryResolveColumn(field, ezfbColumns, out sqlColumn);

            columns.Add(new ResolvedColumn(
                key,
                label,
                sqlColumn,
                NormalizeCalc(setting?.Calc),
                isCalculated ? "calculated" : "value",
                setting?.Width is > 0 ? setting.Width.Value : 160,
                isCalculated,
                setting?.Formula ?? setting?.Expression,
                jsonId));
        }

        foreach (var custom in config.CustomFields ?? [])
        {
            var label = FirstNonEmpty(custom.Label, custom.Id) ?? "Calculated";
            var key = FirstNonEmpty(custom.Id, label) ?? Guid.NewGuid().ToString("N");
            if (!usedKeys.Add(key))
                key = $"{key}_{columns.Count}";
            columns.Add(new ResolvedColumn(
                key,
                label,
                null,
                NormalizeCalc(custom.Calc),
                "calculated",
                custom.Width is > 0 ? custom.Width.Value : 160,
                true,
                custom.Formula ?? custom.Expression,
                custom.Id));
        }

        if (columns.Count == 0)
        {
            foreach (var control in controls)
            {
                if (string.IsNullOrWhiteSpace(control.JsonId))
                    continue;
                if (!ReportEzfbNaming.TryResolveColumn(control.ColumnName, control.Name, control.JsonId, ezfbColumns, out var sqlColumn)
                    || string.IsNullOrWhiteSpace(sqlColumn))
                    continue;
                var label = FirstNonEmpty(control.Name, control.JsonId)!;
                columns.Add(new ResolvedColumn(control.JsonId, label, sqlColumn, "None", "value", 160, false, null, control.JsonId));
            }
        }

        return columns;
    }

    private static ControlRow? FindControl(IReadOnlyList<ControlRow> controls, string? jsonId, string field)
    {
        if (!string.IsNullOrWhiteSpace(jsonId))
        {
            var byId = controls.FirstOrDefault(c =>
                string.Equals(c.JsonId, jsonId, StringComparison.OrdinalIgnoreCase));
            if (byId != null)
                return byId;
        }

        return controls.FirstOrDefault(c =>
            string.Equals(c.Name, field, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.JsonId, field, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<Dictionary<string, string?>>> TryReadRowsAsync(
        NpgsqlConnection connection,
        string tableName,
        string? processFormTable,
        bool applyDeletedFilter,
        IReadOnlyList<ResolvedColumn> columns,
        IReadOnlyList<ReportFilter>? filters,
        IReadOnlySet<string> ezfbColumns,
        IReadOnlyList<ControlRow> controls,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadRowsAsync(
                connection, tableName, processFormTable, applyDeletedFilter,
                columns, filters, ezfbColumns, controls, cancellationToken);
        }
        catch (PostgresException)
        {
            return [];
        }
        catch (NpgsqlException)
        {
            return [];
        }
    }

    private static async Task<List<Dictionary<string, string?>>> ReadRowsAsync(
        NpgsqlConnection connection,
        string tableName,
        string? processFormTable,
        bool applyDeletedFilter,
        IReadOnlyList<ResolvedColumn> columns,
        IReadOnlyList<ReportFilter>? filters,
        IReadOnlySet<string> ezfbColumns,
        IReadOnlyList<ControlRow> controls,
        CancellationToken cancellationToken)
    {
        var selectSql = BuildSelect(
            tableName, processFormTable, applyDeletedFilter,
            columns, filters, ezfbColumns, controls, out var parameters);
        var rows = new List<Dictionary<string, string?>>();

        await using var cmd = new NpgsqlCommand(selectSql, connection);
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        cmd.CommandTimeout = 120;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            ordinals[reader.GetName(i)] = i;

        while (await reader.ReadAsync(cancellationToken) && rows.Count < MaxRows)
        {
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in columns.Where(c => !string.IsNullOrWhiteSpace(c.SqlColumn)))
            {
                if (!ordinals.TryGetValue(col.SqlColumn!, out var ord)
                    && !ordinals.TryGetValue(col.Label, out ord))
                {
                    var compact = ReportEzfbNaming.CompactToken(col.SqlColumn!);
                    var match = ordinals.Keys.FirstOrDefault(n =>
                        ReportEzfbNaming.CompactToken(n).Equals(compact, StringComparison.OrdinalIgnoreCase));
                    if (match == null || !ordinals.TryGetValue(match, out ord))
                        continue;
                }

                var cell = ReportEzfbNaming.DisplayCell(reader.IsDBNull(ord) ? null : reader.GetValue(ord));
                row[col.Label] = cell;
            }

            foreach (var col in columns)
                row.TryAdd(col.Label, string.Empty);

            rows.Add(row);
        }

        return rows;
    }

    private static string BuildSelect(
        string tableName,
        string? processFormTable,
        bool applyDeletedFilter,
        IReadOnlyList<ResolvedColumn> columns,
        IReadOnlyList<ReportFilter>? filters,
        IReadOnlySet<string> ezfbColumns,
        IReadOnlyList<ControlRow> controls,
        out List<NpgsqlParameter> parameters)
    {
        parameters = [];
        var selectCols = columns
            .Where(c => !string.IsNullOrWhiteSpace(c.SqlColumn))
            .Select(c => c.SqlColumn!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectCols.Count == 0)
            selectCols.Add("item_id");

        var prefix = processFormTable == null ? "" : "e.";
        var selectList = string.Join(", ", selectCols.Select(c => $"{prefix}{ReportEzfbNaming.QuoteEzfbColumn(c)}"));
        var sql = new StringBuilder();
        sql.AppendLine($"SELECT {selectList}");
        if (processFormTable == null)
        {
            sql.AppendLine($"FROM dbo.\"{tableName}\"");
            if (applyDeletedFilter)
                sql.AppendLine("WHERE (is_deleted = false OR is_deleted IS NULL)");
            else
                sql.AppendLine("WHERE 1 = 1");
        }
        else
        {
            // Join ezfb.item_id (uuid) to process_form.form_entry_id (uuid) — never int.
            sql.AppendLine($"FROM workflow.{processFormTable} pf");
            sql.AppendLine($"INNER JOIN dbo.\"{tableName}\" e ON e.item_id = pf.form_entry_id");
            sql.AppendLine("WHERE (pf.is_deleted = false OR pf.is_deleted IS NULL)");
            if (applyDeletedFilter)
                sql.AppendLine(" AND (e.is_deleted = false OR e.is_deleted IS NULL)");
        }

        var index = 0;
        foreach (var filter in filters ?? [])
        {
            var op = NormalizeOperator(filter.Operator ?? filter.Op);
            var fieldToken = FirstNonEmpty(filter.FieldId, filter.Field);
            if (string.IsNullOrWhiteSpace(fieldToken) || string.IsNullOrWhiteSpace(op))
                continue;

            var rawValue = JsonValue(filter.Value);
            if (string.IsNullOrWhiteSpace(rawValue) && op is not ("isempty" or "isnotempty"))
                continue;

            var control = FindControl(controls, filter.FieldId, filter.Field ?? fieldToken);
            var jsonId = FirstNonEmpty(filter.FieldId, control?.JsonId, fieldToken)!;
            if (!ReportEzfbNaming.TryResolveColumn(control?.ColumnName, control?.Name ?? jsonId, jsonId, ezfbColumns, out var col)
                && (control?.Name == null || !ReportEzfbNaming.TryResolveColumn(control.Name, ezfbColumns, out col)))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(col))
                continue;

            var ident = $"{prefix}{ReportEzfbNaming.QuoteEzfbColumn(col)}";
            var pName = $"@f{index++}";

            switch (op)
            {
                case "isempty":
                    sql.AppendLine($" AND ({ident} IS NULL OR TRIM(({ident})::text) = '')");
                    break;
                case "isnotempty":
                    sql.AppendLine($" AND {ident} IS NOT NULL AND TRIM(({ident})::text) <> ''");
                    break;
                case "contains":
                    sql.AppendLine($" AND ({ident})::text LIKE {pName}");
                    parameters.Add(new NpgsqlParameter(pName, $"%{rawValue}%"));
                    break;
                case "startswith":
                    sql.AppendLine($" AND ({ident})::text LIKE {pName}");
                    parameters.Add(new NpgsqlParameter(pName, $"{rawValue}%"));
                    break;
                case "endswith":
                    sql.AppendLine($" AND ({ident})::text LIKE {pName}");
                    parameters.Add(new NpgsqlParameter(pName, $"%{rawValue}"));
                    break;
                case "notequals":
                    sql.AppendLine($" AND ({ident})::text <> {pName}");
                    parameters.Add(new NpgsqlParameter(pName, rawValue));
                    break;
                case "gt":
                case "gte":
                case "lt":
                case "lte":
                    var cmp = op switch
                    {
                        "gt" => ">",
                        "gte" => ">=",
                        "lt" => "<",
                        _ => "<="
                    };
                    sql.AppendLine($" AND dbo.try_cast_numeric(({ident})::text) {cmp} {pName}");
                    parameters.Add(new NpgsqlParameter(pName, ReportFormulaEvaluator.TryParseNumber(rawValue) ?? 0m));
                    break;
                default:
                    sql.AppendLine($" AND ({ident})::text = {pName}");
                    parameters.Add(new NpgsqlParameter(pName, rawValue));
                    break;
            }
        }

        sql.AppendLine($"ORDER BY {prefix}item_id DESC");
        sql.AppendLine($"LIMIT {MaxRows}");
        return sql.ToString();
    }

    private static void ApplyCalculatedColumns(
        ReportBuilderConfig config,
        IReadOnlyList<ResolvedColumn> columns,
        List<Dictionary<string, string?>> rows)
    {
        var labels = columns.Select(c => c.Label).ToList();
        foreach (var col in columns.Where(c => c.IsCalculated))
        {
            foreach (var row in rows)
            {
                var value = ReportFormulaEvaluator.TryEvaluate(col.Formula, row, labels);
                row[col.Label] = value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
    }

    private static IReadOnlyDictionary<string, string?>? BuildTotals(
        IReadOnlyList<ResolvedColumn> columns,
        IReadOnlyList<Dictionary<string, string?>> rows)
    {
        if (rows.Count == 0 || columns.All(c => c.Calc == "None"))
            return null;

        var totals = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in columns)
        {
            if (col.Calc == "None")
            {
                totals[col.Label] = col == columns[0] ? "Total" : string.Empty;
                continue;
            }

            var numbers = rows
                .Select(r => ReportFormulaEvaluator.TryParseNumber(r.TryGetValue(col.Label, out var v) ? v : null))
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .ToList();

            decimal? result = col.Calc switch
            {
                "Count" => rows.Count,
                "Sum" => numbers.Count == 0 ? null : numbers.Sum(),
                "Avg" => numbers.Count == 0 ? null : numbers.Average(),
                "Min" => numbers.Count == 0 ? null : numbers.Min(),
                "Max" => numbers.Count == 0 ? null : numbers.Max(),
                _ => null
            };
            totals[col.Label] = result?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return totals;
    }

    private static bool IsCalculated(string? colType, string? calc, string? formula) =>
        string.Equals(colType, "calculated", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(formula);

    private static string NormalizeCalc(string? calc)
    {
        if (string.IsNullOrWhiteSpace(calc) || calc.Equals("None", StringComparison.OrdinalIgnoreCase))
            return "None";
        if (calc.Equals("Average", StringComparison.OrdinalIgnoreCase) || calc.Equals("Avg", StringComparison.OrdinalIgnoreCase))
            return "Avg";
        if (calc.Equals("Sum", StringComparison.OrdinalIgnoreCase))
            return "Sum";
        if (calc.Equals("Count", StringComparison.OrdinalIgnoreCase))
            return "Count";
        if (calc.Equals("Min", StringComparison.OrdinalIgnoreCase))
            return "Min";
        if (calc.Equals("Max", StringComparison.OrdinalIgnoreCase))
            return "Max";
        return "None";
    }

    private static string NormalizeOperator(string? op)
    {
        if (string.IsNullOrWhiteSpace(op))
            return "equals";
        var v = op.Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        return v switch
        {
            "eq" or "=" or "equal" or "equals" => "equals",
            "ne" or "!=" or "<>" or "notequal" or "notequals" => "notequals",
            "like" or "contains" => "contains",
            "startswith" or "beginswith" => "startswith",
            "endswith" => "endswith",
            "gt" or ">" or "greaterthan" => "gt",
            "gte" or ">=" or "greaterorequal" or "greaterthanorequal" => "gte",
            "lt" or "<" or "lessthan" => "lt",
            "lte" or "<=" or "lessorequal" or "lessthanorequal" => "lte",
            "isempty" or "empty" or "isnull" => "isempty",
            "isnotempty" or "notempty" or "isnotnull" => "isnotempty",
            _ => v
        };
    }

    private static string JsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString()
        };
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private sealed record ResolvedWorkflow(Guid Id, string Name, string? FormId);

    private sealed record ControlRow(string? JsonId, string? Name, string? Type, bool IsMandatory, string? ColumnName = null);

    private sealed record ResolvedColumn(
        string Key,
        string Label,
        string? SqlColumn,
        string Calc,
        string ColType,
        int Width,
        bool IsCalculated,
        string? Formula,
        string? FieldId = null);
}
