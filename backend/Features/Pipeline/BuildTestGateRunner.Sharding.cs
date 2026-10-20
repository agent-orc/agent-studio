using System.Diagnostics;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Shard inputs of one gate run on this host, and how it obtains parallel lanes.</summary>
internal sealed record GateShardContext(
    GateShardPart? Part,
    GateShardTimingSnapshot? Timings,
    Func<int, CancellationToken, Task<GateShardLanes>> AcquireLanes)
{
    /// <summary>A secondary host part runs builds and its own shards; part 0 runs everything else.</summary>
    public bool IsSecondary => Part is { IsPrimary: false };
}

/// <summary>Parallel lanes for one sharded suite step: the gate's own slot plus borrowed free slots.</summary>
internal sealed class GateShardLanes(int count, IReadOnlyList<IDisposable> borrowed) : IDisposable
{
    public int Count { get; } = count;

    public void Dispose()
    {
        foreach (var lease in borrowed) lease.Dispose();
    }
}

/// <summary>A suite step the planner split into more than one shard.</summary>
internal sealed record PlannedShardSuite(
    ProjectTestSuite Suite,
    GateShardableCommand Command,
    GateShardPlan Plan);

/// <summary>Online cores and the processor budget of one test process on this host.</summary>
internal static class GateHostCapacity
{
    internal static int OnlineCores(Action<IOException>? unreadable = null)
    {
        try
        {
            return ParseOnlineCpus(File.ReadAllText("/sys/devices/system/cpu/online"));
        }
        catch (IOException exception)
        {
            unreadable?.Invoke(exception);
            return Environment.ProcessorCount;
        }
    }

    internal static int ParseOnlineCpus(string online)
        => online.Trim().Split(',').Sum(range =>
        {
            var limits = range.Split('-');
            return limits.Length == 2 && int.TryParse(limits[0], out var first)
                && int.TryParse(limits[1], out var last) && last >= first
                ? last - first + 1 : 1;
        });

    /// <summary>The remote transport pins each gate suite to <c>DOTNET_PROCESSOR_COUNT</c>; 0 when unset.</summary>
    internal static int ProcessorsPerTestProcess()
        => int.TryParse(Environment.GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT"), out var processors)
           && processors > 0 ? processors : 0;
}

public sealed partial class BuildTestGateRunner
{
    /// <summary>Phase stamped on one shard process of a sharded suite (AGT-3232).</summary>
    internal const string TestShardPhase = "test-shard";

    /// <summary>Lines of a red shard's own output appended to the gate output.</summary>
    private const int RedShardTailLines = 60;

    /// <summary>Lanes a hermetic test runner grants; production borrows free machine slots.</summary>
    private readonly int _hermeticShardLanes;

    private sealed record ShardedStep(
        BuildTestGateFailureKind Kind,
        BuildTestGateProcessEvidence Verification,
        IReadOnlyList<BuildTestGateProcessEvidence> Processes,
        GateShardSuiteEvidence Evidence,
        IReadOnlyList<string> FlakyTests,
        IReadOnlyList<TestQuarantineHit> QuarantineHits,
        bool RetryPerformed);

    private sealed record ShardRun(
        GateShardEvidence Evidence,
        IReadOnlyList<BuildTestGateProcessEvidence> Processes,
        RingOutput Output,
        IReadOnlyList<string> FlakyTests,
        IReadOnlyList<TestQuarantineHit> QuarantineHits,
        IReadOnlyDictionary<string, double> ClassDurationsMs);

    private GateShardContext ShardContext(BuildTestGateRequest request)
    {
        var slots = request.GateSlots == 1 ? _localGateSlots : request.GateSlots;
        return new GateShardContext(
            request.Sharding?.Part,
            request.Sharding?.Timings,
            (runnable, ct) => AcquireShardLanesAsync(slots, runnable, ct));
    }

    private async Task<GateShardLanes> AcquireShardLanesAsync(int slots, int runnable, CancellationToken ct)
    {
        if (_machineGateMode == BuildTestMachineGateMode.BypassForHermeticTest)
            return new GateShardLanes(Math.Clamp(_hermeticShardLanes, 1, Math.Max(1, runnable)), []);
        var cores = GateHostCapacity.OnlineCores();
        var processors = GateHostCapacity.ProcessorsPerTestProcess();
        var wanted = GateShardPlanner.Lanes(runnable, slots, cores, processors) - 1;
        var borrowed = wanted > 0
            ? await _machineSlots.TryAcquireFreeAsync(slots, wanted, ct).ConfigureAwait(false)
            : [];
        return new GateShardLanes(1 + borrowed.Count, borrowed);
    }

    private static IReadOnlyList<ProjectTestSuite> ShardSuites(string repositoryPath)
    {
        var read = ProjectDefinitionReader.ReadWorkspace(repositoryPath);
        return read.IsValid && read.Definition is { } definition ? definition.TestSuites : [];
    }

    /// <summary>
    /// The shard plan of <paramref name="command"/>, or null when the step is
    /// not one of the project's declared suites, not a single <c>dotnet test</c>
    /// of one project, or plans to a single shard. Every host of a run computes
    /// the same answer from the same subject and timing snapshot.
    /// </summary>
    internal static PlannedShardSuite? PlanShardedSuite(
        IReadOnlyList<ProjectTestSuite> suites,
        string workingDirectory,
        VerifyCommand command,
        GateShardTimingSnapshot? timings)
    {
        if (command.Kind != VerifyCommandKind.Test || GuardFirstGatePlan.IsGuardStep(command)) return null;
        var suite = suites.FirstOrDefault(item =>
            string.Equals(item.Command.Trim(), command.Command.Trim(), StringComparison.Ordinal));
        // Opt-in per suite: without a shards field the suite runs in one process as before.
        if (suite is null || suite.Shards is null or 1) return null;
        var parsed = GateShardCommand.TryParse(command.Command);
        if (parsed is null) return null;
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(
            workingDirectory, parsed.TestProject.Replace('/', Path.DirectorySeparatorChar))));
        if (projectDirectory is null || !Directory.Exists(projectDirectory)) return null;
        var classes = GateShardTestCatalog.Classes(projectDirectory);
        var suiteTimings = timings?.For(suite.Id);
        var (count, source) = GateShardPlanner.ResolveCount(
            suite.Shards == ProjectTestSuite.AutoShards ? null : suite.Shards,
            suiteTimings, suite.ExpectedDurationSeconds, classes.Count);
        if (count < 2) return null;
        return new PlannedShardSuite(suite, parsed,
            GateShardPlanner.Plan(suite.Id, classes, count, source, suiteTimings));
    }

    /// <summary>
    /// Runs this host's shards of one suite step in parallel lanes. Each red
    /// shard gets its own quarantine check and at most one targeted re-run of
    /// exactly its failed tests; both outcomes stay in the shard evidence.
    /// After a shard is definitively red, shards not yet started are skipped.
    /// </summary>
    private async Task<ShardedStep> RunShardedSuiteAsync(
        PlannedShardSuite planned,
        VerifyCommand command,
        string workingDirectory,
        GateShardContext context,
        GateContentionBudget budget,
        Stopwatch gateClock,
        RingOutput output,
        ProjectPreparationResult? projectPreparation,
        TestQuarantineReadResult quarantine,
        DateOnly quarantineDay,
        CancellationToken ct)
    {
        var plan = planned.Plan;
        var host = Environment.MachineName;
        var owned = plan.Shards
            .Where(shard => context.Part?.Owns(shard.Index) ?? true)
            .ToArray();
        var runnable = owned.Where(plan.Runs)
            .OrderByDescending(shard => shard.EstimatedMs)
            .ThenBy(shard => shard.Index)
            .ToArray();
        var startedAt = DateTimeOffset.UtcNow;
        var stepClock = Stopwatch.StartNew();
        using var lanes = await context.AcquireLanes(Math.Max(1, runnable.Length), ct).ConfigureAwait(false);
        output.AppendLine(
            $"# test shards: suite={planned.Suite.Id} shards={plan.Count} count-source={plan.CountSource} " +
            $"strategy={plan.Strategy} plan={plan.Digest} lanes={lanes.Count} " +
            $"host={host} part={context.Part?.ToString() ?? "whole"} owned={owned.Length}");

        var settingsPrefix = Path.Combine(Path.GetTempPath(), "agentstudio-gate-shard-" + Guid.NewGuid().ToString("N"));
        var runs = new ShardRun?[plan.Count];
        var red = 0;
        var next = -1;
        try
        {
            async Task LaneAsync(int lane)
            {
                while (true)
                {
                    var position = Interlocked.Increment(ref next);
                    if (position >= runnable.Length) return;
                    var shard = runnable[position];
                    if (Volatile.Read(ref red) != 0)
                    {
                        runs[shard.Index] = NotRun(shard, host, lane);
                        continue;
                    }
                    var run = await RunShardAsync(planned, shard, lane, host, settingsPrefix, command,
                        workingDirectory, budget, gateClock, projectPreparation, quarantine, quarantineDay, ct)
                        .ConfigureAwait(false);
                    runs[shard.Index] = run;
                    if (run.Evidence.Outcome == GateShardOutcomes.Failed) Interlocked.Exchange(ref red, 1);
                }
            }

            await Task.WhenAll(Enumerable.Range(0, lanes.Count).Select(LaneAsync)).ConfigureAwait(false);
        }
        finally
        {
            foreach (var shard in runnable)
            {
                try { File.Delete(settingsPrefix + "-" + shard.Index + ".runsettings"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { SilentCatch.Note(ex, "BuildTestGateRunner: shard run-settings cleanup"); }
            }
        }

        foreach (var shard in owned.Where(shard => !plan.Runs(shard)))
            runs[shard.Index] = new ShardRun(
                new GateShardEvidence(shard.Index, host, 0, 0, 0, 0, null, GateShardOutcomes.Empty,
                    GateTestCounts.None, []),
                [], new RingOutput(1), [], [], new Dictionary<string, double>(StringComparer.Ordinal));
        var completed = runs.Where(run => run is not null).Select(run => run!).OrderBy(run => run.Evidence.Index).ToArray();
        var shards = completed.Select(run => run.Evidence).ToArray();
        var kind = GateShardVerdictPolicy.MergeShards(shards);
        stepClock.Stop();

        foreach (var run in completed)
        {
            var shard = run.Evidence;
            output.AppendLine(
                $"# shard {shard.Index + 1}/{plan.Count} host={shard.Host} lane={shard.Lane} classes={shard.Classes} " +
                $"tests={shard.Tests.Total} passed={shard.Tests.Passed} failed={shard.Tests.Failed} " +
                $"skipped={shard.Tests.Skipped} durationMs={shard.DurationMs} exit={shard.ExitCode?.ToString() ?? "n/a"} " +
                $"outcome={shard.Outcome}" +
                (shard.Rerun is { } rerun
                    ? $" rerun={(rerun.Performed ? rerun.Outcome : "not-performed")} rerunReason={rerun.Reason}"
                    : ""));
            if (shard.FailedTests.Count > 0)
                output.AppendLine($"#   failed tests: {string.Join(", ", shard.FailedTests)}");
            if (shard.Outcome is GateShardOutcomes.Failed or GateShardOutcomes.Infrastructure)
                foreach (var line in run.Output.Text.Split('\n').TakeLast(RedShardTailLines))
                    output.AppendLine(line.TrimEnd('\r'));
        }

        var shardProcesses = completed.SelectMany(run => run.Processes).ToArray();
        var classDurations = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, ms) in completed.SelectMany(run => run.ClassDurationsMs))
            classDurations[name] = classDurations.GetValueOrDefault(name) + ms;
        var evidence = new GateShardSuiteEvidence(
            planned.Suite.Id, command.Command, plan.Count, plan.CountSource, plan.Strategy, plan.Digest,
            lanes.Count, stepClock.ElapsedMilliseconds, shards)
        {
            Part = context.Part,
            Hosts = [host],
            ClassDurationsMs = classDurations,
            SerialWallMs = shards.Sum(shard => shard.DurationMs),
        };
        var verification = SyntheticVerification(command, workingDirectory, startedAt, kind, completed);
        return new ShardedStep(
            kind, verification, shardProcesses, evidence,
            completed.SelectMany(run => run.FlakyTests).Distinct(StringComparer.Ordinal).ToArray(),
            completed.SelectMany(run => run.QuarantineHits).ToArray(),
            completed.Any(run => run.Evidence.Rerun?.Performed == true));
    }

    private async Task<ShardRun> RunShardAsync(
        PlannedShardSuite planned,
        GateShardAssignment shard,
        int lane,
        string host,
        string settingsPrefix,
        VerifyCommand command,
        string workingDirectory,
        GateContentionBudget budget,
        Stopwatch gateClock,
        ProjectPreparationResult? projectPreparation,
        TestQuarantineReadResult quarantine,
        DateOnly quarantineDay,
        CancellationToken ct)
    {
        var plan = planned.Plan;
        var settings = settingsPrefix + "-" + shard.Index + ".runsettings";
        await File.WriteAllTextAsync(settings,
            GateShardCommand.RunSettings(GateShardCommand.Filter(planned.Command, plan, shard.Index)), ct)
            .ConfigureAwait(false);
        var shardOutput = new RingOutput(MaxOutputLines);
        shardOutput.AppendLine($"# shard {shard.Index + 1}/{plan.Count} of {planned.Suite.Id}: {shard.Classes.Count} class(es)" +
                               (shard.Index == plan.ComplementIndex ? " plus every class outside the other shards" : ""));
        var process = await RunShellAsync(
            workingDirectory, GateShardCommand.ShardInvocation(planned.Command, settings), command.Shell,
            budget, gateClock.Elapsed, shardOutput, ct, TestShardPhase, projectPreparation).ConfigureAwait(false);
        var processes = new List<BuildTestGateProcessEvidence> { process };
        var report = process.TestReport;
        var processOutput = $"{process.StandardOutput}\n{process.StandardError}";
        var parsedFailures = GateFlakyRerunPolicy.ParseFailedTests(processOutput);
        var failedTests = report is { FailedTests.Count: > 0 } ? report.FailedTests : parsedFailures;
        var classDurations = report?.ClassDurationsMs ?? new Dictionary<string, double>(StringComparer.Ordinal);
        GateShardEvidence Evidence(string outcome, BuildTestGateFailureKind failureKind,
            GateShardRerunEvidence? rerun = null, string? detail = null)
            => new(shard.Index, host, lane, shard.Classes.Count, shard.EstimatedMs,
                (long)(process.CompletedAtUtc - process.StartedAtUtc).TotalMilliseconds, process.ExitCode, outcome,
                report?.Counts ?? GateTestCounts.None, failedTests)
            {
                FailureKind = failureKind,
                Rerun = rerun,
                Detail = detail,
            };
        ShardRun Infrastructure(BuildTestGateFailureKind failureKind, string detail, string message)
        {
            shardOutput.AppendLine($"# shard {shard.Index + 1}/{plan.Count}: {message}; infrastructure failure, no quarantine and no re-run");
            return new ShardRun(Evidence(GateShardOutcomes.Infrastructure, failureKind, detail: detail),
                processes, shardOutput, [], [], classDurations);
        }

        var green = process.ExitCode == 0 && CompletedNormally(process);
        var kind = green ? BuildTestGateFailureKind.None : ClassifyFailure(process);
        if (kind is not (BuildTestGateFailureKind.None or BuildTestGateFailureKind.Code))
            return new ShardRun(Evidence(GateShardOutcomes.Infrastructure, kind), processes, shardOutput, [], [], classDurations);
        // A crashed test host or aborted run never passes, whatever its exit code says.
        if (GateShardRunChecks.Abort(report, processOutput) is { } abort)
            return Infrastructure(BuildTestGateFailureKind.Environment, GateShardDetails.TestRunAborted, $"test run aborted ({abort})");
        if (report is null && green)
            return Infrastructure(BuildTestGateFailureKind.Environment, GateShardDetails.NoTestReport, "no TRX report proves which tests ran");
        if (report is not null && GateShardRunChecks.ForeignClasses(plan, shard.Index, report) is { Count: > 0 } foreign)
            return Infrastructure(BuildTestGateFailureKind.Environment, GateShardDetails.ShardFilterNotApplied,
                $"{GateShardDetails.ShardFilterNotApplied}, it ran {foreign.Count} class(es) outside its partition " +
                $"({string.Join(", ", foreign.Take(5))})");
        if (green)
            return new ShardRun(Evidence(GateShardOutcomes.Passed, BuildTestGateFailureKind.None),
                processes, shardOutput, [], [], classDurations);

        if (!GateShardRunChecks.FailuresConfirmed(parsedFailures, report))
        {
            shardOutput.AppendLine(
                $"# shard {shard.Index + 1}/{plan.Count}: the console failures ({string.Join(", ", parsedFailures)}) " +
                $"do not match the TRX report ({string.Join(", ", report?.FailedTests ?? [])}); no quarantine and no re-run");
            return new ShardRun(
                Evidence(GateShardOutcomes.Failed, kind,
                    new GateShardRerunEvidence(GateShardDetails.FailuresUnconfirmed, parsedFailures, false, null, null, 0)),
                processes, shardOutput, [], [], classDurations);
        }

        var partition = TestQuarantinePolicy.Partition(parsedFailures, quarantine.Entries, quarantineDay);
        foreach (var hit in partition.Ignored)
            shardOutput.AppendLine(
                $"# test quarantine: {hit.Test} failed; quarantined by {hit.Card} until {hit.ExpiresOn:yyyy-MM-dd}, not blocking");
        if (partition.AllQuarantined)
            return new ShardRun(Evidence(GateShardOutcomes.QuarantinePassed, BuildTestGateFailureKind.None),
                processes, shardOutput, [], partition.Ignored, classDurations);

        // AGT-2853 per shard: one targeted re-run of exactly this shard's
        // failed tests, on the same build and the same gate-run budget. The
        // suite's own command is the base, so the re-run carries no shard filter.
        var decision = GateFlakyRerunPolicy.Decide(
            command.Kind, kind, command.Command, processOutput,
            Remaining(budget.Limit, gateClock.Elapsed, allowExhausted: true));
        shardOutput.AppendLine(
            $"# flaky re-run decision: {decision.Reason} " +
            $"failed={(decision.FailedTests.Count == 0 ? "none" : string.Join(", ", decision.FailedTests))}");
        if (!decision.ShouldRerun)
            return new ShardRun(
                Evidence(GateShardOutcomes.Failed, kind,
                    new GateShardRerunEvidence(decision.Reason, decision.FailedTests, false, null, null, 0)),
                processes, shardOutput, [], partition.Ignored, classDurations);

        var rerun = await RunShellAsync(
            workingDirectory, decision.Command!, command.Shell, budget, gateClock.Elapsed, shardOutput, ct,
            FlakyRerunPhase, projectPreparation).ConfigureAwait(false);
        processes.Add(rerun);
        var rerunPassed = GateShardRunChecks.RerunConfirmed(
            CompletedNormally(rerun) && rerun.ExitCode == 0, rerun.TestReport,
            $"{rerun.StandardOutput}\n{rerun.StandardError}", decision.FailedTests.Count);
        var rerunEvidence = new GateShardRerunEvidence(
            rerunPassed ? decision.Reason : GateShardDetails.RerunUnconfirmed, decision.FailedTests, true,
            rerunPassed ? GateShardOutcomes.Passed : GateShardOutcomes.Failed,
            rerun.ExitCode, (long)(rerun.CompletedAtUtc - rerun.StartedAtUtc).TotalMilliseconds);
        if (rerunPassed)
        {
            shardOutput.AppendLine(
                $"# {ReviewFlakyQuarantine.Classification}: the targeted re-run of " +
                $"{string.Join(", ", decision.FailedTests)} passed; recorded as flaky, shard not blocked");
            return new ShardRun(Evidence(GateShardOutcomes.FlakyPassed, BuildTestGateFailureKind.None, rerunEvidence),
                processes, shardOutput, decision.FailedTests, partition.Ignored, classDurations);
        }
        var counts = rerun.TestReport?.Counts;
        shardOutput.AppendLine(
            $"# the targeted re-run did not confirm {decision.FailedTests.Count} passed test(s) " +
            $"(exit {rerun.ExitCode?.ToString() ?? "n/a"}, report " +
            $"{(counts is null ? "missing" : $"total={counts.Total} passed={counts.Passed} failed={counts.Failed}")}); " +
            "the shard's original red is the verdict");
        return new ShardRun(Evidence(GateShardOutcomes.Failed, kind, rerunEvidence),
            processes, shardOutput, [], partition.Ignored, classDurations);
    }

    private static ShardRun NotRun(GateShardAssignment shard, string host, int lane)
        => new(new GateShardEvidence(shard.Index, host, lane, shard.Classes.Count, shard.EstimatedMs, 0, null,
                GateShardOutcomes.NotRun, GateTestCounts.None, []),
            [], new RingOutput(1), [], [], new Dictionary<string, double>(StringComparer.Ordinal));

    /// <summary>
    /// The one <c>verification</c> record of a sharded step. Positional readers
    /// (the coverage audit, the from-scratch retry, failure diagnosis) see one
    /// process per planned command exactly as before; the shard processes keep
    /// their own <see cref="TestShardPhase"/> records.
    /// </summary>
    private static BuildTestGateProcessEvidence SyntheticVerification(
        VerifyCommand command,
        string workingDirectory,
        DateTimeOffset startedAt,
        BuildTestGateFailureKind kind,
        IReadOnlyList<ShardRun> runs)
    {
        var lead = runs.FirstOrDefault(run => run.Evidence.Outcome == GateShardOutcomes.Failed)
            ?? runs.FirstOrDefault(run => run.Evidence.Outcome == GateShardOutcomes.Infrastructure);
        var leadProcess = lead?.Processes.FirstOrDefault();
        var red = runs.Where(run => run.Evidence.Outcome is GateShardOutcomes.Failed or GateShardOutcomes.Infrastructure)
            .SelectMany(run => run.Processes.Take(1))
            .ToArray();
        var shardProcesses = runs.SelectMany(run => run.Processes.Take(1)).ToArray();
        return new BuildTestGateProcessEvidence
        {
            Phase = "verification",
            Command = command.Command,
            FileName = "test-shards",
            Arguments = [],
            WorkingDirectory = workingDirectory,
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ExitCode = kind == BuildTestGateFailureKind.None ? 0 : leadProcess?.ExitCode,
            TimedOut = kind == BuildTestGateFailureKind.Timeout && leadProcess?.TimedOut == true,
            Cancelled = kind == BuildTestGateFailureKind.Cancellation && leadProcess?.Cancelled == true,
            LaunchError = kind == BuildTestGateFailureKind.ProcessLaunch ? leadProcess?.LaunchError : null,
            TerminationSignal = kind == BuildTestGateFailureKind.None ? null : leadProcess?.TerminationSignal,
            ViolatedBudget = kind == BuildTestGateFailureKind.None ? null : leadProcess?.ViolatedBudget,
            StandardOutput = string.Join('\n', red.Select(process => process.StandardOutput)),
            StandardError = string.Join('\n', red.Select(process => process.StandardError)),
            FailedTestsObserved = shardProcesses.Any(process => process.FailedTestsObserved),
            SlowTests = shardProcesses.SelectMany(process => process.SlowTests)
                .OrderByDescending(test => test.DurationMs).Take(10).ToArray(),
            OriginalBudgetMs = shardProcesses.Select(process => process.OriginalBudgetMs).FirstOrDefault(),
            BudgetExtension = shardProcesses.Select(process => process.BudgetExtension).FirstOrDefault(extension => extension is not null),
            TestReport = new GateTestReport(
                runs.Aggregate(GateTestCounts.None, (sum, run) => sum.Add(run.Evidence.Tests)),
                runs.SelectMany(run => run.Evidence.FailedTests).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
        };
    }
}
