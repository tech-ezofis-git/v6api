using SaaSApp.Workflow.Application.Connectors;
using SaaSApp.Workflow.Application.Workflows;

namespace SaaSApp.Workflow.Infrastructure.Tests;

public sealed class ApAgentPoMasterStartPayloadEnricherTests
{
    private static readonly Guid SapConnectorId = Guid.Parse("983bddbe-6a1a-4cd8-a024-9b4d84ba9981");
    private static readonly Guid QbConnectorId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Enrich_InternalForm_LeavesPayloadUnchanged()
    {
        var payload = BasePayload();
        ApAgentPoMasterStartPayloadEnricher.Enrich(
            payload,
            EmailIngestMasterSources.InternalForm,
            SapConnectorId);

        Assert.False(payload.ContainsKey("resource"));
        Assert.False(payload.ContainsKey("connector_id"));
        Assert.False(payload.ContainsKey("skills"));
    }

    [Fact]
    public void Enrich_Sap_InjectsResourceConnectorAndSkills()
    {
        var payload = BasePayload();
        ApAgentPoMasterStartPayloadEnricher.Enrich(
            payload,
            EmailIngestMasterSources.Sap,
            SapConnectorId);

        Assert.Equal("SAP", payload["resource"]);
        Assert.Equal(SapConnectorId.ToString("D"), payload["connector_id"]);
        var skills = Assert.IsType<List<string>>(payload["skills"]);
        Assert.Contains(ApAgentPoMasterStartPayloadEnricher.SkillPoLookupSap, skills);
        Assert.Contains(ApAgentPoMasterStartPayloadEnricher.SkillPoMatch, skills);
        Assert.True(
            skills.IndexOf(ApAgentPoMasterStartPayloadEnricher.SkillPoLookupSap)
            < skills.IndexOf(ApAgentPoMasterStartPayloadEnricher.SkillPoMatch));
        Assert.Equal("extract_invoice", skills[0]);
        Assert.Contains("finalize_decision", skills);
        Assert.Contains("workflow_move_next", skills);
    }

    [Fact]
    public void Enrich_QuickBooks_InjectsResourceConnectorAndSkills()
    {
        var payload = BasePayload();
        ApAgentPoMasterStartPayloadEnricher.Enrich(
            payload,
            EmailIngestMasterSources.QuickBooks,
            QbConnectorId);

        Assert.Equal("QUICKBOOKS", payload["resource"]);
        Assert.Equal(QbConnectorId.ToString("D"), payload["connector_id"]);
        var skills = Assert.IsType<List<string>>(payload["skills"]);
        Assert.Contains(ApAgentPoMasterStartPayloadEnricher.SkillPoLookupQuickBooks, skills);
        Assert.True(
            skills.IndexOf(ApAgentPoMasterStartPayloadEnricher.SkillPoLookupQuickBooks)
            < skills.IndexOf(ApAgentPoMasterStartPayloadEnricher.SkillPoMatch));
    }

    [Fact]
    public void Enrich_DoesNotOverwriteExistingResourceConnectorOrSkillsListPresence()
    {
        var payload = BasePayload();
        payload["resource"] = "CUSTOM";
        payload["connector_id"] = "already-set";
        payload["skills"] = new List<string> { "extract_invoice", "po_match", "finalize_decision" };

        ApAgentPoMasterStartPayloadEnricher.Enrich(
            payload,
            EmailIngestMasterSources.Sap,
            SapConnectorId);

        Assert.Equal("CUSTOM", payload["resource"]);
        Assert.Equal("already-set", payload["connector_id"]);
        var skills = Assert.IsType<List<string>>(payload["skills"]);
        // Still inserts lookup before po_match when skills already present without it.
        Assert.Equal(
            new[] { "extract_invoice", "po_lookup_sap", "po_match", "finalize_decision" },
            skills);
    }

    [Fact]
    public void TryReadMasterFromContext_ReadsEmailIngestFields()
    {
        var context = """
            {
              "emailIngest": true,
              "masterSource": "SAP",
              "masterConnectorId": "983bddbe-6a1a-4cd8-a024-9b4d84ba9981"
            }
            """;

        ApAgentPoMasterStartPayloadEnricher.TryReadMasterFromContext(
            context,
            out var source,
            out var connectorId);

        Assert.Equal("SAP", source);
        Assert.Equal(SapConnectorId, connectorId);
    }

    private static Dictionary<string, object?> BasePayload() => new()
    {
        ["tenantId"] = Guid.NewGuid().ToString("D"),
        ["workflowId"] = Guid.NewGuid().ToString("D"),
        ["formId"] = Guid.NewGuid().ToString("D"),
    };
}
