using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGatePolicyTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly BatchGateScope Scope = new("project", "repo", "develop", "full", "digest", "v1");
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static BatchGateSubject Subject(string key, int minute = 0, long sequence = 1) => new(
        key, "project", "repo", "develop", "full", "digest", "v1",
        "refs/heads/agent-studio/results/one", Sha, "run-1", 1, 1, Sha,
        true, true, true, true, false, null, true,
        Now.AddMinutes(minute), sequence);
    private static readonly BatchGateFormationOptions Enabled = new(Enabled: true);

    [Fact]
    public void Eligibility_RecordsEveryExclusionAndDoesNotSelectCode()
    {
        var result = BatchGatePolicy.Form([
            Subject("a", -20), Subject("f", -19), Subject("g", -18), Subject("h", -17),
            Subject("b") with { DocumentationOnly = false },
            Subject("c") with { CurrentGeneration = false },
            Subject("d") with { BuildTestDeferredToBatch = false },
            Subject("e") with { ActivePerTaskGate = true },
        ], Scope, Sha, Enabled, Now);
        Assert.Equal(["a", "f", "g", "h"], result.Manifest!.EligibleKeys);
        Assert.Equal(4, result.Exclusions.Count);
        Assert.Contains(result.Exclusions, x => x.TaskKey == "b" && x.Reason == BatchExclusionReason.NotDocumentationOnly);
        Assert.Contains(result.Exclusions, x => x.TaskKey == "c" && x.Reason == BatchExclusionReason.SupersededGeneration);
        Assert.Contains(result.Exclusions, x => x.TaskKey == "d" && x.Reason == BatchExclusionReason.BuildTestNotDeferred);
        Assert.Contains(result.Exclusions, x => x.TaskKey == "e" && x.Reason == BatchExclusionReason.ActivePerTaskGate);
    }

    [Fact]
    public void LeavesAsIntegrated_OnlyACurrentUnownedContainedMemberLeaves()
    {
        // AGT-3103: containment is asked last, and only for a member that could leave.
        var asked = 0;
        bool Contained(string sha) { asked++; return sha == Sha; }

        Assert.True(BatchGatePolicy.LeavesAsIntegrated(Subject("a"), Contained));
        Assert.False(BatchGatePolicy.LeavesAsIntegrated(Subject("b"), _ => false));
        Assert.Equal(1, asked);
        Assert.False(BatchGatePolicy.LeavesAsIntegrated(
            Subject("c") with { CurrentGeneration = false }, Contained));
        Assert.False(BatchGatePolicy.LeavesAsIntegrated(
            Subject("d") with { OwningBatchId = "batch-1" }, Contained));
        Assert.False(BatchGatePolicy.LeavesAsIntegrated(
            Subject("e") with { ActivePerTaskGate = true }, Contained));
        Assert.False(BatchGatePolicy.LeavesAsIntegrated(
            Subject("f") with { ResultSha = "" }, Contained));
        Assert.Equal(1, asked);
        // AGT-3139: a per-task gate the process lost no longer settles its member.
        Assert.True(BatchGatePolicy.LeavesAsIntegrated(
            Subject("g") with { ActivePerTaskGate = true }, Contained, gateInterrupted: true));
        Assert.False(BatchGatePolicy.LeavesAsIntegrated(
            Subject("h") with { ActivePerTaskGate = true, OwningBatchId = "batch-1" },
            Contained, gateInterrupted: true));
        Assert.Equal(2, asked);
    }

    [Fact]
    public void OrderAndDigestAreStableAndSubjectSensitive()
    {
        var members = new[] { Subject("z", -2, 2), Subject("b", -2, 1), Subject("a", -2, 1), Subject("c", -1, 1) };
        var first = BatchGatePolicy.Form(members, Scope, Sha, Enabled, Now).Manifest!;
        var second = BatchGatePolicy.Form(members.Reverse(), Scope, Sha, Enabled, Now).Manifest!;
        Assert.Equal(["a", "b", "z", "c"], first.Members.Select(x => x.TaskKey));
        Assert.Equal(first.MembershipDigest, second.MembershipDigest);
        Assert.NotEqual(first.MembershipDigest, BatchGatePolicy.MembershipDigest(
            Sha, Scope, first.Members.Select(x => x.TaskKey == "a" ? x with { DeliveryEpoch = 2 } : x).ToArray()));
    }

    [Fact]
    public void Proven_green_member_is_selected_first_on_its_tested_base()
    {
        var options = Enabled with { CloseSize = 2, MaximumSize = 2 };
        var members = new[] { Subject("old", -3), Subject("middle", -2), Subject("green", -1) };

        var formed = BatchGatePolicy.Form(members, Scope, Sha, options, Now,
            priorityKeys: new HashSet<string>(["green"], StringComparer.Ordinal));

        Assert.Equal(["green", "old"], formed.Manifest!.Members.Select(member => member.TaskKey));
    }

    [Theory]
    [InlineData(8, 2, 0, false)]
    [InlineData(8, 3, 0, true)]
    [InlineData(8, 1, 3, true)]
    public void ConflictCascadeGuardIsStrictlyMoreThanQuarterOrThreeConsecutive(
        int total, int conflicts, int consecutive, bool expected)
        => Assert.Equal(expected, BatchGatePolicy.CascadeGuard(total, conflicts, consecutive));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(8, 3)]
    public void HalvingBudgetIsCeilingLog2(int members, int expected)
        => Assert.Equal(expected, BatchGatePolicy.DiagnosticRunsPerCycle(members));

    [Fact]
    public void FormationTriggersHonorDeadlinePressureAndLoneFallback()
    {
        var code = Enabled with { Scope = "code" };
        BatchGateSubject Code(string key, int minute = 0) => Subject(key, minute) with { DocumentationOnly = false };
        Assert.Null(BatchGatePolicy.Form([Code("a")], Scope, Sha, code, Now).Manifest);
        Assert.False(BatchGatePolicy.Form([Code("a")], Scope, Sha, code, Now).UsePerTaskGate);
        var agedSolo = BatchGatePolicy.Form([Code("a", -16)], Scope, Sha, code, Now);
        Assert.True(agedSolo.UsePerTaskGate);
        Assert.Equal("oldest eligible card reached deadline without a batch partner", agedSolo.Reason);
        Assert.NotNull(BatchGatePolicy.Form([Code("a"), Code("b")], Scope, Sha, code, Now,
            reviewQueueLength: 12).Manifest);
        Assert.Null(BatchGatePolicy.Form([Code("a"), Code("b")], Scope, Sha, code, Now).Manifest);
    }

    [Fact]
    public void Threshold_does_not_form_below_configured_minimum_even_for_documentation()
    {
        var options = Enabled with { MinimumSize = 3, CloseSize = 2 };
        var result = BatchGatePolicy.Form([Subject("a"), Subject("b")],
            Scope, Sha, options, Now);

        Assert.Null(result.Manifest);
        Assert.False(result.UsePerTaskGate);
        Assert.Equal("eligible members below minimum batch size", result.Reason);
    }

    [Fact]
    public void Due_single_card_waits_for_a_partner_while_the_project_gate_is_busy()
    {
        var options = Enabled with { Scope = "code", DeadlineMinutes = 45 };
        var due = Subject("a", -46) with { DocumentationOnly = false };

        var held = BatchGatePolicy.Form([due], Scope, Sha, options, Now,
            projectGateBusy: true);

        Assert.Null(held.Manifest);
        Assert.False(held.UsePerTaskGate);
        Assert.True(BatchGatePolicy.Form([due], Scope, Sha, options, Now).UsePerTaskGate);
    }

    [Fact]
    public void Forced_close_below_minimum_explains_its_per_task_fallback()
    {
        var code = Enabled with { Scope = "code", MinimumSize = 2 };
        var formed = BatchGatePolicy.Close(
            [Subject("a") with { DocumentationOnly = false }], Scope, Sha,
            code, ["a"], Now);

        Assert.True(formed.UsePerTaskGate);
        Assert.Equal("close requested below minimum batch size", formed.Reason);
    }

    [Theory]
    [InlineData("documentation")]
    [InlineData("code")]
    public void DocumentationOnlyCardClosesAtOnceInsteadOfWaitingForItsDeadline(string scope)
    {
        // AGT-3414: its gate runs no test, so a partner saves no suite.
        var options = Enabled with { Scope = scope, MinimumSize = 1 };
        var lone = BatchGatePolicy.Form([Subject("a")], Scope, Sha, options, Now);
        Assert.Equal(["a"], lone.Manifest!.Members.Select(member => member.TaskKey));
        Assert.False(lone.UsePerTaskGate);
        // Past the deadline the lone card keeps its per-task gate, at the same level.
        Assert.True(BatchGatePolicy.Form([Subject("a", -16)], Scope, Sha, options, Now).UsePerTaskGate);
        var pair = BatchGatePolicy.Form([Subject("a"), Subject("b", sequence: 2)], Scope, Sha, options, Now);
        Assert.Equal(["a", "b"], pair.Manifest!.Members.Select(member => member.TaskKey));
    }

    [Fact]
    public void DocumentationOnlyCardStillWaitsForAPartnerBehindAGateBacklog()
    {
        // AGT-3219 still holds: a backlogged gate queue holds a lone card for a partner.
        var held = BatchGatePolicy.Form([Subject("a")], Scope, Sha, Enabled, Now,
            reviewQueueLength: 1, gateBacklog: true);
        Assert.False(held.UsePerTaskGate);
        Assert.Null(held.Manifest);
    }

    [Fact]
    public void DocumentationOnlyCardKeepsTheHoldOnARedBase()
    {
        var held = BatchGatePolicy.Form([Subject("a")], Scope, Sha, Enabled, Now, baseRed: true);
        Assert.Null(held.Manifest);
        Assert.False(held.UsePerTaskGate);
    }

    [Fact]
    public void MixedCodeSetKeepsTheThresholds()
    {
        var code = Enabled with { Scope = "code" };
        var mixed = BatchGatePolicy.Form([Subject("a"), Subject("b", sequence: 2) with { DocumentationOnly = false }],
            Scope, Sha, code, Now);
        Assert.Null(mixed.Manifest);
        Assert.False(mixed.UsePerTaskGate);
    }

    [Fact]
    public void GateTestLevel_DocumentationSubjectsWithADocumentationDiffSkipTheTests()
        => Assert.Equal(TestExecutionLevels.BuildOnly, BatchGatePolicy.GateTestLevel(
            [Subject("a"), Subject("b")], ["docs/concepts/batch.html", "README.md"], buildStepPlanned: true));

    [Fact]
    public void GateTestLevel_AnyCodeSubjectRunsTheCompleteSuite()
        => Assert.Equal(TestExecutionLevels.Full, BatchGatePolicy.GateTestLevel(
            [Subject("a"), Subject("b") with { DocumentationOnly = false }], ["docs/a.md"], buildStepPlanned: true));

    [Theory]
    [InlineData("backend/Program.cs")]
    [InlineData(".agent-studio/project.yml")]
    [InlineData("prompts/runtime/review.md")]
    public void GateTestLevel_AMixedGatedDiffRunsTheCompleteSuiteDespiteTheFlag(string product)
        => Assert.Equal(TestExecutionLevels.Full, BatchGatePolicy.GateTestLevel(
            [Subject("a")], ["docs/a.md", product], buildStepPlanned: true));

    [Fact]
    public void GateTestLevel_FailsClosed()
    {
        Assert.Equal(TestExecutionLevels.Full, BatchGatePolicy.GateTestLevel([Subject("a")], null, true));
        Assert.Equal(TestExecutionLevels.Full, BatchGatePolicy.GateTestLevel([Subject("a")], [], true));
        Assert.Equal(TestExecutionLevels.Full, BatchGatePolicy.GateTestLevel([], ["docs/a.md"], true));
        // Nothing but tests to run: the build-only level would verify nothing.
        Assert.Equal(TestExecutionLevels.Full, BatchGatePolicy.GateTestLevel([Subject("a")], ["docs/a.md"], false));
    }

    [Theory]
    [InlineData(true, false, 0, BatchReplayRoute.AlreadyContained)]
    [InlineData(true, true, 2, BatchReplayRoute.AlreadyContained)]
    [InlineData(false, true, 0, BatchReplayRoute.Identity)]
    [InlineData(false, false, 0, BatchReplayRoute.Rebase)]
    [InlineData(false, true, 1, BatchReplayRoute.Merge)]
    [InlineData(false, false, 3, BatchReplayRoute.Merge)]
    public void Replay_route_merges_only_a_delivery_with_merge_commits(
        bool contained, bool descendant, int merges, BatchReplayRoute expected)
        => Assert.Equal(expected, BatchReplayPolicy.Choose(contained, descendant, merges));

    [Fact]
    public void Replay_route_rejects_a_negative_merge_count()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BatchReplayPolicy.Choose(false, false, -1));

    [Fact]
    public void Replay_disposition_ejects_only_an_unattributable_member_and_pauses_on_infrastructure()
    {
        Assert.Equal(BatchReplayDisposition.Admit, BatchGatePolicy.ReplayDisposition(
            new BatchReplayResult(true, Sha, [new RebasedCommitReplacement(Sha, Sha)], [], null)));
        Assert.Equal(BatchReplayDisposition.EjectUnattributable, BatchGatePolicy.ReplayDisposition(
            BatchReplayResult.Unattributable("no single candidate commit")));
        Assert.Equal(BatchReplayDisposition.EjectUnattributable, BatchGatePolicy.ReplayDisposition(
            new BatchReplayResult(true, Sha, [], [], null)));
        Assert.Equal(BatchReplayDisposition.EjectUnattributable, BatchGatePolicy.ReplayDisposition(
            new BatchReplayResult(true, null, [new RebasedCommitReplacement(Sha, Sha)], [], null)));
        Assert.Equal(BatchReplayDisposition.Pause, BatchGatePolicy.ReplayDisposition(
            BatchReplayResult.Infrastructure("worktree")));
        // An unclassified failure keeps the old fail-closed pause.
        Assert.Equal(BatchReplayDisposition.Pause, BatchGatePolicy.ReplayDisposition(
            new BatchReplayResult(false, null, [], [], "unknown")));
    }
}
