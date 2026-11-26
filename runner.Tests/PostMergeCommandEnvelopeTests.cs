using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class PostMergeCommandEnvelopeTests
{
    [Fact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public void Missing_delegation_is_recorded_without_refusing_the_command()
    {
        using var envelope = WorkerCgroup.PrepareTestCommand(TestEnvelope.EmptyEnvironment, _ => { });
        var fingerprint = envelope.Fingerprint(TestEnvelope.EmptyEnvironment, "suite=1");
        Assert.Contains(fingerprint.CgroupKind, new[] { "worker", "unavailable", "none" });
        Assert.Equal(TestEnvelope.ProcessorCount, fingerprint.ProcessorCount);
        Assert.Equal(fingerprint.CgroupKind == "worker" ? TestEnvelope.CpuMax : null, fingerprint.CpuMax);
        var home = envelope.Environment["HOME"]!;
        Assert.True(Directory.Exists(home));
        envelope.Dispose();
        Assert.False(Directory.Exists(home));
    }
}
