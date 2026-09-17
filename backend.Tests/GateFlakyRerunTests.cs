using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2853, acceptance item 1 and 4: the pure decision behind the gate's one
/// targeted re-run. A red test step earns the re-run only when the failure is a
/// product failure, the command is a filterable <c>dotnet test</c>, the exact
/// failed names could be read, and the gate-run budget still has room.
/// </summary>
public sealed class GateFlakyRerunPolicyTests
{
    private const string OneFailure =
        "  Failed AgentStudio.Tests.WikiContentCacheTests.Warmup_StartAsync [102 ms]\n" +
        "  Error Message: expected 1, was 0";

    private const string TwoFailures =
        "  Failed AgentStudio.Tests.BoardConditionalReadTests.ListRead_ValidatesTheSameWay [12 ms]\n" +
        "  Failed AgentStudio.Tests.TaskIndexCacheTests.MutationDuringInFlightRefresh [7 ms]";

    [Fact]
    public void Red_dotnet_test_step_with_readable_names_and_budget_earns_one_targeted_rerun()
    {
        var decision = Decide(OneFailure, "dotnet test agent-taskboard.sln --filter Category!=MachineBound");

        Assert.True(decision.ShouldRerun);
        Assert.Equal(GateFlakyRerunReasons.Eligible, decision.Reason);
        Assert.Equal(["AgentStudio.Tests.WikiContentCacheTests.Warmup_StartAsync"], decision.FailedTests);
    }

    [Fact]
    public void Filter_expression_is_built_from_the_exact_failed_test_names_on_the_same_build()
    {
        var decision = Decide(TwoFailures, "dotnet test agent-taskboard.sln --filter Category!=MachineBound");

        Assert.Equal(
            "FullyQualifiedName=AgentStudio.Tests.BoardConditionalReadTests.ListRead_ValidatesTheSameWay" +
            "|FullyQualifiedName=AgentStudio.Tests.TaskIndexCacheTests.MutationDuringInFlightRefresh",
            GateFlakyRerunPolicy.FilterExpression(decision.FailedTests));
        Assert.Equal(
            "dotnet test agent-taskboard.sln --no-build --filter " +
            "\"FullyQualifiedName=AgentStudio.Tests.BoardConditionalReadTests.ListRead_ValidatesTheSameWay" +
            "|FullyQualifiedName=AgentStudio.Tests.TaskIndexCacheTests.MutationDuringInFlightRefresh\"",
            decision.Command);
    }

    [Fact]
    public void An_existing_no_build_option_is_not_duplicated()
    {
        var decision = Decide(OneFailure, "dotnet test Product.Tests.csproj --no-build");

        Assert.Equal(
            "dotnet test Product.Tests.csproj --no-build --filter " +
            "\"FullyQualifiedName=AgentStudio.Tests.WikiContentCacheTests.Warmup_StartAsync\"",
            decision.Command);
    }

    [Fact]
    public void Spent_gate_run_budget_leaves_the_original_red_standing()
    {
        var decision = GateFlakyRerunPolicy.Decide(
            VerifyCommandKind.Test,
            BuildTestGateFailureKind.Code,
            "dotnet test agent-taskboard.sln",
            OneFailure,
            TimeSpan.Zero);

        Assert.False(decision.ShouldRerun);
        Assert.Equal(GateFlakyRerunReasons.NoBudgetLeft, decision.Reason);
        Assert.Null(decision.Command);
        Assert.NotEmpty(decision.FailedTests);
    }

    [Theory]
    [InlineData(BuildTestGateFailureKind.Environment)]
    [InlineData(BuildTestGateFailureKind.Timeout)]
    [InlineData(BuildTestGateFailureKind.Lock)]
    [InlineData(BuildTestGateFailureKind.OutOfMemory)]
    [InlineData(BuildTestGateFailureKind.MissingSource)]
    public void Only_a_product_failure_earns_a_rerun(BuildTestGateFailureKind kind)
    {
        var decision = GateFlakyRerunPolicy.Decide(
            VerifyCommandKind.Test, kind, "dotnet test agent-taskboard.sln",
            OneFailure, TimeSpan.FromMinutes(10));

        Assert.False(decision.ShouldRerun);
        Assert.Equal(GateFlakyRerunReasons.NotAProductFailure, decision.Reason);
    }

    [Theory]
    [InlineData(VerifyCommandKind.Build)]
    [InlineData(VerifyCommandKind.Lint)]
    public void Only_a_test_step_earns_a_rerun(VerifyCommandKind kind)
    {
        var decision = GateFlakyRerunPolicy.Decide(
            kind, BuildTestGateFailureKind.Code, "dotnet test agent-taskboard.sln",
            OneFailure, TimeSpan.FromMinutes(10));

        Assert.False(decision.ShouldRerun);
        Assert.Equal(GateFlakyRerunReasons.NotATestCommand, decision.Reason);
    }

    [Theory]
    [InlineData("npm test")]
    [InlineData("npm run test -- --watch=false")]
    [InlineData("dotnet build")]
    [InlineData("dotnet test a.sln && dotnet test b.sln")]
    [InlineData("dotnet test a.sln | tee log.txt")]
    public void A_command_whose_filter_cannot_be_rewritten_safely_is_left_alone(string command)
    {
        var decision = Decide(OneFailure, command);

        Assert.False(decision.ShouldRerun);
        Assert.Equal(GateFlakyRerunReasons.NotATestCommand, decision.Reason);
    }

    [Fact]
    public void A_selection_filter_containing_its_own_operators_is_still_targetable()
    {
        var decision = Decide(
            OneFailure,
            "dotnet test agent-taskboard.sln --filter Category!=MachineBound&Category!=Slow");

        Assert.True(decision.ShouldRerun);
        Assert.DoesNotContain("MachineBound", decision.Command);
    }

    [Fact]
    public void Unparseable_output_keeps_the_original_red()
    {
        var decision = Decide("Build FAILED.\n1 Error(s)", "dotnet test agent-taskboard.sln");

        Assert.False(decision.ShouldRerun);
        Assert.Equal(GateFlakyRerunReasons.NoParsedTestNames, decision.Reason);
    }

    [Fact]
    public void A_broad_red_is_a_real_red_and_is_not_retried()
    {
        var output = string.Join(
            "\n",
            Enumerable.Range(0, GateFlakyRerunPolicy.MaxTargetedTests + 1)
                .Select(i => $"  Failed Product.Tests.Suite.Case{i} [1 ms]"));

        var decision = Decide(output, "dotnet test agent-taskboard.sln");

        Assert.False(decision.ShouldRerun);
        Assert.Equal(GateFlakyRerunReasons.TooManyFailures, decision.Reason);
    }

    [Fact]
    public void A_theory_case_printed_with_its_arguments_is_not_targeted_by_a_filter_it_cannot_express()
    {
        var decision = Decide(
            "  Failed Product.Tests.Suite.Case(value: \"a|b\") [1 ms]",
            "dotnet test agent-taskboard.sln");

        Assert.False(decision.ShouldRerun);
        Assert.Equal(GateFlakyRerunReasons.UnfilterableTestName, decision.Reason);
    }

    [Fact]
    public void Both_dotnet_logger_shapes_and_ansi_coloured_output_are_read()
    {
        var parsed = GateFlakyRerunPolicy.ParseFailedTests(
            "[31m  Failed Product.Tests.Alpha [12 ms][0m\n" +
            "    Product.Tests.Beta [FAIL]\n" +
            "  Failed Product.Tests.Alpha [13 ms]\n" +
            "  Passed Product.Tests.Gamma [1 ms]");

        Assert.Equal(["Product.Tests.Alpha", "Product.Tests.Beta"], parsed);
    }

    [Fact]
    public void The_gate_and_the_remote_review_executor_use_the_same_classification()
        => Assert.Equal("FlakyQuarantine", ReviewFlakyQuarantine.Classification);

    private static GateFlakyRerunDecision Decide(string output, string command)
        => GateFlakyRerunPolicy.Decide(
            VerifyCommandKind.Test,
            BuildTestGateFailureKind.Code,
            command,
            output,
            TimeSpan.FromMinutes(10));
}

/// <summary>
/// AGT-2853, acceptance items 1-3 end to end: the real gate runner drives a fake
/// <c>dotnet</c> that fails once and passes on the targeted re-run, and one that
/// fails twice. The machine gate is bypassed so the test stays hermetic.
/// </summary>
public sealed class GateFlakyRerunBehaviorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gate-flaky-rerun-" + Guid.NewGuid().ToString("N"));

    private string FakeDotNet => Path.Combine(_root, "dotnet");
    private string Invocations => Path.Combine(_root, "invocations.txt");

    private const string FlakyTest = "Product.Tests.LaneMutexRegistryConcurrencyTests.ConcurrentMoveAndDelete";
    private const string StubbornTest = "Product.Tests.UpdateServiceRestartIdentityDrillTests.RestartKeepsIdentity";

    public GateFlakyRerunBehaviorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public async Task A_test_that_fails_once_and_passes_on_the_targeted_rerun_yields_a_green_gate_with_a_flaky_record()
    {
        WriteFakeDotNet(FlakyTest, failEveryRun: false);

        var result = await RunGateAsync();

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.True(result.RetryPerformed);
        Assert.Equal([FlakyTest], result.FlakyQuarantinedFailures);
        Assert.Equal(ReviewFlakyQuarantine.Classification, result.FlakyClassification);
        Assert.Contains(FlakyTest, result.Reason);
        Assert.Contains(ReviewFlakyQuarantine.Classification, result.Reason);
    }

    [Fact]
    public async Task The_rerun_targets_exactly_the_failed_test_names_on_the_same_build()
    {
        WriteFakeDotNet(FlakyTest, failEveryRun: false);

        await RunGateAsync();

        var invocations = File.ReadAllLines(Invocations);
        Assert.Equal(2, invocations.Length);
        Assert.DoesNotContain("--no-build", invocations[0]);
        Assert.Contains("--no-build", invocations[1]);
        Assert.Contains($"--filter FullyQualifiedName={FlakyTest}", invocations[1]);
        Assert.DoesNotContain("Category!=MachineBound", invocations[1]);
    }

    [Fact]
    public async Task A_test_that_fails_twice_stays_red()
    {
        WriteFakeDotNet(StubbornTest, failEveryRun: true);

        var result = await RunGateAsync();

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Code, result.FailureKind);
        Assert.True(result.RetryPerformed);
        Assert.Empty(result.FlakyQuarantinedFailures);
        Assert.Null(result.FlakyClassification);
        Assert.Equal(2, File.ReadAllLines(Invocations).Length);
    }

    private Task<BuildTestGateResult> RunGateAsync()
    {
        var runner = new BuildTestGateRunner(
            NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest,
            Path.Combine(_root, "cache"));
        return runner.RunAsync(
            new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false)
            {
                RequiredTestLevel = TestExecutionLevels.Full,
            },
            changedFiles: null,
            new BuildProfile
            {
                TestCmds = [$"{FakeDotNet} test Product.Tests.csproj --filter Category!=MachineBound"],
            },
            PostStepMode.Fail,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
    }

    /// <summary>
    /// A fake <c>dotnet</c> that reports one red test in the VSTest console
    /// shape. Unless <paramref name="failEveryRun"/> is set it passes from its
    /// second invocation on, which is exactly the flake the gate must survive.
    /// </summary>
    private void WriteFakeDotNet(string testName, bool failEveryRun)
    {
        File.WriteAllText(FakeDotNet,
            "#!/bin/sh\n" +
            $"echo \"$@\" >> \"{Invocations}\"\n" +
            $"attempts=\"{Path.Combine(_root, "attempts.txt")}\"\n" +
            "count=$(cat \"$attempts\" 2>/dev/null || echo 0)\n" +
            "count=$((count+1))\n" +
            "echo \"$count\" > \"$attempts\"\n" +
            $"if [ \"$count\" -gt 1 ] && [ \"{(failEveryRun ? "1" : "0")}\" = \"0\" ]; then\n" +
            "  echo \"  Passed " + testName + " [11 ms]\"\n" +
            "  exit 0\n" +
            "fi\n" +
            "echo \"  Failed " + testName + " [12 ms]\"\n" +
            "exit 1\n");
        File.SetUnixFileMode(
            FakeDotNet,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

/// <summary>
/// AGT-2853, acceptance item 2: a quarantined flake is durable on the card, in
/// the gate evidence log and as a timeline receipt, under the same
/// classification the remote review executor writes.
/// </summary>
public sealed class GateFlakyRerunReceiptsTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "gate-flaky-receipts-" + Guid.NewGuid().ToString("N"));
    private readonly TimelineLog _timeline = new(NullLogger<TimelineLog>.Instance);

    public GateFlakyRerunReceiptsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void Quarantined_tests_become_one_timeline_receipt_naming_every_test()
    {
        var recorded = GateFlakyRerunReceipts.Record(
            _timeline, _folder, "pre-develop-build-gate", "abc123",
            ["Product.Tests.Alpha", "Product.Tests.Beta"]);

        Assert.True(recorded);
        var evt = Assert.Single(_timeline.ReadAll(_folder));
        Assert.Equal(TimelineEventKinds.IntegrationGateFlakyRerun, evt.Kind);
        Assert.Equal(TimelineActors.System, evt.Actor);
        Assert.Equal("pre-develop-build-gate", evt.Details![GateFlakyRerunReceipts.GateKey]);
        Assert.Equal("abc123", evt.Details[GateFlakyRerunReceipts.ShaKey]);
        Assert.Equal(
            ReviewFlakyQuarantine.Classification,
            evt.Details[GateFlakyRerunReceipts.ClassificationKey]);
        Assert.Equal("Product.Tests.Alpha, Product.Tests.Beta", evt.Details[GateFlakyRerunReceipts.TestsKey]);
        Assert.Contains("Product.Tests.Beta", evt.Summary);
    }

    [Fact]
    public void A_gate_that_quarantined_nothing_writes_no_receipt()
    {
        Assert.False(GateFlakyRerunReceipts.Record(
            _timeline, _folder, "pre-main-test-gate", "abc123", []));
        Assert.Empty(_timeline.ReadAll(_folder));
    }
}
