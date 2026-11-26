using Xunit;

namespace AgentStudio.Tests;

public partial class ReviewDecisionOrchestratorCompletionGateTests
{
    [Fact]
    public async Task Fast_local_review_gate_before_integration_requests_compile_only()
    {
        SeedReviewJobWithDone("fast-local-gate", status: "Build completed.\n");
        BuildTestGateRequest? observed = null;
        var gate = new FakeBuildTestGateRunner(request =>
        {
            observed = request;
            return new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 1, "", "passed", true, false);
        });
        var aspect = new CountingAspect();
        var orchestrator = BuildOrchestrator(aspect.Cli, maxReissues: 3, buildGate: gate);
        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);
        Assert.NotNull(observed);
        Assert.False(observed.DeliveryIntegratedAtStart);
        Assert.Equal(TestExecutionLevels.CompileOnly, observed.RequiredTestLevel);
    }
}
