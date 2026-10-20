using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

/// <summary>
/// The parsed project.yml pipeline as the settings page draws it:
/// gate, then the artifacts it keeps, then each environment in lifecycle order.
/// Every node carries the definition path it came from so the page can attach
/// validation issues to the node they concern.
/// </summary>
public sealed record PipelineDiagram(
    int SchemaVersion,
    PipelineGateNode Gate,
    IReadOnlyList<PipelineArtifactNode> Artifacts,
    IReadOnlyList<PipelineEnvironmentNode> Environments,
    bool EnvironmentsDefaulted,
    bool LifecycleDefaulted);

/// <param name="Source"><c>pipeline</c> when schema 2 declares its own CI stages, otherwise <c>commands</c>.</param>
public sealed record PipelineGateNode(
    string Source,
    IReadOnlyList<string> Build,
    IReadOnlyList<string> Test,
    IReadOnlyList<string> TestSuites);

public sealed record PipelineArtifactNode(string Path, string Name, string Type, string? Source, string? Image);

public sealed record PipelineEnvironmentNode(
    string Path,
    string Name,
    int Order,
    bool Protected,
    bool Automatic,
    IReadOnlyList<string> Approval,
    string? Requires,
    IReadOnlyList<PipelineStepNode> Steps,
    IReadOnlyList<PipelineVariableNode> Variables);

/// <param name="Destination">Where the step writes: target directory, compose file and service, deploy branch, or command.</param>
public sealed record PipelineStepNode(
    string Path,
    string Kind,
    string? Artifact,
    string? Target,
    string? Destination,
    string? HealthUrl,
    int? HealthTimeoutSeconds);

/// <summary>Secrets appear by name only; plain values are not repeated in the diagram.</summary>
public sealed record PipelineVariableNode(string Path, string Name, bool IsSecret, string? Secret);

public static class PipelineDiagramProjection
{
    public static PipelineDiagram Project(ProjectExecutionDefinition definition)
    {
        var deployment = definition.Deployment;
        var usesPipeline = deployment is not null
            && (deployment.PipelineBuild.Count > 0 || deployment.PipelineTest.Count > 0);
        var gate = new PipelineGateNode(
            usesPipeline ? "pipeline" : "commands",
            usesPipeline ? deployment!.PipelineBuild : definition.Commands.Build,
            usesPipeline ? deployment!.PipelineTest : definition.Commands.Test,
            definition.TestSuites.Select(suite => suite.Id).ToArray());
        if (deployment is null || !deployment.IsConfigured)
            return new(definition.SchemaVersion, gate, [], [], false, false);

        var artifacts = deployment.Artifacts
            .Select((artifact, index) => new PipelineArtifactNode(
                $"artifacts[{index}]", artifact.Name, artifact.Type,
                artifact.Type == "container" ? artifact.Dockerfile : artifact.Path, artifact.Image))
            .ToArray();
        var environments = deployment.Environments
            .Select((environment, order) =>
            {
                var phaseIndex = IndexOf(deployment.StandardLifecycle, environment.Name);
                var phase = phaseIndex < 0 ? null : deployment.StandardLifecycle[phaseIndex];
                return new PipelineEnvironmentNode(
                    deployment.EnvironmentsDefaulted ? "environments" : $"environments[{order}]",
                    environment.Name,
                    order,
                    environment.Protected,
                    phase?.Automatic ?? false,
                    phase?.Approval ?? [],
                    phase?.Requires,
                    Steps(deployment, environment.Name),
                    Variables(deployment, environment.Name));
            })
            .ToArray();
        return new(definition.SchemaVersion, gate, artifacts, environments,
            deployment.EnvironmentsDefaulted, deployment.LifecycleDefaulted);
    }

    private static IReadOnlyList<PipelineStepNode> Steps(ProjectDeploymentDefinition deployment, string environment)
        => deployment.Deploy
            .Select((step, index) => (step, index))
            .Where(item => Applies(item.step.Environments, environment))
            .Select(item => new PipelineStepNode(
                $"deploy[{item.index}]", item.step.Step, item.step.Artifact, item.step.Target,
                Destination(item.step), item.step.HealthUrl ?? item.step.Health, item.step.HealthTimeoutSeconds))
            .ToArray();

    private static IReadOnlyList<PipelineVariableNode> Variables(ProjectDeploymentDefinition deployment, string environment)
        => deployment.Variables
            .Select((variable, index) => (variable, index))
            .Where(item => Applies(item.variable.Environments, environment))
            .Select(item => new PipelineVariableNode(
                $"variables[{item.index}]", item.variable.Name, item.variable.Secret is not null, item.variable.Secret))
            .ToArray();

    private static string? Destination(ProjectDeployStep step) => step.Step switch
    {
        "static-site" => step.Directory,
        "container" when step.ComposeFile is not null => $"{step.ComposeFile} ({step.Service})",
        "publish-static" => step.Branch,
        "run" => step.Run,
        "approval" => string.Join(", ", step.Approval),
        _ => null,
    };

    private static bool Applies(IReadOnlyList<string> scope, string environment)
        => scope.Count == 0 || scope.Contains(environment, StringComparer.Ordinal);

    private static int IndexOf(IReadOnlyList<ProjectLifecyclePhase> phases, string name)
    {
        for (var index = 0; index < phases.Count; index++)
            if (string.Equals(phases[index].Phase, name, StringComparison.Ordinal)) return index;
        return -1;
    }
}
