using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DurableAgentProcessTests
{
    [Fact]
    public void Terminal_result_wins_when_worker_exits_between_discovery_checks()
    {
        var terminal = new DetachedJobResult(
            0,
            "before-restart\n[[TASK_DONE]]\n",
            "",
            false,
            DateTime.UtcNow);
        var resultReads = 0;

        var observation = DurableAgentProcess.InspectForReattach(
            () => ++resultReads == 1 ? null : terminal,
            () => (false, "process has exited"));

        Assert.False(observation.IsLive);
        Assert.Same(terminal, observation.Result);
        Assert.Equal("durable result ready", observation.Detail);
        Assert.Equal(2, resultReads);
    }
}
