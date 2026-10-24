using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DecisionRunLimitTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(7200)]
    public void Applied_card_limit_reaches_normal_worker(int cardSeconds)
    {
        var runSpec = new RunSpecDto(RunTimeoutSeconds: cardSeconds);

        var normal = DurableAgentProcess.BuildSpec(Options(), Path.GetTempPath(), "prompt",
            Path.GetTempPath(), runSpec,
            timeoutSeconds: RemoteTaskRunner.EffectiveRunTimeoutSeconds(runSpec, mechanicalResume: false));
        Assert.Equal(cardSeconds, normal.TimeoutSeconds);
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(7200, 300)]
    public void Mechanical_resume_honors_cut_below_safety_ceiling(int cardSeconds, int expectedSeconds)
    {
        var spec = new RunSpecDto(RunTimeoutSeconds: cardSeconds);
        var mechanical = DurableAgentProcess.BuildSpec(Options(), Path.GetTempPath(), "prompt",
            Path.GetTempPath(), spec,
            timeoutSeconds: RemoteTaskRunner.EffectiveRunTimeoutSeconds(spec, mechanicalResume: true));
        Assert.Equal(expectedSeconds, mechanical.TimeoutSeconds);
    }

    private static RunnerOptions Options() => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "runner-test",
        RunnerName = "runner-test",
        Hostname = "host-test",
        BackendName = "test",
        WorkDir = Path.GetTempPath(),
        BaseBranch = "main",
        CliType = "codex",
        CodexCliBin = "codex",
        RunTimeoutSeconds = 3600,
    };
}
