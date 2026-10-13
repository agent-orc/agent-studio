using AgentRunner;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProviderLimitRecoveryTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 13, 11, 0, 0, TimeSpan.Zero));

    private ProviderAuthProbe Probe(ProcessResult answer, Action<string>? log = null)
        => new((_, _, _) => Task.FromResult(answer), executableExists: _ => true,
            clock: _clock.GetUtcNow, diagnosticLog: log,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "Fixture credentials."));

    [Theory]
    [InlineData("unknown command: auth")]
    [InlineData("HTTP 503 service unavailable")]
    public async Task Refresh_after_reset_recovers_without_a_successful_provider_request(string output)
    {
        var logs = new List<string>();
        var probe = Probe(new ProcessResult(1, "", output), logs.Add);
        var limited = Limit(probe);
        Assert.Equal(ProviderAuthProbe.Limited, limited.Status);
        Assert.Equal(_clock.GetUtcNow().AddHours(1), limited.LimitedUntil);

        _clock.Advance(TimeSpan.FromMinutes(61));
        var recovered = await probe.RefreshAsync("claude", CancellationToken.None);
        Assert.Equal(ProviderAuthProbe.Ready, recovered.Status);
        Assert.Contains("unverified", recovered.Detail);
        Assert.Null(recovered.LimitedUntil);
        Assert.True(recovered.ProbeDegraded);
        Assert.Equal(ProviderProbeOutcome.Indeterminate, recovered.Outcome);

        await probe.RefreshAsync("claude", CancellationToken.None);
        Assert.Single(logs, line => line.StartsWith("provider-auth recovered provider=claude after="));
    }

    [Theory]
    [InlineData("review")]
    [InlineData("coding")]
    public async Task Advertisement_regains_claude_after_reset(string role)
    {
        var probe = Probe(new ProcessResult(1, "", "unknown command: auth"));
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server", RunnerId = "test", RunnerName = "test",
            Hostname = "test", BackendName = "test", WorkDir = Path.GetTempPath(),
            BaseBranch = "main", Role = role, ClaudeCliBin = "claude",
        };
        Limit(probe);
        var before = RunnerCapabilityProbe.Advertise(options, gitPushReady: false, providerAuth: probe);
        Assert.Equal(ProviderAuthProbe.Limited,
            Assert.Single(before, item => item.Key == "provider-auth:claude").Status);

        _clock.Advance(TimeSpan.FromMinutes(61));
        await probe.RefreshAsync("claude", CancellationToken.None);
        var after = RunnerCapabilityProbe.Advertise(options, gitPushReady: false, providerAuth: probe);
        Assert.Equal(ProviderAuthProbe.Ready,
            Assert.Single(after, item => item.Key == "provider-auth:claude").Status);
    }

    private static ProviderAuthStatus Limit(ProviderAuthProbe probe)
        => probe.RecordProcessResult("claude", new ProcessResult(1, "",
            "usage limit reached; resets at 2026-10-13T12:00:00Z"));

    [Theory]
    [InlineData(false, 60)]
    [InlineData(true, 60)]
    [InlineData(false, 15)]
    public void Unknown_limit_expires_from_last_limit_not_last_probe(
        bool transient, int windowMinutes)
    {
        var kind = transient ? ProviderAuthObservationKind.Transient : ProviderAuthObservationKind.Indeterminate;
        Assert.Equal(TimeSpan.FromMinutes(60), ProviderAuthProbe.UnknownLimitTtl);
        var window = TimeSpan.FromMinutes(windowMinutes);
        var original = Decide(null, ProviderAuthObservationKind.Limited, window);
        Assert.Null(original.Status.LimitedUntil);

        _clock.Advance(window - TimeSpan.FromMinutes(1));
        var within = Decide(original, kind, window);
        Assert.Equal(ProviderAuthProbe.Limited, within.Status.Status);
        Assert.Equal(original.Status.ObservedAt, within.Status.ObservedAt);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = Decide(within, kind, window);
        Assert.Equal(ProviderAuthProbe.Ready, recovered.Status.Status);
        Assert.Contains("unverified", recovered.Status.Detail);
        Assert.Null(recovered.Status.LimitedUntil);
    }

    [Fact]
    public void New_limit_renews_unknown_window_and_blocks_immediately_after_recovery()
    {
        var window = ProviderAuthProbe.UnknownLimitTtl;
        var original = Decide(null, ProviderAuthObservationKind.Limited, window);
        _clock.Advance(window);
        var renewed = Decide(original, ProviderAuthObservationKind.Limited, window);
        Assert.Equal(ProviderAuthProbe.Limited, renewed.Status.Status);
        Assert.Equal(_clock.GetUtcNow(), renewed.Status.ObservedAt);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ProviderAuthProbe.Limited,
            Decide(renewed, ProviderAuthObservationKind.Indeterminate, window).Status.Status);
        _clock.Advance(window);
        var recovered = Decide(renewed, ProviderAuthObservationKind.Indeterminate, window);
        Assert.Equal(ProviderAuthProbe.Ready, recovered.Status.Status);
        Assert.Equal(ProviderAuthProbe.Limited,
            Decide(recovered, ProviderAuthObservationKind.Limited, window).Status.Status);
    }

    [Theory]
    [InlineData(ProviderAuthProbe.Limited)]
    [InlineData(ProviderAuthProbe.Unavailable)]
    [InlineData(ProviderAuthProbe.Ready)]
    public void Unauthorized_retains_existing_status(string status)
    {
        var original = new ProviderAuthCacheEntry(new ProviderAuthStatus(status, "previous", _clock.GetUtcNow()), 0);
        _clock.Advance(TimeSpan.FromHours(2));
        var retained = Decide(original, ProviderAuthObservationKind.Unauthorized, ProviderAuthProbe.UnknownLimitTtl);
        Assert.Equal(status, retained.Status.Status);
        Assert.Equal("indeterminate", retained.Status.Signal);
        Assert.Equal(ProviderProbeOutcome.Indeterminate, retained.Status.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inconclusive_probe_does_not_restore_confirmed_signed_out_status(bool transient)
    {
        var kind = transient ? ProviderAuthObservationKind.Transient : ProviderAuthObservationKind.Indeterminate;
        var original = new ProviderAuthCacheEntry(
            new ProviderAuthStatus(ProviderAuthProbe.Unavailable, "signed out", _clock.GetUtcNow()), 2);
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(ProviderAuthProbe.Unavailable,
            Decide(original, kind, ProviderAuthProbe.UnknownLimitTtl).Status.Status);
    }

    [Fact]
    public void Current_recovers_at_reset_and_logs_once_without_a_launcher()
    {
        var logs = new List<string>();
        var probe = new ProviderAuthProbe(executableExists: _ => true, clock: _clock.GetUtcNow, diagnosticLog: logs.Add);
        Limit(probe);
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(ProviderAuthProbe.Ready, probe.Current("claude").Status);
        Assert.Equal(ProviderAuthProbe.Ready, probe.Current("claude").Status);
        Assert.Single(logs, line => line.StartsWith("provider-auth recovered provider=claude after=01:00:00"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Unknown_limit_window_must_be_positive(int minutes)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderAuthProbe(
            clock: _clock.GetUtcNow, unknownLimitTtl: TimeSpan.FromMinutes(minutes)));

    private ProviderAuthCacheEntry Decide(
        ProviderAuthCacheEntry? previous, ProviderAuthObservationKind kind, TimeSpan window)
        => ProviderAuthProbe.Decide(previous, new ProviderAuthObservation(kind, "Fixture observation."),
            ProviderAuthProbe.DefaultNegativeConfirmations, _clock.GetUtcNow(), window);
}
