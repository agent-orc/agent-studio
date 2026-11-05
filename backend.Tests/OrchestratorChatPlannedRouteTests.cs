using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Cli;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OrchestratorChatPlannedRouteTests
{
    [Fact]
    public async Task Local_chat_selects_fallback_before_dispatching()
    {
        var runner = new RecordingRunner();
        var selection = new FallbackSelection();

        var result = await OrchestratorChatService.RunLocalSelectedRouteAsync(
            runner, selection, ModelIds.Gpt56Sol, "high", "prompt", "/tmp",
            "project", "/tmp/project", CancellationToken.None);

        Assert.Equal(SelectionConsumers.Chat, selection.Call?.Request.Consumer);
        Assert.Equal(CliTypes.Claude, runner.CliCalled);
        Assert.Equal(ModelIds.ClaudeOpus5, runner.ModelCalled);
        Assert.Null(runner.CodexModel);
        Assert.True(result.QuotaFallback);
        Assert.Equal(ModelIds.Gpt56Sol, result.ConfiguredModel);
    }

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

    private sealed class FallbackSelection : IModelSelectionService
    {
        public ModelSelectionCall? Call { get; private set; }

        public ModelSelectionDecision Select(ModelSelectionCall call)
        {
            Call = call;
            var primary = new SelectionRoute(CliTypes.Codex, ModelIds.Gpt56Sol, "high");
            var fallback = new SelectionRoute(CliTypes.Claude, ModelIds.ClaudeOpus5, "high");
            var result = new SelectionResult
            {
                Outcome = SelectionOutcome.Fallback,
                Route = fallback,
                Primary = primary,
                Rule = SelectionRules.QuotaCap,
                Reason = "quota fallback",
            };
            return new ModelSelectionDecision(result, QuotaAdmissionPlan.From(result));
        }

        public bool IsOperatorSwitchActive(string? cliType) => false;
    }
}
