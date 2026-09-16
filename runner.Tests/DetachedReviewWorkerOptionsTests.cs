using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// The detached review worker rebuilds its RunnerOptions from the spec the
/// daemon wrote. On 16.09.2026 the two hang detectors were left at their
/// property defaults (600 s silence, 900 s without CPU) no matter what
/// RUNNER_COMMAND_SILENCE_WATCHDOG_SECONDS / RUNNER_REVIEW_NO_CPU_PROGRESS_SECONDS
/// said, so every quiet backend suite was killed after ten minutes.
/// </summary>
public sealed class DetachedReviewWorkerOptionsTests
{
    [Fact]
    public void Worker_options_carry_the_hang_detectors_from_the_spec()
    {
        var spec = Spec(commandSilenceWatchdogSeconds: 7200, reviewNoCpuProgressSeconds: 0);

        var options = DurableReviewProcess.WorkerOptions(spec);

        Assert.Equal(7200, options.CommandSilenceWatchdogSeconds);
        Assert.Equal(0, options.ReviewNoCpuProgressSeconds);
        Assert.Equal("review-runner", options.RunnerId);
        Assert.Equal("review", options.Role);
    }

    [Fact]
    public void Spec_round_trips_the_hang_detectors_through_json()
    {
        var spec = Spec(commandSilenceWatchdogSeconds: 1800, reviewNoCpuProgressSeconds: 2400);
        var json = JsonSerializer.Serialize(spec, JsonOptions);

        var restored = JsonSerializer.Deserialize<DetachedReviewSpec>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(1800, restored!.CommandSilenceWatchdogSeconds);
        Assert.Equal(2400, restored.ReviewNoCpuProgressSeconds);
    }

    [Fact]
    public void A_spec_from_an_older_daemon_falls_back_to_the_environment()
    {
        var previous = Environment.GetEnvironmentVariable("RUNNER_COMMAND_SILENCE_WATCHDOG_SECONDS");
        Environment.SetEnvironmentVariable("RUNNER_COMMAND_SILENCE_WATCHDOG_SECONDS", "4321");
        try
        {
            var options = DurableReviewProcess.WorkerOptions(Spec(null, null));

            Assert.Equal(4321, options.CommandSilenceWatchdogSeconds);
            Assert.Equal(900, options.ReviewNoCpuProgressSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RUNNER_COMMAND_SILENCE_WATCHDOG_SECONDS", previous);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static DetachedReviewSpec Spec(int? commandSilenceWatchdogSeconds, int? reviewNoCpuProgressSeconds)
    {
        var createdAt = new DateTime(2026, 9, 16, 15, 0, 0, DateTimeKind.Utc);
        var subject = new ReviewSubjectDto(
            "subject-1",
            "AGT-2851",
            "run-1",
            "example/repository",
            null,
            new string('a', 40),
            null,
            "bundle",
            new string('b', 64),
            "coding-host",
            "policy-v1",
            new ReviewPlanDto([], []),
            createdAt);
        var lease = new ReviewLeaseDto(
            "lease-1",
            "review_1",
            "subject-1",
            "review-runner",
            "instance-1",
            "review-host",
            7,
            createdAt,
            createdAt.AddMinutes(10),
            "active",
            "resource-1",
            25000,
            11);
        return new DetachedReviewSpec(
            subject,
            lease,
            Path.GetTempPath(),
            [],
            CommandSilenceWatchdogSeconds: commandSilenceWatchdogSeconds,
            ReviewNoCpuProgressSeconds: reviewNoCpuProgressSeconds);
    }
}
