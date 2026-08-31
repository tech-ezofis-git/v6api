using System.Text;
using System.Text.Json;
using Npgsql;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Forms;
using SaaSApp.Workflow.Application.Workflows;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// v5 newformAsync parity: wForm row, JSON storage, security, published controls and ezfb_{id}_items.
///
/// PHASE 4 PORT NOTE (significant deviation, not just dialect translation): the SQL Server
/// original spent ~300 lines detecting and adapting to schema DRIFT across pre-existing v5
/// tenant installations -- numeric vs GUID vs nvarchar wForm.id, IDENTITY vs non-IDENTITY
/// columns, INT vs UNIQUEIDENTIFIER tenantId (via a TenantIdMap side-table), column-width
/// checks before writing a 36-char GUID, etc. None of that drift can exist on Postgres: every
/// tenant database is freshly created by this migration's own scripts with exactly one schema
/// shape, so there is nothing to detect or adapt to. This port creates dbo.wForm/wFormControl/
/// wFormSecurity with that one fixed shape (wForm.id text GUID string, wFormControl/
/// wFormSecurity.id GENERATED ALWAYS AS IDENTITY, tenantId uuid) and drops the entire
/// detection/adaptation layer (EnsureWFormReferenceIdColumnsNvarcharAsync,
/// EnsureWFormIdColumnSupportsGuidAsync, ResolveTenantKeyAsync/EnsureTenantIntIdAsync +
/// dbo.TenantIdMap, IsIdentityColumnAsync-based branching, CoerceIdValue's numeric branch,
/// GetNextNumericIdAsync's TRY_CAST-based fallback) -- it existed solely to accommodate
/// drift that cannot occur here. Everything else (form-creation business logic, JSON storage,
/// security rows, controls sync, ezfb_{id}_items entry table) is preserved.
/// </summary>
public sealed partial class FormService : IFormService
{
    private static readonly HashSet<string> SkippedFieldTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PARAGRAPH", "DIVIDER", "LABEL"
    };

    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly IFormJsonStorageService _formJsonStorage;
    private readonly ILogger<FormService> _logger;

    public FormService(
        ITenantContext tenantContext,
        ICurrentUserProvider currentUserProvider,
        IFormJsonStorageService formJsonStorage,
        ILogger<FormService> logger)
    {
        _tenantContext = tenantContext;
        _currentUserProvider = currentUserProvider;
        _formJsonStorage = formJsonStorage;
        _logger = logger;
    }

    public async Task<FormCreateResult> CreateFormAsync(FormJsonDto formJson, string rawJson, CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        var tenantGuid = _tenantContext.TenantId
            ?? throw new InvalidOperationException("Tenant context is required.");

        var general = formJson.Settings?.General
            ?? throw new InvalidOperationException("settings.general is required.");
        var name = general.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("settings.general.name is required.");

        var publish = formJson.Settings?.Publish;
        var publishOption = publish?.PublishOption?.Trim() ?? "DRAFT";
        var isPublished = publishOption.Equals("PUBLISHED", StringComparison.OrdinalIgnoreCase);

        var userId = _currentUserProvider.GetUserId() ?? throw new InvalidOperationException("User context is required.");
        var createdBy = userId.ToString("D");
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureFormSchemaAsync(connection, cancellationToken);

        if (await FormNameExistsAsync(connection, tenantGuid, name, cancellationToken))
            return new FormCreateResult(FormCreateStatus.NameConflict, null, "form already Exist");

        var formId = await InsertWFormAsync(
            connection,
            tenantGuid,
            formJson,
            general,
            publishOption,
            createdBy,
            now,
            cancellationToken);

        var jsonToStore = !string.IsNullOrWhiteSpace(rawJson)
            ? rawJson
            : JsonSerializer.Serialize(formJson, WorkflowJsonSerializerOptions.Storage);
        await _formJsonStorage.SaveFormJsonAsync(formId, jsonToStore, cancellationToken);

        var securityUserIds = CollectSecurityUserIds(createdBy, general.SuperUser, general.EntryUser);
        await InsertFormSecurityAsync(connection, formId, securityUserIds, createdBy, now, cancellationToken);

        if (isPublished)
        {
            var panels = formJson.Panels ?? new List<FormPanelDto>();
            var secondaryPanels = formJson.SecondaryPanels ?? new List<FormPanelDto>();
            var fields = CollectEntryFields(panels);

            if (fields.Count == 0)
                return new FormCreateResult(FormCreateStatus.NotFound, formId, "Formfields not found");

            var fieldCols = BuildFieldColumnsFromLabels(fields);
            var existingEzfbColumns = await TryLoadEzfbColumnsAsync(connection, formId, cancellationToken);
            await SyncFormControlsAsync(
                connection, formId, panels, secondaryPanels, fields, fieldCols, existingEzfbColumns, createdBy, now, cancellationToken);
            await EnsureFormEntryTableAsync(connection, formId, fields, fieldCols, cancellationToken);
        }

        _logger.LogInformation("Created form {FormId} ({Name}), published={Published}", formId, name, isPublished);
        return new FormCreateResult(FormCreateStatus.Created, formId, formId);
    }

    private static List<string> CollectSecurityUserIds(string createdBy, string[]? superUser, string[]? entryUser)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { createdBy };
        if (superUser != null)
        {
            foreach (var u in superUser.Where(x => !string.IsNullOrWhiteSpace(x)))
                set.Add(u.Trim());
        }
        if (entryUser != null)
        {
            foreach (var u in entryUser.Where(x => !string.IsNullOrWhiteSpace(x)))
                set.Add(u.Trim());
        }
        return set.ToList();
    }

    private static List<FormFieldDto> CollectEntryFields(List<FormPanelDto> panels)
    {
        var list = new List<FormFieldDto>();
        foreach (var panel in panels)
        {
            if (panel.Fields == null)
                continue;
            foreach (var field in panel.Fields)
            {
                if (field.Type == null || SkippedFieldTypes.Contains(field.Type))
                    continue;
                if (string.IsNullOrWhiteSpace(field.Id))
                    continue;
                list.Add(field);
            }
        }
        return list;
    }

    /// <summary>
    /// Called on every POST /api/form before insert. Creates dbo.wForm (+ related tables) only when missing.
    /// Fixed shape (see class doc comment) -- no legacy-drift detection needed on Postgres.
    /// </summary>
    private static async Task EnsureFormSchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using (var schemaCmd = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS dbo;", connection))
            await schemaCmd.ExecuteNonQueryAsync(cancellationToken);

        const string wFormSql = """
            CREATE TABLE IF NOT EXISTS dbo."wForm"(
                id varchar(64) NOT NULL PRIMARY KEY,
                uid varchar(500) NULL,
                "tenantId" uuid NOT NULL,
                name varchar(500) NOT NULL,
                description varchar(2000) NULL,
                type varchar(100) NULL,
                layout varchar(500) NULL,
                "publishOption" varchar(500) NULL,
                error text NULL,
                "createdAt" varchar(50) NULL,
                "modifiedAt" varchar(50) NULL,
                "createdBy" varchar(50) NOT NULL DEFAULT '0',
                "modifiedBy" varchar(50) NOT NULL DEFAULT '0',
                "isDeleted" boolean NOT NULL DEFAULT false,
                "qrFields" text NULL,
                "isEdit" integer NOT NULL DEFAULT 0,
                "repositoryId" integer NULL,
                "uniqueColumns" text NULL,
                "superUser" varchar(1000) NULL,
                "entryUser" varchar(1000) NULL,
                "activityBy" varchar(50) NULL,
                "activityOn" varchar(50) NULL,
                "activityId" integer NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_wForm_tenantId_name" ON dbo."wForm"("tenantId", name);
            """;
        await using (var cmd = new NpgsqlCommand(wFormSql, connection) { CommandTimeout = 120 })
            await cmd.ExecuteNonQueryAsync(cancellationToken);

        const string controlSql = """
            CREATE TABLE IF NOT EXISTS dbo."wFormControl"(
                id integer GENERATED ALWAYS AS IDENTITY NOT NULL PRIMARY KEY,
                "wFormId" varchar(64) NOT NULL,
                "jsonId" varchar(200) NULL,
                name varchar(1000) NULL,
                "columnName" varchar(200) NULL,
                type varchar(200) NULL,
                "isMandatory" boolean NOT NULL DEFAULT false,
                "parentId" integer NOT NULL DEFAULT 0,
                "createdAt" varchar(50) NULL,
                "modifiedAt" varchar(50) NULL,
                "createdBy" varchar(50) NOT NULL DEFAULT '0',
                "modifiedBy" varchar(50) NOT NULL DEFAULT '0',
                "isDeleted" boolean NOT NULL DEFAULT false,
                "activityBy" varchar(50) NULL,
                "activityOn" varchar(50) NULL,
                "activityId" integer NULL,
                "validationJson" text NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_wFormControl_wFormId" ON dbo."wFormControl"("wFormId");
            """;
        await using (var cmd = new NpgsqlCommand(controlSql, connection) { CommandTimeout = 120 })
            await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Existing tenant DBs created before columnName: add idempotently.
        await using (var alterCmd = new NpgsqlCommand(
            """ALTER TABLE dbo."wFormControl" ADD COLUMN IF NOT EXISTS "columnName" varchar(200) NULL;""",
            connection)
        { CommandTimeout = 120 })
            await alterCmd.ExecuteNonQueryAsync(cancellationToken);

        const string securitySql = """
            CREATE TABLE IF NOT EXISTS dbo."wFormSecurity"(
                id integer GENERATED ALWAYS AS IDENTITY NOT NULL PRIMARY KEY,
                "wFormId" varchar(64) NOT NULL,
                "userId" varchar(50) NULL,
                "userCategory" varchar(500) NULL,
                "createdAt" varchar(50) NULL,
                "modifiedAt" varchar(50) NULL,
                "createdBy" varchar(50) NOT NULL DEFAULT '0',
                "modifiedBy" varchar(50) NOT NULL DEFAULT '0',
                "isDeleted" boolean NOT NULL DEFAULT false
            );
            CREATE INDEX IF NOT EXISTS "IX_wFormSecurity_wFormId" ON dbo."wFormSecurity"("wFormId");
            """;
        await using (var cmd = new NpgsqlCommand(securitySql, connection) { CommandTimeout = 120 })
            await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM information_schema.tables
            WHERE table_schema = 'dbo' AND LOWER(table_name) = LOWER(@TableName)
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> FormNameExistsAsync(
        NpgsqlConnection connection,
        Guid tenantGuid,
        string name,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1) FROM dbo."wForm" WHERE "isDeleted" = false AND "tenantId" = @TenantId AND name = @Name
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@TenantId", tenantGuid);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        return count > 0;
    }

    private static async Task<string> InsertWFormAsync(
        NpgsqlConnection connection,
        Guid tenantGuid,
        FormJsonDto formJson,
        FormGeneralDto general,
        string publishOption,
        string createdBy,
        string now,
        CancellationToken cancellationToken)
    {
        var formId = await ResolveNewFormIdAsync(connection, formJson, cancellationToken);

        var qrFields = general.QrFields is { Length: > 0 } ? string.Join(",", general.QrFields) : "";
        var uniqueColumns = general.UniqueColumns is { Length: > 0 } ? string.Join(",", general.UniqueColumns) : "";
        var superUser = general.SuperUser is { Length: > 0 } ? string.Join(",", general.SuperUser) : "";
        var entryUser = general.EntryUser is { Length: > 0 } ? string.Join(",", general.EntryUser) : "";

        const string sql = """
            INSERT INTO dbo."wForm"
                (id, uid, "tenantId", name, description, type, layout, "publishOption", error,
                 "createdAt", "createdBy", "modifiedBy", "superUser", "entryUser", "qrFields", "uniqueColumns", "isDeleted", "isEdit")
            VALUES
                (@Id, @Uid, @TenantId, @Name, @Description, @Type, @Layout, @PublishOption, @Error,
                 @CreatedAt, @CreatedBy, @CreatedBy, @SuperUser, @EntryUser, @QrFields, @UniqueColumns, false, 0)
            RETURNING id;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", formId);
        cmd.Parameters.AddWithValue("@Uid", (object?)formJson.Uid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TenantId", tenantGuid);
        cmd.Parameters.AddWithValue("@Name", general.Name!.Trim());
        cmd.Parameters.AddWithValue("@Description", (object?)general.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Type", (object?)general.Type ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Layout", (object?)general.Layout ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PublishOption", publishOption);
        cmd.Parameters.AddWithValue("@Error", "");
        cmd.Parameters.AddWithValue("@CreatedAt", now);
        cmd.Parameters.AddWithValue("@CreatedBy", createdBy);
        cmd.Parameters.AddWithValue("@SuperUser", superUser);
        cmd.Parameters.AddWithValue("@EntryUser", entryUser);
        cmd.Parameters.AddWithValue("@QrFields", qrFields);
        cmd.Parameters.AddWithValue("@UniqueColumns", uniqueColumns);

        var idObj = await cmd.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Form insert did not return an id.");
        return Convert.ToString(idObj)?.Trim() ?? formId;
    }

    private static async Task InsertFormSecurityAsync(
        NpgsqlConnection connection,
        string formId,
        List<string> userIds,
        string createdBy,
        string now,
        CancellationToken cancellationToken)
    {
        const string existsSql = """SELECT COUNT(1) FROM dbo."wFormSecurity" WHERE "wFormId" = @FormId AND "isDeleted" = false""";
        await using (var existsCmd = new NpgsqlCommand(existsSql, connection))
        {
            existsCmd.Parameters.AddWithValue("@FormId", formId);
            if (Convert.ToInt32(await existsCmd.ExecuteScalarAsync(cancellationToken)) > 0)
                return;
        }

        const string insertSql = """
            INSERT INTO dbo."wFormSecurity"("wFormId", "userId", "createdAt", "createdBy", "isDeleted")
            VALUES(@FormId, @UserId, @CreatedAt, @CreatedBy, false);
            """;

        foreach (var userId in userIds)
        {
            await using var cmd = new NpgsqlCommand(insertSql, connection);
            cmd.Parameters.AddWithValue("@FormId", formId);
            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@CreatedAt", now);
            cmd.Parameters.AddWithValue("@CreatedBy", createdBy);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task SyncFormControlsAsync(
        NpgsqlConnection connection,
        string formId,
        List<FormPanelDto> panels,
        List<FormPanelDto> secondaryPanels,
        List<FormFieldDto> topLevelFields,
        IReadOnlyList<string> fieldColumnNames,
        IReadOnlySet<string>? existingEzfbColumns,
        string createdBy,
        string now,
        CancellationToken cancellationToken)
    {
        var columnByJsonId = BuildColumnNameByJsonId(topLevelFields, fieldColumnNames, existingEzfbColumns);

        await using (var delCmd = new NpgsqlCommand("""DELETE FROM dbo."wFormControl" WHERE "wFormId" = @FormId""", connection))
        {
            delCmd.Parameters.AddWithValue("@FormId", formId);
            await delCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var field in topLevelFields)
        {
            columnByJsonId.TryGetValue(field.Id!, out var columnName);
            var parentId = await InsertControlAsync(
                connection, formId, field, 0, columnName, createdBy, now, cancellationToken);

            if (string.Equals(field.Type, "TABLE", StringComparison.OrdinalIgnoreCase)
                && field.Settings?.Specific?.TableColumns != null)
            {
                foreach (var col in field.Settings.Specific.TableColumns)
                {
                    if (col.Type != null && !SkippedFieldTypes.Contains(col.Type) && !string.IsNullOrWhiteSpace(col.Id))
                        await InsertControlAsync(connection, formId, col, parentId, null, createdBy, now, cancellationToken);
                }
            }
            else if (string.Equals(field.Type, "POPUP", StringComparison.OrdinalIgnoreCase)
                     && field.Settings?.Specific != null
                     && secondaryPanels.Count > 0)
            {
                var panelIndex = field.Settings.Specific.MappedPopupPanel;
                if (panelIndex >= 0 && panelIndex < secondaryPanels.Count && secondaryPanels[panelIndex].Fields != null)
                {
                    foreach (var popupField in secondaryPanels[panelIndex].Fields!)
                    {
                        if (popupField.Type != null && !SkippedFieldTypes.Contains(popupField.Type) && !string.IsNullOrWhiteSpace(popupField.Id))
                            await InsertControlAsync(connection, formId, popupField, parentId, null, createdBy, now, cancellationToken);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Maps top-level field jsonId → physical ezfb column. When the ezfb table already exists,
    /// only assigns names that are present on the table (no ALTER). When creating a new table,
    /// <paramref name="existingEzfbColumns"/> is null and all computed names are stored.
    /// </summary>
    private static Dictionary<string, string?> BuildColumnNameByJsonId(
        List<FormFieldDto> topLevelFields,
        IReadOnlyList<string> fieldColumnNames,
        IReadOnlySet<string>? existingEzfbColumns)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < topLevelFields.Count && i < fieldColumnNames.Count; i++)
        {
            var jsonId = topLevelFields[i].Id!;
            var candidate = fieldColumnNames[i];
            if (existingEzfbColumns == null || existingEzfbColumns.Contains(candidate))
                map[jsonId] = candidate;
            else
                map[jsonId] = null;
        }

        return map;
    }

    private static async Task<int> InsertControlAsync(
        NpgsqlConnection connection,
        string formId,
        FormFieldDto field,
        int parentId,
        string? columnName,
        string createdBy,
        string now,
        CancellationToken cancellationToken)
    {
        var validationJson = BuildValidationJson(field);
        var fieldRule = field.Settings?.Validation?.FieldRule;
        var isMandatory = string.Equals(fieldRule, "required", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fieldRule, "REQUIRED", StringComparison.Ordinal);

        const string sql = """
            INSERT INTO dbo."wFormControl"("wFormId", "jsonId", name, "columnName", type, "isMandatory", "parentId", "createdAt", "createdBy", "isDeleted", "validationJson")
            VALUES(@FormId, @JsonId, @Name, @ColumnName, @Type, @Mandatory, @ParentId, @CreatedAt, @CreatedBy, false, @ValidationJson)
            RETURNING id;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", formId);
        cmd.Parameters.AddWithValue("@JsonId", field.Id!);
        cmd.Parameters.AddWithValue("@Name", (object?)field.Label ?? field.Id!);
        cmd.Parameters.AddWithValue("@ColumnName", (object?)columnName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Type", (object?)field.Type ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Mandatory", isMandatory);
        cmd.Parameters.AddWithValue("@ParentId", parentId);
        cmd.Parameters.AddWithValue("@CreatedAt", now);
        cmd.Parameters.AddWithValue("@CreatedBy", createdBy);
        cmd.Parameters.AddWithValue("@ValidationJson", (object?)validationJson ?? DBNull.Value);

        var idObj = await cmd.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("wFormControl insert did not return an id.");
        return Convert.ToInt32(idObj);
    }

    private static string? BuildValidationJson(FormFieldDto field)
    {
        var type = field.Type ?? "";
        var validation = field.Settings?.Validation;
        var specific = field.Settings?.Specific;

        if (type is "SINGLE_CHOICE" or "SINGLE_SELECT")
        {
            var options = (specific?.CustomOptions ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            return JsonSerializer.Serialize(new { validation = new { }, specific = new { customOptions = options } });
        }

        if (type is "SHORT_TEXT" or "NUMBER" && validation != null)
        {
            return JsonSerializer.Serialize(new
            {
                specific = new { },
                validation = new
                {
                    validation.ContentRule,
                    validation.Maximum,
                    validation.Minimum
                }
            });
        }

        return null;
    }

    /// <summary>
    /// System/reserved physical columns on dbo.ezfb_*_items (see below) -- a NEW form's
    /// Label-derived column must never collide with one of these, so a colliding label gets a
    /// numeric suffix the same way a duplicate label does (see <see cref="BuildFieldColumnsFromLabels"/>).
    /// </summary>
    private static readonly string[] ReservedEntryColumns =
        { "item_id", "created_at", "modified_at", "created_by", "modified_by", "is_deleted", "today_task", "is_marked" };

    /// <summary>
    /// dbo.ezfb_{id}_items -- trigger-based history (Decision 2) replaces SYSTEM_VERSIONING,
    /// same pattern already proven for workflow.WorkflowInstances (02_CreateTenantDatabase.sql)
    /// and repository items (StaticRepositoryProvisioner.cs). Column set is entirely dynamic
    /// (one column per form field), so the trigger function's explicit column list is built from
    /// the live field list, same approach as StaticRepositoryProvisioner.BuildItemsTriggerFunctionScript.
    ///
    /// NEW forms (this table doesn't exist yet): column = sanitized field Label, e.g.
    /// "PO Number" -&gt; "PO_Number", with collision suffixes (Address, Address_2).
    /// wFormControl.jsonId stays the designer field id; wFormControl.columnName stores the
    /// exact physical column. OLD forms already have their table (this method returns immediately)
    /// and keep their existing jsonId-named columns forever; no ALTER/migration from here.
    /// </summary>
    private static async Task EnsureFormEntryTableAsync(
        NpgsqlConnection connection,
        string formId,
        List<FormFieldDto> fields,
        IReadOnlyList<string> fieldCols,
        CancellationToken cancellationToken)
    {
        var tableSuffix = FormIdNaming.GetEzfbTableSuffix(formId);
        var tableName = $"ezfb_{tableSuffix}_items";
        var historyTable = $"ezfb_{tableSuffix}_history";
        if (await TableExistsAsync(connection, tableName, cancellationToken))
            return;

        var columns = fieldCols.Count > 0 ? fieldCols : BuildFieldColumnsFromLabels(fields);

        // Fixed/system columns are snake_case unquoted (system-controlled, matching the
        // dynamic-DDL convention used everywhere else in this migration -- WorkflowTableCreator.cs,
        // StaticRepositoryProvisioner.cs). An earlier draft of this method left them unquoted
        // camelCase (itemId, createdAt, ...), which Postgres silently folds to all-lowercase on
        // both definition and every reference -- functionally consistent as long as they're
        // NEVER quoted anywhere, but fragile across ~4 more files that read this table. Fixed
        // here to genuine snake_case so quoting is a non-issue either way. Per-field columns
        // (fieldCols) stay double-quoted with EzfbColumnNaming's casing preserved -- same
        // reasoning as repository custom columns: arbitrary user input, referenced by exact
        // string elsewhere (WorkflowEzfbFormDataLoader.cs).
        var sb = new StringBuilder();
        sb.Append($"CREATE TABLE dbo.\"{tableName}\" (");
        sb.Append("item_id uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),");
        foreach (var col in columns)
            sb.Append($"\"{col}\" text NULL,");
        sb.Append("created_at varchar(50) NULL, modified_at varchar(50) NULL,");
        sb.Append("created_by varchar(50) NOT NULL DEFAULT '0', modified_by varchar(50) NOT NULL DEFAULT '0',");
        sb.Append("is_deleted boolean NOT NULL DEFAULT false, today_task boolean NOT NULL DEFAULT true, is_marked boolean NOT NULL DEFAULT false");
        sb.Append(");");
        await using (var createCmd = new NpgsqlCommand(sb.ToString(), connection) { CommandTimeout = 120 })
            await createCmd.ExecuteNonQueryAsync(cancellationToken);

        var allColsForTrigger = ReservedEntryColumns.Concat(columns.Select(c => $"\"{c}\"")).ToList();
        var colList = string.Join(", ", allColsForTrigger);
        var oldColList = string.Join(", ", allColsForTrigger.Select(c => $"OLD.{c}"));

        var historySql = $"""
            CREATE TABLE IF NOT EXISTS dbo."{historyTable}" (
                LIKE dbo."{tableName}" INCLUDING DEFAULTS,
                history_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                history_operation varchar(1) NOT NULL,
                history_recorded_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS "IX_{historyTable}_item_id" ON dbo."{historyTable}" (item_id);

            CREATE OR REPLACE FUNCTION dbo.fn_{tableName}_history() RETURNS trigger AS $body$
            BEGIN
                IF (TG_OP = 'UPDATE') THEN
                    INSERT INTO dbo."{historyTable}" ({colList}, history_operation, history_recorded_at)
                    SELECT {oldColList}, 'U', now();
                    RETURN NEW;
                ELSIF (TG_OP = 'DELETE') THEN
                    INSERT INTO dbo."{historyTable}" ({colList}, history_operation, history_recorded_at)
                    SELECT {oldColList}, 'D', now();
                    RETURN OLD;
                END IF;
                RETURN NULL;
            END;
            $body$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS trg_{tableName}_history ON dbo."{tableName}";
            CREATE TRIGGER trg_{tableName}_history
                AFTER UPDATE OR DELETE ON dbo."{tableName}"
                FOR EACH ROW EXECUTE FUNCTION dbo.fn_{tableName}_history();
            """;
        await using var historyCmd = new NpgsqlCommand(historySql, connection) { CommandTimeout = 120 };
        await historyCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string EscapeSqlIdentifier(string name) =>
        EzfbColumnNaming.ToSqlBracketIdentifier(name);

    /// <summary>
    /// One ezfb column per field, named from the field's Label (falling back to its jsonId when
    /// the label sanitizes to nothing, e.g. a label that is pure punctuation). Guards against two
    /// kinds of collision, both resolved the same way -- append "_2", "_3", ... until free:
    ///   - two fields whose labels sanitize to the same column ("PO Number" / "PO  Number")
    ///   - a label that happens to sanitize to a reserved system column name (e.g. "Created At")
    /// </summary>
    private static List<string> BuildFieldColumnsFromLabels(List<FormFieldDto> fields)
    {
        var reserved = new HashSet<string>(ReservedEntryColumns, StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = new List<string>(fields.Count);

        foreach (var field in fields)
        {
            var label = !string.IsNullOrWhiteSpace(field.Label) ? field.Label! : field.Id!;
            if (!EzfbColumnNaming.TryToColumnNameFromLabel(label, out var baseColumn) || string.IsNullOrWhiteSpace(baseColumn))
                baseColumn = EscapeSqlIdentifier(field.Id!);

            var candidate = baseColumn;
            var suffix = 2;
            while (reserved.Contains(candidate) || !used.Add(candidate))
            {
                candidate = $"{baseColumn}_{suffix}";
                suffix++;
            }

            columns.Add(candidate);
        }

        return columns;
    }

    /// <summary>Always allocates a new dashed GUID for dbo.wForm.id (designer uid stays in wForm.uid only).</summary>
    private static async Task<string> ResolveNewFormIdAsync(
        NpgsqlConnection connection,
        FormJsonDto formJson,
        CancellationToken cancellationToken)
    {
        _ = formJson;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = FormIdNaming.GenerateFormId();
            if (!await FormIdExistsAsync(connection, candidate, cancellationToken))
                return candidate;
        }

        throw new InvalidOperationException("Could not allocate a unique form id.");
    }

    private static async Task<bool> FormIdExistsAsync(
        NpgsqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        const string sql = """SELECT COUNT(1) FROM dbo."wForm" WHERE id = @Id""";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", formId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    /// <summary>
    /// Returns existing ezfb_*_items column names when the table already exists; otherwise null
    /// (caller treats null as "new table — store all computed columnNames").
    /// </summary>
    private static async Task<IReadOnlySet<string>?> TryLoadEzfbColumnsAsync(
        NpgsqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        var tableName = $"ezfb_{FormIdNaming.GetEzfbTableSuffix(formId)}_items";
        if (!await TableExistsAsync(connection, tableName, cancellationToken))
            return null;

        const string sql = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'dbo' AND table_name = @TableName
            """;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            set.Add(reader.GetString(0));
        return set;
    }
}
