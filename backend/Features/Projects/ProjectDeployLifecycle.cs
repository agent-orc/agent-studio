using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

/// <summary>
/// One deploy step of an environment phase. <see cref="Destination"/> is where
/// the step writes or what it runs: deploy branch, command, or approval roles.
/// </summary>
public sealed record DeployLifecycleStep(
    string Kind, string? Artifact, string? Target, string? Destination, string? Health);

/// <summary>One node of the read-only lifecycle diagram: gate, artifact, or environment phase.</summary>
public sealed record DeployLifecycleNode(
    string Id, string Kind, string Name, IReadOnlyList<string> Items, IReadOnlyList<DeployLifecycleStep> Steps,
    bool Automatic, IReadOnlyList<string> Approval, string? Requires);

/// <summary>
/// The project.yml schema 2 lifecycle as the Deploy area draws it
/// (gate, artifact, then each phase of <c>lifecycles.standard</c>).
/// <see cref="Declared"/> is false when the definition has no deployment section.
/// The Deploy area prefers the AGT-3221 pipeline-definition diagram when the
/// server has it; this projection serves servers without that route.
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
            new("gate", "gate", "Gate", gate, [], true, [], null),
            new("artifact", "artifact", "Artifact",
                deployment.Artifacts.Select(DescribeArtifact).ToArray(), [], true, [], "gate"),
        };
        foreach (var phase in deployment.StandardLifecycle)
        {
            // A step without an environment list runs in every environment.
            var steps = deployment.Deploy
                .Where(step => step.Environments.Count == 0
                    || step.Environments.Contains(phase.Phase, StringComparer.OrdinalIgnoreCase))
                .Select(Step).ToArray();
            nodes.Add(new(phase.Phase, "environment", phase.Phase, [], steps, phase.Automatic, phase.Approval,
                phase.Requires ?? "artifact"));
        }
        return new(true, nodes);
    }

    private static string DescribeArtifact(ProjectArtifactDefinition artifact)
    {
        var source = artifact.Path ?? artifact.Dockerfile;
        return source is null ? $"{artifact.Name} ({artifact.Type})" : $"{artifact.Name} ({artifact.Type}, {source})";
    }

    /// <summary>
    /// Every step kind keeps its artifact and target, so static-site and
    /// container steps (AGT-3221) still show what they deploy where; a kind
    /// this contract does not know is drawn with its name.
    /// </summary>
    private static DeployLifecycleStep Step(ProjectDeployStep step)
    {
        var destination = step.Step switch
        {
            "publish-static" => step.Branch,
            "run" => step.Run,
            "approval" => step.Approval.Count > 0 ? string.Join(", ", step.Approval) : null,
            _ => null,
        };
        return new(step.Step, Blank(step.Artifact), Blank(step.Target), Blank(destination), Blank(step.Health));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
