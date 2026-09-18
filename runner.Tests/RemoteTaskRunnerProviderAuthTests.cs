using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteTaskRunnerProviderAuthTests
{
    private const string Refusal =
        "usage limit reached; resets at 2026-09-19T12:40:00Z";

    [Theory]
    [InlineData(137, null, false, ProviderAuthProbe.Limited)]
    [InlineData(137, 9, false, ProviderAuthProbe.Ready)]
    [InlineData(137, null, true, ProviderAuthProbe.Ready)]
    [InlineData(1, null, false, ProviderAuthProbe.Limited)]
    public async Task Production_result_adapter_uses_recorded_termination_facts_only(
        int exitCode,
        int? recordedSignal,
        bool operatorStopped,
        string expectedStatus)
    {
        var probe = Probe();
        await probe.RefreshAsync("claude", CancellationToken.None);
        var facts = Facts(exitCode, recordedSignal);

        var status = RemoteTaskRunner.RecordProviderProcessResult(
            probe,
            "claude",
            new ProcessResult(exitCode, string.Empty, Refusal),
            facts,
            evidenceId: "run-provider-evidence",
            stopDirectiveRecorded: operatorStopped);

        Assert.Equal(expectedStatus, status.Status);
        if (expectedStatus == ProviderAuthProbe.Limited)
        {
            Assert.Equal("run-provider-evidence", status.EvidenceId);
            Assert.Equal(
                new DateTimeOffset(2026, 9, 19, 12, 40, 0, TimeSpan.Zero),
                status.LimitedUntil);
        }
        else
        {
            Assert.Null(status.EvidenceId);
            Assert.Null(status.LimitedUntil);
        }
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
