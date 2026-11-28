using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProviderRequestRejectionClassifierTests
{
    internal const string Refusal = """
        HTTP 403 {"error":{"type":"invalid_request_error","code":"usage_limit_reached","param":"model","message":"You've hit your usage limit.","resets_at":1795854600}}
        """;

    [Fact]
    public void Exhausted_usage_window_takes_precedence_over_request_rejection()
    {
        Assert.False(ProviderRequestRejectionClassifier.TryClassify(Refusal, out _));
        var access = ProviderAccessClassifier.ClassifyRun(1, "", Refusal);
        Assert.Equal(ProviderAccessEvidenceKind.RateLimited, access.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795854600), access.LimitedUntil);
        Assert.True(access.ResetTimeReported);
        var decision = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts("attempt", ExecutionAttemptKind.Coding,
            StdErr: Refusal, ExitCode: 1, EffectiveCliType: "codex", EffectiveModel: "gpt-6.1-sol"));
        Assert.Equal(ExecutionOutcomeKind.QuotaExceeded, decision.Outcome);
        Assert.Equal(ExecutionRecoveryAction.WaitForCapabilityRecovery, decision.RecoveryAction);
    }
}
