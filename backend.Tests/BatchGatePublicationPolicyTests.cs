using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGatePublicationPolicyTests
{
    private const string Base = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Candidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly BatchGateScope Scope = new("p", "r", "develop", "full", "digest", "v1");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static BatchGateSubject Member() => new(
        "task", "p", "r", "develop", "full", "digest", "v1",
        "refs/heads/agent-studio/results/one", Base, "run-1", 1, 1, Base,
        true, true, true, true, false, null, true, Now, 1);

    [Fact]
    public void PublicationRequiresUnmovedBaseCurrentGenerationAndBothLeases()
    {
        var member = Member();
        var digest = BatchGatePolicy.MembershipDigest(Base, Scope, [member]);
        var manifest = new BatchGateManifest("batch", Scope, Base, digest, Now,
            [member], [], [member.TaskKey]);
        var assembly = new BatchGateAssembly("batch", digest, "ref", Candidate,
            [member.TaskKey], [], [], false);
        var run = new BatchGateRunRecord("run", "batch", digest, Base,
            Candidate, "full", "digest", "host", 1, ["test"], Now, "evidence");
        var verdict = new BatchGateRunVerdict("run", "batch", digest,
            Candidate, "digest", "pass", "passed", "evidence", Now);
        BatchPublishDecision Decide(string preTip = Base, bool coordinator = true,
            bool refLease = true, BatchGateSubject? current = null,
            BatchGateRunVerdict? actualVerdict = null)
            => BatchGatePublicationPolicy.Decide(manifest, assembly, run,
                actualVerdict ?? verdict,
                new Dictionary<string, BatchGateSubject> { [member.TaskKey] = current ?? member },
                preTip, coordinator, refLease, true);
        Assert.Equal(BatchPublishDecision.FastForward, Decide());
        Assert.Equal(BatchPublishDecision.StaleBase, Decide(preTip: Candidate));
        Assert.Equal(BatchPublishDecision.LeaseLost, Decide(coordinator: false));
        Assert.Equal(BatchPublishDecision.LeaseLost, Decide(refLease: false));
        Assert.Equal(BatchPublishDecision.Superseded,
            Decide(current: member with { DeliveryEpoch = 2 }));
        Assert.Equal(BatchPublishDecision.UntestedCandidate,
            Decide(actualVerdict: verdict with { TestedCandidateSha = Base }));
        Assert.Equal(BatchPublishDecision.UntestedCandidate,
            BatchGatePublicationPolicy.Decide(manifest, assembly, run, verdict,
                new Dictionary<string, BatchGateSubject> { [member.TaskKey] = member },
                Base, true, true, false));
    }
}
