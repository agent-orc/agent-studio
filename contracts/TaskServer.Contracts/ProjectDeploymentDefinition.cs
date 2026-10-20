using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

public sealed record ProjectArtifactDefinition(string Name, string Type, string? Path, string? Dockerfile)
{
    /// <summary>Registry repository a container artifact is pushed to and pulled from.</summary>
    public string? Image { get; init; }
}

/// <summary>
/// One ordered deployment environment. A protected environment accepts a deploy,
/// redeploy, or rollback only after an Owner or Maintainer approval.
/// </summary>
public sealed record ProjectEnvironmentDefinition(string Name, bool Protected);

public sealed record ProjectLifecyclePhase(string Phase, bool Automatic, IReadOnlyList<string> Approval, string? Requires);

public sealed record ProjectDeployStep(
    string Step, string? Artifact, string? Target, string? Branch, string? Run,
    string? Health, IReadOnlyList<string> Approval, IReadOnlyList<string> Environments)
{
    /// <summary>static-site: absolute directory on the SSH target that receives the bundle.</summary>
    public string? Directory { get; init; }
    /// <summary>container: absolute compose file on the SSH target.</summary>
    public string? ComposeFile { get; init; }
    /// <summary>container: compose service that is pulled and restarted.</summary>
    public string? Service { get; init; }
    public string? HealthUrl { get; init; }
    public int? HealthTimeoutSeconds { get; init; }
}

/// <summary>
/// A deploy variable. Plain values live in the file; secrets are named only and
/// resolved by the secret provider for the environment at deploy time.
/// </summary>
public sealed record ProjectVariableDefinition(
    string Name, string? Value, string? Secret, IReadOnlyList<string> Environments);

/// <summary>
/// Deploy hosts registered for one project, per P1 environment. The backend
/// builds it from the AGT-3176 declarations under docs/deployments/environments,
/// whose host ids also address the deploy queue. With a registry the validator
/// checks every static-site and container target against it; without one, as in
/// preparation, which reads only the checkout, it checks the target's shape.
/// </summary>
public sealed record ProjectDeployHostRegistry(IReadOnlyDictionary<string, IReadOnlyList<string>> HostsByEnvironment)
{
    public IReadOnlyList<string> HostsFor(string environment)
        => HostsByEnvironment.TryGetValue(environment, out var hosts) ? hosts : [];
}

public sealed record ProjectDeploymentDefinition(
    IReadOnlyList<string> PipelineBuild,
    IReadOnlyList<string> PipelineTest,
    IReadOnlyList<ProjectArtifactDefinition> Artifacts,
    IReadOnlyList<ProjectEnvironmentDefinition> Environments,
    IReadOnlyList<ProjectLifecyclePhase> StandardLifecycle,
    IReadOnlyList<ProjectDeployStep> Deploy)
{
    public IReadOnlyList<ProjectVariableDefinition> Variables { get; init; } = [];
    /// <summary>True when the file omits environments and the P1 default applies.</summary>
    public bool EnvironmentsDefaulted { get; init; }
    /// <summary>True when the file omits lifecycles and the P1 standard lifecycle applies.</summary>
    public bool LifecycleDefaulted { get; init; }

    public bool IsConfigured => Artifacts.Count > 0 || Environments.Count > 0
        || StandardLifecycle.Count > 0 || Deploy.Count > 0 || Variables.Count > 0;
}

/// <summary>
/// The P1 vocabulary of project.yml schema 2. The reader, the validator, and the
/// sync test against docs/app/schemas/project-execution.schema.json share it.
/// </summary>
public static class ProjectDeploymentVocabulary
{
    public static readonly IReadOnlyList<string> Sections =
        ["pipeline", "artifacts", "environments", "lifecycles", "deploy", "variables"];
    public static readonly IReadOnlyList<string> ArtifactTypes = ["static", "container"];
    public static readonly IReadOnlyList<string> ArtifactKeys = ["name", "type", "path", "dockerfile", "image"];
    public static readonly IReadOnlyList<string> EnvironmentNames = ["staging", "production"];
    public static readonly IReadOnlyList<string> EnvironmentKeys = ["name", "protected"];
    public static readonly IReadOnlyList<string> LifecyclePhaseKeys = ["phase", "automatic", "approval", "requires"];
    public static readonly IReadOnlyList<string> ApprovalRoles = ["Owner", "Maintainer"];
    public static readonly IReadOnlyList<string> VariableKeys = ["name", "value", "secret", "environments"];
    public static readonly IReadOnlyList<string> StepKinds =
        ["static-site", "container", "publish-static", "start-container", "run", "health", "approval"];
    public const int MaxHealthTimeoutSeconds = 900;

    /// <summary>Artifact names, deploy host ids, and compose services; the JSON schema's <c>identifier</c>.</summary>
    public const string IdentifierPattern = "^[a-z0-9][a-z0-9._-]{0,63}$";

    /// <summary>A secret-provider entry name; the JSON schema's <c>variables[].secret</c>.</summary>
    public const string SecretReferencePattern = "^[A-Za-z][A-Za-z0-9._-]{0,63}$";

    /// <summary>
    /// The P1 base directory of every SSH destination. A later executor hands
    /// these paths to the remote shell and to <c>rsync --delete</c>, so they stay
    /// strictly below this base and use a shell-inert alphabet.
    /// </summary>
    public const string RemoteBaseDirectory = "/srv";

    /// <summary>
    /// <c>directory</c> and <c>composeFile</c>: below <see cref="RemoteBaseDirectory"/>,
    /// segments of letters, digits, '.', '_' and '-' that do not start with '-',
    /// and no '.', '..' or empty segment. The JSON schema's <c>remotePath</c> uses
    /// this exact pattern; ECMA-262 and .NET read it alike.
    /// </summary>
    public const string RemotePathPattern =
        "^" + RemoteBaseDirectory + @"(?:/(?!\.{1,2}(?:/|$))[A-Za-z0-9._][A-Za-z0-9._-]*)+$";

    /// <summary>The suffix a <c>composeFile</c> needs on top of <see cref="RemotePathPattern"/>.</summary>
    public const string ComposeFilePattern = @"\.ya?ml$";

    /// <summary>
    /// Artifact <c>path</c> and <c>dockerfile</c>: repository-relative with the same
    /// segment alphabet as <see cref="RemotePathPattern"/>; a lone '.' names the
    /// repository root as a build context. The JSON schema's <c>artifactPath</c>
    /// uses this exact pattern.
    /// </summary>
    public const string ArtifactPathPattern =
        @"^(?:\.|(?!\.{1,2}(?:/|$))[A-Za-z0-9._][A-Za-z0-9._-]*(?:/(?!\.{1,2}(?:/|$))[A-Za-z0-9._][A-Za-z0-9._-]*)*)$";

    /// <summary>Allowed keys per step kind. The AGT-3198 kinds keep their shared key set.</summary>
    public static IReadOnlyList<string> StepKeys(string kind) => kind switch
    {
        "static-site" => ["step", "artifact", "target", "directory", "healthUrl", "healthTimeoutSeconds", "environments"],
        "container" => ["step", "artifact", "target", "composeFile", "service", "healthUrl", "healthTimeoutSeconds", "environments"],
        _ => ["step", "artifact", "target", "branch", "run", "health", "approval", "environments"],
    };

    /// <summary>The artifact type a step kind deploys, or null for steps without an artifact.</summary>
    public static string? ArtifactTypeFor(string kind) => kind switch
    {
        "static-site" or "publish-static" => "static",
        "container" or "start-container" => "container",
        _ => null,
    };

    public static IReadOnlyList<ProjectEnvironmentDefinition> DefaultEnvironments() =>
        [new("staging", false), new("production", true)];

    public static IReadOnlyList<ProjectLifecyclePhase> DefaultStandardLifecycle() =>
    [
        new("staging", true, [], null),
        new("production", false, ["Owner", "Maintainer"], "staging"),
    ];
}

public static partial class ProjectDefinitionReader
{
    private static ProjectDeploymentDefinition? ParseDeployment(
        IReadOnlyList<YamlLine> lines, List<ProjectDefinitionIssue> issues)
    {
        bool Declared(string section) => lines.Any(line => line.Indent == 0 && KeyValue(line.Text).Key == section);
        if (!ProjectDeploymentVocabulary.Sections.Any(Declared))
            return null;

        CheckKeys(Section(lines, "pipeline"), 2, ["build", "test"], "pipeline", issues);
        CheckKeys(Section(lines, "lifecycles"), 2, ["standard"], "lifecycles", issues);
        var artifacts = SequenceMaps(lines, "artifacts", 2, 4, issues)
            .Select((item, index) =>
            {
                CheckMapKeys(item, ProjectDeploymentVocabulary.ArtifactKeys, $"artifacts[{index}]", issues);
                return new ProjectArtifactDefinition(Get(item, "name") ?? "", Get(item, "type") ?? "",
                    Get(item, "path"), Get(item, "dockerfile")) { Image = Get(item, "image") };
            }).ToArray();
        var environments = Environments(lines, issues);
        var phases = SequenceMaps(lines, "lifecycles.standard", 4, 6, issues)
            .Select((item, index) =>
            {
                CheckMapKeys(item, ProjectDeploymentVocabulary.LifecyclePhaseKeys, $"lifecycles.standard[{index}]", issues);
                var automaticText = Get(item, "automatic");
                if (automaticText is not null && !bool.TryParse(automaticText, out _))
                    issues.Add(new($"lifecycles.standard[{index}].automatic", "lifecycle-automatic-invalid", "automatic must be true or false."));
                return new ProjectLifecyclePhase(Get(item, "phase") ?? "",
                    bool.TryParse(automaticText, out var automatic) && automatic,
                    List(item, "approval"), Get(item, "requires"));
            }).ToArray();
        var steps = SequenceMaps(lines, "deploy", 2, 4, issues)
            .Select((item, index) =>
            {
                var kind = Get(item, "step") ?? "";
                CheckMapKeys(item, ProjectDeploymentVocabulary.StepKeys(kind), $"deploy[{index}]", issues);
                var timeoutText = Get(item, "healthTimeoutSeconds");
                int? timeout = null;
                if (timeoutText is not null)
                {
                    if (int.TryParse(timeoutText, out var parsed)) timeout = parsed;
                    else issues.Add(new($"deploy[{index}].healthTimeoutSeconds", "deploy-health-timeout-invalid",
                        "healthTimeoutSeconds must be a whole number of seconds."));
                }
                return new ProjectDeployStep(kind, Get(item, "artifact"),
                    Get(item, "target"), Get(item, "branch"), Get(item, "run"),
                    Get(item, "health"), List(item, "approval"), List(item, "environments"))
                {
                    Directory = Get(item, "directory"),
                    ComposeFile = Get(item, "composeFile"),
                    Service = Get(item, "service"),
                    HealthUrl = Get(item, "healthUrl"),
                    HealthTimeoutSeconds = timeout,
                };
            }).ToArray();
        var variables = SequenceMaps(lines, "variables", 2, 4, issues)
            .Select((item, index) =>
            {
                CheckMapKeys(item, ProjectDeploymentVocabulary.VariableKeys, $"variables[{index}]", issues);
                return new ProjectVariableDefinition(Get(item, "name") ?? "",
                    item.TryGetValue("value", out var value) ? value : null,
                    Get(item, "secret"), List(item, "environments"));
            }).ToArray();

        // Staging and Production with the standard lifecycle are the P1 default
        // once the file starts describing a deployment.
        var deploying = artifacts.Length > 0 || steps.Length > 0 || phases.Length > 0 || variables.Length > 0
            || Declared("environments");
        var defaultEnvironments = deploying && !Declared("environments");
        var defaultLifecycle = deploying && !Declared("lifecycles");
        return new(NestedStringList(lines, "pipeline", "build"),
            NestedStringList(lines, "pipeline", "test"), artifacts,
            defaultEnvironments ? ProjectDeploymentVocabulary.DefaultEnvironments() : environments,
            defaultLifecycle ? ProjectDeploymentVocabulary.DefaultStandardLifecycle() : phases,
            steps)
        {
            Variables = variables,
            EnvironmentsDefaulted = defaultEnvironments,
            LifecycleDefaulted = defaultLifecycle,
        };
    }

    /// <summary>
    /// Environments are either the inline shorthand <c>[staging, production]</c>
    /// or a list of maps with <c>name</c> and <c>protected</c>. The shorthand
    /// protects production.
    /// </summary>
    private static IReadOnlyList<ProjectEnvironmentDefinition> Environments(
        IReadOnlyList<YamlLine> lines, List<ProjectDefinitionIssue> issues)
    {
        var top = lines.FirstOrDefault(line => line.Indent == 0 && KeyValue(line.Text).Key == "environments");
        if (top is null) return [];
        if (!string.IsNullOrWhiteSpace(Value(KeyValue(top.Text).Value)))
            return StringList(lines, "environments")
                .Select(name => new ProjectEnvironmentDefinition(name, name == "production"))
                .ToArray();
        var section = Section(lines, "environments");
        if (section.Count > 0 && section.All(line => line.Indent == 2 && line.Text.StartsWith("- ", StringComparison.Ordinal)
                && !line.Text.Contains(':', StringComparison.Ordinal)))
            return StringList(lines, "environments")
                .Select(name => new ProjectEnvironmentDefinition(name, name == "production"))
                .ToArray();
        return SequenceMaps(lines, "environments", 2, 4, issues)
            .Select((item, index) =>
            {
                CheckMapKeys(item, ProjectDeploymentVocabulary.EnvironmentKeys, $"environments[{index}]", issues);
                var name = Get(item, "name") ?? "";
                var protectedText = Get(item, "protected");
                if (protectedText is not null && !bool.TryParse(protectedText, out _))
                    issues.Add(new($"environments[{index}].protected", "environment-protected-invalid",
                        "protected must be true or false."));
                var isProtected = protectedText is null
                    ? name == "production"
                    : bool.TryParse(protectedText, out var parsed) && parsed;
                return new ProjectEnvironmentDefinition(name, isProtected);
            }).ToArray();
    }

    private static IReadOnlyList<Dictionary<string, string>> SequenceMaps(
        IReadOnlyList<YamlLine> lines, string section, int itemIndent, int fieldIndent,
        List<ProjectDefinitionIssue> issues)
    {
        IReadOnlyList<YamlLine> content;
        if (section == "lifecycles.standard")
        {
            var start = Section(lines, "lifecycles").FirstOrDefault(line =>
                line.Indent == 2 && KeyValue(line.Text).Key == "standard");
            content = start is null ? [] : lines.Skip(lines.IndexOf(start) + 1)
                .TakeWhile(line => line.Indent > 2).ToArray();
        }
        else content = Section(lines, section);
        var result = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var line in content)
        {
            if (line.Indent == itemIndent && line.Text.StartsWith("- ", StringComparison.Ordinal))
            {
                current = new(StringComparer.Ordinal);
                result.Add(current);
                var (key, value) = KeyValue(line.Text[2..]);
                if (key.Length > 0) current[key] = Value(value);
            }
            else if (line.Indent == fieldIndent && current is not null)
            {
                var (key, value) = KeyValue(line.Text);
                if (key.Length > 0) current[key] = Value(value);
            }
            else issues.Add(new($"line:{line.Number}", "deployment-yaml-shape-invalid",
                $"Unsupported YAML shape in {section}; use a list of maps with two-space indentation."));
        }
        return result;
    }

    private static void CheckKeys(IReadOnlyList<YamlLine> lines, int indent, string[] allowed,
        string path, List<ProjectDefinitionIssue> issues)
    {
        foreach (var line in lines.Where(line => line.Indent == indent))
        {
            var key = KeyValue(line.Text).Key;
            if (!allowed.Contains(key, StringComparer.Ordinal))
                issues.Add(new($"{path}.{key}", "unknown-field", $"{path} does not support '{key}'."));
        }
    }

    private static void CheckMapKeys(IReadOnlyDictionary<string, string> map, IReadOnlyList<string> allowed,
        string path, List<ProjectDefinitionIssue> issues)
    {
        foreach (var key in map.Keys.Where(key => !allowed.Contains(key, StringComparer.Ordinal)))
            issues.Add(new($"{path}.{key}", "unknown-field", $"{path} does not support '{key}'."));
    }

    private static string? Get(IReadOnlyDictionary<string, string> map, string key)
        => map.TryGetValue(key, out var value) ? NullIfBlank(value) : null;

    private static IReadOnlyList<string> List(IReadOnlyDictionary<string, string> map, string key)
        => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? ParseInlineList(value) : [];
}

public static partial class ProjectDefinitionValidator
{
    private static IReadOnlyList<ProjectDefinitionIssue> ValidateDeployment(
        ProjectDeploymentDefinition value, ProjectDeployHostRegistry? hosts)
    {
        var issues = new List<ProjectDefinitionIssue>();
        if (!value.IsConfigured)
            return issues;
        var environments = ValidateEnvironments(value, issues);
        var names = ValidateArtifacts(value, issues);
        ValidateLifecycle(value, issues);
        if (value.Artifacts.Count > 0 && value.Deploy.Count == 0)
            issues.Add(new("deploy", "deploy-steps-required", "Declared artifacts need at least one deploy step."));
        for (var i = 0; i < value.Deploy.Count; i++)
            ValidateStep(value, value.Deploy[i], $"deploy[{i}]", environments, names, hosts, issues);
        ValidateVariables(value.Variables, environments, issues);
        return issues;
    }

    private static HashSet<string> ValidateEnvironments(ProjectDeploymentDefinition value, List<ProjectDefinitionIssue> issues)
    {
        var environments = value.Environments.Select(environment => environment.Name).ToHashSet(StringComparer.Ordinal);
        if (value.Environments.Count == 0)
        {
            // Only an explicitly empty environments section gets here with deploy content.
            if (value.Artifacts.Count > 0 || value.Deploy.Count > 0 || value.Variables.Count > 0)
                issues.Add(new("environments", "deployment-environments-invalid",
                    "P1 environments must be [staging, production] in deployment order."));
            return environments;
        }
        if (value.Environments.Count != 2 || !environments.SetEquals(ProjectDeploymentVocabulary.EnvironmentNames))
            issues.Add(new("environments", "deployment-environments-invalid",
                "P1 environments must be [staging, production] in deployment order."));
        else if (value.Environments[0].Name != "staging")
            issues.Add(new("environments", "deployment-environment-order-invalid",
                "Staging must precede production."));
        for (var i = 0; i < value.Environments.Count; i++)
        {
            if (value.Environments[i] is { Name: "production", Protected: false })
                issues.Add(new($"environments[{i}].protected", "production-protection-required",
                    "Production must be protected: every deploy into it needs an Owner or Maintainer approval."));
            // P1 Staging deploys automatically after integration, so an approval gate there has no effect.
            if (value.Environments[i] is { Name: "staging", Protected: true })
                issues.Add(new($"environments[{i}].protected", "staging-protection-unsupported",
                    "Staging deploys automatically after integration in P1 and cannot be protected; only Production is."));
        }
        return environments;
    }

    private static HashSet<string> ValidateArtifacts(ProjectDeploymentDefinition value, List<ProjectDefinitionIssue> issues)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < value.Artifacts.Count; i++)
        {
            var artifact = value.Artifacts[i];
            var path = $"artifacts[{i}]";
            if (string.IsNullOrWhiteSpace(artifact.Name) || !Identifier().IsMatch(artifact.Name) || !names.Add(artifact.Name))
                issues.Add(new($"{path}.name", "artifact-name-invalid", "Artifact names must be unique lower-case identifiers."));
            if (!ProjectDeploymentVocabulary.ArtifactTypes.Contains(artifact.Type))
                issues.Add(new($"{path}.type", "artifact-type-invalid", "P1 artifact type must be static or container."));
            if (artifact.Type == "static" && !ArtifactPath(artifact.Path))
                issues.Add(new($"{path}.path", "artifact-path-invalid",
                    $"A static artifact needs a repository-relative bundle path such as dist. {ArtifactPathRule}"));
            if (artifact.Type == "container" && !ArtifactPath(artifact.Dockerfile))
                issues.Add(new($"{path}.dockerfile", "artifact-dockerfile-invalid",
                    $"A container artifact needs a repository-relative dockerfile such as Dockerfile. {ArtifactPathRule}"));
            if (artifact.Type == "container" && artifact.Path is not null && !ArtifactPath(artifact.Path))
                issues.Add(new($"{path}.path", "artifact-path-invalid",
                    $"A container build context must be repository-relative, or '.' for the repository root. {ArtifactPathRule}"));
            if (artifact.Image is not null && (artifact.Type != "container" || !ImageRepository().IsMatch(artifact.Image)))
                issues.Add(new($"{path}.image", "artifact-image-invalid",
                    "image names the registry repository of a container artifact, without tag or digest."));
        }
        return names;
    }

    private static void ValidateLifecycle(ProjectDeploymentDefinition value, List<ProjectDefinitionIssue> issues)
    {
        if (value.StandardLifecycle.Count == 0 && value.Environments.Count == 0)
            return;
        if (value.StandardLifecycle.Count != 2
            || value.StandardLifecycle[0].Phase != "staging"
            || value.StandardLifecycle[1].Phase != "production")
        {
            issues.Add(new("lifecycles.standard", "standard-lifecycle-invalid",
                "P1 standard lifecycle must contain staging followed by production."));
            return;
        }
        if (!value.StandardLifecycle[0].Automatic)
            issues.Add(new("lifecycles.standard[0].automatic", "staging-automatic-required",
                "Staging must deploy automatically after integration."));
        if (value.StandardLifecycle[1].Approval.Count == 0
            || value.StandardLifecycle[1].Approval.Any(role => !ProjectDeploymentVocabulary.ApprovalRoles.Contains(role)))
            issues.Add(new("lifecycles.standard[1].approval", "production-approval-invalid",
                "Production approval must name Owner or Maintainer."));
        if (value.StandardLifecycle[1].Requires is not ("staging" or "{ staging: success }" or "{staging: success}"))
            issues.Add(new("lifecycles.standard[1].requires", "production-requires-staging",
                "Production must require successful staging. Use 'requires: staging'."));
    }

    private static void ValidateStep(ProjectDeploymentDefinition value, ProjectDeployStep step, string path,
        HashSet<string> environments, HashSet<string> names, ProjectDeployHostRegistry? hosts,
        List<ProjectDefinitionIssue> issues)
    {
        if (!ProjectDeploymentVocabulary.StepKinds.Contains(step.Step))
            issues.Add(new($"{path}.step", "deploy-step-invalid",
                "P1 deploy step must be static-site, container, publish-static, start-container, run, health, or approval."));
        if (step.Environments.Any(environment => !environments.Contains(environment)))
            issues.Add(new($"{path}.environments", "deploy-environment-unknown", "Step environments must be declared in environments."));
        if (ProjectDeploymentVocabulary.ArtifactTypeFor(step.Step) is { } artifactType)
        {
            if (step.Artifact is null || !names.Contains(step.Artifact))
                issues.Add(new($"{path}.artifact", "deploy-artifact-unknown", "Step artifact must name a declared artifact."));
            else if (value.Artifacts.First(artifact => artifact.Name == step.Artifact).Type != artifactType)
                issues.Add(new($"{path}.artifact", $"deploy-{artifactType}-artifact-required",
                    $"{step.Step} needs a {artifactType} artifact."));
            if (string.IsNullOrWhiteSpace(step.Target))
                issues.Add(new($"{path}.target", "deploy-target-required", "A target is required."));
        }
        if (step.Step is "static-site" or "container")
            ValidateSshStep(value, step, path, hosts, issues);
        if (step.Step == "publish-static" && string.IsNullOrWhiteSpace(step.Branch))
            issues.Add(new($"{path}.branch", "deploy-branch-required", "publish-static needs a deploy branch pulled by the web target."));
        if (step.Step == "run" && string.IsNullOrWhiteSpace(step.Run))
            issues.Add(new($"{path}.run", "deploy-run-required", "run needs a command."));
        if (step.Step == "health" && (step.Health is null || !step.Health.StartsWith("/", StringComparison.Ordinal)))
            issues.Add(new($"{path}.health", "deploy-health-invalid", "health needs an absolute HTTP path such as /healthz."));
        if (step.Step == "approval" && (step.Approval.Count == 0
            || step.Approval.Any(role => !ProjectDeploymentVocabulary.ApprovalRoles.Contains(role))))
            issues.Add(new($"{path}.approval", "deploy-approval-invalid", "approval needs Owner or Maintainer."));
    }

    /// <summary>
    /// static-site copies a bundle and container restarts a compose service, both
    /// over SSH once the executor exists; P1 only validates the definition.
    /// </summary>
    private static void ValidateSshStep(ProjectDeploymentDefinition value, ProjectDeployStep step, string path,
        ProjectDeployHostRegistry? hosts, List<ProjectDefinitionIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(step.Target) && !Identifier().IsMatch(step.Target))
            issues.Add(new($"{path}.target", "deploy-target-invalid",
                "target names a deploy host id, such as web-vm; credentials never appear in project.yml."));
        else if (!string.IsNullOrWhiteSpace(step.Target) && hosts is not null)
            ValidateRegisteredTarget(value, step, path, hosts, issues);
        if (step.Step == "static-site" && !RemotePath(step.Directory))
            issues.Add(new($"{path}.directory", "deploy-directory-invalid",
                $"static-site needs a target directory such as /srv/www/site. {RemotePathRule}"));
        if (step.Step == "container")
        {
            if (!RemotePath(step.ComposeFile) || !ComposeFileSuffix().IsMatch(step.ComposeFile!))
                issues.Add(new($"{path}.composeFile", "deploy-compose-file-invalid",
                    $"container needs the .yml or .yaml compose file on the target, such as /srv/apps/web/compose.yml. {RemotePathRule}"));
            if (step.Service is null || !Identifier().IsMatch(step.Service))
                issues.Add(new($"{path}.service", "deploy-service-invalid", "container needs the compose service to pull and restart."));
        }
        if (step.HealthUrl is null || !Uri.TryCreate(step.HealthUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            issues.Add(new($"{path}.healthUrl", "deploy-health-url-invalid",
                "healthUrl must be an absolute http or https URL without credentials."));
        if (step.HealthTimeoutSeconds is not { } timeout || timeout < 1
            || timeout > ProjectDeploymentVocabulary.MaxHealthTimeoutSeconds)
            issues.Add(new($"{path}.healthTimeoutSeconds", "deploy-health-timeout-invalid",
                $"healthTimeoutSeconds must be between 1 and {ProjectDeploymentVocabulary.MaxHealthTimeoutSeconds}."));
    }

    private static void ValidateVariables(IReadOnlyList<ProjectVariableDefinition> variables,
        HashSet<string> environments, List<ProjectDefinitionIssue> issues)
    {
        for (var i = 0; i < variables.Count; i++)
        {
            var variable = variables[i];
            var path = $"variables[{i}]";
            if (!EnvironmentName().IsMatch(variable.Name))
                issues.Add(new($"{path}.name", "variable-name-invalid", "Variable names must be portable shell identifiers."));
            if ((variable.Value is null) == (variable.Secret is null))
                issues.Add(new(path, "variable-source-invalid", "A variable has either a value or a secret name, not both."));
            if (variable.Secret is not null && !SecretReference().IsMatch(variable.Secret))
                issues.Add(new($"{path}.secret", "variable-secret-name-invalid",
                    "secret names a secret of the secret provider, such as DATABASE_URL; it never holds the value."));
            if (variable.Value is not null && (SensitiveVariableName(variable.Name) || CredentialShapedValue().IsMatch(variable.Value)))
                issues.Add(new($"{path}.value", "secret-value-inline",
                    "Secret values are refused in project.yml. Store the value in the secret provider and reference it with 'secret: <name>'."));
            if (variable.Environments.Any(environment => !environments.Contains(environment)))
                issues.Add(new($"{path}.environments", "variable-environment-unknown", "Variable environments must be declared in environments."));
            for (var j = 0; j < i; j++)
            {
                if (variables[j].Name != variable.Name || !ScopesOverlap(variables[j].Environments, variable.Environments))
                    continue;
                issues.Add(new($"{path}.name", "variable-duplicate",
                    $"Variable '{variable.Name}' is already defined for an overlapping environment scope."));
                break;
            }
        }
    }

    /// <summary>The target must be registered for every environment the step deploys to.</summary>
    private static void ValidateRegisteredTarget(ProjectDeploymentDefinition value, ProjectDeployStep step, string path,
        ProjectDeployHostRegistry hosts, List<ProjectDefinitionIssue> issues)
    {
        var scope = step.Environments.Count > 0
            ? step.Environments
            : value.Environments.Select(environment => environment.Name).ToArray();
        var missing = scope.Where(environment => !hosts.HostsFor(environment).Contains(step.Target, StringComparer.Ordinal))
            .ToArray();
        if (missing.Length == 0)
            return;
        var registered = missing.Select(environment => hosts.HostsFor(environment) is { Count: > 0 } ids
            ? $"{environment}: {string.Join(", ", ids)}" : $"{environment}: none");
        issues.Add(new($"{path}.target", "deploy-target-unregistered",
            $"target '{step.Target}' is not a deploy host registered for {string.Join(" and ", missing)} "
            + $"({string.Join("; ", registered)}). Hosts are registered by the environment declarations under docs/deployments/environments."));
    }

    private static bool ScopesOverlap(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Count == 0 || right.Count == 0 || left.Intersect(right, StringComparer.Ordinal).Any();

    private const string RemotePathRule = "Paths stay below " + ProjectDeploymentVocabulary.RemoteBaseDirectory
        + "/ and use only letters, digits, '.', '_' and '-', with no segment that is empty, '.', '..', or starts with '-'.";

    private const string ArtifactPathRule =
        "Paths use only letters, digits, '.', '_' and '-', with no segment that is empty, '.', '..', or starts with '-'.";

    // .NET '$' also matches before a final newline; the schema's ECMA-262 '$' does not.
    private static bool RemotePath(string? value)
        => value is not null && !value.Contains('\n') && RemotePathShape().IsMatch(value);

    private static bool ArtifactPath(string? value)
        => value is not null && !value.Contains('\n') && ArtifactPathShape().IsMatch(value);

    [GeneratedRegex(ProjectDeploymentVocabulary.RemotePathPattern, RegexOptions.CultureInvariant)]
    private static partial Regex RemotePathShape();

    [GeneratedRegex(ProjectDeploymentVocabulary.ComposeFilePattern, RegexOptions.CultureInvariant)]
    private static partial Regex ComposeFileSuffix();

    [GeneratedRegex(ProjectDeploymentVocabulary.ArtifactPathPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactPathShape();

    [GeneratedRegex(ProjectDeploymentVocabulary.IdentifierPattern, RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*(?::[0-9]{1,5})?(?:/[a-z0-9][a-z0-9._-]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageRepository();

    [GeneratedRegex(ProjectDeploymentVocabulary.SecretReferencePattern, RegexOptions.CultureInvariant)]
    private static partial Regex SecretReference();

    /// <summary>
    /// Curated last name segments that hold a secret, such as DB_PASS, STRIPE_KEY,
    /// SMTP_PASSWORD, or API_TOKEN. Only the last segment decides, so TOKEN_TTL or
    /// PASSWORD_MIN_LENGTH stay plain configuration.
    /// </summary>
    private static readonly HashSet<string> SensitiveNameSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "PASSWORD", "PASSWD", "PASS", "PWD", "PASSPHRASE", "SECRET", "TOKEN", "KEY", "APIKEY",
        "CREDENTIAL", "CREDENTIALS",
    };

    /// <summary>Words that end a joined last segment such as AUTHTOKEN or DBPASSWORD; short words like KEY or PASS do not.</summary>
    private static readonly string[] SensitiveNameEndings =
        ["PASSWORD", "PASSWD", "PASSPHRASE", "SECRET", "TOKEN", "APIKEY", "CREDENTIAL", "CREDENTIALS"];

    /// <summary>Two-segment endings: a connection string or a key base (SECRET_KEY_BASE) is secret, a public or cache key is not.</summary>
    private static readonly HashSet<string> SensitiveNamePairs = new(StringComparer.OrdinalIgnoreCase)
        { "CONNECTION_STRING", "KEY_BASE" };
    private static readonly HashSet<string> PlainNamePairs = new(StringComparer.OrdinalIgnoreCase)
        { "PUBLIC_KEY", "PUBLISHABLE_KEY", "CACHE_KEY", "SORT_KEY", "PARTITION_KEY" };

    private static bool SensitiveVariableName(string name)
    {
        var segments = NameSegment().Matches(name).Select(match => match.Value).ToArray();
        if (segments.Length == 0)
            return false;
        var last = segments[^1];
        var pair = segments.Length > 1 ? $"{segments[^2]}_{last}" : null;
        if (pair is not null && SensitiveNamePairs.Contains(pair)) return true;
        if (pair is not null && PlainNamePairs.Contains(pair)) return false;
        return SensitiveNameSegments.Contains(last)
               || SensitiveNameEndings.Any(ending => last.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Name segments split at '_' and at camelCase humps: dbPass is [db, Pass].</summary>
    [GeneratedRegex("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NameSegment();

    /// <summary>
    /// Values shaped like a credential whatever their name: a URL with a password
    /// or a credential query parameter, a connection-string password, a PEM private
    /// key, an authorization header, a JWT, and common provider key prefixes.
    /// </summary>
    [GeneratedRegex(
        @"^[A-Za-z][A-Za-z0-9+.-]*://[^/\s:@]+:[^/\s@]+@"
        + @"|(?i:[?&](?:access_token|token|api_?key|key|password|passwd|pwd|secret|client_secret|sig|signature|auth)=)[^&#\s]"
        + @"|(?i:(?:^|;)\s*(?:password|pwd)\s*=)\s*[^;\s]"
        + @"|-----BEGIN [A-Z ]*PRIVATE KEY-----"
        + @"|^Bearer\s+[A-Za-z0-9._~+/-]+=*$|^Basic\s+[A-Za-z0-9+/]{8,}={0,2}$"
        + @"|^eyJ[A-Za-z0-9_-]{4,}\.eyJ[A-Za-z0-9_-]{4,}\."
        + @"|^(?:sk|rk)_(?:live|test)_[A-Za-z0-9]{8,}|^gh[pousr]_[A-Za-z0-9]{20,}|^github_pat_[A-Za-z0-9_]{20,}"
        + @"|^glpat-[A-Za-z0-9_-]{20,}|^xox[abprs]-[A-Za-z0-9-]{10,}|^(?:AKIA|ASIA)[0-9A-Z]{16}$|^sk-[A-Za-z0-9_-]{20,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialShapedValue();
}
