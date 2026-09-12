using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class ReviewRestartVisibilityTests
{
    private static readonly DateTimeOffset Start =
        DateTimeOffset.Parse("2026-09-07T05:25:00+02:00");

    [Fact]
    public async Task Changed_review_instance_persists_recovery_pending_without_false_loss()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var publisher = new RecordingOperationalEventPublisher();
        var store = Store(temp.Path, clock, publisher);
        await store.InitializeAsync();

        await store.RegisterRunnerAsync(
            "review-a",
            Registration("review-instance-a"),
            "review-a",
            default);
        var activeAttempts = new[]
        {
            new RunnerActiveAttempt(
                RunnerAttemptKinds.Review,
                "rat_missing_after_restart",
                "CARD-42",
                "lease-before-restart",
                41,
                LeaseInstanceId: "review-instance-a"),
        };
        var replacement = Registration("review-instance-b", activeAttempts);

        var registered = await store.RegisterRunnerAsync(
            "review-a",
            replacement,
            "review-a",
            default);

        var rejected = Assert.Single(registered.AttemptAdoptions!);
        Assert.Equal("not-found", rejected.Status);
        var snapshot = Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.Equal(Start.UtcDateTime, snapshot.RestartedAt);
        Assert.Equal(0, snapshot.ReviewsLost);

        var audit = await store.ListAuditAsync(0, default);
        var restartAudit = Assert.Single(
            audit,
            record => record.Action == "review-daemon.restarted");
        Assert.Equal("review-a", restartAudit.TargetId);
        AssertRecoveryPendingRestart(JsonDocument.Parse(restartAudit.DetailJson).RootElement);
        Assert.DoesNotContain(audit, record => record.Action == "review-attempt.lost-on-restart");

        var durableEvents = await store.ListStudioStreamEventsSinceAsync(0, default);
        var durableRestart = Assert.Single(durableEvents);
        Assert.Equal("review-daemon.restarted", durableRestart.Kind);
        AssertRecoveryPendingRestart(JsonDocument.Parse(durableRestart.PayloadJson).RootElement);
        var publishedRestart = Assert.Single(publisher.Messages);
        Assert.Equal("review-daemon.restarted", publishedRestart.Kind);
        Assert.Equal(Start.UtcDateTime, publishedRestart.OccurredAt);
        AssertRecoveryPendingRestart(Payload(publishedRestart));

        // The replacement's routine registration can re-report its slot but
        // must not become a second restart observation or duplicate advisories.
        await store.RegisterRunnerAsync(
            "review-a",
            replacement,
            "review-a",
            default);
        audit = await store.ListAuditAsync(0, default);
        Assert.Single(audit, record => record.Action == "review-daemon.restarted");
        Assert.DoesNotContain(audit, record => record.Action == "review-attempt.lost-on-restart");
        Assert.Single(await store.ListStudioStreamEventsSinceAsync(0, default));
        Assert.Single(publisher.Messages);

        // The row is durable across Task Server restart and leaves the host
        // projection after its bounded 24-hour visibility window.
        var reopened = Store(temp.Path, clock, new RecordingOperationalEventPublisher());
        await reopened.InitializeAsync();
        var persisted = Assert.Single(await reopened.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.Equal(Start.UtcDateTime, persisted.RestartedAt);
        Assert.Equal(0, persisted.ReviewsLost);

        clock.Advance(TimeSpan.FromHours(24).Add(TimeSpan.FromSeconds(1)));
        var expired = Assert.Single(await reopened.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.Null(expired.RestartedAt);
        Assert.Equal(0, expired.ReviewsLost);
    }

    private static RegisterRunnerRequest Registration(
        string instanceId,
        IReadOnlyList<RunnerActiveAttempt>? activeAttempts = null)
        => new(
            "review-a",
            "review-host",
            instanceId,
            "1.0.0",
            TaskServerProtocol.Current,
            [ReviewCapabilities.ReviewExecutor],
            ActiveAttempts: activeAttempts);

    private static TaskServerStore Store(
        string dataDirectory,
        TimeProvider clock,
        ITaskServerEventPublisher publisher)
        => new(
            Options.Create(new TaskServerOptions { DataDirectory = dataDirectory }),
            clock,
            new ApplicationResultFinalizationSummaryGenerator(),
            publisher,
            NullLogger<TaskServerStore>.Instance);

    private static JsonElement Payload(TaskServerOperationalEvent message)
        => JsonSerializer.SerializeToElement(message.Payload, message.Payload.GetType());

    private static void AssertRecoveryPendingRestart(JsonElement payload)
    {
        Assert.Equal("supervisor", payload.GetProperty("audience").GetString());
        Assert.Contains("rat_missing_after_restart", payload.GetProperty("message").GetString());
        Assert.Contains("CARD-42", payload.GetProperty("message").GetString());
        Assert.Equal("runner-instance-generation-changed", payload.GetProperty("cause").GetString());
        Assert.Equal(0, payload.GetProperty("reviewsLost").GetInt32());
        Assert.Equal(
            "not-found",
            payload.GetProperty("attempts")[0].GetProperty("status").GetString());
    }

    private sealed class RecordingOperationalEventPublisher : ITaskServerEventPublisher
    {
        public List<TaskServerOperationalEvent> Messages { get; } = [];

        public Task PublishAsync(TaskServerOperationalEvent message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
