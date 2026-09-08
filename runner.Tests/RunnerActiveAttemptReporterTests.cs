using System.Diagnostics;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2762: a review slot whose verification already produced a durable
/// terminal result (report-pending or report-delivered) must not be reported
/// as an active attempt on re-registration - the authority already settled
/// it, and offering it for re-adoption made the server reject the daemon's
/// next registration as "claim authority lost".
/// </summary>
public sealed class RunnerActiveAttemptReporterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "runner-active-attempt-reporter-tests-" + Guid.NewGuid().ToString("N"));

    public RunnerActiveAttemptReporterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Live_process_with_no_durable_result_is_reported_active()
    {
        var slot = Slot(withDurableResult: false, live: true);
        var active = RunnerActiveAttemptReporter.Review([slot]);
        Assert.Single(active);
        Assert.Equal("attempt-1", active[0].AttemptId);
    }

    [Fact]
    public void Durable_terminal_result_is_not_reported_active_even_though_the_process_is_live()
    {
        var slot = Slot(withDurableResult: true, live: true);
        var active = RunnerActiveAttemptReporter.Review([slot]);
        Assert.Empty(active);
    }

    [Fact]
    public void Dead_process_with_no_durable_result_is_not_reported_active()
    {
        var slot = Slot(withDurableResult: false, live: false);
        var active = RunnerActiveAttemptReporter.Review([slot]);
        Assert.Empty(active);
    }

    private PersistedReviewSlot Slot(bool withDurableResult, bool live)
    {
        var workerDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workerDirectory);
        if (withDurableResult)
            File.WriteAllText(Path.Combine(workerDirectory, "review-result.json"), "{}");

        var current = Process.GetCurrentProcess();
        var processId = live ? Environment.ProcessId : int.MaxValue - 7;
        var processStartedAtUtc = live ? current.StartTime.ToUniversalTime() : DateTime.UtcNow;
        var workspacePath = live ? Directory.GetCurrentDirectory() : Path.Combine(_root, "not-the-cwd");

        return new PersistedReviewSlot(
            Claim(),
            workerDirectory,
            workspacePath,
            processId,
            processStartedAtUtc,
            Phase: "running",
            UpdatedAtUtc: DateTime.UtcNow);
    }

    private static ReviewClaimResponse Claim()
    {
        var now = DateTime.UtcNow;
        var attempt = new ReviewAttemptDto(
            "attempt-1", "subject-1", "AGT-2762", 1, "leased",
            "review-runner", "review-host", 17, now, null, null, null, null);
        var subject = new ReviewSubjectDto(
            "subject-1", "AGT-2762", "run-1", "example/repository", null,
            new string('a', 40), null, null, null, "coding-host", "policy-v1",
            new ReviewPlanDto([], []), now);
        var lease = new ReviewLeaseDto(
            "lease-1", attempt.AttemptId, subject.SubjectId, "review-runner",
            "instance-1", "review-host", 17, now, now.AddMinutes(2), "active",
            "review-attempt-1-f17", 25000, 23);
        return new ReviewClaimResponse("claimed", attempt, subject, lease);
    }
}
