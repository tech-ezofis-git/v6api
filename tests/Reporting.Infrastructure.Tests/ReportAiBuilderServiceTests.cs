using System.Text.Json;
using SaaSApp.Reporting.Application.Contracts;
using SaaSApp.Reporting.Infrastructure.Services;

namespace SaaSApp.Reporting.Infrastructure.Tests;

public sealed class ReportAiBuilderServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("b843b988-00ec-44e3-aca2-b8470133ef63");
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task GenerateAndSave_persists_prompt_and_returns_id()
    {
        var store = new MemoryStore();
        var client = new FakeClient
        {
            Prompt = AgentResult(200, """
                {"reply":"Report prompt generated.","report_result":{
                  "title":"Aging",
                  "reportType":"all_workflows",
                  "reportPrompt":"{\"Objective\":\"Aging\"}",
                  "reportSpec":{"title":"Aging","columns":[{"key":"Aging_Bucket","label":"Aging Bucket"}]}
                }}
                """)
        };
        var service = new ReportAiBuilderService(client, store);

        var result = await service.GenerateAndSaveAsync(
            TenantId,
            UserId,
            new ReportAiPromptRequest
            {
                SessionId = "report-session-1",
                Description = "Aging",
                ReportType = "all_workflows"
            });

        Assert.Equal(200, result.StatusCode);
        Assert.Single(store.Rows);
        var saved = store.Rows.Values.Single();
        Assert.Equal("Aging", saved.Name);
        Assert.Contains("Objective", saved.ReportPrompt);
        Assert.Equal("report-session-1", saved.SessionId);
        Assert.Contains(saved.Id.ToString(), JsonSerializer.Serialize(result.Payload));
    }

    [Fact]
    public async Task RunSaved_loads_prompt_and_forwards_it_to_agents()
    {
        var store = new MemoryStore();
        var id = Guid.NewGuid();
        store.Rows[id] = new ReportAiDefinitionRecord
        {
            Id = id,
            TenantId = TenantId,
            OwnerUserId = UserId,
            SessionId = "report-session-1",
            Name = "Aging",
            Description = "Aging",
            ReportType = "all_workflows",
            ReportPrompt = """{"Objective":"Aging"}""",
            ReportSpecJson = """{"title":"Aging","sourceTable":"workflow_instances_all"}"""
        };
        var client = new FakeClient
        {
            Run = AgentResult(200, """
                {"reply":"Report ready.","report_result":{
                  "title":"Aging",
                  "columns":[{"key":"Aging_Bucket","label":"Aging Bucket"}],
                  "rows":[{"Aging_Bucket":"0-5","Request_Count":49}],
                  "totalCount":1,
                  "page":1,
                  "pageSize":25,
                  "warnings":[]
                }}
                """)
        };
        var service = new ReportAiBuilderService(client, store);

        var result = await service.RunSavedAsync(TenantId, id, new ReportAiRunRequest());

        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(client.LastRun);
        Assert.Equal("""{"Objective":"Aging"}""", client.LastRun!.ReportPrompt);
        Assert.Equal("all_workflows", client.LastRun.ReportType);
        Assert.Equal(1, store.Rows[id].Runs);
    }

    [Fact]
    public async Task RunSaved_ignores_swagger_placeholder_overlay()
    {
        var store = new MemoryStore();
        var id = Guid.NewGuid();
        store.Rows[id] = new ReportAiDefinitionRecord
        {
            Id = id,
            TenantId = TenantId,
            OwnerUserId = UserId,
            SessionId = "report-session-1",
            ReportType = "all_workflows",
            ReportPrompt = """{"Objective":"Aging"}""",
            ReportSpecJson = """{"title":"Aging","sourceTable":"workflow_instances_all"}"""
        };
        var client = new FakeClient
        {
            Run = AgentResult(200, """
                {"reply":"Report ready.","report_result":{
                  "title":"Aging",
                  "columns":[{"key":"Aging_Bucket","label":"Aging Bucket"}],
                  "rows":[],
                  "totalCount":0,
                  "page":1,
                  "pageSize":25
                }}
                """)
        };
        using var specDoc = JsonDocument.Parse("\"string\"");
        var service = new ReportAiBuilderService(client, store);

        var result = await service.RunSavedAsync(
            TenantId,
            id,
            new ReportAiRunRequest
            {
                Page = 0,
                PageSize = 0,
                ReportPrompt = "string",
                ReportSpec = specDoc.RootElement.Clone(),
                Model = "string"
            });

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("""{"Objective":"Aging"}""", store.Rows[id].ReportPrompt);
        Assert.Equal("""{"Objective":"Aging"}""", client.LastRun!.ReportPrompt);
        Assert.Null(client.LastRun.Model);
        Assert.Equal(1, client.LastRun.Page);
        Assert.Equal(25, client.LastRun.PageSize);
    }

    [Fact]
    public async Task RunSaved_surfaces_agents_detail_on_4xx()
    {
        var store = new MemoryStore();
        var id = Guid.NewGuid();
        store.Rows[id] = new ReportAiDefinitionRecord
        {
            Id = id,
            TenantId = TenantId,
            ReportType = "all_workflows",
            ReportPrompt = """{"Objective":"Aging"}""",
            ReportSpecJson = """{"title":"Aging"}"""
        };
        var client = new FakeClient
        {
            Run = AgentResult(422, """{"detail":"reportPrompt or reportSpec is required for phase=run."}""")
        };
        var service = new ReportAiBuilderService(client, store);
        var result = await service.RunSavedAsync(TenantId, id, new ReportAiRunRequest());
        Assert.Equal(422, result.StatusCode);
        var json = JsonSerializer.Serialize(result.Payload);
        Assert.Contains("reportPrompt or reportSpec is required", json);
    }

    [Fact]
    public async Task RunSaved_maps_lock_redirect_to_422()
    {
        var store = new MemoryStore();
        var id = Guid.NewGuid();
        store.Rows[id] = new ReportAiDefinitionRecord
        {
            Id = id,
            TenantId = TenantId,
            OwnerUserId = UserId,
            ReportType = "specific_workflow",
            WorkflowName = "RFQ Process",
            ReportPrompt = "{}",
            ReportSpecJson = "{}"
        };
        var client = new FakeClient
        {
            Run = AgentResult(200, """
                {"reply":"Report ready.","report_result":{
                  "columns":[{"key":"Name","label":"Name"}],
                  "rows":[{"Name":"Accounts Payable"}],
                  "warnings":["Dropped unknown source table 'ezfb_787372d8_items'."]
                }}
                """)
        };
        var service = new ReportAiBuilderService(client, store);

        var result = await service.RunSavedAsync(TenantId, id, new ReportAiRunRequest());

        Assert.Equal(422, result.StatusCode);
        Assert.Equal(0, store.Rows[id].Runs);
    }

    [Fact]
    public async Task GenerateAndSave_requires_workflow_name_for_specific_workflow()
    {
        var service = new ReportAiBuilderService(new FakeClient(), new MemoryStore());
        var result = await service.GenerateAndSaveAsync(
            TenantId,
            UserId,
            new ReportAiPromptRequest { Description = "RFQ", ReportType = "specific_workflow" });
        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public async Task RunSaved_missing_id_is_404()
    {
        var service = new ReportAiBuilderService(new FakeClient(), new MemoryStore());
        var result = await service.RunSavedAsync(TenantId, Guid.NewGuid(), new ReportAiRunRequest());
        Assert.Equal(404, result.StatusCode);
    }

    [Fact]
    public async Task GenerateAndSave_timeout_is_504()
    {
        var service = new ReportAiBuilderService(
            new FakeClient { Prompt = new ReportAgentProxyResult { StatusCode = 504, TimedOut = true } },
            new MemoryStore());
        var result = await service.GenerateAndSaveAsync(
            TenantId,
            UserId,
            new ReportAiPromptRequest { Description = "Aging", ReportType = "all_workflows" });
        Assert.Equal(504, result.StatusCode);
    }

    private static ReportAgentProxyResult AgentResult(int status, string body) =>
        ReportAgentChatMapper.Parse(status, body);

    private sealed class MemoryStore : IReportAiDefinitionService
    {
        public Dictionary<Guid, ReportAiDefinitionRecord> Rows { get; } = new();

        public Task<ReportAiDefinitionRecord> SaveAsync(
            ReportAiDefinitionRecord record,
            CancellationToken cancellationToken = default)
        {
            if (record.Id == Guid.Empty)
                record.Id = Guid.NewGuid();
            Rows[record.Id] = Clone(record);
            return Task.FromResult(Clone(record));
        }

        public Task<ReportAiDefinitionRecord?> GetAsync(
            Guid tenantId,
            Guid reportId,
            CancellationToken cancellationToken = default)
        {
            if (!Rows.TryGetValue(reportId, out var row) || row.TenantId != tenantId)
                return Task.FromResult<ReportAiDefinitionRecord?>(null);
            return Task.FromResult<ReportAiDefinitionRecord?>(Clone(row));
        }

        public Task<IReadOnlyList<ReportAiDefinitionRecord>> ListAsync(
            Guid tenantId,
            Guid userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReportAiDefinitionRecord>>(
                Rows.Values.Where(r => r.TenantId == tenantId).Select(Clone).ToList());

        public Task<ReportAiDefinitionRecord?> UpdateAsync(
            Guid tenantId,
            Guid reportId,
            ReportAiUpdateRequest patch,
            CancellationToken cancellationToken = default)
        {
            if (!Rows.TryGetValue(reportId, out var row) || row.TenantId != tenantId)
                return Task.FromResult<ReportAiDefinitionRecord?>(null);
            if (patch.ReportPrompt is not null)
                row.ReportPrompt = patch.ReportPrompt;
            if (patch.ReportSpec.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                row.ReportSpecJson = ReportAgentChatMapper.SpecJson(patch.ReportSpec);
            return Task.FromResult<ReportAiDefinitionRecord?>(Clone(row));
        }

        public Task IncrementRunsAsync(
            Guid tenantId,
            Guid reportId,
            CancellationToken cancellationToken = default)
        {
            if (Rows.TryGetValue(reportId, out var row) && row.TenantId == tenantId)
                row.Runs++;
            return Task.CompletedTask;
        }

        private static ReportAiDefinitionRecord Clone(ReportAiDefinitionRecord row) =>
            new()
            {
                Id = row.Id,
                TenantId = row.TenantId,
                OwnerUserId = row.OwnerUserId,
                SessionId = row.SessionId,
                Name = row.Name,
                Description = row.Description,
                ReportType = row.ReportType,
                WorkflowName = row.WorkflowName,
                RepositoryName = row.RepositoryName,
                ReportPrompt = row.ReportPrompt,
                ReportSpecJson = row.ReportSpecJson,
                Status = row.Status,
                Runs = row.Runs,
                CreatedAtUtc = row.CreatedAtUtc,
                ModifiedAtUtc = row.ModifiedAtUtc
            };
    }

    private sealed class FakeClient : IReportAgentPythonClient
    {
        public ReportAgentProxyResult Prompt { get; set; } = new() { StatusCode = 500 };
        public ReportAgentProxyResult Run { get; set; } = new() { StatusCode = 500 };
        public ReportAgentChatRequest? LastRun { get; private set; }

        public Task<ReportAgentProxyResult> GeneratePromptAsync(
            ReportAgentChatRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Prompt);

        public Task<ReportAgentProxyResult> RunReportAsync(
            ReportAgentChatRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRun = request;
            return Task.FromResult(Run);
        }

        public Task<ReportAgentProxyResult> GetTypesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReportAgentProxyResult { StatusCode = 200, RawBody = "[]" });

        public Task<ReportAgentProxyResult> GetScopeOptionsAsync(
            string scope,
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReportAgentProxyResult { StatusCode = 200, RawBody = "[]" });
    }
}
