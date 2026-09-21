using SaaSApp.Workflow.Application.Connectors;
using SaaSApp.Workflow.Application.Workflows;

namespace SaaSApp.Workflow.Infrastructure.Tests;

public sealed class ApAgentPoMasterStartPayloadEnricherTests
{
    private static readonly Guid SapConnectorId = Guid.Parse("983bddbe-6a1a-4cd8-a024-9b4d84ba9981");
    private static readonly Guid QbConnectorId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid MasterFormId = Guid.Parse("168f611f-464e-47c5-be1d-d194d5a3c0dc");

    [Fact]
    public void Enrich_InternalForm_SetsMasterSourceAndFormId()
    {
        var payload = BasePayload();
        ApAgentPoMasterStartPayloadEnricher.Enrich(
            payload,
            EmailIngestMasterSources.InternalForm,
            null,
            MasterFormId.ToString("D"));

        Assert.Equal(EmailIngestMasterSources.InternalForm, payload["master_source"]);
        Assert.Equal(MasterFormId.ToString("D"), payload["master_form_id"]);
        Assert.False(payload.ContainsKey("resource"));
        Assert.False(payload.ContainsKey("connector_id"));
        Assert.False(payload.ContainsKey("skills"));
    }

    [Fact]
    public void Enrich_FormIdOnly_TreatsAsInternalForm()
    {
        var payload = BasePayload();
        ApAgentPoMasterStartPayloadEnricher.Enrich(
            payload,
            null,
            null,
            MasterFormId.ToString("D"));

        Assert.Equal(EmailIngestMasterSources.InternalForm, payload["master_source"]);
        Assert.Equal(MasterFormId.ToString("D"), payload["master_form_id"]);
        Assert.False(payload.ContainsKey("resource"));
    }

    [Fact]
    public void Enrich_InternalForm_WithoutFormId_SetsMasterSourceOnly()
    {
        var payload = BasePayload();
        ApAgentPoMasterStartPayloadEnricher.Enrich(
            payload,
            EmailIngestMasterSources.InternalForm,
            null);

        Assert.Equal(EmailIngestMasterSources.InternalForm, payload["master_source"]);
        Assert.False(payload.ContainsKey("master_form_id"));
        Assert.False(payload.ContainsKey("resource"));
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

        Assert.Equal("SAP", payload["master_source"]);
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
    public void Enrich_Hana_InjectsHanaResourceAndSapLookupSkill()
    {
        var payload = BasePayload();
        ApAgentPoMasterStartPayloadEnricher.Enrich(
            payload,
            "HANA",
            SapConnectorId);

        Assert.Equal("HANA", payload["master_source"]);
        Assert.Equal("HANA", payload["resource"]);
        Assert.Equal(SapConnectorId.ToString("D"), payload["connector_id"]);
        var skills = Assert.IsType<List<string>>(payload["skills"]);
        Assert.Contains(ApAgentPoMasterStartPayloadEnricher.SkillPoLookupSap, skills);
    }

    [Fact]
    public void Enrich_QuickBooks_InjectsResourceConnectorAndSkills()
    {
        var payload = BasePayload();
        ApAgentPoMasterStartPayloadEnricher.Enrich(
            payload,
            EmailIngestMasterSources.QuickBooks,
            QbConnectorId);

        Assert.Equal("QuickBooks", payload["master_source"]);
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
              "masterConnectorId": "983bddbe-6a1a-4cd8-a024-9b4d84ba9981",
              "masterFormId": "168f611f-464e-47c5-be1d-d194d5a3c0dc"
            }
            """;

        ApAgentPoMasterStartPayloadEnricher.TryReadMasterFromContext(
            context,
            out var source,
            out var connectorId,
            out var formId);

        Assert.Equal("SAP", source);
        Assert.Equal(SapConnectorId, connectorId);
        Assert.Equal(MasterFormId.ToString("D"), formId);
    }

    [Fact]
    public void TryReadPoMaster_FromApAgentBlock_InternalForm_UsesBlockFormIdNotInvoiceForm()
    {
        const string invoiceFormId = "6e45749f-c65d-4f28-9e0f-f22fdde4cb03";
        const string poMasterFormId = "1e16dd88-28b9-4da4-b577-eb0ec6d4d621";
        var workflowJson = $$"""
            {
              "Settings": {
                "General": { "InitiateUsing": { "Type": "DOCUMENT_FORM", "FormId": "{{invoiceFormId}}" } }
              },
              "Blocks": [
                {
                  "type": "AP_AGENT",
                  "settings": {
                    "label": "AP AGENT 1",
                    "formId": "{{poMasterFormId}}",
                    "poMasterSourceType": "internal",
                    "resource": "FORM",
                    "apAgent": {
                      "formId": "{{poMasterFormId}}",
                      "resource": "FORM",
                      "connectorId": ""
                    }
                  }
                }
              ]
            }
            """;

        Assert.True(WorkflowApAgentJson.TryReadPoMaster(
            workflowJson,
            out var source,
            out var connectorId,
            out var formId));

        Assert.Equal(EmailIngestMasterSources.InternalForm, source);
        Assert.Equal(poMasterFormId, formId);
        Assert.Null(connectorId);
        Assert.NotEqual(invoiceFormId, formId);

        var payload = BasePayload();
        payload["formId"] = invoiceFormId;
        ApAgentPoMasterStartPayloadEnricher.Enrich(payload, source, connectorId, formId);

        Assert.Equal(EmailIngestMasterSources.InternalForm, payload["master_source"]);
        Assert.Equal(poMasterFormId, payload["master_form_id"]);
        Assert.Equal(invoiceFormId, payload["formId"]);
    }

    private static Dictionary<string, object?> BasePayload() => new()
    {
        ["tenantId"] = Guid.NewGuid().ToString("D"),
        ["workflowId"] = Guid.NewGuid().ToString("D"),
        ["formId"] = Guid.NewGuid().ToString("D"),
    };
}
