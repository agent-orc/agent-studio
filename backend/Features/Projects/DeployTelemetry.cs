using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

/// <summary>
/// What a container deploy job needs to connect its workload to the Studio
/// OTLP intake (M5). The queue stores the secret name and protocol; the claim
/// adds the endpoint the host reaches Studio at and the resource attributes of
/// the release. The ingest token itself stays in the secret provider.
/// </summary>
public sealed record DeployJobTelemetry(string SecretName, string Protocol)
{
    public string? Endpoint { get; init; }
    public string? ResourceAttributes { get; init; }
}

/// <summary>Which deploy jobs get telemetry, and what a repository may not declare next to it.</summary>
public static class DeployTelemetryPolicy
{
    /// <summary>
    /// The telemetry of a job for <paramref name="declaration"/>: only container
    /// deploys that a deploy host runs get one. The run zone (edge hosting) and
    /// static sites have no workload environment to set.
    /// </summary>
    public static DeployJobTelemetry? For(EnvironmentDeclaration declaration)
        => declaration.Telemetry && declaration.Hosting is null
           && ProjectEnvironmentService.ArtifactTemplate(declaration.Template, declaration.SshSteps) == "container"
            ? new DeployJobTelemetry(DeployTelemetryVocabulary.SecretName, DeployTelemetryVocabulary.Protocol) : null;

    /// <summary>A repository must not set the variables Studio owns: they would send the token elsewhere.</summary>
    public static void RequireNoDeclaredVariables(EnvironmentDeclaration declaration)
    {
        if (!declaration.Telemetry) return;
        if (declaration.Variables.FirstOrDefault(item =>
                DeployTelemetryVocabulary.Variables.Contains(item.Name, StringComparer.OrdinalIgnoreCase)) is { } clash)
            throw new JsonException($"Schema 2 {declaration.Name} enables telemetry, so Studio sets {clash.Name}; remove the variable.");
    }
}
