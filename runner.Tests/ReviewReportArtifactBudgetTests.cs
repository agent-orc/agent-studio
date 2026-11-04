using System.Security.Cryptography;
using System.Text;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewReportArtifactBudgetTests
{
    [Fact]
    public void Oversized_logs_keep_full_digests_and_head_tail_excerpts_under_wire_budget()
    {
        var source = Encoding.UTF8.GetBytes("HEAD\n" + new string('x', 9 * 1024 * 1024) + "\nTAIL");
        var digest = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        var artifact = new ReviewArtifactEvidenceDto(
            "candidate.verify-3.stdout.log", "text/plain", digest, source.Length,
            Convert.ToBase64String(source));
        var request = Request([artifact, artifact with { Name = "baseline.verify-3.stdout.log" },
            artifact with { Name = "clean-repeat.verify-3.stdout.log" }]);

        var budgeted = ReviewReportArtifactBudget.Apply(request);

        Assert.True(budgeted.ArtifactsReduced);
        Assert.True(ReviewReportArtifactBudget.WireBytes(budgeted) < ReviewReportLimits.MaxRequestBodyBytes);
        Assert.True(ReviewReportArtifactBudget.WireBytes(budgeted) <= ReviewReportLimits.TargetRequestBytes);
        Assert.Equal(3, budgeted.Artifacts.Count(item => item.Sha256 == digest && item.SizeBytes == source.Length));
        var excerpt = Assert.Single(budgeted.Artifacts, item => item.Name == artifact.Name + ".excerpt");
        var bytes = Convert.FromBase64String(Assert.IsType<string>(excerpt.ContentBase64));
        Assert.StartsWith("HEAD", Encoding.UTF8.GetString(bytes));
        Assert.EndsWith("TAIL", Encoding.UTF8.GetString(bytes));
        Assert.Contains($"full bytes={source.Length}; sha256={digest}", Encoding.UTF8.GetString(bytes));
        Assert.Equal(excerpt.Sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal(bytes.Length, excerpt.SizeBytes);
    }

    private static ReviewReportRequest Request(IReadOnlyList<ReviewArtifactEvidenceDto> artifacts)
        => new("runner", "instance", "lease", 1, "report", "ProductFailure", "NewTestFailures",
            "verification failed",
            new ReviewWorkspaceProofDto("example/repo", new string('a', 40), new string('a', 40),
                new string('b', 40), false, false, "workspace", "namespace"),
            new ReviewEnvironmentDto("host", "runner", "instance", "linux", "x64", "10",
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], artifacts, [new ReviewVerdictDto("tests", "block", "NewTestFailures", "failed")]);
}
