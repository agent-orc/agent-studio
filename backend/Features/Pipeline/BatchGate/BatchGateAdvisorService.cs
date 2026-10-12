using System.Text.Json;
using AgentStudio.Cli;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.Pipeline;

public sealed class BatchGateAdvisorService
{
    private readonly CliOneShotRegistry _oneShots;
    private readonly PipelineStepEconomyAdvisor _economy;
    private readonly RuntimePromptService _prompts;
    private readonly ILogger<BatchGateAdvisorService> _logger;

    public BatchGateAdvisorService(CliOneShotRegistry oneShots,
        PipelineStepEconomyAdvisor economy, RuntimePromptService prompts,
        ILogger<BatchGateAdvisorService> logger)
    {
        _oneShots = oneShots;
        _economy = economy;
        _prompts = prompts;
        _logger = logger;
    }

    public async Task<BatchPlanDecision> AdviseAsync(string project,
        ProjectSettings settings, BatchPlanInput snapshot,
        BatchPlanDecision deterministic, CancellationToken ct)
    {
        var options = settings.BatchGate;
        var advisor = options.Advisor ?? new BatchGateAdvisorOptions();
        if (advisor.Mode == "off" || deterministic.Decision != "close") return deterministic;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(advisor.TimeoutSeconds));
        var model = advisor.Model
            ?? ModelMetadataRegistry.DefaultForCli(advisor.CliType)
            ?? PipelineStepModelDefaults.SupportModel;
        try
        {
            if (advisor.Model is null && advisor.CliType == CliTypes.Codex)
            {
                var economy = await _economy.SuggestModelAsync(settings,
                    "batch-plan-advisor", deadline.Token, forceEconomy: true)
                    .ConfigureAwait(false);
                model = economy?.Model ?? model;
            }
            var cli = _oneShots.Get(advisor.CliType)
                ?? throw new InvalidOperationException("Batch advisor CLI unavailable.");
            var input = JsonSerializer.Serialize(new
            {
                deterministic, snapshot.Waiting, snapshot.Upcoming,
                snapshot.MinimumSize, snapshot.MaximumSize,
                snapshot.LookAheadMinutes, snapshot.RiskPolicy, snapshot.Overlap,
            });
            var prompt = _prompts.Render("batch-plan-advisor.md",
                new Dictionary<string, string?> { ["batch_input"] = input },
                new PromptCallContext(project, "batch-plan-advisor", model));
            var result = await cli.RunAsync(new CliOneShotRequest(advisor.CliType, model, prompt)
            {
                Timeout = TimeSpan.FromSeconds(advisor.TimeoutSeconds),
                Source = "batch-plan-advisor",
                Project = project,
                StepId = "batch-plan-advisor",
                ExtraArgs = advisor.CliType == CliTypes.Claude
                    ? ["--tools", "", "--max-budget-usd", "0.10"] : null,
            }, deadline.Token).ConfigureAwait(false);
            if (!result.Ok) throw new InvalidDataException(result.Error ?? "Batch advisor failed.");
            var answer = (result.ParsedText ?? result.Stdout ?? "").Trim();
            if (!BatchGateAdvisorPolicy.TryApply(answer, deterministic,
                    snapshot.Waiting.Select(card => card.TaskKey).ToArray(),
                    options.MinimumSize, options.MaximumSize, snapshot.NowUtc,
                    options.LookAheadMinutes, out var adjusted, out var error))
                throw new InvalidDataException(error);
            var verdict = JsonSerializer.Deserialize<BatchAdvisorAnswer>(answer,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            return (advisor.Mode == "enforce" ? adjusted : deterministic) with
            {
                Advisor = new BatchAdvisorView(advisor.Mode, verdict.Verdict,
                    verdict.Reasons, result.EffectiveModel ?? model, DateTimeOffset.UtcNow),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "batch-plan-advisor-fallback project={Project}", project);
            return deterministic with
            {
                Advisor = new BatchAdvisorView(advisor.Mode, "fallback",
                    [ex is OperationCanceledException ? "advisor deadline" : ex.Message],
                    model, DateTimeOffset.UtcNow),
            };
        }
    }
}
