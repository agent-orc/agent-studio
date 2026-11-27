using System.Collections.Concurrent;
using AgentStudio.Diagnostics;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-4239: start-up names its phases and never waits for repair work. A
/// start-up phase logs one line with its elapsed time, the watchdog names a
/// phase running past the threshold, and a deferred repair closes only the
/// admissions it protects until it ends.
/// </summary>
public sealed class StartupPhaseLogTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly LineLogger _log = new();

    [Fact]
    public void A_phase_logs_one_line_with_its_elapsed_time()
    {
        using var phases = new StartupPhaseLog(HostTimelineRecorder.Disabled, _clock);
        phases.Attach(_log, startWatchdog: false);

        phases.Run("CommitRepositoryBackfill", () => _clock.Advance(TimeSpan.FromMilliseconds(1500)));

        Assert.Equal("startup-phase phase=CommitRepositoryBackfill elapsedMs=1500 outcome=completed", Assert.Single(_log.Lines));
    }

    [Fact]
    public void Checkpoints_taken_before_the_logger_exists_are_logged_when_it_attaches()
    {
        using var phases = new StartupPhaseLog(HostTimelineRecorder.Disabled, _clock);
        _clock.Advance(TimeSpan.FromMilliseconds(40));
        phases.Checkpoint("entryToBuild");
        _clock.Advance(TimeSpan.FromMilliseconds(20));
        phases.Checkpoint("build");

        phases.Attach(_log, startWatchdog: false);

        Assert.Equal(
            ["startup-phase phase=entryToBuild elapsedMs=40 outcome=completed",
             "startup-phase phase=build elapsedMs=20 outcome=completed"],
            _log.Lines);
    }

    [Fact]
    public void The_watchdog_names_a_phase_running_past_the_threshold_once_per_interval()
    {
        using var phases = new StartupPhaseLog(HostTimelineRecorder.Disabled, _clock);
        phases.Attach(_log, startWatchdog: false);
        using var fast = phases.Begin("RegistryBootstrap");
        _clock.Advance(TimeSpan.FromSeconds(5));
        fast.Dispose();
        using var slow = phases.Begin("CommitRepositoryBackfill");

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(phases.CheckSlowPhases());
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(["CommitRepositoryBackfill"], phases.CheckSlowPhases());
        Assert.Empty(phases.CheckSlowPhases());
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(["CommitRepositoryBackfill"], phases.CheckSlowPhases());

        Assert.Contains(
            "startup-phase-slow phase=CommitRepositoryBackfill runningSeconds=31 thresholdSeconds=30; start-up is still inside this phase",
            _log.Lines);
        Assert.Equal(["CommitRepositoryBackfill"], phases.RunningPhases);
    }

    [Fact]
    public void The_watchdog_thread_reports_a_blocked_phase_while_the_phase_still_blocks()
    {
        using var phases = new StartupPhaseLog(HostTimelineRecorder.Disabled, _clock,
            pollInterval: TimeSpan.FromMilliseconds(10));
        phases.Attach(_log);
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var blocked = Task.Run(() => phases.Run("HostStart", () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        _clock.Advance(TimeSpan.FromSeconds(31));

        Assert.True(_log.WaitFor(line => line.StartsWith("startup-phase-slow phase=HostStart ", StringComparison.Ordinal),
            TimeSpan.FromSeconds(30)));
        Assert.False(blocked.IsCompleted);
        release.Set();
        blocked.Wait(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_failed_phase_says_so_and_rethrows()
    {
        using var phases = new StartupPhaseLog(HostTimelineRecorder.Disabled, _clock);
        phases.Attach(_log, startWatchdog: false);

        Assert.Throws<InvalidOperationException>(() => phases.Run("CrashRecoveryService",
            () => throw new InvalidOperationException("boom")));

        Assert.Equal("startup-phase phase=CrashRecoveryService elapsedMs=0 outcome=failed", Assert.Single(_log.Lines));
    }
}

public sealed class StartupRepairAdmissionTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_queued_repair_closes_its_admissions_until_it_ends_then_waiters_proceed()
    {
        var admission = new StartupRepairAdmission(NullLogger<StartupRepairAdmission>.Instance);
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        admission.Enqueue("CommitMetadataBackfill", StartupAdmission.Integration | StartupAdmission.Push, () =>
        {
            entered.TrySetResult();
            release.Wait(Bounded);
        });

        Assert.Equal("startup-repair:CommitMetadataBackfill (queued)", admission.WaitingReason(StartupAdmission.Integration));
        var merge = admission.WaitAsync(StartupAdmission.Integration, CancellationToken.None);
        var push = admission.WaitAsync(StartupAdmission.Push, CancellationToken.None);
        var drain = Task.Factory.StartNew(() => admission.Drain(null, null, CancellationToken.None),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await entered.Task.WaitAsync(Bounded);

        Assert.Equal("startup-repair:CommitMetadataBackfill (running)", admission.WaitingReason(StartupAdmission.Push));
        await Task.WhenAny(merge, push, Task.Delay(200));
        Assert.False(merge.IsCompleted);
        Assert.False(push.IsCompleted);

        release.Set();
        await merge.WaitAsync(Bounded);
        await push.WaitAsync(Bounded);
        await drain.WaitAsync(Bounded);
        await admission.Drained.WaitAsync(Bounded);
        Assert.Null(admission.WaitingReason(StartupAdmission.Integration));
        Assert.Equal([("CommitMetadataBackfill", "completed")], admission.Snapshot());
    }

    [Fact]
    public async Task An_admission_no_repair_protects_stays_open()
    {
        var admission = new StartupRepairAdmission(NullLogger<StartupRepairAdmission>.Instance);
        admission.Enqueue("IntegrationOnly", StartupAdmission.Integration, () => { });

        Assert.Null(admission.WaitingReason(StartupAdmission.Push));
        await admission.WaitAsync(StartupAdmission.Push, CancellationToken.None).WaitAsync(Bounded);
        Assert.NotNull(admission.WaitingReason(StartupAdmission.Integration));
    }

    [Fact]
    public async Task A_failed_repair_is_reported_and_still_releases_its_admissions()
    {
        var admission = new StartupRepairAdmission(NullLogger<StartupRepairAdmission>.Instance);
        var later = 0;
        admission.Enqueue("SupersededCommitSweep", StartupAdmission.Integration, () => throw new IOException("disk"));
        admission.Enqueue("RemoteCompletionAttributionSweep", StartupAdmission.Integration, () => later++);

        admission.Drain(null, null, CancellationToken.None);

        await admission.WaitAsync(StartupAdmission.Integration, CancellationToken.None).WaitAsync(Bounded);
        Assert.Equal(1, later);
        Assert.Equal([("SupersededCommitSweep", "failed"), ("RemoteCompletionAttributionSweep", "completed")],
            admission.Snapshot());
    }

    [Fact]
    public async Task Repairs_wait_for_the_first_index_publication_before_they_run()
    {
        var admission = new StartupRepairAdmission(NullLogger<StartupRepairAdmission>.Instance);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ran = 0;
        admission.Enqueue("CommitRepositoryBackfill", StartupAdmission.Integration, () => Interlocked.Increment(ref ran));

        var drain = Task.Factory.StartNew(() => admission.Drain(null, _ => published.Task, CancellationToken.None),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await Task.WhenAny(drain, Task.Delay(200));
        Assert.Equal(0, Volatile.Read(ref ran));

        published.TrySetResult();
        await drain.WaitAsync(Bounded);
        Assert.Equal(1, Volatile.Read(ref ran));
    }
}

/// <summary>Formats log lines the way an operator reads them in the backend log.</summary>
internal sealed class LineLogger : ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly SemaphoreSlim _written = new(0);

    public IReadOnlyList<string> Lines => [.. _lines];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _lines.Enqueue(formatter(state, exception));
        _written.Release();
    }

    public bool WaitFor(Func<string, bool> match, TimeSpan timeout)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!_lines.Any(match))
        {
            var left = timeout - waited.Elapsed;
            if (left <= TimeSpan.Zero || !_written.Wait(left)) return _lines.Any(match);
        }
        return true;
    }
}
