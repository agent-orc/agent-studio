using AgentStudio.Git;
using AgentStudio.Runner;
using AgentStudio.OrchestrationDecisions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteFenceRefusalContinuationTests
{
    private const string BaseSha = "eeed50650a000000000000000000000000000000";
    private const string ResultSha = "0c0a1a8e47000000000000000000000000000000";
    private const string ResultRef = "refs/heads/agent-studio/results/run_next/fence-2/result";

    [Fact]
    public void Agt3245_disproved_ancestry_requeues_on_the_retained_result()
    {
        var refusal = new RemoteDeliveryCommitRange(false, null, null, ResultSha, [],
            $"Fenced delivery base {BaseSha} is not an ancestor of result {ResultSha}:",
            DeliveryVerificationStatus.ShaMismatch);

        Assert.Equal(DeliveryFenceRefusalAction.RequeueOnResult,
            DeliveryFenceContinuationPolicy.Decide(
                new DeliveryFenceRefusalFacts(refusal.IsDisproved, refusal.Warning,
                    BaseSha, ResultSha, ResultRef)));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));
        var next = RemoteFenceRefusalContinuation.Base(
            ResultRef, ResultSha, "run_next", clock.GetUtcNow().UtcDateTime);
        Assert.Equal(ResultRef, next.Ref);
        Assert.Equal(ResultSha, next.CommitSha);
        Assert.Contains(BaseSha, RemoteFenceRefusalContinuation.Message(BaseSha, ResultSha));
        Assert.Contains(ResultSha, RemoteFenceRefusalContinuation.Message(BaseSha, ResultSha));
    }

    [Fact]
    public void Other_disproved_deliveries_keep_their_existing_escalation_path()
    {
        var mismatch = new RemoteDeliveryCommitRange(false, null, null, ResultSha, [],
            "Fenced delivery mismatch", DeliveryVerificationStatus.ShaMismatch);
        Assert.Equal(DeliveryFenceRefusalAction.Escalate,
            DeliveryFenceContinuationPolicy.Decide(
                new DeliveryFenceRefusalFacts(mismatch.IsDisproved, mismatch.Warning,
                    BaseSha, ResultSha, ResultRef)));
    }

    [Fact]
    public void Refusal_note_names_both_commits_and_is_idempotent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fence-refusal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            RemoteFenceRefusalContinuation.AppendNote(folder, "run_next", BaseSha, ResultSha, ResultRef);
            RemoteFenceRefusalContinuation.AppendNote(folder, "run_next", BaseSha, ResultSha, ResultRef);
            foreach (var file in new[] { "status.md", "prompt.md" })
            {
                var text = File.ReadAllText(Path.Combine(folder, file));
                Assert.Equal(1, text.Split("agent-studio:fence-refusal-continuation:").Length - 1);
                Assert.Contains(BaseSha, text);
                Assert.Contains(ResultSha, text);
                Assert.Contains(ResultRef, text);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
