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
    public async Task Changed_review_instance_persists_and_publishes_restart_and_loss_advisories_once()
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
        Assert.Equal(1, snapshot.ReviewsLost);

        var audit = await store.ListAuditAsync(0, default);
        var restartAudit = Assert.Single(
            audit,
            record => record.Action == "review-daemon.restarted");
        var lossAudit = Assert.Single(
            audit,
            record => record.Action == "review-attempt.lost-on-restart");
        Assert.Equal("review-a", restartAudit.TargetId);
        Assert.Equal("rat_missing_after_restart", lossAudit.TargetId);
        AssertAdvisory(JsonDocument.Parse(restartAudit.DetailJson).RootElement, restart: true);
        AssertAdvisory(JsonDocument.Parse(lossAudit.DetailJson).RootElement, restart: false);

        var durableEvents = await store.ListStudioStreamEventsSinceAsync(0, default);
        Assert.Collection(
            durableEvents,
            item =>
            {
                Assert.Equal("review-daemon.restarted", item.Kind);
                AssertAdvisory(JsonDocument.Parse(item.PayloadJson).RootElement, restart: true);
            },
            item =>
            {
                Assert.Equal("review-attempt.lost-on-restart", item.Kind);
                AssertAdvisory(JsonDocument.Parse(item.PayloadJson).RootElement, restart: false);
            });
        Assert.Collection(
            publisher.Messages,
            item =>
            {
                Assert.Equal("review-daemon.restarted", item.Kind);
                Assert.Equal(Start.UtcDateTime, item.OccurredAt);
                AssertAdvisory(Payload(item), restart: true);
            },
            item =>
            {
                Assert.Equal("review-attempt.lost-on-restart", item.Kind);
                Assert.Equal(Start.UtcDateTime, item.OccurredAt);
                AssertAdvisory(Payload(item), restart: false);
            });

        // The replacement's routine registration can re-report its slot but
        // must not become a second restart observation or duplicate advisories.
        await store.RegisterRunnerAsync(
            "review-a",
            replacement,
            "review-a",
            default);
        audit = await store.ListAuditAsync(0, default);
        Assert.Single(audit, record => record.Action == "review-daemon.restarted");
        Assert.Single(audit, record => record.Action == "review-attempt.lost-on-restart");
        Assert.Equal(2, (await store.ListStudioStreamEventsSinceAsync(0, default)).Count);
        Assert.Equal(2, publisher.Messages.Count);

        // The row is durable across Task Server restart and leaves the host
        // projection after its bounded 24-hour visibility window.
        var reopened = Store(temp.Path, clock, new RecordingOperationalEventPublisher());
        await reopened.InitializeAsync();
        var persisted = Assert.Single(await reopened.ListRunnerCapabilitySnapshotsAsync(default));
        Assert.Equal(Start.UtcDateTime, persisted.RestartedAt);
        Assert.Equal(1, persisted.ReviewsLost);

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

    private static void AssertAdvisory(JsonElement payload, bool restart)
    {
        Assert.Equal("supervisor", payload.GetProperty("audience").GetString());
        Assert.Contains("rat_missing_after_restart", payload.GetProperty("message").GetString());
        Assert.Contains("CARD-42", payload.GetProperty("message").GetString());
        if (restart)
        {
            Assert.Equal("runner-instance-generation-changed", payload.GetProperty("cause").GetString());
            Assert.Equal(1, payload.GetProperty("reviewsLost").GetInt32());
            Assert.Equal(
                "not-found",
                payload.GetProperty("attempts")[0].GetProperty("status").GetString());
        }
        else
        {
            Assert.Equal("not-found", payload.GetProperty("status").GetString());
            Assert.Contains("ReviewAttempt was not found", payload.GetProperty("cause").GetString());
        }
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
