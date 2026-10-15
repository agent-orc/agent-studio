using System.Text.Json;
using AgentStudio.Cli;
using AgentStudio.Runner;
using AgentStudio.OrchestrationDecisions;
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
        var advisor = settings.BatchGate.Advisor ?? new BatchGateAdvisorOptions();
        if (advisor.Mode == "off" || deterministic.Decision != "close") return deterministic;
        var facts = new BatchPlanFacts(project, "", snapshot, null, advisor.Mode);
        var reply = await AskAsync(project, settings, snapshot, deterministic, ct).ConfigureAwait(false);
        var advised = BatchPlanDecisionPolicy.Advise(facts, deterministic, reply);
        if (reply.Failure is null && advised.Advisor is { Verdict: "fallback" } rejected)
            _logger.LogWarning("batch-plan-advisor-fallback project={Project} reason={Reason}",
                project, string.Join("; ", rejected.Reasons));
        return advised;
    }

    /// <summary>Runs the advisor model call. Policy validation stays in the shared library.</summary>
    public async Task<BatchAdvisorReply> AskAsync(string project,
        ProjectSettings settings, BatchPlanInput snapshot,
        BatchPlanDecision deterministic, CancellationToken ct)
    {
        var advisor = settings.BatchGate.Advisor ?? new BatchGateAdvisorOptions();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(advisor.TimeoutSeconds));
        var model = StaticModel(advisor);
        try
        {
            model = await ResolveModelAsync(settings, deadline.Token).ConfigureAwait(false);
            var cli = _oneShots.Get(advisor.CliType)
                ?? throw new InvalidOperationException("Batch advisor CLI unavailable.");
            var prompt = Render(project, model,
                BatchPlanDecisionPolicy.AdvisorInput(snapshot, deterministic));
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
            var reply = new BatchAdvisorReply((result.ParsedText ?? result.Stdout ?? "").Trim(),
                result.EffectiveModel ?? model, DateTimeOffset.UtcNow);
            return reply;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "batch-plan-advisor-fallback project={Project}", project);
            return new BatchAdvisorReply(null, model, DateTimeOffset.UtcNow,
                ex is OperationCanceledException ? "advisor deadline" : ex.Message);
        }
    }

    /// <summary>
    /// Advisor request for an engine-owned decision. The prompt keeps
    /// <see cref="BatchPlanDecisionPolicy.EngineInputMarker"/> because the engine computes the
    /// deterministic plan that the input carries. The request names the CLI type and the model
    /// this authority resolved, so the engine calls the same provider and model as the monolith.
    /// </summary>
    public async Task<BatchAdvisorRequest?> EngineRequestAsync(string project, ProjectSettings settings,
        CancellationToken ct)
    {
        var advisor = settings.BatchGate.Advisor ?? new BatchGateAdvisorOptions();
        if (advisor.Mode == "off") return null;
        var model = await ResolveModelAsync(settings, ct).ConfigureAwait(false);
        return new BatchAdvisorRequest(Render(project, model, BatchPlanDecisionPolicy.EngineInputMarker),
            advisor.CliType, model, advisor.TimeoutSeconds);
    }

    /// <summary>Effective advisor model: the configured one, else the economy pick for Codex, else the CLI default.</summary>
    private async Task<string> ResolveModelAsync(ProjectSettings settings, CancellationToken ct)
    {
        var advisor = settings.BatchGate.Advisor ?? new BatchGateAdvisorOptions();
        var model = StaticModel(advisor);
        if (advisor.Model is null && advisor.CliType == CliTypes.Codex)
        {
            var economy = await _economy.SuggestModelAsync(settings,
                "batch-plan-advisor", ct, forceEconomy: true).ConfigureAwait(false);
            model = economy?.Model ?? model;
        }
        return model;
    }

    private static string StaticModel(BatchGateAdvisorOptions advisor)
        => advisor.Model
           ?? ModelMetadataRegistry.DefaultForCli(advisor.CliType)
           ?? PipelineStepModelDefaults.SupportModel;

    private string Render(string project, string model, string input)
        => _prompts.Render("batch-plan-advisor.md",
            new Dictionary<string, string?> { ["batch_input"] = input },
            new PromptCallContext(project, "batch-plan-advisor", model));
}
