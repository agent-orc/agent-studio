using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

public sealed record ProjectArtifactDefinition(string Name, string Type, string? Path, string? Dockerfile);
public sealed record ProjectLifecyclePhase(string Phase, bool Automatic, IReadOnlyList<string> Approval, string? Requires);
public sealed record ProjectDeployStep(
    string Step, string? Artifact, string? Target, string? Branch, string? Run,
    string? Health, IReadOnlyList<string> Approval, IReadOnlyList<string> Environments);
public sealed record ProjectDeploymentDefinition(
    IReadOnlyList<string> PipelineBuild,
    IReadOnlyList<string> PipelineTest,
    IReadOnlyList<ProjectArtifactDefinition> Artifacts,
    IReadOnlyList<string> Environments,
    IReadOnlyList<ProjectLifecyclePhase> StandardLifecycle,
    IReadOnlyList<ProjectDeployStep> Deploy);

public static partial class ProjectDefinitionReader
{
    private static ProjectDeploymentDefinition? ParseDeployment(
        IReadOnlyList<YamlLine> lines, List<ProjectDefinitionIssue> issues)
    {
        if (!lines.Any(line => line.Indent == 0 &&
            KeyValue(line.Text).Key is "pipeline" or "artifacts" or "environments" or "lifecycles" or "deploy"))
            return null;

        CheckKeys(Section(lines, "pipeline"), 2, ["build", "test"], "pipeline", issues);
        CheckKeys(Section(lines, "lifecycles"), 2, ["standard"], "lifecycles", issues);
        var artifacts = SequenceMaps(lines, "artifacts", 2, 4, issues)
            .Select((item, index) =>
            {
                CheckMapKeys(item, ["name", "type", "path", "dockerfile"], $"artifacts[{index}]", issues);
                return new ProjectArtifactDefinition(Get(item, "name") ?? "", Get(item, "type") ?? "",
                    Get(item, "path"), Get(item, "dockerfile"));
            }).ToArray();
        var phases = SequenceMaps(lines, "lifecycles.standard", 4, 6, issues)
            .Select((item, index) =>
            {
                CheckMapKeys(item, ["phase", "automatic", "approval", "requires"], $"lifecycles.standard[{index}]", issues);
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
                CheckMapKeys(item, ["step", "artifact", "target", "branch", "run", "health", "approval", "environments"], $"deploy[{index}]", issues);
                return new ProjectDeployStep(Get(item, "step") ?? "", Get(item, "artifact"),
                    Get(item, "target"), Get(item, "branch"), Get(item, "run"),
                    Get(item, "health"), List(item, "approval"), List(item, "environments"));
            }).ToArray();
        return new(NestedStringList(lines, "pipeline", "build"),
            NestedStringList(lines, "pipeline", "test"), artifacts,
            StringList(lines, "environments"), phases, steps);
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

    private static void CheckMapKeys(IReadOnlyDictionary<string, string> map, string[] allowed,
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
    private static IReadOnlyList<ProjectDefinitionIssue> ValidateDeployment(ProjectDeploymentDefinition value)
    {
        var issues = new List<ProjectDefinitionIssue>();
        if (value.Artifacts.Count == 0 && value.Environments.Count == 0
            && value.StandardLifecycle.Count == 0 && value.Deploy.Count == 0)
            return issues;
        var environments = value.Environments.ToHashSet(StringComparer.Ordinal);
        if (value.Environments.Count != 2 || !environments.SetEquals(["staging", "production"]))
            issues.Add(new("environments", "deployment-environments-invalid",
                "P1 environments must be [staging, production] in deployment order."));
        else if (value.Environments[0] != "staging")
            issues.Add(new("environments", "deployment-environment-order-invalid",
                "Staging must precede production."));

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < value.Artifacts.Count; i++)
        {
            var artifact = value.Artifacts[i];
            var path = $"artifacts[{i}]";
            if (string.IsNullOrWhiteSpace(artifact.Name) || !names.Add(artifact.Name))
                issues.Add(new($"{path}.name", "artifact-name-invalid", "Artifact names must be nonempty and unique."));
            if (artifact.Type is not ("static" or "container"))
                issues.Add(new($"{path}.type", "artifact-type-invalid", "P1 artifact type must be static or container."));
            if (artifact.Type == "static" && !SafeRelativePath(artifact.Path))
                issues.Add(new($"{path}.path", "artifact-path-invalid", "A static artifact needs a repository-relative path."));
            if (artifact.Type == "container" && !SafeRelativePath(artifact.Dockerfile))
                issues.Add(new($"{path}.dockerfile", "artifact-dockerfile-invalid", "A container artifact needs a repository-relative dockerfile."));
        }
        if (value.StandardLifecycle.Count != 2
            || value.StandardLifecycle[0].Phase != "staging"
            || value.StandardLifecycle[1].Phase != "production")
            issues.Add(new("lifecycles.standard", "standard-lifecycle-invalid",
                "P1 standard lifecycle must contain staging followed by production."));
        else
        {
            if (!value.StandardLifecycle[0].Automatic)
                issues.Add(new("lifecycles.standard[0].automatic", "staging-automatic-required",
                    "Staging must deploy automatically after integration."));
            if (value.StandardLifecycle[1].Approval.Count == 0
                || value.StandardLifecycle[1].Approval.Any(role => role is not ("Owner" or "Maintainer")))
                issues.Add(new("lifecycles.standard[1].approval", "production-approval-invalid",
                    "Production approval must name Owner or Maintainer."));
            if (value.StandardLifecycle[1].Requires is not ("staging" or "{ staging: success }" or "{staging: success}"))
                issues.Add(new("lifecycles.standard[1].requires", "production-requires-staging",
                    "Production must require successful staging. Use 'requires: staging'."));
        }
        if (value.Deploy.Count == 0)
            issues.Add(new("deploy", "deploy-steps-required", "At least one deploy step is required."));
        for (var i = 0; i < value.Deploy.Count; i++)
        {
            var step = value.Deploy[i];
            var path = $"deploy[{i}]";
            if (step.Step is not ("publish-static" or "start-container" or "run" or "health" or "approval"))
                issues.Add(new($"{path}.step", "deploy-step-invalid",
                    "P1 deploy step must be publish-static, start-container, run, health, or approval."));
            if (step.Environments.Any(environment => !environments.Contains(environment)))
                issues.Add(new($"{path}.environments", "deploy-environment-unknown", "Step environments must be declared in environments."));
            if (step.Step is "publish-static" or "start-container")
            {
                if (step.Artifact is null || !names.Contains(step.Artifact))
                    issues.Add(new($"{path}.artifact", "deploy-artifact-unknown", "Step artifact must name a declared artifact."));
                if (string.IsNullOrWhiteSpace(step.Target))
                    issues.Add(new($"{path}.target", "deploy-target-required", "A target is required."));
                if (step.Step == "publish-static" && value.Artifacts.FirstOrDefault(a => a.Name == step.Artifact)?.Type != "static")
                    issues.Add(new($"{path}.artifact", "deploy-static-artifact-required", "publish-static needs a static artifact."));
                if (step.Step == "start-container" && value.Artifacts.FirstOrDefault(a => a.Name == step.Artifact)?.Type != "container")
                    issues.Add(new($"{path}.artifact", "deploy-container-artifact-required", "start-container needs a container artifact."));
            }
            if (step.Step == "publish-static" && string.IsNullOrWhiteSpace(step.Branch))
                issues.Add(new($"{path}.branch", "deploy-branch-required", "publish-static needs a deploy branch pulled by the web target."));
            if (step.Step == "run" && string.IsNullOrWhiteSpace(step.Run))
                issues.Add(new($"{path}.run", "deploy-run-required", "run needs a command."));
            if (step.Step == "health" && (step.Health is null || !step.Health.StartsWith("/", StringComparison.Ordinal)))
                issues.Add(new($"{path}.health", "deploy-health-invalid", "health needs an absolute HTTP path such as /healthz."));
            if (step.Step == "approval" && (step.Approval.Count == 0 || step.Approval.Any(role => role is not ("Owner" or "Maintainer"))))
                issues.Add(new($"{path}.approval", "deploy-approval-invalid", "approval needs Owner or Maintainer."));
        }
        return issues;
    }
}
