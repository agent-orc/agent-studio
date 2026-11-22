using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class WeeklyQuotaRefusalTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 11, 17, 8, 19, 0, TimeSpan.Zero);
    // Stream envelope reconstructed from the retained provider refusal. The
    // original central cli-output.log files were unavailable on this host.
    private const string Frame = """
        {"type":"result","subtype":"error_during_execution","is_error":true,"errors":["You've hit your weekly limit · resets 1pm (Europe/Berlin)"]}
        """;

    [Theory]
    [InlineData(ExecutionAttemptKind.Coding)]
    [InlineData(ExecutionAttemptKind.Review)]
    public void Weekly_refusal_waits_instead_of_resuming_exhausted_provider(ExecutionAttemptKind kind)
    {
        var evidence = ProviderOutputEvidenceExtractor.Extract(Frame);
        var result = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            "weekly-quota", kind,
            ProviderTerminalEvent: evidence.TerminalEvent,
            StdOut: Frame, ExitCode: 1,
            SessionState: ExecutionSessionState.Resumable, SessionId: "existing-session",
            DurableOutputState: DurableOutputState.Published,
            DurableOutputReference: "refs/heads/agent-studio/salvage/weekly-quota"));

        Assert.Equal(ExecutionOutcomeKind.QuotaExceeded, result.Outcome);
        Assert.Equal(ExecutionRecoveryAction.WaitForCapabilityRecovery, result.RecoveryAction);
        Assert.False(result.ConsumesCompletionBudget);
        Assert.False(result.ConsumesCodingReworkBudget);
        Assert.Contains("weekly limit", result.Detail);
        Assert.Null(MechanicalRoundFallbackPolicy.Reason(true, RunOutcomeKind.Unknown, result.Outcome, null));
    }

    [Theory]
    [InlineData("1pm (Europe/Berlin)", 12)]
    [InlineData("12pm (UTC)", 12)]
    [InlineData("1:00pm (Europe/Berlin)", 12)]
    public void Clock_reset_uses_provider_zone_instead_of_host_zone(string clock, int utcHour)
    {
        var result = ProviderAccessClassifier.ClassifyRun(1, null,
            $"You've hit your weekly limit · resets {clock}",
            observedAt: ObservedAt, localZone: TimeZoneInfo.Utc);
        Assert.Equal(ProviderAccessEvidenceKind.RateLimited, result.Kind);
        Assert.True(result.ResetTimeReported);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, utcHour, 0, 0, TimeSpan.Zero), result.LimitedUntil);
    }

    [Fact]
    public async Task Refusal_removes_provider_from_admission_until_reset_even_after_login_probe()
    {
        var now = ObservedAt;
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(0, "Logged in", "")),
            executableExists: _ => true, clock: () => now,
            credentialFreshness: _ => new ProviderCredentialFreshness(null, null, "Fixture credentials."));
        await probe.RefreshAsync("claude", CancellationToken.None);
        var limited = probe.RecordProcessResult("claude", new ProcessResult(1, Frame, ""));
        Assert.Equal(ProviderAuthProbe.Limited, limited.Status);
        Assert.Contains("weekly limit", limited.Detail);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero), limited.LimitedUntil);

        now = now.AddMinutes(1);
        await probe.RefreshAsync("claude", CancellationToken.None);
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server", RunnerId = "test", RunnerName = "test",
            Hostname = "test", BackendName = "test", WorkDir = Path.GetTempPath(),
            BaseBranch = "develop", Role = "coding", ClaudeCliBin = "claude",
        };
        var advertised = RunnerCapabilityProbe.Advertise(options, gitPushReady: false, providerAuth: probe);
        Assert.Equal(ProviderAuthProbe.Limited,
            Assert.Single(advertised, item => item.Key == "provider-auth:claude").Status);
        now = limited.LimitedUntil!.Value;
        Assert.Equal(ProviderAuthProbe.Ready, probe.Current("claude").Status);
    }

    [Theory]
    [InlineData("""{"type":"assistant","error":"rate_limit","message":{"content":[{"type":"text","text":"You've hit your weekly limit · resets 1pm (Europe/Berlin)"}]}}""", ProviderAccessEvidenceKind.RateLimited)]
    [InlineData("""{"type":"assistant","error":"rate_limit","message":{"content":[{"type":"text","text":"Provider refused the request."}]}}""", ProviderAccessEvidenceKind.RateLimited)]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"You've hit your weekly limit · resets 1pm (Europe/Berlin)"}]}}""", ProviderAccessEvidenceKind.IndeterminateFailure)]
    [InlineData("""{"type":"user","message":{"content":[{"type":"tool_result","content":"You've hit your weekly limit · resets 1pm (Europe/Berlin)"}]}}""", ProviderAccessEvidenceKind.IndeterminateFailure)]
    public void Only_provider_error_frames_diagnose_a_weekly_refusal(string stdout, ProviderAccessEvidenceKind expected)
        => Assert.Equal(expected, ProviderAccessClassifier.ClassifyRun(1, stdout, null, observedAt: ObservedAt).Kind);
}
