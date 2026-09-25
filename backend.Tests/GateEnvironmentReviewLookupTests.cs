using System.Globalization;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-2880 review facts read through persisted attempt authority.</summary>
public sealed class GateEnvironmentReviewLookupTests
{
    private const string DeliverySha = "3577cfa1452274c845fc36efd72ad9ffd7712bdb";

    [Fact]
    public void PersistedAGT2880Projection_MatchesTheExactPassedDelivery()
    {
        var root = Path.Combine(Path.GetTempPath(), "gate-review-lookup-" + Guid.NewGuid().ToString("N"));
        var metadata = Path.Combine(root, ".metadata");
        Directory.CreateDirectory(metadata);
        try
        {
            // This compact archive uses the identity, subject, verdict, and
            // terminal facts observed in AGT-2880's attempt projection. The
            // authority store persists enums numerically; the API names them.
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "gate-environment-retry",
                    "agt-2880-review-archive.json"),
                Path.Combine(metadata, "attempt-authority.archive-2026-09-25.json"));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["TaskRepository"] = root }).Build();
            var authority = new AttemptAuthorityService(
                configuration, NullLogger<AttemptAuthorityService>.Instance);
            var projection = authority.GetTaskProjection("AGT-2880", includeArchived: true);

            Assert.Equal("AGT-2880", projection.TaskKey);
            Assert.Empty(authority.GetTaskProjection("AGT-2880").ReviewAttempts);
            var lookup = GateEnvironmentRetryService.MatchReview(projection, DeliverySha);
            Assert.True(lookup.Passed);
            Assert.Equal("review_38d945dc04a04ea0bd1aa4cab34f6b20", lookup.LatestAttempt?.Id);
            Assert.Equal(ReviewTerminalOutcome.Pass, lookup.LatestAttempt?.Outcome);
            Assert.Equal(DateTime.Parse("2026-09-25T07:57:25.5005712Z",
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), lookup.LatestAttempt?.TerminalAt);

            Assert.False(GateEnvironmentRetryService.MatchReview(
                projection, DeliverySha.ToUpperInvariant()).Passed);
            Assert.False(GateEnvironmentRetryService.MatchReview(
                projection, DeliverySha[..^1] + "c").Passed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
