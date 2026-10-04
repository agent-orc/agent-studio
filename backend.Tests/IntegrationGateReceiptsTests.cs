using AgentStudio.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public void Failed_receipt_preserves_diagnosis_and_raises_budget_finding()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var assessment = new GateFailureAssessment(GateFailureAssessmentPolicy.Environment,
            "gate:budget", [], "violated gate-run budget");
        var failed = new BuildTestGateResult(BuildTestGateVerdict.Fail, 1, 1200,
            "violated gate-run budget", "violated gate-run budget", false, false)
        {
            ExpectedSha = sha,
            TestedSha = sha,
            FailureKind = BuildTestGateFailureKind.Environment,
            FailureAssessment = assessment,
            ViolatedBudget = new BuildTestGateBudgetEvidence("gate-run", 1000, 1200, "verification"),
        };

        IntegrationGateReceipts.Record(_root, "pre-develop-build-gate", failed, timeline);
        var recovered = IntegrationGateReceipts.ReadExact(_root, "pre-develop-build-gate", sha);

        Assert.Equal(assessment.Classification, recovered?.FailureAssessment?.Classification);
        Assert.Equal(assessment.Fingerprint, recovered?.FailureAssessment?.Fingerprint);
        Assert.Single(timeline.ReadAll(_root), entry =>
            entry.Kind == TimelineEventKinds.IntegrationGateBudgetExceeded);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
