using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

/// <summary>One node of the read-only lifecycle diagram: gate, artifact, or environment phase.</summary>
public sealed record DeployLifecycleNode(
    string Id, string Kind, string Name, IReadOnlyList<string> Items,
    bool Automatic, IReadOnlyList<string> Approval, string? Requires);

/// <summary>
/// The project.yml schema 2 lifecycle as the Deploy area draws it
/// (gate, artifact, then each phase of <c>lifecycles.standard</c>).
/// <see cref="Declared"/> is false when the definition has no deployment section.
/// </summary>
public sealed record ProjectDeployLifecycleView(bool Declared, IReadOnlyList<DeployLifecycleNode> Nodes)
{
    public static ProjectDeployLifecycleView? From(ProjectExecutionDefinition? definition)
    {
        if (definition is null) return null;
        if (definition.Deployment is not { } deployment) return new(false, []);
        var pipeline = deployment.PipelineBuild.Concat(deployment.PipelineTest).ToArray();
        var gate = pipeline.Length > 0
            ? pipeline
            : definition.Commands.Build.Concat(definition.Commands.Test).Concat(definition.Commands.Lint).ToArray();
        var nodes = new List<DeployLifecycleNode>
        {
            new("gate", "gate", "Gate", gate, true, [], null),
            new("artifact", "artifact", "Artifact",
                deployment.Artifacts.Select(DescribeArtifact).ToArray(), true, [], "gate"),
        };
        foreach (var phase in deployment.StandardLifecycle)
        {
            // A step without an environment list runs in every environment.
            var steps = deployment.Deploy
                .Where(step => step.Environments.Count == 0
                    || step.Environments.Contains(phase.Phase, StringComparer.OrdinalIgnoreCase))
                .Select(DescribeStep).ToArray();
            nodes.Add(new(phase.Phase, "environment", phase.Phase, steps, phase.Automatic, phase.Approval,
                phase.Requires ?? "artifact"));
        }
        return new(true, nodes);
    }

    private static string DescribeArtifact(ProjectArtifactDefinition artifact)
    {
        var source = artifact.Path ?? artifact.Dockerfile;
        return source is null ? $"{artifact.Name} ({artifact.Type})" : $"{artifact.Name} ({artifact.Type}, {source})";
    }

    private static string DescribeStep(ProjectDeployStep step)
    {
        var detail = step.Step switch
        {
            "publish-static" or "start-container" => string.Join(" to ",
                new[] { step.Artifact, step.Target }.Where(value => !string.IsNullOrWhiteSpace(value))),
            "run" => step.Run,
            "health" => step.Health,
            "approval" => string.Join(", ", step.Approval),
            _ => null,
        };
        if (step.Step == "publish-static" && !string.IsNullOrWhiteSpace(step.Branch)) detail += $" ({step.Branch})";
        return string.IsNullOrWhiteSpace(detail) ? step.Step : $"{step.Step}: {detail}";
    }
}
