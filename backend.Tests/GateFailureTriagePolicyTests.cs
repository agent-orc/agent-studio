using AgentStudio.Pipeline;
using ProjectDefinitionIssueText = AgentStudio.TaskServer.Contracts.ProjectDefinitionIssueText;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3009: one test per gate-failure class, each fed a captured merge-gate
/// evidence log (see <c>Fixtures/gate-failure-triage/README.md</c> for the two
/// derived ones). The assertions name the marker or experiment that decided
/// the class, so a changed marker list fails here rather than on the board.
/// </summary>
public sealed class GateFailureTriagePolicyTests
{
    private const string SharedFailure =
        "AgentStudio.Tests.BatchMoveJobTests.Endpoint_ReportsProgress_ContinuesAfterItemFailure_AndDoesNotBlockReviewRead";

    internal static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gate-failure-triage", name));

    [Theory]
    [InlineData("environment-worker-crash.AGT-2724.log", GateEnvironmentKinds.WorkerCrash, "Worker exited unexpectedly")]
    [InlineData("environment-run-budget.AGT-2839.log", GateEnvironmentKinds.RunBudget, "violated gate-run budget")]
    [InlineData("environment-transport.derived.log", GateEnvironmentKinds.Transport, "ECONNRESET")]
    public void EnvironmentLog_IsEnvironment_WithTheDecidingMarker(string fixture, string kind, string marker)
    {
        var triage = GateFailureTriagePolicy.Classify(Fixture(fixture));

        Assert.Equal(GateFailureClasses.Environment, triage.Class);
        Assert.Equal(kind, triage.Kind);
        Assert.Contains(marker, triage.Markers);
        Assert.Empty(triage.FailingItems);
        Assert.Equal("gate-environment:" + kind, triage.Fingerprint);
    }

    [Fact]
    public void RealTestFailure_IsProduct_CarryingTheFailingItemNames()
    {
        var triage = GateFailureTriagePolicy.Classify(Fixture("product.AGT-2722.log"));

        Assert.Equal(GateFailureClasses.Product, triage.Class);
        Assert.Equal(["AgentStudio.Tests.AutoPushStrategyTests.CompletedPushWorker_PushesQueuedCommitToMain"],
            triage.FailingItems);
        Assert.Contains("AutoPushStrategyTests.CompletedPushWorker_PushesQueuedCommitToMain", triage.ReasonLine);
        Assert.StartsWith("gate-items:", triage.Fingerprint);
    }

    [Fact]
    public void SameFailingItemOnTwoCards_IsOneFingerprint()
    {
        var first = GateFailureTriagePolicy.Classify(Fixture("shared-cause.AGT-2677.log"));
        var second = GateFailureTriagePolicy.Classify(Fixture("shared-cause.AGT-2752.log"));

        Assert.Equal(GateFailureClasses.Product, first.Class);
        Assert.Equal([SharedFailure], first.FailingItems);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void RedIntegrationBaseline_IsIntegrationBranch_NotChargedToTheCard()
    {
        var triage = GateFailureTriagePolicy.Classify(Fixture("integration-branch.derived-from-AGT-2752.log"));

        Assert.Equal(GateFailureClasses.IntegrationBranch, triage.Class);
        Assert.Equal([SharedFailure], triage.FailingItems);
        Assert.Contains("baseline=red", triage.Markers);
        // The same items keep the same fingerprint whichever class they land in,
        // so a branch defect and the cards it reddened are counted as one cause.
        Assert.Equal(
            GateFailureTriagePolicy.Classify(Fixture("shared-cause.AGT-2752.log")).Fingerprint,
            triage.Fingerprint);
    }

    [Fact]
    public void BudgetMarkerAndFailingTest_Conflict_IsUndecidable_StatingTheMissingEvidence()
    {
        var triage = GateFailureTriagePolicy.Classify(Fixture("undecidable-conflict.AGT-2712.log"));

        Assert.Equal(GateFailureClasses.Undecidable, triage.Class);
        Assert.Contains("violated gate-run budget", triage.Markers);
        Assert.Contains(
            "AgentStudio.Tests.WikiContentCacheTests.WatcherEvent_EagerlyRebuilds_BeforePublishingTheEvent",
            triage.FailingItems);
        Assert.Contains("no clean repeat on the same tree", triage.MissingEvidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoGateLog_IsUndecidable(string? log)
    {
        var triage = GateFailureTriagePolicy.Classify(log);

        Assert.Equal(GateFailureClasses.Undecidable, triage.Class);
        Assert.Contains("no merge-gate evidence log", triage.MissingEvidence);
    }

    [Fact]
    public void FlakeLabelWithoutARerun_IsUndecidable_NotASilentFlake()
    {
        var log = WithHeaderLine(Fixture("product.AGT-2722.log"),
            "retryPerformed=false classification=FlakyQuarantine flakyQuarantined=" +
            "AgentStudio.Tests.AutoPushStrategyTests.CompletedPushWorker_PushesQueuedCommitToMain");

        var triage = GateFailureTriagePolicy.Classify(log);

        Assert.Equal(GateFailureClasses.Undecidable, triage.Class);
        Assert.Contains("without a passing re-run", triage.MissingEvidence);
        // Counted like any other failure: the items keep their fingerprint.
        Assert.StartsWith("gate-items:", triage.Fingerprint);
    }

    [Fact]
    public void FlakeLabelWithARerunOnTheSameTree_IsAllowed()
    {
        Assert.True(GateFlakeLabelPolicy.Check(true, "FlakyQuarantine", ["A.B.C"]).Allowed);
        Assert.False(GateFlakeLabelPolicy.Check(false, "FlakyQuarantine", ["A.B.C"]).Allowed);
        Assert.True(GateFlakeLabelPolicy.Check(false, "none", []).Allowed);
    }

    [Fact]
    public void CleanRepeatGreenOnTheSameTree_IsAProvenIntermittentEnvironmentFailure()
    {
        var log = WithReasonSuffix(Fixture("product.AGT-2722.log"),
            "; diagnosis=intermittent confidence=0.90; fingerprint=gate:a; baseline=green; fingerprint=none; " +
            "clean-repeat=green; fingerprint=none; history-24h: other-cards=0; prior=2");

        var triage = GateFailureTriagePolicy.Classify(log);

        Assert.Equal(GateFailureClasses.Environment, triage.Class);
        Assert.Equal(GateEnvironmentKinds.ProvenIntermittent, triage.Kind);
    }

    [Fact]
    public void VitestFailureLines_AreFailingItems_WithoutTheWorkspaceLabel()
    {
        var items = GateFailureTriagePolicy.FailingItems(
            "[stderr]  FAIL  |frontend| src/app/features/orchestrator/orchestrator-side-sheet.pin.spec.ts > OrchestratorSideSheet > pins the sheet\n" +
            "[stderr]  \u001b[31mFAIL\u001b[39m  src/app/features/orchestrator/orchestrator-side-sheet.pin.spec.ts > OrchestratorSideSheet > pins the sheet\n");

        Assert.Equal(
            ["src/app/features/orchestrator/orchestrator-side-sheet.pin.spec.ts > OrchestratorSideSheet > pins the sheet"],
            items);
    }

    [Fact]
    public void GreenNewestLogAfterAProvenFlake_IsNotTreatedAsAFailure()
    {
        // AGT-2867's gate went green after a targeted re-run of its two failures:
        // a legitimate flake label, and nothing for the router to act on.
        var triage = GateFailureTriagePolicy.Classify(Fixture("green-proven-flake.AGT-2867.log"));

        Assert.Equal(GateFailureClasses.Undecidable, triage.Class);
        Assert.Contains("verdict Ok", triage.MissingEvidence);
    }

    /// <summary>The newest pre-main gate log of AOW-15, as written before the fix.</summary>
    internal const string Aow15Log =
        "verdict=Fail exit=n/a durationMs=812 failureKind=Environment\n" +
        "expectedSha=4f1c2e9a0b7d3c5e8f6a1b2c3d4e5f6a7b8c9d0e testedSha=4f1c2e9a0b7d3c5e8f6a1b2c3d4e5f6a7b8c9d0e\n" +
        "reason=Repository execution definition is invalid.; diagnosis=unclassified-first-occurrence confidence=0.20; " +
        "fingerprint=unavailable; baseline=green; fingerprint=none; clean-repeat=unavailable; fingerprint=none; " +
        "history-24h: other-cards=0; prior=0\n" +
        "budget=none\n" +
        "--- last-300-lines ---\n";

    [Fact]
    public void Aow15_InvalidDefinitionLoggedAsEnvironment_IsADefinitionProductFailure()
    {
        var triage = GateFailureTriagePolicy.Classify(Aow15Log);

        Assert.Equal(GateFailureClasses.Product, triage.Class);
        Assert.Equal(GateProductKinds.Definition, triage.Kind);
        Assert.Equal(["definition-invalid"], triage.Markers);
        Assert.DoesNotContain("diagnosis=", triage.ReasonLine);
        Assert.DoesNotContain("toolchain", triage.ReasonLine);
        Assert.Contains("commands.prepare is required", triage.ReasonLine);
    }

    [Fact]
    public void InvalidDefinitionWithIssues_CarriesEachIssueAsAFailingItem()
    {
        var reason = ProjectDefinitionIssueText.InvalidReason(
        [
            new("commands.prepare", "prepare-path-invalid", "Prepare must be a repository-relative path without traversal."),
            new("testSuites[0].id", "suite-id-invalid", "Test suite ids are required and unique."),
        ]);
        var log = Aow15Log
            .Replace("failureKind=Environment", "failureKind=Code", StringComparison.Ordinal)
            .Replace("reason=Repository execution definition is invalid.; diagnosis=", "reason=" + reason + " ignored=",
                StringComparison.Ordinal);

        var triage = GateFailureTriagePolicy.Classify(log);

        Assert.Equal(GateFailureClasses.Product, triage.Class);
        Assert.Equal(GateProductKinds.Definition, triage.Kind);
        Assert.Equal(["commands.prepare [prepare-path-invalid]", "testSuites[0].id [suite-id-invalid]"],
            triage.FailingItems);
        Assert.StartsWith("gate-definition:", triage.Fingerprint);
        Assert.Contains("commands.prepare [prepare-path-invalid] Prepare must be a repository-relative path",
            triage.ReasonLine);
    }

    private static string WithHeaderLine(string log, string line)
    {
        var lines = log.Split('\n').ToList();
        lines.Insert(3, line);
        return string.Join('\n', lines);
    }

    private static string WithReasonSuffix(string log, string suffix)
    {
        var lines = log.Split('\n');
        var index = Array.FindIndex(lines, item => item.StartsWith("reason=", StringComparison.Ordinal));
        lines[index] = lines[index].TrimEnd('\r') + suffix;
        return string.Join('\n', lines);
    }
}

/// <summary>AGT-3009: the routing matrix, one row per class and budget state.</summary>
public sealed class GateFailureRoutingPolicyTests
{
    private static GateFailureTriage Triage(string cls, params string[] items) => new(
        cls,
        cls == GateFailureClasses.Environment ? GateEnvironmentKinds.WorkerCrash : null,
        items.Length == 0 ? "gate-environment:worker-crash" : GateFailureTriagePolicy.ItemFingerprint(items),
        items,
        "reason.",
        cls == GateFailureClasses.Undecidable ? "evidence" : null,
        []);

    public static TheoryData<string, bool, int?, int, bool, GateFailureRouteAction, string?> Matrix => new()
    {
        { GateFailureClasses.Environment, true, null, 0, true, GateFailureRouteAction.ReplayGate, null },
        { GateFailureClasses.Environment, false, null, 0, true, GateFailureRouteAction.ParkExhausted, GateFailureParkCategories.EnvironmentExhausted },
        { GateFailureClasses.Product, true, 0, 0, true, GateFailureRouteAction.FixRound, null },
        { GateFailureClasses.Product, true, null, 0, true, GateFailureRouteAction.FixRound, null },
        { GateFailureClasses.Product, true, 1, 0, true, GateFailureRouteAction.AttachToCause, GateFailureParkCategories.SharedCause },
        { GateFailureClasses.Product, true, 0, 1, true, GateFailureRouteAction.ParkExhausted, GateFailureParkCategories.Product },
        { GateFailureClasses.Product, true, 0, 0, false, GateFailureRouteAction.ParkExhausted, GateFailureParkCategories.Product },
        { GateFailureClasses.IntegrationBranch, true, 0, 0, true, GateFailureRouteAction.AttachToCause, GateFailureParkCategories.SharedCause },
        { GateFailureClasses.Undecidable, true, 5, 0, true, GateFailureRouteAction.ParkUndecidable, GateFailureParkCategories.Undecidable },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Decide_RoutesEveryClass(
        string cls, bool replayBudgetLeft, int? otherCards, int fixRounds, bool automatic,
        GateFailureRouteAction expected, string? category)
    {
        var triage = cls == GateFailureClasses.Environment ? Triage(cls) : Triage(cls, "A.B.C");

        var route = GateFailureRoutingPolicy.Decide(triage,
            new GateFailureRoutingFacts(replayBudgetLeft, otherCards, fixRounds, automatic));

        Assert.Equal(expected, route.Action);
        Assert.Equal(category, route.Category);
    }

    [Fact]
    public void Aow15_InvalidDefinition_StartsTheFixRound_WithoutReplayBudgetOrSharedCause()
    {
        var triage = GateFailureTriagePolicy.Classify(GateFailureTriagePolicyTests.Aow15Log);

        // AOW-15 had spent its replay ladder; a definition failure never reads it.
        var route = GateFailureRoutingPolicy.Decide(triage, new GateFailureRoutingFacts(false, 4, 0, true));

        Assert.Equal(GateFailureRouteAction.FixRound, route.Action);
        Assert.False(route.Parks);
        var prompt = GateFailureFixRoundPrompt.Build(
            "AOW-15", "refs/heads/aow-15", new string('a', 40), "develop", "pre-main-test-gate", triage, null);
        Assert.Contains(ProjectDefinitionIssueText.InvalidPrefix, prompt);
        Assert.Contains("commands.prepare is required", prompt);
    }

    [Fact]
    public void InvalidDefinition_SpentFixRound_ParksAsProduct_WithoutAHostRepairHint()
    {
        var triage = GateFailureTriagePolicy.Classify(GateFailureTriagePolicyTests.Aow15Log);
        var route = GateFailureRoutingPolicy.Decide(triage, new GateFailureRoutingFacts(false, 0, 1, true));

        var reason = GateFailureRoutingPolicy.ParkReason(triage, route);

        Assert.Equal(GateFailureParkCategories.Product, route.Category);
        Assert.Contains("class product/definition", reason);
        Assert.DoesNotContain("repair the gate host", reason);
        Assert.DoesNotContain("toolchain", reason);
    }

    [Fact]
    public void EnvironmentWithBudget_NeverParks()
    {
        var route = GateFailureRoutingPolicy.Decide(Triage(GateFailureClasses.Environment),
            new GateFailureRoutingFacts(true, 9, 9, true));

        Assert.False(route.Parks);
    }

    [Fact]
    public void ParkReason_IsTyped_AndNamesClassAndMissingEvidence()
    {
        var triage = Triage(GateFailureClasses.Undecidable, "A.B.C");
        var route = GateFailureRoutingPolicy.Decide(triage, new GateFailureRoutingFacts(true, 0, 0, true));

        var reason = GateFailureRoutingPolicy.ParkReason(triage, route);

        Assert.Equal(GateFailureParkCategories.Undecidable, ParkedBlockerCatalog.ReadBlockerType(reason));
        Assert.Contains("class undecidable", reason);
        Assert.Contains("Missing evidence: evidence", reason);
    }
}
