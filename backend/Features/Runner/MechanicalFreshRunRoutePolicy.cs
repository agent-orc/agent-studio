using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

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
        // Thin adapter (MS-5): the fresh-round floor rule lives in the shared
        // planner; this keeps the prompt read lazy and the spec shape.
        var current = new SelectionRoute(spec.CliType ?? string.Empty, spec.Model, spec.ThinkingLevel);
        var route = ModelSelectionPlanner.QualifyFreshRun(
            freshReason,
            task.ModelExplicit,
            current,
            () =>
            {
                var promptPath = Path.Combine(task.FolderPath, "prompt.md");
                var prompt = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;
                return registry.CorrectnessFloor(task.TaskType, task.Title, prompt);
            },
            registry.Policy.Tiers,
            ModelMetadataRegistry.NormalizeId);
        return ReferenceEquals(route, current)
            ? spec
            : spec with { Model = route.Model, ThinkingLevel = route.ThinkingLevel };
    }
}
