using AgentStudio.Pipeline;
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
    public void DifferentRepositoriesBranchesAndProfilesHaveDifferentFormationScopes()
    {
        var first = Subject("first");
        var subjects = new[]
        {
            first,
            Subject("second") with { Repository = "other-repo" },
            Subject("third") with { IntegrationBranch = "release" },
            Subject("fourth") with { GateProfileDigest = "other-digest" },
        };
        var cohorts = subjects.GroupBy(BatchGatePolicy.ScopeOf).ToArray();
        Assert.Equal(4, cohorts.Length);
        Assert.All(cohorts, cohort => Assert.Single(cohort));
        Assert.Equal(Scope, BatchGatePolicy.ScopeOf(first));
        Assert.Equal(BatchExclusionReason.GateProfileMismatch,
            BatchGatePolicy.Exclusion(first, Scope with { GateProfileDigest = "new-digest" }, Enabled));
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
        Assert.Null(BatchGatePolicy.Form([Subject("a")], Scope, Sha, Enabled, Now).Manifest);
        Assert.True(BatchGatePolicy.Form([Subject("a", -16)], Scope, Sha, Enabled, Now).UsePerTaskGate);
        Assert.NotNull(BatchGatePolicy.Form([Subject("a"), Subject("b")], Scope, Sha, Enabled, Now,
            reviewQueueLength: 12).Manifest);
        Assert.Null(BatchGatePolicy.Form([Subject("a"), Subject("b")], Scope, Sha, Enabled, Now).Manifest);
    }

    [Fact]
    public void SupersedeMomentsFailClosed()
    {
        Assert.Equal(BatchSupersedeAction.EjectAndReconstruct, BatchGatePolicy.Supersede(BatchPhase.Formed));
        Assert.Equal(BatchSupersedeAction.EjectAndReconstruct, BatchGatePolicy.Supersede(BatchPhase.Assembled));
        Assert.Equal(BatchSupersedeAction.StopAndDiscardVerdict, BatchGatePolicy.Supersede(BatchPhase.Running));
        Assert.Equal(BatchSupersedeAction.BlockPublication, BatchGatePolicy.Supersede(BatchPhase.Green));
        Assert.Equal(BatchSupersedeAction.PreserveHistory, BatchGatePolicy.Supersede(BatchPhase.Published));
    }
}
