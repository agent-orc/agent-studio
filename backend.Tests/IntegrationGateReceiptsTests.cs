using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationGateReceiptsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "integration-gate-receipt-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReadExact_recovers_legacy_receipt_without_cache_provenance()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var postSteps = Path.Combine(_root, "post-steps");
        Directory.CreateDirectory(postSteps);
        File.WriteAllText(Path.Combine(postSteps, "pre-main-test-gate-1.log"),
            "verdict=Ok exit=0 durationMs=321 failureKind=None\n" +
            $"expectedSha={sha} testedSha={sha}\n" +
            "reason=Legacy gate completed.\n" +
            "testSelectionAuditDigest=legacy-selection\n" +
            "reviewReuse=not-evaluated\n" +
            "--- last-300-lines ---\n");

        var recovered = IntegrationGateReceipts.ReadExact(_root, "pre-main-test-gate", sha);

        Assert.NotNull(recovered);
        Assert.Equal(BuildTestGateVerdict.Ok, recovered.Verdict);
        Assert.Equal(GateVerdictSource.Executed, recovered.VerdictSource);
        Assert.Equal(0, recovered.ExitCode);
        Assert.Equal(321, recovered.DurationMs);
        Assert.Equal("Legacy gate completed.", recovered.Reason);
        Assert.Null(recovered.GateRunId);
        Assert.Null(recovered.GateCompletedAtUtc);
        Assert.Null(recovered.OriginEvidencePath);
        Assert.Null(recovered.GateProfileDigest);
        Assert.Null(recovered.PipelineDefinitionVersion);
        Assert.Null(recovered.ToolchainIdentity);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
