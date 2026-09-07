using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Review mode of dossier section 10.4: a proposal only leaves the proposal
/// state through an attributable operator answer, a rejection feeds a visible
/// expiring suppression, and the Activity feed carries the decision row.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class WatcherReviewModeTests : IDisposable
{
    private readonly WatcherSweepAcceptanceTests _harness = new();

    private async Task<(WatcherSweepAcceptanceTests.Stack Stack, WatcherProposal Proposal)> ProposeAsync()
    {
        var stack = _harness.Build();
        var fixture = WatcherFixtureMatrix.ById("dossier-descriptor-hygiene");
        await stack.Sweep.SweepAsync(fixture.Input);
        await stack.Sweep.SweepAsync(fixture.Input);
        return (stack, Assert.Single(stack.Store.Proposals()));
    }

    [Fact]
    public async Task Approve_MovesTheCardToReadyWithTheRecommendedModel()
    {
        var (stack, proposal) = await ProposeAsync();

        var result = await stack.Review.DecideAsync(
            proposal.Id, WatcherProposalDecisions.Approved, "alice", null, null);

        Assert.True(result.Ok);
        var card = Card(stack, proposal.CreatedTaskKey!);
        Assert.Equal(TaskStates.Ready, card.State);
        Assert.Equal(proposal.Recommendation.Model, card.Model);
        Assert.Equal(proposal.Recommendation.ThinkingLevel, card.ThinkingLevel);
    }

    [Fact]
    public async Task Approve_RecordsAnAttributableDecisionOnTheCardAndTheCase()
    {
        var (stack, proposal) = await ProposeAsync();

        await stack.Review.DecideAsync(proposal.Id, WatcherProposalDecisions.Approved, "alice", null, null);

        var stored = stack.Store.Proposal(proposal.Id)!;
        Assert.Equal(WatcherProposalDecisions.Approved, stored.Decision.State);
        Assert.Equal("alice", stored.Decision.DecidedBy);
        Assert.NotNull(stored.Decision.DecidedAtUtc);

        var card = Card(stack, proposal.CreatedTaskKey!);
        var audit = Assert.Single(
            stack.Timeline.ReadAll(card.FolderPath),
            entry => entry.Kind == TimelineEventKinds.WatcherProposalDecided);
        Assert.Equal("approved", audit.Details!["decision"]);
        Assert.Equal(TimelineActors.Human("alice"), audit.Actor);

        var item = stack.Store.Case(proposal.CaseId)!;
        Assert.Equal(WatcherCaseStates.Resolved, item.State);
        Assert.Contains("approved", item.TerminalReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingReachesReadyWithoutADecision()
    {
        var (stack, proposal) = await ProposeAsync();

        Assert.Equal(TaskStates.Preparation, Card(stack, proposal.CreatedTaskKey!).State);
        Assert.Equal(WatcherProposalDecisions.Pending, proposal.Decision.State);
        Assert.DoesNotContain(
            stack.Scanner.ScanAllAutomationJobs(), card => card.State == TaskStates.Ready);
    }

    [Fact]
    public async Task Edit_AlsoPromotesButIsCountedSeparatelyFromAnUneditedAcceptance()
    {
        var (stack, proposal) = await ProposeAsync();

        await stack.Review.DecideAsync(proposal.Id, WatcherProposalDecisions.Edited, "alice", null, null);

        Assert.Equal(TaskStates.Ready, Card(stack, proposal.CreatedTaskKey!).State);
        var evidence = Assert.Single(
            stack.Review.PromotionEvidence(),
            item => item.DetectorClass == WatcherDetectorClasses.Hygiene);
        Assert.Equal(0, evidence.Accepted);
        Assert.Equal(1, evidence.Edited);
    }

    [Fact]
    public async Task Reject_LeavesTheCardWhereItIsAndSuppressesTheFingerprint()
    {
        var (stack, proposal) = await ProposeAsync();

        var result = await stack.Review.DecideAsync(
            proposal.Id, WatcherProposalDecisions.Rejected, "alice", "planned maintenance window", null);

        Assert.True(result.Ok);
        Assert.Equal(TaskStates.Preparation, Card(stack, proposal.CreatedTaskKey!).State);
        var suppression = Assert.Single(stack.Store.Suppressions());
        Assert.Equal(proposal.Fingerprint, suppression.Fingerprint);
        Assert.Equal("planned maintenance window", suppression.Reason);
        Assert.True(suppression.ExpiresAtUtc > suppression.CreatedAtUtc);
    }

    [Fact]
    public async Task Reject_WithoutAReasonIsRefused()
    {
        var (stack, proposal) = await ProposeAsync();

        var result = await stack.Review.DecideAsync(
            proposal.Id, WatcherProposalDecisions.Rejected, "alice", "   ", null);

        Assert.Equal(WatcherDecisionStatus.ReasonRequired, result.Status);
        Assert.Empty(stack.Store.Suppressions());
        Assert.Equal(
            WatcherProposalDecisions.Pending, stack.Store.Proposal(proposal.Id)!.Decision.State);
    }

    [Fact]
    public async Task ASuppressedFingerprint_StopsProducingProposals()
    {
        var (stack, proposal) = await ProposeAsync();
        await stack.Review.DecideAsync(
            proposal.Id, WatcherProposalDecisions.Rejected, "alice", "known noise", null);
        var fixture = WatcherFixtureMatrix.ById("dossier-descriptor-hygiene");

        await stack.Sweep.SweepAsync(fixture.Input);
        await stack.Sweep.SweepAsync(fixture.Input);

        Assert.Single(stack.Store.Proposals());
        var item = Assert.Single(stack.Store.Cases());
        Assert.Equal(WatcherCaseStates.Suppressed, item.State);
        Assert.Equal(WatcherCasePolicy.TerminalReasons.Suppressed, item.TerminalReason);
    }

    [Fact]
    public async Task AnExpiredSuppression_LetsTheFingerprintReturn()
    {
        var (stack, proposal) = await ProposeAsync();
        var fixture = WatcherFixtureMatrix.ById("dossier-descriptor-hygiene");

        // Suppression always expires; an expired entry must not keep a real
        // problem invisible.
        stack.Store.Upsert(new WatcherSuppression
        {
            Fingerprint = proposal.Fingerprint,
            DetectorClass = proposal.DetectorClass,
            Reason = "expired",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-30),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(-1),
        });

        var result = await stack.Sweep.SweepAsync(fixture.Input);

        var item = Assert.Single(result.Cases);
        Assert.NotEqual(WatcherCaseStates.Suppressed, item.State);
    }

    [Fact]
    public async Task Merge_NeedsATargetAndClosesTheCaseWithoutPromoting()
    {
        var (stack, proposal) = await ProposeAsync();

        var refused = await stack.Review.DecideAsync(
            proposal.Id, WatcherProposalDecisions.Merged, "alice", null, null);
        Assert.Equal(WatcherDecisionStatus.MergeTargetRequired, refused.Status);

        var merged = await stack.Review.DecideAsync(
            proposal.Id, WatcherProposalDecisions.Merged, "alice", null, "AGT-2717");

        Assert.True(merged.Ok);
        Assert.Equal(TaskStates.Preparation, Card(stack, proposal.CreatedTaskKey!).State);
        var item = stack.Store.Case(proposal.CaseId)!;
        Assert.Equal(WatcherCaseStates.Resolved, item.State);
        Assert.Contains("AGT-2717", item.TerminalReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADecidedProposal_CannotBeDecidedTwice()
    {
        var (stack, proposal) = await ProposeAsync();
        await stack.Review.DecideAsync(proposal.Id, WatcherProposalDecisions.Approved, "alice", null, null);

        var second = await stack.Review.DecideAsync(
            proposal.Id, WatcherProposalDecisions.Rejected, "bob", "changed my mind", null);

        Assert.Equal(WatcherDecisionStatus.AlreadyDecided, second.Status);
        Assert.Equal(
            WatcherProposalDecisions.Approved, stack.Store.Proposal(proposal.Id)!.Decision.State);
    }

    [Fact]
    public async Task AnUnknownDecisionWordIsRefused()
    {
        var (stack, proposal) = await ProposeAsync();

        var result = await stack.Review.DecideAsync(proposal.Id, "yolo", "alice", null, null);

        Assert.Equal(WatcherDecisionStatus.InvalidDecision, result.Status);
    }

    [Fact]
    public void PromotionEvidence_NeverOffersContradictionOrDriftForAutoApproval()
    {
        var stack = _harness.Build();

        var evidence = stack.Review.PromotionEvidence();

        Assert.All(evidence, item => Assert.Equal(
            item.DetectorClass is WatcherDetectorClasses.Hygiene or WatcherDetectorClasses.Repetition,
            item.EligibleForAutoApproval));
    }

    // ---- Activity projection -------------------------------------------------

    [Fact]
    public async Task Activity_CarriesTheProblemAndTheDecisionRowForOneCase()
    {
        var (stack, _) = await ProposeAsync();

        var rows = stack.Projection.Read(["Agent Studio"]);

        Assert.Contains(rows, row =>
            row.Entry.Kind == OrchestratorLogKinds.Alert
            && row.Entry.Topic == WatcherBusTopics.FindingRaised);
        Assert.Contains(rows, row =>
            row.Entry.Kind == OrchestratorLogKinds.Decision
            && row.Entry.Topic == WatcherBusTopics.DecisionRequired);
        Assert.All(rows, row =>
            Assert.Equal(WatcherBusPublisher.ParticipantId, row.Entry.ParticipantId));
    }

    [Fact]
    public async Task Activity_AddsTheAnswerRowOnceTheOperatorDecided()
    {
        var (stack, proposal) = await ProposeAsync();

        await stack.Review.DecideAsync(proposal.Id, WatcherProposalDecisions.Approved, "alice", null, null);
        var rows = stack.Projection.Read(["Agent Studio"]);

        Assert.Contains(rows, row =>
            row.Entry.Kind == OrchestratorLogKinds.Action
            && row.Entry.Topic == WatcherBusTopics.DecisionRecorded);
    }

    [Fact]
    public async Task Activity_ShowsNothingToAPrincipalWithNoReadableProject()
    {
        var (stack, _) = await ProposeAsync();

        Assert.Empty(stack.Projection.Read([]));
    }

    [Fact]
    public void Activity_LeavesTheHeartbeatOutOfTheFeed()
    {
        // A liveness beat every minute is for the health monitor, not for the
        // operator's chronological feed.
        var heartbeat = new AgentMessage
        {
            Id = Guid.CreateVersion7().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            ParticipantId = WatcherBusPublisher.ParticipantId,
            Role = "system",
            Kind = "heartbeat",
            Topic = WatcherBusTopics.Heartbeat,
            Summary = "alive",
        };

        Assert.Null(WatcherActivityProjection.ToEntry(heartbeat));
    }

    private static TaskInfo Card(WatcherSweepAcceptanceTests.Stack stack, string taskKey) =>
        Assert.Single(
            stack.Scanner.ScanAllAutomationJobs(),
            card => string.Equals(card.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _harness.Dispose();
}
