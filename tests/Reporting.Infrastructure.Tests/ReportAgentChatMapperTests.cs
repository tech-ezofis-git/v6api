using System.Text.Json;
using SaaSApp.Reporting.Application.Contracts;
using SaaSApp.Reporting.Infrastructure.Services;

namespace SaaSApp.Reporting.Infrastructure.Tests;

public sealed class ReportAgentChatMapperTests
{
    [Theory]
    [InlineData("http://agents:8000/chat", "http://agents:8000")]
    [InlineData("https://cloud.ezofis.com/chat", "https://cloud.ezofis.com")]
    [InlineData("https://cloud.ezofis.com/chat/", "https://cloud.ezofis.com")]
    public void ResolveAgentsBaseUrl_strips_chat(string input, string expected)
    {
        Assert.Equal(expected, ReportAgentChatMapper.ResolveAgentsBaseUrl(input));
    }

    [Fact]
    public void Parse_unwraps_report_result_and_reply()
    {
        var body = """
            {"reply":"Report prompt generated.","report_result":{"title":"Aging","reportPrompt":"{}"}}
            """;
        var parsed = ReportAgentChatMapper.Parse(200, body);
        Assert.True(parsed.HasReportResult);
        Assert.Equal("Report prompt generated.", parsed.Reply);
        Assert.Equal("Aging", ReportAgentChatMapper.ReadString(parsed.ReportResult, "title"));
    }

    [Fact]
    public void Parse_null_report_result_is_not_success()
    {
        var parsed = ReportAgentChatMapper.Parse(200, """{"reply":"workflowName is required.","report_result":null}""");
        Assert.False(parsed.HasReportResult);
        Assert.Equal("workflowName is required.", parsed.Reply);
    }

    [Fact]
    public void IsLockFailure_detects_redirect_warnings()
    {
        Assert.True(ReportAgentChatMapper.IsLockFailure(
            ["Redirected report source from catalog/definition table to workflow.Workflows."]));
        Assert.True(ReportAgentChatMapper.IsLockFailure(
            ["Dropped unknown source table 'ezfb_787372d8_items'."]));
        Assert.False(ReportAgentChatMapper.IsLockFailure(["Counted live EZOFIS workflow instances."]));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("string", false)]
    [InlineData("{\"Objective\":\"Aging\"}", true)]
    public void IsUsablePrompt_rejects_swagger_placeholders(string? prompt, bool expected)
    {
        Assert.Equal(expected, ReportAgentChatMapper.IsUsablePrompt(prompt));
    }

    [Fact]
    public void Parse_reads_fastapi_detail_as_reply()
    {
        var parsed = ReportAgentChatMapper.Parse(422, """{"detail":"workflowName is required."}""");
        Assert.Equal("workflowName is required.", parsed.Reply);
    }

    [Fact]
    public void BuildChatBody_includes_intent_phase_and_prompt()
    {
        var json = ReportAgentPythonClient.BuildChatBody(
            new ReportAgentChatRequest
            {
                SessionId = "report-abc",
                TenantId = Guid.Parse("b843b988-00ec-44e3-aca2-b8470133ef63"),
                Description = "Aging",
                ReportType = "all_workflows",
                ReportPrompt = """{"title":"Aging"}""",
                Page = 1,
                PageSize = 25
            },
            "run");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("report-abc", root.GetProperty("session_id").GetString());
        Assert.Equal("report", root.GetProperty("intent").GetString());
        var payload = root.GetProperty("payload");
        Assert.Equal("run", payload.GetProperty("phase").GetString());
        Assert.Equal("all_workflows", payload.GetProperty("report_type").GetString());
        Assert.Equal("""{"title":"Aging"}""", payload.GetProperty("report_prompt").GetString());
        Assert.Equal("Aging", payload.GetProperty("description").GetString());
    }
}
