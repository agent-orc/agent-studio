using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3948: the 17 November 2026 latch. After a weekly limit the status
/// command kept confirming a session, the real requests during the limit spent
/// the daily budget, and the binding stayed indeterminate after the reset until
/// the daemon was restarted. These tests drive the daemon cadence (one permitted
/// refresh per minute) through healthy, limited, indeterminate, reset and
/// recovered on one probe instance, so no restart is involved.
/// </summary>
public sealed class ProviderAuthLatchRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset LimitHit = new(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"agt-3948-{Guid.NewGuid():N}");

    public ProviderAuthLatchRecoveryTests()
    {
        // A native login file the daemon environment token shadows.
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        File.WriteAllText(Path.Combine(_home, ".claude", ".credentials.json"), "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private sealed class Fleet
    {
        public DateTimeOffset Now;
        public int RealRequests;
        public int StatusRequests;
        public readonly List<string> Log = [];
        public required ProviderAuthProbe Probe { get; init; }
        public required string Binary { get; init; }

        public static Task<ProviderCanaryPermitDto> Permit(string _, CancellationToken __)
            // The worst case for the old code: the server circuit keeps granting
            // a canary, which bypassed the degraded check and spent the budget.
            => Task.FromResult(new ProviderCanaryPermitDto(true, null, "canary"));

        public async Task MinuteAsync()
        {
            await RunnerCapabilityProbe.RefreshPermittedProvidersAsync(
                [Binary], Permit, Probe, Log.Add, CancellationToken.None);
            Now = Now.AddMinutes(1);
        }
    }

    private Fleet Build(string binary, string limitAnswer)
    {
        Fleet? fleet = null;
        var environment = new Dictionary<string, string?>
        {
            // Built at runtime: never a provider-shaped literal in source.
            ["CLAUDE_CODE_OAUTH_TOKEN"] = $"fixture-{Guid.NewGuid():N}",
        };
        var probe = new ProviderAuthProbe(
            (_, _, _) =>
            {
                Interlocked.Increment(ref fleet!.StatusRequests);
                // The status command confirms the session during the whole window.
                return Task.FromResult(new ProcessResult(0,
                    binary == "claude" ? "Logged in using a long-lived token" : "Logged in using ChatGPT", ""));
            },
            executableExists: _ => true,
            clock: () => fleet!.Now,
            diagnosticLog: line => fleet!.Log.Add(line),
            credentialFreshness: name => name == "claude"
                ? ProviderCredentialMonitor.Inspect(name, _home, environment)
                : new ProviderCredentialFreshness(null, null, "Native login fixture.",
                    EffectiveSource: "native-cli-store", CredentialGeneration: "native-cli-store:1"));
        probe.UseRealRequest((_, _, _) =>
        {
            Interlocked.Increment(ref fleet!.RealRequests);
            return Task.FromResult(fleet.Now >= LimitHit && fleet.Now < Reset
                ? new ProcessResult(1, "", limitAnswer)
                : new ProcessResult(0, "OK", ""));
        }, new ProviderStatusIncidentAdapter((_, _) => Task.FromResult("{\"incidents\":[]}"), () => fleet!.Now));
        probe.UseCanaryPermit(Fleet.Permit);
        fleet = new Fleet { Probe = probe, Binary = binary, Now = LimitHit.AddMinutes(-10) };
        return fleet;
    }

    public static TheoryData<string, string> LimitAnswers => new()
    {
        // Keep coverage for genuinely unclassified refusals. The weekly-limit
        // signature is now recognized and covered by WeeklyQuotaRefusalTests.
        { "claude", "Provider returned an unclassified refusal." },
        { "codex", "You've hit your usage limit. Upgrade to Pro or try again later." },
    };

    [Theory]
    [MemberData(nameof(LimitAnswers))]
    public async Task Limit_then_reset_recovers_to_healthy_without_a_restart(string binary, string limitAnswer)
    {
        var fleet = Build(binary, limitAnswer);
        var probe = fleet.Probe;

        // Healthy before the limit, proven by a real request.
        fleet.Now = LimitHit.AddMinutes(-10);
        Assert.Equal(ProviderProbeOutcome.Healthy, (await probe.RefreshAsync(binary, CancellationToken.None)).Outcome);
        Assert.Equal(1, fleet.RealRequests);
        if (binary == "claude")
        {
            var healthy = probe.Current(binary);
            Assert.Equal("environment-file", healthy.EffectiveSource);
            Assert.True(healthy.NativeFileShadowed);
        }

        // A run hits the limit; no reset time is reported.
        fleet.Now = LimitHit;
        var limited = probe.RecordProcessResult(binary, new ProcessResult(1, "", "Claude AI usage limit reached"));
        Assert.Equal(ProviderAuthProbe.Limited, limited.Status);
        var budgetBefore = fleet.RealRequests;

        // Four hours of daemon cadence until the provider resets at 12:00.
        while (fleet.Now < Reset) await fleet.MinuteAsync();
        var latched = probe.Current(binary);
        Assert.Equal(ProviderAuthProbe.Ready, latched.Status);
        Assert.Equal(ProviderProbeOutcome.Indeterminate, latched.Outcome);
        Assert.True(latched.StatusSessionActive);
        Assert.Contains("confirmed an active session", latched.Detail);
        // Paced: the limit window cannot spend the daily budget.
        var windowRequests = fleet.RealRequests - budgetBefore;
        Assert.InRange(windowRequests, 1, 12);

        // After the reset the next due recovery request proves access again.
        var recoveredAt = (DateTimeOffset?)null;
        while (fleet.Now < Reset + ProviderAuthProbe.RecoveryBackoffCeiling + TimeSpan.FromMinutes(1))
        {
            await fleet.MinuteAsync();
            if (probe.Current(binary).Outcome == ProviderProbeOutcome.Healthy)
            {
                recoveredAt = fleet.Now;
                break;
            }
        }
        Assert.NotNull(recoveredAt);
        var recovered = probe.Current(binary);
        Assert.Equal(ProviderAuthProbe.Ready, recovered.Status);
        Assert.Equal("healthy", recovered.Signal);
        Assert.False(recovered.ProbeDegraded);
        Assert.Null(recovered.IndeterminateSince);
        Assert.True(recovered.LastRealSuccessAt >= Reset);
        Assert.Contains(fleet.Log, line => line.StartsWith(
            $"provider-auth recovery request succeeded provider={binary}", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(LimitAnswers))]
    public async Task Successful_status_command_does_not_hold_an_indeterminate_outcome(string binary, string limitAnswer)
    {
        var fleet = Build(binary, limitAnswer);
        fleet.Now = LimitHit;
        fleet.Probe.RecordProcessResult(binary, new ProcessResult(1, "", "Claude AI usage limit reached"));
        fleet.Now = LimitHit.AddMinutes(61);
        await fleet.MinuteAsync();
        var since = fleet.Probe.Current(binary);
        Assert.Equal(ProviderProbeOutcome.Indeterminate, since.Outcome);
        // The clock starts at the refresh that first saw the state.
        Assert.Equal(fleet.Now.AddMinutes(-1), since.IndeterminateSince);

        // The indeterminate clock keeps its start across status answers.
        var started = since.IndeterminateSince;
        for (var minute = 0; minute < 40; minute++) await fleet.MinuteAsync();
        Assert.Equal(started, fleet.Probe.Current(binary).IndeterminateSince);

        fleet.Now = Reset;
        for (var minute = 0; minute <= 30
             && fleet.Probe.Current(binary).Outcome != ProviderProbeOutcome.Healthy; minute++)
            await fleet.MinuteAsync();
        Assert.Equal(ProviderProbeOutcome.Healthy, fleet.Probe.Current(binary).Outcome);
        Assert.Null(fleet.Probe.Current(binary).IndeterminateSince);
    }

    [Fact]
    public async Task Spent_budget_still_leaves_the_recovery_request_after_the_reset()
    {
        var fleet = Build("codex", "unclassified provider refusal");
        fleet.Now = LimitHit.AddMinutes(-10);
        await fleet.Probe.RefreshAsync("codex", CancellationToken.None);
        // Spend the whole daily budget on healthy checks first.
        for (var i = fleet.RealRequests; i < ProviderAuthProbe.DailyRealRequestBudget; i++)
        {
            fleet.Probe.RecordProcessResult("codex", new ProcessResult(1, "", "HTTP 401 Unauthorized"));
            await fleet.Probe.RefreshAsync("codex", CancellationToken.None);
        }
        Assert.Equal(ProviderAuthProbe.DailyRealRequestBudget, fleet.RealRequests);

        fleet.Now = LimitHit;
        fleet.Probe.RecordProcessResult("codex", new ProcessResult(1, "", "usage limit reached"));
        while (fleet.Now < Reset) await fleet.MinuteAsync();
        Assert.Equal(ProviderProbeOutcome.Indeterminate, fleet.Probe.Current("codex").Outcome);
        var overBudget = fleet.RealRequests - ProviderAuthProbe.DailyRealRequestBudget;
        // One request per ceiling beyond the budget: 09:00 to 12:00.
        Assert.InRange(overBudget, 1, 7);

        for (var minute = 0; minute <= 31
             && fleet.Probe.Current("codex").Outcome != ProviderProbeOutcome.Healthy; minute++)
            await fleet.MinuteAsync();
        Assert.Equal(ProviderProbeOutcome.Healthy, fleet.Probe.Current("codex").Outcome);
    }

    [Fact]
    public async Task Known_reset_schedules_the_recovery_request_at_the_reset_and_refunds_limit_answers()
    {
        var fleet = Build("claude", "You've hit your weekly limit · resets 12pm (UTC)");
        fleet.Now = LimitHit.AddMinutes(-10);
        await fleet.Probe.RefreshAsync("claude", CancellationToken.None);
        fleet.Now = LimitHit;
        fleet.Probe.RecordProcessResult("claude", new ProcessResult(1, "", "usage limit reached"));
        while (fleet.Now < Reset) await fleet.MinuteAsync();
        var windowRequests = fleet.RealRequests - 1;
        // 09:00, then every ceiling, the last one landing on the reset.
        Assert.InRange(windowRequests, 1, 8);
        Assert.Equal(ProviderProbeOutcome.QuotaExhausted, fleet.Probe.Current("claude").Outcome);

        await fleet.MinuteAsync();
        Assert.Equal(ProviderProbeOutcome.Healthy, fleet.Probe.Current("claude").Outcome);
        Assert.Equal(Reset, fleet.Probe.Current("claude").LastRealSuccessAt);

        Assert.Contains(fleet.Log, line => line.Contains("resetKnown=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Advertisement_carries_the_latch_facts_for_the_queue_watchdogs()
    {
        var fleet = Build("claude", "Provider returned an unclassified refusal.");
        fleet.Now = LimitHit;
        fleet.Probe.RecordProcessResult("claude", new ProcessResult(1, "", "Claude AI usage limit reached"));
        fleet.Now = LimitHit.AddMinutes(61);
        await fleet.MinuteAsync();
        var started = fleet.Probe.Current("claude").IndeterminateSince;
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server", RunnerId = "kori-review", RunnerName = "kori-review",
            Hostname = "kori-docker", BackendName = "test", WorkDir = Path.GetTempPath(),
            BaseBranch = "main", Role = "review", ClaudeCliBin = "claude",
        };
        var advertised = Assert.Single(RunnerCapabilityProbe.Advertise(options, gitPushReady: false,
            providerAuth: fleet.Probe), item => item.Key == "provider-auth:claude");
        Assert.Equal("indeterminate", advertised.HealthOutcome);
        Assert.True(advertised.StatusSessionActive);
        Assert.Equal(started!.Value.UtcDateTime, advertised.Since);
        Assert.Equal("environment-file", advertised.EffectiveSource);
        Assert.DoesNotContain(fleet.Log, line => line.Contains("fixture-", StringComparison.Ordinal));
        Assert.DoesNotContain("fixture-", advertised.Detail ?? "", StringComparison.Ordinal);
    }
}
