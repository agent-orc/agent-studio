using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateCarryPolicyTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing-replay", false)]
    [InlineData("missing-verdict", false)]
    [InlineData("failed-verdict", false)]
    [InlineData("rejected-replay", false)]
    [InlineData("conflicted-replay", false)]
    [InlineData("wrong-result", false)]
    [InlineData("wrong-member", false)]
    [InlineData("wrong-replay-batch", false)]
    [InlineData("wrong-replay-digest", false)]
    [InlineData("missing-replay-tip", false)]
    [InlineData("wrong-run-batch", false)]
    [InlineData("wrong-run-digest", false)]
    [InlineData("wrong-base", false)]
    [InlineData("wrong-profile", false)]
    [InlineData("wrong-profile-digest", false)]
    [InlineData("invalid-candidate", false)]
    [InlineData("wrong-verdict-batch", false)]
    [InlineData("wrong-verdict-run", false)]
    [InlineData("wrong-verdict-digest", false)]
    [InlineData("wrong-tested-candidate", false)]
    [InlineData("wrong-verdict-profile", false)]
    [InlineData("wrong-evidence", false)]
    public void Only_a_matching_passed_run_and_admitted_replay_prove_carry(string change, bool expected)
    {
        // No time is sampled: timestamps are opaque stored facts for this policy.
        var at = DateTimeOffset.UnixEpoch;
        var original = new string('a', 40);
        var candidate = new string('b', 40);
        var scope = new BatchGateScope("project", "repo", "develop", "full", "profile-digest", "v1");
        var member = new BatchGateSubject("task", "project", "repo", "develop", "full", "profile-digest", "v1",
            "refs/heads/task", original, "attempt", 1, 1, original,
            true, true, true, true, false, null, false, at, 1);
        var manifest = new BatchGateManifest("batch", scope, original, "membership", at, [member], [], [member.TaskKey]);
        BatchGateReplayRecord? replay = new("batch", "membership", "task", original, original, candidate,
            [], [], "admitted", at);
        var run = new BatchGateRunRecord("run", "batch", "membership", original, candidate,
            "full", "profile-digest", "host", 1, ["dotnet test"], at, "evidence");
        BatchGateRunVerdict? verdict = new("run", "batch", "membership", candidate,
            "profile-digest", "pass", "passed", "evidence", at);
        switch (change)
        {
            case "missing-replay": replay = null; break;
            case "missing-verdict": verdict = null; break;
            case "failed-verdict": verdict = verdict with { Outcome = "fail" }; break;
            case "rejected-replay": replay = replay with { Outcome = "rejected" }; break;
            case "conflicted-replay": replay = replay with { ConflictedFiles = ["file.cs"] }; break;
            case "wrong-result": replay = replay with { OriginalResultSha = candidate }; break;
            case "wrong-member": replay = replay with { TaskKey = "other" }; break;
            case "wrong-replay-batch": replay = replay with { BatchId = "other" }; break;
            case "wrong-replay-digest": replay = replay with { MembershipDigest = "other" }; break;
            case "missing-replay-tip": replay = replay with { TipAfterSha = null }; break;
            case "wrong-run-batch": run = run with { BatchId = "other" }; break;
            case "wrong-run-digest": run = run with { MembershipDigest = "other" }; break;
            case "wrong-base": run = run with { BaseSha = candidate }; break;
            case "wrong-profile": run = run with { GateProfile = "other" }; break;
            case "wrong-profile-digest": run = run with { GateProfileDigest = "other" }; break;
            case "invalid-candidate": run = run with { CandidateSha = "invalid" }; break;
            case "wrong-verdict-batch": verdict = verdict with { BatchId = "other" }; break;
            case "wrong-verdict-run": verdict = verdict with { BatchRunId = "other" }; break;
            case "wrong-verdict-digest": verdict = verdict with { MembershipDigest = "other" }; break;
            case "wrong-tested-candidate": verdict = verdict with { TestedCandidateSha = original }; break;
            case "wrong-verdict-profile": verdict = verdict with { GateProfileDigest = "other" }; break;
            case "wrong-evidence": verdict = verdict with { EvidencePath = "other" }; break;
        }

        Assert.Equal(expected, BatchGateCarryPolicy.Accepts(manifest, member, replay, run, verdict));
    }
}
