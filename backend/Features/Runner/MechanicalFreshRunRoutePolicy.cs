using AgentStudio.Pipeline;
using AgentStudio.Shared;

namespace AgentStudio.Runner;

/// <summary>Re-score a fresh round after a resumed mechanical round finds real work.</summary>
public static class MechanicalFreshRunRoutePolicy
{
    public static RunSpecDto Qualify(
        RunSpecDto spec,
        TaskInfo task,
        string? freshReason,
        ModelRoutingPolicyRegistry registry)
    {
        if (freshReason is not ("pending-mechanical-continuation" or "semantic-conflict" or "failed-deterministic-gate"
            or "invalid-session-after-resume" or "resume-token-ceiling"
            or "resume-duration-ceiling" or "resumed-round-inconclusive"))
            return spec;
        if (task.ModelExplicit && !string.IsNullOrWhiteSpace(spec.Model))
            return spec;

        var promptPath = Path.Combine(task.FolderPath, "prompt.md");
        var prompt = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;
        var policyFloor = registry.CorrectnessFloor(task.TaskType, task.Title, prompt);
        var minimumId = freshReason == "semantic-conflict" ? "sol-medium" : "terra-medium";
        var minimum = registry.Policy.Tiers.First(tier => tier.Id == minimumId);
        var floor = policyFloor is not null && policyFloor.Rank > minimum.Rank
            ? policyFloor : minimum;
        if (!string.IsNullOrWhiteSpace(spec.Model)
            && registry.RouteMeetsFloor(spec.Model, spec.ThinkingLevel, floor))
            return spec;

        var anthropic = string.Equals(spec.CliType, "claude", StringComparison.OrdinalIgnoreCase);
        var vendor = anthropic && floor.VendorOverrides.TryGetValue("anthropic", out var overrideRoute)
            ? overrideRoute : null;
        return spec with
        {
            Model = vendor?.Model ?? floor.Model,
            ThinkingLevel = vendor?.ThinkingLevel ?? floor.ThinkingLevel,
        };
    }
}
