namespace AgentStudio.Prompts;

/// <summary>
/// Single binding catalogue between project pipeline-step ids and runtime
/// prompt files. Prompt administration, review telemetry, and project-setting
/// writes consume this map so origin and stale calculations cannot diverge.
/// </summary>
public static class PromptPipelineBindings
{
    private static readonly IReadOnlyDictionary<string, string> Bindings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aspect-requirement-fit"] = "review-aspect-requirement-fit.md",
            ["aspect-code-quality"] = "review-aspect-code-quality.md",
            ["aspect-documentation-impact"] = "review-aspect-documentation-impact.md",
            ["aspect-tests-and-evidence"] = "review-aspect-tests-and-evidence.md",
            [PipelineCatalogue.RemoteConceptFitStepId] = "review-aspect-concept-fit.md",
            [PipelineCatalogue.PostAbortReviewStepId] = "post-abort-review.md",
            [PipelineCatalogue.CodeReviewGradeStepId] = "code-review-grade.md",
            [PipelineCatalogue.TaskSpawnerStepId] = "task-spawner-relevance.md",
            [PipelineCatalogue.OrchestratorDecisionStepId] = "orchestrator-review-decision.md",
            [PipelineCatalogue.DriftAdrCodeStepId] = "adr-code-drift.md",
            [PipelineCatalogue.DriftSoftwareArchitectureStepId] = "software-architecture-drift.md",
            [PipelineCatalogue.DriftDocsMarketingStepId] = "docs-marketing-drift.md",
            [PipelineCatalogue.DriftSpecTaskJobStepId] = "spec-task-job-drift.md",
            [PipelineCatalogue.DriftCodePatternStepId] = "code-pattern-drift-review.md",
        };

    public static string? ForStep(string? stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId)) return null;
        var trimmed = stepId.Trim();
        if (Bindings.TryGetValue(trimmed, out var direct)) return direct;

        foreach (var prefix in new[] { "aspect-", "post-", "pre-" })
        {
            if (Bindings.TryGetValue(prefix + trimmed, out var prefixed)) return prefixed;
        }

        return PipelineCatalogue.Standard.AllSteps
            .Append(PipelineCatalogue.AbortReviewStep)
            .FirstOrDefault(step =>
                string.Equals(step.Id, trimmed, StringComparison.OrdinalIgnoreCase))
            ?.PromptTemplate;
    }
}
