using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OrchestratorChatPlannedRouteTests
{
    [Fact]
    public async Task Unreachable_remote_route_uses_planned_claude_instead_of_codex()
    {
        var runner = new RecordingRunner();

        await OrchestratorChatService.RunLocalPlannedRouteAsync(
            runner, CliTypes.Claude, ModelIds.ClaudeOpus5, "high",
            "prompt", "/tmp", "project", "/tmp/project", CancellationToken.None);

        Assert.Equal(CliTypes.Claude, runner.CliCalled);
        Assert.Null(runner.CodexModel);
        Assert.Equal(ModelIds.ClaudeOpus5, runner.ModelCalled);
    }

    private sealed class RecordingRunner() : OrchestratorRunner(NullLogger<OrchestratorRunner>.Instance)
    {
        public string? CliCalled { get; private set; }
        public string? ModelCalled { get; private set; }
        public string? CodexModel { get; private set; }

        public override Task<OrchestratorDecisionResult> DecideCodexAsync(
            string prompt, string model, string? thinkingLevel, string workingDirectory,
            CancellationToken ct = default, string? projectName = null, string? watchPath = null)
        {
            CodexModel = model;
            return Task.FromResult(new OrchestratorDecisionResult(true, "ok", model, null, null, null));
        }

        public override Task<OrchestratorDecisionResult> DecideWithCliAsync(
            string cliType, string prompt, string? model, string? thinkingLevel,
            string workingDirectory, CancellationToken ct = default)
        {
            CliCalled = cliType;
            ModelCalled = model;
            return Task.FromResult(new OrchestratorDecisionResult(true, "ok", model ?? "", null, null, null));
        }
    }
}
