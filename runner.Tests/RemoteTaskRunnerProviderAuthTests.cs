using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteTaskRunnerProviderAuthTests
{
    private const string Refusal =
        "usage limit reached; resets at 2026-09-19T12:40:00Z";

    [Fact]
    public async Task Production_result_adapter_suppresses_sigterm_but_records_normal_provider_refusal()
    {
        var signalProbe = Probe();
        var signalReady = await signalProbe.RefreshAsync("claude", CancellationToken.None);
        var signalFacts = Facts(exitCode: 143, signal: 15);

        var afterSigterm = RemoteTaskRunner.RecordProviderProcessResult(
            signalProbe,
            "claude",
            new ProcessResult(143, string.Empty, Refusal),
            signalFacts,
            evidenceId: "run-sigterm");

        Assert.Equal(signalReady, afterSigterm);
        Assert.Equal(ProviderAuthProbe.Ready, afterSigterm.Status);

        var refusalProbe = Probe();
        await refusalProbe.RefreshAsync("claude", CancellationToken.None);
        var refusalFacts = Facts(exitCode: 1, signal: null);

        var afterRefusal = RemoteTaskRunner.RecordProviderProcessResult(
            refusalProbe,
            "claude",
            new ProcessResult(1, string.Empty, Refusal),
            refusalFacts,
            evidenceId: "run-refusal");

        Assert.Equal(ProviderAuthProbe.Limited, afterRefusal.Status);
        Assert.Equal("run-refusal", afterRefusal.EvidenceId);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 19, 12, 40, 0, TimeSpan.Zero),
            afterRefusal.LimitedUntil);
    }

    private static ProviderAuthProbe Probe()
        => new(
            launcher: (_, _, _) => Task.FromResult(new ProcessResult(0, "Logged in", string.Empty)),
            executableExists: _ => true,
            credentialFreshness: _ => new ProviderCredentialFreshness(
                null,
                null,
                "Credential metadata fixture has no expiry."));

    private static ExecutionRawFacts Facts(int exitCode, int? signal)
        => new(
            "attempt-provider-auth",
            ExecutionAttemptKind.Coding,
            StdErr: Refusal,
            ExitCode: exitCode,
            Signal: signal,
            DurableOutputState: DurableOutputState.LocalOnly);
}
