using Xunit;

namespace AgentStudio.Tests;

public sealed class GateBudgetOverrunTests
{
    [Theory]
    [InlineData("Passed Product.Tests.Green [12 ms]", BuildTestGateFailureKind.Environment)]
    [InlineData("  Failed AgentStudio.Tests.Foo.Bar [123 ms]", BuildTestGateFailureKind.Code)]
    [InlineData("[xUnit.net 00:06:57.42]     AgentStudio.Tests.Foo.Bar [FAIL]", BuildTestGateFailureKind.Code)]
    [InlineData(" FAIL  frontend/src/app/foo.spec.ts > Foo > renders", BuildTestGateFailureKind.Code)]
    [InlineData(" ❯ frontend/src/app/foo.spec.ts (1 test | 1 failed) 120ms", BuildTestGateFailureKind.Code)]
    [InlineData(" Test Files  1 failed | 9 passed (10)", BuildTestGateFailureKind.Code)]
    [InlineData(" Tests  2 failed | 53 passed (55)", BuildTestGateFailureKind.Code)]
    public void Gate_run_overrun_preserves_red_tests(string output, BuildTestGateFailureKind expected)
    {
        var process = new BuildTestGateProcessEvidence
        {
            TimedOut = true,
            StandardOutput = output,
            ViolatedBudget = new("gate-run", 1800000, 1800164, "verification"),
        };
        Assert.Equal(expected, BuildTestGateRunner.ClassifyFailure(process));
    }

    [Fact]
    public void Trx_failed_outcome_wins_when_console_has_no_red_line()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results><UnitTestResult testName="AgentStudio.Tests.Foo.Bar" outcome="Failed" duration="00:00:12.5000000" /></Results>
                </TestRun>
                """);
            var timings = new GateTestTiming();
            timings.Observe("Test Run Aborted.");
            timings.ReadTrx(path);

            var process = new BuildTestGateProcessEvidence
            {
                TimedOut = true,
                StandardOutput = "Test Run Aborted.",
                FailedTestsObserved = timings.Failed,
                ViolatedBudget = new("gate-run", 1800000, 1800164, "verification"),
            };

            Assert.True(timings.Failed);
            Assert.Equal(BuildTestGateFailureKind.Code, BuildTestGateRunner.ClassifyFailure(process));
            Assert.Equal("AgentStudio.Tests.Foo.Bar", Assert.Single(timings.Slowest).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class GateContentionPolicyTests
{
    internal static GateResourceEvidence Saturated => new(60_000, 8000, 98, 100, 60_000, 60_000, 8000, 12, true);

    [Theory]
    [InlineData(90, 65, true)]
    [InlineData(89, 10, false)]
    [InlineData(100, 90, false)]
    public void Host_saturation_must_come_from_other_processes(double host, double tree, bool expected)
        => Assert.Equal(expected, GateContentionBudget.IsExternalContention(host, tree));

    [Fact]
    public void Extension_is_applied_once_and_retains_the_measurement()
    {
        var budget = new GateContentionBudget(TimeSpan.FromMinutes(60));
        Assert.True(budget.TryExtend(Saturated, false));
        Assert.Equal(TimeSpan.FromMinutes(75), budget.Limit);
        Assert.False(budget.TryExtend(Saturated, false));
        Assert.Equal(3600000, budget.Extension!.OriginalLimitMs);
        Assert.Equal(4500000, budget.Extension.EffectiveLimitMs);
        Assert.Equal(Saturated, budget.Extension.Resources);
    }

    [Fact]
    public void Short_budgets_extend_by_at_most_half()
    {
        var budget = new GateContentionBudget(TimeSpan.FromMinutes(10));
        Assert.True(budget.TryExtend(Saturated, false));
        Assert.Equal(TimeSpan.FromMinutes(15), budget.Limit);
    }

    [Fact]
    public void Red_tests_missing_measurements_short_contention_and_no_progress_refuse_extension()
    {
        Assert.False(GateContentionBudget.ShouldExtend(Saturated, true, false));
        Assert.False(GateContentionBudget.ShouldExtend(Saturated with { MeasurementAvailable = false }, false, false));
        Assert.False(GateContentionBudget.ShouldExtend(Saturated with { RecentExternalContentionMs = 59_999 }, false, false));
        Assert.False(GateContentionBudget.ShouldExtend(Saturated with { RecentProcessTreeCpuMs = 0 }, false, false));
    }

    [Fact]
    public void Slow_report_survives_output_tail_truncation_and_starts_at_eighty_percent()
    {
        var timings = new GateTestTiming();
        for (var i = 1; i <= 15; i++) timings.Observe($"  Passed Product.Test{i} [{i} s]");
        timings.Observe("\u001b[32m ✓ src/slow.spec.ts (3 tests) 25000ms\u001b[0m");
        var result = new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 800, "output tail", "ok", false, false)
        {
            Processes = [new() { OriginalBudgetMs = 1000, SlowTests = timings.Slowest }],
        };
        Assert.Equal(10, timings.Slowest.Count);
        Assert.Equal(25000, timings.Slowest[0].DurationMs);
        Assert.Contains("src/slow.spec.ts", IntegrationGateReceipts.SlowTestReport(result));
        Assert.DoesNotContain("Product.Test1\"", IntegrationGateReceipts.SlowTestReport(result));
        Assert.Empty(IntegrationGateReceipts.SlowTestReport(result with { DurationMs = 799 }));
    }

    [Fact]
    public void Compound_dotnet_durations_survive_cutoff_without_a_final_trx()
    {
        var timings = new GateTestTiming();
        timings.Observe("  Passed Product.LongTest [2 m 3 s]");
        Assert.Equal(123000, Assert.Single(timings.Slowest).DurationMs);
    }

    [Theory]
    [InlineData("Passed Product.TimeoutTests [12 ms]")]
    [InlineData(" ✓ src/failure.spec.ts (1 test) 23ms")]
    [InlineData("Test Files  3 passed (3)")]
    public void Passing_names_and_summaries_are_not_red(string output)
        => Assert.False(GateTestTiming.HasFailedTests(output));

    [Fact]
    public void Recorded_windows_snapshot_exercises_process_tree_and_host_cpu_sampling()
    {
        var ticks = new Queue<SystemLoadThrottle.CpuTicks?>(
        [
            new(1_000, 10_000),
            new(1_000, 10_000),
            new(1_100, 12_000),
        ]);
        var currentPid = Environment.ProcessId;
        var now = 0L;
        var platform = new GateProcessResourcePlatform(
            GateResourceOperatingSystem.Windows,
            () => ticks.Count > 0 ? ticks.Dequeue() : new(1_100, 12_000),
            () =>
            [
                new(currentPid, 4),
                new(int.MaxValue, currentPid),
            ],
            processorCount: 16);

        var parents = platform.ReadParents();
        Assert.Equal(currentPid, parents[int.MaxValue]);

        using var resources = new GateProcessResources(currentPid, platform, () => now);
        now = 1_000;
        var sample = resources.Sample();

        Assert.True(sample.MeasurementAvailable);
        Assert.Equal(16, sample.HostProcessorCount);
        Assert.Equal(95, sample.AverageHostCpuPercent);
        Assert.Equal(95, sample.PeakHostCpuPercent);
    }
}

[Trait("Category", "MachineBound")]
public sealed class GateBudgetProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-budget-" + Guid.NewGuid().ToString("N"));
    public GateBudgetProcessTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cutoff_records_extension_slow_tests_and_red_precedence(bool red)
    {
        var runner = new BuildTestGateRunner(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest,
            Path.Combine(_root, "cache"), _ => new SaturatedResources());
        var result = await runner.RunAsync(
            new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false), null,
            new BuildProfile { BuildCmds = [$"echo '  {(red ? "Failed" : "Passed")} Product.Slow [123 ms]'; i=0; while [ $i -lt 350 ]; do echo log-line; i=$((i+1)); done; sleep 10"] },
            PostStepMode.Fail, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(red ? BuildTestGateFailureKind.Code : BuildTestGateFailureKind.Environment, result.FailureKind);
        var process = Assert.Single(result.Processes);
        Assert.True(process.TimedOut);
        Assert.Equal(red ? 2000 : 3000, process.ViolatedBudget!.LimitMs);
        Assert.Equal(red, process.FailedTestsObserved);
        Assert.Equal(!red, process.BudgetExtension is not null);
        Assert.True(result.DurationMs >= process.ViolatedBudget.LimitMs);
        Assert.DoesNotContain("Product.Slow", result.Output);
        if (red)
        {
            var failure = AcceptedIntegrationFailurePolicy.Classify(PipelineStepStatus.Failed,
                "gate-failed", result.Reason, null, AcceptedIntegrationFailureCodes.BuildGateFailed);
            Assert.Equal(AgentStudio.TaskServer.Contracts.RunFailureClass.Product, failure!.FailureClass);
        }
        Assert.Equal("Product.Slow", Assert.Single(process.SlowTests).Name);
        IntegrationGateReceipts.Record(_root, "pre-develop-build-gate", result);
        var log = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(_root, "post-steps"), "*.log")));
        Assert.Contains("ProcessTreeCpuMs", log);
        Assert.Contains("slowest-tests.json", log);
        Assert.Contains("Product.Slow", log);
    }

    [Fact]
    public async Task A_progressing_command_can_complete_inside_the_extended_budget()
    {
        var runner = new BuildTestGateRunner(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest,
            Path.Combine(_root, "cache"), _ => new SaturatedResources());
        var result = await runner.RunAsync(
            new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false), null,
            new BuildProfile { BuildCmds = ["sleep 2.3"] },
            PostStepMode.Fail, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.NotNull(Assert.Single(result.Processes).BudgetExtension);
        Assert.Null(result.ViolatedBudget);
    }

    [Fact]
    public void Trx_durations_and_failed_outcomes_are_retained()
    {
        var file = Path.Combine(_root, "gate.trx");
        File.WriteAllText(file, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="Product.Red" outcome="Failed" duration="00:01:23.5000000" /></Results>
            </TestRun>
            """);
        var timing = new GateTestTiming();
        timing.ReadTrx(file);
        Assert.True(timing.Failed);
        Assert.Equal(83500, Assert.Single(timing.Slowest).DurationMs);
        File.WriteAllText(file, "<partial>");
        timing.ReadTrx(file);
        Assert.True(timing.Failed);
        Assert.Single(timing.Slowest);
    }

    [Fact]
    public async Task Live_sampler_observes_child_cpu_separately_from_host_cpu()
    {
        var start = new System.Diagnostics.ProcessStartInfo(BashExecutable.Path)
        {
            UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("bash -c 'while :; do :; done' & wait");
        using var process = System.Diagnostics.Process.Start(start)!;
        try
        {
            using var sampler = new GateProcessResources(process.Id);
            await Task.Delay(1000);
            var sample = sampler.Sample();
            Assert.True(sample.WallMs >= 500);
            Assert.True(sample.ProcessTreeCpuMs > 0);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            {
                Assert.True(sample.MeasurementAvailable);
                Assert.InRange(sample.AverageHostCpuPercent!.Value, 0, 100);
            }
            var results = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR");
            if (!string.IsNullOrEmpty(results))
                File.WriteAllText(Path.Combine(results, "live-process-measurement.json"),
                    System.Text.Json.JsonSerializer.Serialize(sample));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private sealed class SaturatedResources : IGateProcessResources
    {
        public GateResourceEvidence Sample() => GateContentionPolicyTests.Saturated;
        public void Dispose() { }
    }
}
