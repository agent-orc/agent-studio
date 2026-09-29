using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2932: a typed prelaunch infrastructure release is routed to the failure
/// budget and never to the lost-worker continuation, because no agent process
/// ever held the authority that a lost worker lost.
/// </summary>
public sealed class RemoteLeaseReleasePolicyTests
{
    [Theory]
    [InlineData("runner-environment-preparation-failed", RemoteLeaseReleaseRoute.PrelaunchInfrastructure)]
    [InlineData("runner-salvage-failed", RemoteLeaseReleaseRoute.PrelaunchInfrastructure)]
    [InlineData("runner-results-handling-failed", RemoteLeaseReleaseRoute.PrelaunchInfrastructure)]
    [InlineData("worker-lost", RemoteLeaseReleaseRoute.LostWorker)]
    [InlineData(" Worker-Lost ", RemoteLeaseReleaseRoute.LostWorker)]
    [InlineData("released", RemoteLeaseReleaseRoute.Plain)]
    [InlineData("", RemoteLeaseReleaseRoute.Plain)]
    [InlineData(null, RemoteLeaseReleaseRoute.Plain)]
    public void Classifies_each_release_outcome_into_exactly_one_route(
        string? outcome, RemoteLeaseReleaseRoute expected)
        => Assert.Equal(expected, RemoteLeaseReleasePolicy.Classify(outcome));

    [Fact]
    public void No_prelaunch_infrastructure_code_is_a_lost_worker_release()
    {
        foreach (var code in RemoteLeaseReleasePolicy.PrelaunchInfrastructureOutcomes)
        {
            Assert.Equal(RemoteLeaseReleaseRoute.PrelaunchInfrastructure, RemoteLeaseReleasePolicy.Classify(code));
            Assert.False(LostWorkerContinuationPolicy.IsLostWorkerRelease(code));
        }
    }
}
