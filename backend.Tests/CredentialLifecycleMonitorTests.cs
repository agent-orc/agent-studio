using AgentStudio.Bus;
using AgentStudio.GitHubApp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CredentialLifecycleMonitorTests
{
    [Theory]
    [InlineData("expiry")]
    [InlineData("rotation")]
    public async Task Owner_feed_warns_at_14_and_3_days_once_and_survives_restart(string subject)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var created = clock.GetUtcNow().UtcDateTime;
        var due = created.AddDays(90);
        var path = Path.Combine(Path.GetTempPath(), "wi-monitor-" + Guid.NewGuid().ToString("N"), "metadata.json");
        var feed = new Feed();
        try
        {
            var monitor = new CredentialLifecycleMonitor(path, clock, feed);
            CredentialInventoryDto Inventory() => new(clock.GetUtcNow().UtcDateTime,
                [new("credential-a", "service", "host-a", "service_credential", "Credential", [], null, null,
                    subject == "expiry" ? due : null, null, "unused", false, "Metadata only", [],
                    CreatedAt: created, RotationIntervalDays: subject == "rotation" ? 90 : 180)]);
            await monitor.CompleteAsync(Inventory(), true, default);
            clock.Advance(TimeSpan.FromDays(75));
            await monitor.CompleteAsync(Inventory(), true, default);
            Assert.Empty(feed.Items);
            clock.Advance(TimeSpan.FromDays(1));
            await monitor.CompleteAsync(Inventory(), true, default);
            Assert.Equal((subject, 14), (Assert.Single(feed.Items).Subject, feed.Items[0].ThresholdDays));
            monitor = new CredentialLifecycleMonitor(path, clock, feed);
            await monitor.CompleteAsync(Inventory(), true, default);
            Assert.Single(feed.Items);
            clock.Advance(TimeSpan.FromDays(11));
            await monitor.CompleteAsync(Inventory(), true, default);
            Assert.Equal([14, 3], feed.Items.Select(item => item.ThresholdDays));
            await monitor.CompleteAsync(Inventory(), true, default);
            Assert.Equal(2, feed.Items.Count);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("rotation")]
    public async Task Thresholds_are_appended_to_the_operator_feed_with_the_injected_time(string subject)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var root = Path.Combine(Path.GetTempPath(), "wi-feed-" + Guid.NewGuid().ToString("N"));
        var created = clock.GetUtcNow().UtcDateTime.AddDays(-76);
        var due = created.AddDays(90);
        var bus = new AgentMessageBusStore();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = root,
        }).Build();
        var channel = new CredentialLifecycleNotifier(new AgentMessageBusBridge(bus, config,
            NullLogger<AgentMessageBusBridge>.Instance, clock));
        var monitor = new CredentialLifecycleMonitor(Path.Combine(root, "metadata.json"), clock, channel);
        CredentialInventoryDto Inventory() => new(clock.GetUtcNow().UtcDateTime,
            [new("credential-a", "service", "host-a", "service_credential", "Credential", [], null, null,
                subject == "expiry" ? due : null, null, "ok", false, "Metadata only", [],
                CreatedAt: created, RotationIntervalDays: subject == "rotation" ? 90 : 180)]);
        try
        {
            await monitor.CompleteAsync(Inventory(), true, default);
            var first = Assert.Single(bus.Recent(root, null, 10));
            Assert.Equal("credential_lifecycle_warning", first.Topic);
            Assert.Equal(clock.GetUtcNow().UtcDateTime, first.CreatedAt);
            Assert.Equal(14, first.Payload!.Value.GetProperty("thresholdDays").GetInt32());
            Assert.Equal(subject, first.Payload.Value.GetProperty("subject").GetString());
            clock.Advance(TimeSpan.FromDays(11));
            await monitor.CompleteAsync(Inventory(), true, default);
            var messages = bus.Recent(root, null, 10);
            Assert.Equal(2, messages.Count);
            Assert.Contains(messages, message => message.Payload!.Value.GetProperty("thresholdDays").GetInt32() == 3);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Unknown_creation_keeps_a_stable_rotation_plan_and_failed_delivery_retries()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var first = clock.GetUtcNow().UtcDateTime;
        var path = Path.Combine(Path.GetTempPath(), "wi-plan-" + Guid.NewGuid().ToString("N"), "metadata.json");
        var feed = new Feed { Deliver = false };
        CredentialInventoryDto Inventory() => new(clock.GetUtcNow().UtcDateTime,
            [new("provider-a", "provider", "host-a", "codex_chatgpt_login", "Provider login", [], null, null,
                null, null, "ok", false, "Metadata only", [], Refreshable: true)]);
        try
        {
            var monitor = new CredentialLifecycleMonitor(path, clock, feed);
            var initial = Assert.Single((await monitor.CompleteAsync(Inventory(), false, default)).Items);
            Assert.Null(initial.CreatedAt);
            Assert.Equal("none", initial.Expiry);
            Assert.Equal(first.AddDays(90), initial.RotationDueAt);
            clock.Advance(TimeSpan.FromDays(76));
            var next = Assert.Single((await monitor.CompleteAsync(Inventory(), true, default)).Items);
            Assert.Equal(initial.RotationDueAt, next.RotationDueAt);
            Assert.Equal("expiring", next.State);
            Assert.Empty(feed.Items);
            feed.Deliver = true;
            await new CredentialLifecycleMonitor(path, clock, feed).CompleteAsync(Inventory(), true, default);
            Assert.Single(feed.Items);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [InlineData(24, false, "short-lived")]
    [InlineData(25, false, "long-lived")]
    [InlineData(1, true, "long-lived")]
    public void Classification_uses_issued_lifetime_and_keeps_refreshable_logins_durable(int hours, bool refreshable, string expected)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var created = clock.GetUtcNow().UtcDateTime;
        var item = new CredentialInventoryItemDto("credential-a", "service", "host-a", "test", "Credential", [], null, null,
            created.AddHours(hours), null, "ok", false, "Metadata only", [], CreatedAt: created, Refreshable: refreshable);
        Assert.Equal(expected, item.LifetimeClass);
    }

    [Theory]
    [InlineData("github_app_token")]
    [InlineData("attempt_repository_token")]
    [InlineData("enrolment_code")]
    public void Known_issuer_limits_classify_unissued_or_historical_short_credentials_without_inventing_dates(string kind)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var item = CredentialLifecyclePolicy.Complete(new("credential-a", "service", "host-a", kind,
            "Credential", [], null, null, null, null, "unused", false, "Metadata only", []), clock.GetUtcNow().UtcDateTime);
        Assert.Equal("short-lived", item.LifetimeClass);
        Assert.Null(item.CreatedAt);
        Assert.Null(item.ExpiresAt);
        Assert.Null(item.RotationDueAt);
    }

    private sealed class Feed : ICredentialLifecycleNotifier
    {
        public bool Deliver { get; set; } = true;
        public List<CredentialLifecycleWarningDto> Items { get; } = [];
        public Task<bool> NotifyAsync(CredentialLifecycleWarningDto warning, CancellationToken ct)
        {
            if (Deliver) Items.Add(warning);
            return Task.FromResult(Deliver);
        }
    }
}
