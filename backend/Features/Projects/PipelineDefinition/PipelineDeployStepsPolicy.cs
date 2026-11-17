using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

/// <summary>The deploy-step form of Settings > Pipeline: one artifact and one step per declared environment.</summary>
/// <param name="ArtifactType"><c>static</c> or <c>container</c>.</param>
/// <param name="ArtifactPath">The bundle directory of a static artifact, or the Dockerfile of a container artifact.</param>
public sealed record PipelineDeployStepsRequest(
    string? ArtifactType,
    string? ArtifactPath,
    IReadOnlyList<PipelineDeployStepsEnvironment>? Environments,
    string? BaseSha256 = null,
    string? ProposalSha256 = null);

/// <summary>
/// One environment of the form. A static artifact uses <see cref="Directory"/>;
/// a container artifact uses <see cref="ComposeFile"/> and <see cref="Service"/>.
/// </summary>
public sealed record PipelineDeployStepsEnvironment(
    string? Name,
    string? Target,
    string? Directory,
    string? ComposeFile,
    string? Service,
    string? HealthUrl,
    int? HealthTimeoutSeconds);

/// <summary>
/// Proposes the deploy section of a schema 2 project.yml that declares its
/// environments but no artifacts or deploy steps yet (F5). The proposal keeps
/// the current file byte-identical and appends <c>artifacts</c> and <c>deploy</c>
/// with the file's own line ending, so the operator reviews an addition only.
/// The project.yml reader is line based without escapes, so every value must be
/// one plain token; the schema validator then judges the result. Pure: no
/// filesystem, clock, or git.
/// </summary>
public static partial class PipelineDeployStepsPolicy
{
    /// <summary>Whether the form applies: a schema 2 file with environments but no artifacts or deploy steps.</summary>
    public static bool Offered(ProjectDefinitionReadResult current)
        => current.Parsed is { SchemaVersion: 2, Deployment: { } deployment }
           && deployment.Environments.Count > 0 && deployment.Artifacts.Count == 0 && deployment.Deploy.Count == 0;

    public static PipelineMigrationProposal Propose(
        string? currentYaml, ProjectDefinitionReadResult current, PipelineDeployStepsRequest? request)
    {
        if (currentYaml is null)
            return Unavailable("definition-missing", "The project has no .agent-studio/project.yml.");
        if (current.Parsed is not { SchemaVersion: 2 } parsed)
            return Unavailable("schema-2-required", "Write schema 2 to project.yml before adding deploy steps.");
        var deployment = parsed.Deployment;
        if (deployment is null || deployment.Environments.Count == 0)
            return Unavailable("environments-missing", "project.yml declares no environments to deploy to.");
        if (deployment.Artifacts.Count > 0 || deployment.Deploy.Count > 0 || DeploySection().IsMatch(currentYaml))
            return Unavailable("deploy-steps-present",
                "project.yml already declares artifacts or deploy steps. Edit them in the repository.");
        if (request is null)
            return Unavailable("validation", "The deploy step form is required.");

        var type = request.ArtifactType?.Trim();
        if (type is not ("static" or "container"))
            return Unavailable("validation", "Choose a static or container artifact.");
        if (Token(request.ArtifactPath) is not { } artifactPath)
            return Unavailable("validation", type == "static"
                ? "Name the directory your build writes, such as dist."
                : "Name the Dockerfile, such as Dockerfile.");

        var inputs = (request.Environments ?? []).Where(item => item is not null).ToArray();
        var names = deployment.Environments.Select(environment => environment.Name).ToArray();
        var unknown = inputs.Select(item => item.Name?.Trim() ?? "").Where(name => !names.Contains(name, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0)
            return Unavailable("validation", $"project.yml does not declare the environment '{unknown[0]}'.");
        var missing = names.Where(name => inputs.Count(item => item.Name?.Trim() == name) != 1).ToArray();
        if (missing.Length > 0)
            return Unavailable("validation", $"Fill in exactly one deploy step for {string.Join(" and ", missing)}.");

        var ending = currentYaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var yaml = new StringBuilder(currentYaml.TrimEnd('\r', '\n'));
        void Line(string text) => yaml.Append(text).Append(ending);
        yaml.Append(ending);
        Line(string.Empty);
        Line("# Deploy steps written by Agent Studio from the Settings > Pipeline form.");
        var artifact = type == "static" ? "site" : "app";
        Line("artifacts:");
        Line($"  - name: {artifact}");
        Line($"    type: {type}");
        Line(type == "static" ? $"    path: {artifactPath}" : $"    dockerfile: {artifactPath}");
        Line("deploy:");
        foreach (var name in names)
        {
            var input = inputs.Single(item => item.Name?.Trim() == name);
            var label = name == "staging" ? "Staging" : name == "production" ? "Production" : name;
            if (Token(input.Target) is not { } target)
                return Unavailable("validation", $"{label} needs the deploy host id, such as web-vm.");
            if (Token(input.HealthUrl) is not { } healthUrl)
                return Unavailable("validation", $"{label} needs a health URL without spaces, quotes, or '#'.");
            if (input.HealthTimeoutSeconds is not { } timeout)
                return Unavailable("validation", $"{label} needs a health timeout in seconds.");
            Line(type == "static" ? "  - step: static-site" : "  - step: container");
            Line($"    artifact: {artifact}");
            Line($"    target: {target}");
            if (type == "static")
            {
                if (Token(input.Directory) is not { } directory)
                    return Unavailable("validation", $"{label} needs the target directory, such as /srv/www/site.");
                Line($"    directory: {directory}");
            }
            else
            {
                if (Token(input.ComposeFile) is not { } composeFile)
                    return Unavailable("validation", $"{label} needs the compose file, such as /srv/apps/web/compose.yml.");
                if (Token(input.Service) is not { } service)
                    return Unavailable("validation", $"{label} needs the compose service to restart.");
                Line($"    composeFile: {composeFile}");
                Line($"    service: {service}");
            }
            Line($"    healthUrl: {healthUrl}");
            Line($"    healthTimeoutSeconds: {timeout}");
            Line($"    environments: [{name}]");
        }

        var proposed = yaml.ToString();
        return new(true, null, proposed, PipelineMigrationPolicy.Sha256(proposed), []);
    }

    /// <summary>A single plain YAML token, or null when blank or not representable without quoting.</summary>
    private static string? Token(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || !PlainToken().IsMatch(trimmed) ? null : trimmed;
    }

    private static PipelineMigrationProposal Unavailable(string reason, string note)
        => new(false, reason, null, null, [note]);

    [GeneratedRegex(@"\A[A-Za-z0-9._~:/?=&%+@-]{1,512}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainToken();

    [GeneratedRegex(@"(?m)^(?:artifacts|deploy):", RegexOptions.CultureInvariant)]
    private static partial Regex DeploySection();
}
