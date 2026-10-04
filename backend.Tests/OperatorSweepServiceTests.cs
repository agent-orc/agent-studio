using Xunit;

namespace AgentStudio.Tests;

public sealed class OperatorSweepServiceTests
{
    private static ReviewAttemptDto Review(AttemptLifecycleState state) => new(
        "review-1", "AGT-1", "repo", "run-1", null,
        new ReviewSubjectDto("subject-1", "repo", new string('a', 40), "run-1", "requirements", "policy", [], DateTime.UtcNow),
        state, null, 0, 0, DateTime.UtcNow, null, null, null, null, null, []);

    [Theory]
    [InlineData(AttemptLifecycleState.Pending, true)]
    [InlineData(AttemptLifecycleState.Leased, true)]
    [InlineData(AttemptLifecycleState.Completed, false)]
    [InlineData(AttemptLifecycleState.Superseded, false)]
    public void ActiveReviewAuthority_PreventsSweepAction(AttemptLifecycleState state, bool expected)
        => Assert.Equal(expected, OperatorSweepPolicy.HasActiveReview([Review(state)]));

    [Fact]
    public void SweepAndOrchestratorReissues_UseOnePerCardBudgetAcrossEpochs()
    {
        var records = new[]
        {
            Decision(ReviewDecisionKind.Reissue, "orchestrator", 0),
            Decision(ReviewDecisionKind.OperatorRequeue, "operator", 1),
            Decision(ReviewDecisionKind.Reissue, "operator-sweep:auto-fix", 1),
        };
        Assert.Equal(2, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, "AGT-1"));
        Assert.Equal(0, Math.Max(0, 2 - ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, "AGT-1")));
    }

    [Fact]
    public async Task FailedTick_DoesNotPreventNextTick()
    {
        var calls = 0;
        var errors = new List<Exception>();
        Task Tick()
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("transient");
            return Task.CompletedTask;
        }

        await OperatorSweepService.RunTickSafelyAsync(Tick, errors.Add);
        await OperatorSweepService.RunTickSafelyAsync(Tick, errors.Add);

        Assert.Equal(2, calls);
        Assert.Single(errors);
    }

    [Fact]
    public void TaskFolderJournal_DeduplicatesUnchangedReasonsAndRetainsDecisionAfterReload()
    {
        var folder = Path.Combine(Path.GetTempPath(), "operator-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var row = new OperatorSweepCardStatus("AGT-1", "job-1", OperatorSweepNames.AutoFix,
                "active-review-attempt", "review-1", DateTime.UtcNow, 0, 2, true);
            Assert.True(OperatorSweepJournal.Record(folder, row));
            Assert.False(OperatorSweepJournal.Record(folder, row with { AtUtc = row.AtUtc.AddMinutes(1) }));
            Assert.Single(OperatorSweepJournal.Read(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static ReviewDecisionRecord Decision(ReviewDecisionKind kind, string reason, int epoch)
        => new(DateTime.UtcNow, "AGT-1", "demo", kind, reason, "", "", "")
        { AttemptEpoch = epoch };
}
