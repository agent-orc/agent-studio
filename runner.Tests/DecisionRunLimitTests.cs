using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DecisionRunLimitTests
{
    [Fact]
    public void Applied_card_limit_reaches_detached_worker_and_mechanical_ceiling_wins()
    {
        var options = new RunnerOptions
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
        var runSpec = new RunSpecDto(RunTimeoutSeconds: 7200);

        var normal = DurableAgentProcess.BuildSpec(options, Path.GetTempPath(), "prompt",
            Path.GetTempPath(), runSpec);
        var mechanical = DurableAgentProcess.BuildSpec(options, Path.GetTempPath(), "prompt",
            Path.GetTempPath(), runSpec, timeoutSeconds: 300);

        Assert.Equal(7200, normal.TimeoutSeconds);
        Assert.Equal(300, mechanical.TimeoutSeconds);
    }
}
