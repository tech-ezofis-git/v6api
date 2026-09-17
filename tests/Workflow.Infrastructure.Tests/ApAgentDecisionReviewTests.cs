using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Domain.Entities;
using SaaSApp.Workflow.Domain.Enums;
using Xunit;

namespace SaaSApp.Workflow.Infrastructure.Tests;

public sealed class ApAgentDecisionReviewTests
{
    [Theory]
    [InlineData("Matched", true)]
    [InlineData("Partially Matched", true)]
    [InlineData("Not Matched", true)]
    [InlineData("Non-Invoice", true)]
    [InlineData("Approve", true)]
    [InlineData("Reject", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsApAgentDecisionReview_RecognizesAgentLabels(string? review, bool expected)
    {
        Assert.Equal(expected, WorkflowStepTransitionHelper.IsApAgentDecisionReview(review));
    }

    [Fact]
    public void HasMatchingAction_MatchesNotMatchedAndNonInvoiceBuckets()
    {
        var step = WorkflowStep.Create(
            Guid.NewGuid(),
            "AP AGENT 1",
            StepType.Automated,
            order: 2,
            stageType: "AP_AGENT",
            actionsJson: """
            [
              {"Id":"1","ProceedAction":"Not Matched","ToBlockId":"verifier"},
              {"Id":"2","ProceedAction":"Non-Invoice","ToBlockId":"end"}
            ]
            """);

        Assert.True(WorkflowStepActionsHelper.HasMatchingAction(step, "Not Matched"));
        Assert.True(WorkflowStepActionsHelper.HasMatchingAction(step, "Non-Invoice"));
        Assert.False(WorkflowStepActionsHelper.HasMatchingAction(step, "Matched"));
    }
}
