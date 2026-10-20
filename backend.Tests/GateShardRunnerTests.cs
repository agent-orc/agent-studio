using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3232: the real gate loop with a fake <c>dotnet</c> that honours the
/// shard run settings and writes TRX reports. A declared suite runs as shards
/// in parallel lanes with the suite's category filter intact, a red shard
/// re-runs exactly its failed tests once and records both outcomes, and an
/// infrastructure failure of one shard is not a code failure.
/// </summary>
public sealed class GateShardRunnerTests : IDisposable
{
    private const string FlakyTest = "Product.Tests.FlakyTests.Sometimes";
    private static readonly string[] Classes = ["AlphaTests", "BetaTests", "FlakyTests", "GammaTests"];
    private static readonly string[] QualifiedClasses = Classes.Select(name => "Product.Tests." + name).ToArray();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-shard-runner-" + Guid.NewGuid().ToString("N"));

    private string Repository => Path.Combine(_root, "repo");
    private string FakeDotNet => Path.Combine(_root, "bin", "dotnet");
    private string Invocations => Path.Combine(_root, "invocations.txt");
    private string SettingsLog => Path.Combine(_root, "settings.txt");
    private string Mode => Path.Combine(_root, "mode.txt");
    private static string ShellPath(string path) => path.Replace('\\', '/');
    private string SuiteCommand =>
        $"{ShellPath(FakeDotNet)} test Product.Tests/Product.Tests.csproj --no-build --filter Category!=MachineBound";

    public GateShardRunnerTests()
    {
        Directory.CreateDirectory(Repository);
        WriteRepository();
        WriteFakeDotNet();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException exception) { AgentStudio.Diagnostics.SilentCatch.Note(exception, "gate shard test temp dir"); }
    }

    private static GateShardPlan ExpectedPlan()
        => GateShardPlanner.Plan("product-routine", QualifiedClasses, 3, GateShardCountSources.Profile, null);

    [SkippableFact]
    public async Task A_declared_suite_runs_as_shards_with_its_category_filter_and_a_flaky_test_reruns_once()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake dotnet is a POSIX shell script.");
        File.WriteAllText(Mode, "flaky");

        var result = await RunGateAsync(shardLanes: 2);

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.True(result.RetryPerformed);
        Assert.Equal([FlakyTest], result.FlakyQuarantinedFailures);
        var plan = ExpectedPlan();
        var suite = Assert.Single(result.TestShards);
        Assert.Equal(("product-routine", 3, GateShardCountSources.Profile, GateShardStrategies.Hash, plan.Digest),
            (suite.SuiteId, suite.ShardCount, suite.CountSource, suite.Strategy, suite.PlanDigest));
        Assert.Equal(Math.Min(2, plan.Shards.Count(plan.Runs)), suite.Lanes);
        Assert.Equal([0, 1, 2], suite.Shards.Select(shard => shard.Index));
        Assert.Equal(new GateTestCounts(4, 3, 1, 0), suite.Tests);
        Assert.Equal(QualifiedClasses, suite.ClassDurationsMs.Keys.Order(StringComparer.Ordinal));
        Assert.All(suite.ClassDurationsMs.Values, ms => Assert.Equal(50, ms, 3));

        var flaky = Assert.Single(suite.Shards, shard => shard.FailedTests.Contains(FlakyTest));
        Assert.Equal(GateShardOutcomes.FlakyPassed, flaky.Outcome);
        Assert.Equal(1, flaky.Tests.Failed);
        Assert.NotNull(flaky.Rerun);
        Assert.True(flaky.Rerun!.Performed);
        Assert.Equal(GateShardOutcomes.Passed, flaky.Rerun.Outcome);
        Assert.Equal([FlakyTest], flaky.Rerun.Tests);
        Assert.All(suite.Shards.Where(shard => shard != flaky),
            shard => Assert.Contains(shard.Outcome, new[] { GateShardOutcomes.Passed, GateShardOutcomes.Empty }));

        var runnable = plan.Shards.Count(plan.Runs);
        Assert.Equal(runnable, result.Processes.Count(process => process.Phase == BuildTestGateRunner.TestShardPhase));
        Assert.Single(result.Processes, process => process.Phase == BuildTestGateRunner.FlakyRerunPhase);
        // The guard step and one aggregate record for the sharded suite.
        Assert.Equal(2, result.Processes.Count(process => process.Phase == "verification"));

        var settings = File.ReadAllLines(SettingsLog);
        Assert.Equal(runnable, settings.Length);
        Assert.All(settings, line => Assert.Contains("(Category!=MachineBound)&amp;(", line));
        var invocations = File.ReadAllLines(Invocations).Where(line => !line.Contains("Architecture")).ToArray();
        Assert.Equal(runnable, invocations.Count(line => line.Contains("--settings")));
        Assert.All(invocations.Where(line => line.Contains("--settings")), line => Assert.DoesNotContain("--filter", line));
        var rerun = Assert.Single(invocations, line => !line.Contains("--settings"));
        Assert.Contains($"--filter FullyQualifiedName={FlakyTest}", rerun);
        Assert.Contains("# test shards: suite=product-routine shards=3", result.Output);
    }

    [SkippableFact]
    public async Task A_shard_that_stays_red_on_its_one_rerun_fails_the_gate_and_records_both_outcomes()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake dotnet is a POSIX shell script.");
        File.WriteAllText(Mode, "broken");

        var result = await RunGateAsync(shardLanes: 1);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Code, result.FailureKind);
        Assert.Empty(result.FlakyQuarantinedFailures);
        var suite = Assert.Single(result.TestShards);
        var red = Assert.Single(suite.Shards, shard => shard.Outcome == GateShardOutcomes.Failed);
        Assert.Equal([FlakyTest], red.FailedTests);
        Assert.Equal(1, red.Tests.Failed);
        Assert.True(red.Rerun!.Performed);
        Assert.Equal(GateShardOutcomes.Failed, red.Rerun.Outcome);
        Assert.Equal(1, red.Rerun.ExitCode);
        Assert.Single(File.ReadAllLines(Invocations), line => line.Contains($"FullyQualifiedName={FlakyTest}"));
        // One lane runs the shards in index order; nothing starts after a definitive red.
        var plan = ExpectedPlan();
        Assert.All(suite.Shards.Where(shard => shard.Index > red.Index && plan.Runs(plan.Shards[shard.Index])),
            shard => Assert.Equal(GateShardOutcomes.NotRun, shard.Outcome));
        Assert.Contains($"#   failed tests: {FlakyTest}", result.Output);
        Assert.Contains("the shard's original red is the verdict", result.Output);
    }

    [SkippableFact]
    public async Task A_killed_shard_next_to_green_shards_is_an_infrastructure_verdict()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake dotnet is a POSIX shell script.");
        File.WriteAllText(Mode, "oom");

        var result = await RunGateAsync(shardLanes: 2);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.OutOfMemory, result.FailureKind);
        Assert.True(result.IsInfrastructureFailure);
        Assert.False(result.RetryPerformed);
        var suite = Assert.Single(result.TestShards);
        var killed = Assert.Single(suite.Shards, shard => shard.Outcome == GateShardOutcomes.Infrastructure);
        Assert.Equal(137, killed.ExitCode);
        Assert.Equal(BuildTestGateFailureKind.OutOfMemory, killed.FailureKind);
        Assert.Null(killed.Rerun);
    }

    [SkippableFact]
    public async Task A_secondary_host_part_runs_only_its_own_shards_and_skips_other_test_steps()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake dotnet is a POSIX shell script.");
        File.WriteAllText(Mode, "pass");

        var result = await RunGateAsync(shardLanes: 2, part: new GateShardPart(1, 2));

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        var suite = Assert.Single(result.TestShards);
        Assert.Equal([1], suite.Shards.Select(shard => shard.Index));
        Assert.Equal(new GateShardPart(1, 2), suite.Part);
        Assert.Contains("runs on the primary gate host", result.Output);
        Assert.DoesNotContain(File.ReadAllLines(Invocations), line => line.Contains("Architecture"));
    }

    [SkippableFact]
    public async Task One_printed_failure_plus_a_test_host_crash_is_an_infrastructure_failure_without_a_rerun()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake dotnet is a POSIX shell script.");
        File.WriteAllText(Mode, "abort");

        var result = await RunGateAsync(shardLanes: 2);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Environment, result.FailureKind);
        Assert.False(result.RetryPerformed);
        Assert.Empty(result.FlakyQuarantinedFailures);
        var crashed = Assert.Single(Assert.Single(result.TestShards).Shards,
            shard => shard.Outcome == GateShardOutcomes.Infrastructure);
        Assert.Equal(GateShardDetails.TestRunAborted, crashed.Detail);
        Assert.Null(crashed.Rerun);
        Assert.DoesNotContain(File.ReadAllLines(Invocations), line => line.Contains($"FullyQualifiedName={FlakyTest}"));
        Assert.Contains("test run aborted (TRX run outcome Aborted)", result.Output);
    }

    [SkippableFact]
    public async Task A_crashed_shard_whose_exit_code_is_zero_fails_the_gate_as_environment()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake dotnet is a POSIX shell script.");
        File.WriteAllText(Mode, "abort0");

        var result = await RunGateAsync(shardLanes: 2);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Environment, result.FailureKind);
        var crashed = Assert.Single(Assert.Single(result.TestShards).Shards,
            shard => shard.Outcome == GateShardOutcomes.Infrastructure);
        Assert.Equal(0, crashed.ExitCode);
        Assert.Equal(GateShardDetails.TestRunAborted, crashed.Detail);
    }

    [SkippableTheory]
    [InlineData("zero")]
    [InlineData("failed0")]
    [InlineData("early1")]
    public async Task An_unverified_shard_is_infrastructure_without_quarantine_or_rerun(string mode)
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake dotnet is a POSIX shell script.");
        File.WriteAllText(Mode, mode);

        var result = await RunGateAsync(shardLanes: 2);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Environment, result.FailureKind);
        Assert.False(result.RetryPerformed);
        Assert.Empty(result.FlakyQuarantinedFailures);
        var bad = Assert.Single(Assert.Single(result.TestShards).Shards,
            shard => shard.Outcome == GateShardOutcomes.Infrastructure);
        Assert.Equal(mode == "early1" ? GateShardDetails.NoTestReport : GateShardDetails.UnverifiedTestReport,
            bad.Detail);
        Assert.Null(bad.Rerun);
    }

    [SkippableFact]
    public async Task A_rerun_whose_filter_matches_no_test_is_not_green()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake dotnet is a POSIX shell script.");
        File.WriteAllText(Mode, "nomatch");

        var result = await RunGateAsync(shardLanes: 2);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Code, result.FailureKind);
        Assert.Empty(result.FlakyQuarantinedFailures);
        var red = Assert.Single(Assert.Single(result.TestShards).Shards, shard => shard.Outcome == GateShardOutcomes.Failed);
        Assert.True(red.Rerun!.Performed);
        Assert.Equal(0, red.Rerun.ExitCode);
        Assert.Equal(GateShardOutcomes.Failed, red.Rerun.Outcome);
        Assert.Equal(GateShardDetails.RerunUnconfirmed, red.Rerun.Reason);
        Assert.Contains("report total=0 passed=0 failed=0", result.Output);
    }

    [SkippableFact]
    public async Task A_shard_that_ran_classes_outside_its_partition_reports_the_filter_was_not_applied()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake dotnet is a POSIX shell script.");
        File.WriteAllText(Mode, "leak");

        var result = await RunGateAsync(shardLanes: 2);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Environment, result.FailureKind);
        var suite = Assert.Single(result.TestShards);
        Assert.Contains(suite.Shards, shard => shard.Detail == GateShardDetails.ShardFilterNotApplied);
        Assert.Contains(GateShardDetails.ShardFilterNotApplied, result.Output);
    }

    [SkippableFact]
    public async Task Console_failures_the_report_does_not_confirm_get_no_quarantine_and_no_rerun()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake dotnet is a POSIX shell script.");
        File.WriteAllText(Mode, "mismatch");

        var result = await RunGateAsync(shardLanes: 2);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Code, result.FailureKind);
        Assert.False(result.RetryPerformed);
        var red = Assert.Single(Assert.Single(result.TestShards).Shards, shard => shard.Outcome == GateShardOutcomes.Failed);
        Assert.False(red.Rerun!.Performed);
        Assert.Equal(GateShardDetails.FailuresUnconfirmed, red.Rerun.Reason);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(1, null, null)]
    [InlineData(3, 3, GateShardCountSources.Profile)]
    [InlineData(ProjectTestSuite.AutoShards, 2, GateShardCountSources.ExpectedDuration)]
    public void Only_a_suite_that_opts_in_with_a_shard_count_is_planned_in_shards(
        int? shards, int? expectedCount, string? expectedSource)
    {
        // A one-process suite keeps xUnit's in-process class parallelism; a
        // class-sharded backend slice ran 2x slower on agent-runner-01.
        var suites = new[]
        {
            new ProjectTestSuite("product-routine", "routine", 720, SuiteCommand) { Shards = shards },
        };
        var command = new VerifyCommand(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", SuiteCommand);

        var planned = BuildTestGateRunner.PlanShardedSuite(suites, Repository, command, timings: null);

        Assert.Equal(expectedCount, planned?.Plan.Count);
        Assert.Equal(expectedSource, planned?.Plan.CountSource);
    }

    private Task<BuildTestGateResult> RunGateAsync(int shardLanes, GateShardPart? part = null)
    {
        var runner = new BuildTestGateRunner(
            NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest,
            Path.Combine(_root, "cache"),
            shardLanes: shardLanes);
        return runner.RunAsync(
            new BuildTestGateRequest(Repository, null, "test", RequireExactSubject: false)
            {
                RequiredTestLevel = TestExecutionLevels.Full,
                Sharding = part is null ? null : new GateShardRequest { Part = part },
            },
            changedFiles: null,
            profile: null,
            PostStepMode.Fail,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
    }

    private void WriteRepository()
    {
        Write(".agent-studio/project.yml", $"""
            schemaVersion: 1
            stack: [dotnet]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
              test:
                - {SuiteCommand}
              lint:
            testSuites:
              - id: product-routine
                category: routine
                expectedDurationSeconds: 60
                shards: 3
                command: {SuiteCommand}
            cachePaths: [bin]
            capabilities: [linux]
            environment:
              CI: "true"
            """);
        Write(".agent-studio/prepare", "#!/bin/sh\nexit 0\n");
        Write("Product.Tests/Product.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write("Product.Tests/ProductTests.cs", "namespace Product.Tests;\n\n" + string.Concat(Classes.Select(name =>
            $"public sealed class {name}\n{{\n    [Fact] public void Works() {{ }}\n}}\n\n")));
    }

    /// <summary>
    /// A fake <c>dotnet</c>. With <c>--settings</c> it runs the classes the
    /// shard's TestCaseFilter selects (positive class list, or the complement
    /// of the excluded ones); with a targeted <c>--filter</c> it runs the named
    /// tests. <c>mode.txt</c>: <c>flaky</c> fails FlakyTests in its shard and
    /// passes the re-run, <c>broken</c> fails both, <c>oom</c> kills the shard
    /// holding GammaTests with exit 137, <c>pass</c> keeps everything green.
    /// <c>abort</c> prints FlakyTests' failure and then a test host crash,
    /// <c>abort0</c> prints the crash and still exits 0, <c>nomatch</c> fails
    /// FlakyTests and its re-run selects no test, <c>leak</c> ignores the shard
    /// filter, and <c>mismatch</c> prints a failure the TRX report records as passed.
    /// </summary>
    private void WriteFakeDotNet()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FakeDotNet)!);
        File.WriteAllText(FakeDotNet, $$"""
            #!/bin/sh
            echo "$*" >> "{{ShellPath(Invocations)}}"
            mode=$(cat "{{ShellPath(Mode)}}")
            prefix=""; dir=""; settings=""; prev=""
            for arg in "$@"; do
              case "$prev" in
                --logger) case "$arg" in trx\;LogFilePrefix=*) prefix="${arg#trx;LogFilePrefix=}";; esac;;
                --results-directory) dir="$arg";;
                --settings) settings="$arg";;
              esac
              prev="$arg"
            done
            case "$*" in *Architecture*) exit 0;; esac
            run=""
            for c in {{string.Join(' ', Classes)}}; do
              if [ -n "$settings" ]; then
                if [ "$mode" = leak ]; then
                  run="$run $c"
                elif grep -q 'FullyQualifiedName~' "$settings"; then
                  grep -q "FullyQualifiedName~Product\.Tests\.$c\." "$settings" && run="$run $c"
                else
                  grep -q "FullyQualifiedName!~Product\.Tests\.$c\." "$settings" || run="$run $c"
                fi
              else
                case "$*" in *"Product.Tests.$c."*) run="$run $c";; esac
              fi
            done
            # One append per lane: parallel lanes must not interleave a line and its newline.
            if [ -n "$settings" ]; then printf '%s\n' "$(tr -d '\n' < "$settings")" >> "{{ShellPath(SettingsLog)}}"; fi
            if [ "$mode" = oom ]; then case "$run" in *GammaTests*) exit 137;; esac; fi
            if [ "$mode" = early1 ] && [ -n "$settings" ]; then case "$run" in *FlakyTests*) exit 1;; esac; fi
            if [ "$mode" = zero ] && [ -n "$settings" ]; then case "$run" in *FlakyTests*) run="";; esac; fi
            # nomatch: the targeted re-run's filter matches nothing, VSTest still exits 0.
            if [ "$mode" = nomatch ] && [ -z "$settings" ]; then run=""; fi
            failed=""; summary=Completed; aborted=""
            results=""; definitions=""
            for c in $run; do
              name=Works; outcome=Passed
              if [ "$c" = FlakyTests ]; then
                name=Sometimes
                case "$mode" in
                  broken) outcome=Failed; failed=1;;
                  flaky|nomatch|abort) if [ -n "$settings" ]; then outcome=Failed; failed=1; fi;;
                  mismatch) if [ -n "$settings" ]; then failed=1; fi;;
                  abort0) aborted=1;;
                  failed0) if [ -n "$settings" ]; then outcome=Failed; fi;;
                esac
                if [ "$mode" = abort ] && [ -n "$settings" ]; then aborted=1; summary=Aborted; fi
              fi
              if [ "$outcome" = Failed ] || { [ "$mode" = mismatch ] && [ -n "$failed" ] && [ "$c" = FlakyTests ]; }; then
                echo "  Failed Product.Tests.$c.$name [12 ms]"
              else
                echo "  Passed Product.Tests.$c.$name [5 ms]"
              fi
              results="$results<UnitTestResult testId=\"id-$c\" testName=\"Product.Tests.$c.$name\" duration=\"00:00:00.0500000\" outcome=\"$outcome\" />"
              definitions="$definitions<UnitTest id=\"id-$c\" name=\"$name\"><TestMethod className=\"Product.Tests.$c\" name=\"$name\" /></UnitTest>"
            done
            if [ -n "$aborted" ]; then echo "The active test run was aborted. Reason: Test host process crashed"; echo "Test Run Aborted."; fi
            if [ -n "$prefix" ] && [ -n "$dir" ]; then
              echo "<?xml version=\"1.0\" encoding=\"utf-8\"?><TestRun><Results>$results</Results><TestDefinitions>$definitions</TestDefinitions><ResultSummary outcome=\"$summary\" /></TestRun>" > "$dir/${prefix}_1.trx"
            fi
            if [ -n "$failed" ]; then exit 1; fi
            exit 0
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FakeDotNet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(Repository, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
