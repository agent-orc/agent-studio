using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ApprovalOnlyNeedsInputPolicyTests
{
    // Quoted open items from the incident report and the AGT-2913 NeedsInput artifact.
    public static TheoryData<string, string> ApprovalItems => new()
    {
        { "AGT-2905", "Human sight review and acceptance of eight recommended decisions" },
        { "AGT-2906", "Review and approve decisions D5-D13" },
        { "AGT-2910", "Human review of design, measurement plan and seven implementation scopes required" },
        { "AGT-2913", "Product code is unchanged; human sight review remains pending." },
    };

    [Theory]
    [MemberData(nameof(ApprovalItems))]
    public void ApprovalOnly_DeliversToReview(string card, string item)
    {
        var outcome = ApprovalOnlyNeedsInputPolicy.Classify(new RunOutcome(
            RunOutcomeKind.NeedsInput, card, "Open item: " + item));

        Assert.Equal(RunOutcomeKind.Done, outcome.Kind);
        Assert.StartsWith("review-requested:", outcome.Reason);
        Assert.Null(outcome.NeedsInputMessage);
        Assert.Equal("4-auto-review", outcome.TargetState);
    }

    [Theory]
    [InlineData("Approval needs missing credentials for the gate")]
    [InlineData("Sight review cannot proceed: absent required file")]
    [InlineData("Which scope? There is no recommendation")]
    [InlineData("Which column is primary?")]
    [InlineData("Implementation is waiting on the operator's D5 and D6 decisions. Option A is recommended for each, but no selection is recorded. No files were changed. Please confirm option A for the pull-request approval surface.")]
    [InlineData("I stopped before changing code because the concept makes the operator's D1 answer a prerequisite. Please choose A or B. No files were changed.")]
    [InlineData("Implementation is blocked by D7. The delivery-chain dossier still lists the execution path as an open decision. Please select A, B, or C. No files were changed or tests run.")]
    [InlineData("No production gate host, source-run canary set, or bridge baseline provided to worktree for throughput parity measurement.")]
    [InlineData("The remote agent reported a blocker: missing-cross-executor-diagnostic-lease")]
    public void MissingFacts_StillNeedInput(string item)
    {
        var outcome = ApprovalOnlyNeedsInputPolicy.Classify(new RunOutcome(
            RunOutcomeKind.NeedsInput, "question", item));

        Assert.Equal(RunOutcomeKind.NeedsInput, outcome.Kind);
    }
}
