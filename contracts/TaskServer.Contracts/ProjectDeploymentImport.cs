using System.Text;
using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

public sealed record ProjectDeploymentImportResult(
    bool Changed, IReadOnlyList<ProjectDefinitionIssue> Issues, IReadOnlyList<string> ImportedFiles);

/// <summary>
/// Explicit, one-time repository migration. Legacy descriptors remain intact for review;
/// the source of future deploy definitions is the versioned project.yml.
/// </summary>
public static class ProjectDeploymentImport
{
    public static ProjectDeploymentImportResult ImportWorkspace(string workspace)
    {
        var root = Path.Combine(workspace, "docs", "deployments");
        var files = Directory.Exists(root)
            ? Directory.GetFiles(root, "deployment.json", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : [];
        if (files.Length == 0) return new(false, [], []);
        var definitionPath = Path.Combine(workspace, ProjectPreparationPaths.Definition);
        var read = ProjectDefinitionReader.ReadWorkspace(workspace);
        if (!read.IsValid || read.Definition is null)
            return new(false, read.Issues, []);
        if (read.Definition.Deployment is { } current
            && (current.Artifacts.Count > 0 || current.Environments.Count > 0
                || current.StandardLifecycle.Count > 0 || current.Deploy.Count > 0))
            return new(false, [], []);

        var issues = new List<ProjectDefinitionIssue>();
        var descriptors = new List<(string File, string Id, string? Command, string? Target)>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(workspace, file).Replace('\\', '/');
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var json = document.RootElement;
                var id = Text(json, "id");
                var command = Text(json, "command");
                var target = Text(json, "targetHostId");
                var template = Text(json, "template");
                var publish = command is null && template == "caddy-site" && target is not null;
                if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9._-]+$")
                    || (command is null && !publish)
                    || command?.IndexOfAny(['\r', '\n']) >= 0)
                {
                    issues.Add(new(relative, "deployment-import-incomplete",
                        "Legacy deployment needs a safe id and either a command or a caddy-site template with targetHostId."));
                    continue;
                }
                descriptors.Add((relative, id, command, target));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                issues.Add(new(relative, "deployment-import-unreadable", $"Cannot read legacy deployment: {ex.Message}"));
            }
        }
        if (issues.Count > 0) return new(false, issues, []);

        var original = File.ReadAllText(definitionPath);
        var ending = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var projectId = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace));
        var builder = new StringBuilder(original.TrimEnd('\r', '\n'));
        if (read.Definition.SchemaVersion == 1)
        {
            var replaced = new System.Text.RegularExpressions.Regex(@"(?m)^schemaVersion: 1(?=\r?$)")
                .Replace(builder.ToString(), "schemaVersion: 2", 1);
            builder.Clear().Append(replaced);
            builder.Append(ending).Append("project:").Append(ending).Append("  id: ").Append(projectId).Append(ending);
        }
        builder.Append(ending).Append("artifacts:").Append(ending)
            .Append("  - name: legacy-static").Append(ending)
            .Append("    type: static").Append(ending)
            .Append("    path: .").Append(ending)
            .Append("environments: [staging, production]").Append(ending)
            .Append("lifecycles:").Append(ending)
            .Append("  standard:").Append(ending)
            .Append("    - phase: staging").Append(ending)
            .Append("      automatic: true").Append(ending)
            .Append("    - phase: production").Append(ending)
            .Append("      approval: [Owner, Maintainer]").Append(ending)
            .Append("      requires: staging").Append(ending)
            .Append("deploy:").Append(ending);
        foreach (var descriptor in descriptors)
        {
            builder.Append("  # Imported from ").Append(descriptor.File).Append(ending);
            if (descriptor.Command is not null)
                builder.Append("  - step: run").Append(ending)
                    .Append("    run: ").Append(descriptor.Command).Append(ending);
            else
                builder.Append("  - step: publish-static").Append(ending)
                    .Append("    artifact: legacy-static").Append(ending)
                    .Append("    target: ").Append(descriptor.Target).Append(ending)
                    .Append("    branch: deploy/").Append(descriptor.Id).Append(ending);
            builder.Append("    environments: [production]").Append(ending);
        }
        var proposed = builder.ToString();
        var parsed = ProjectDefinitionReader.Parse(proposed, workspace);
        if (!parsed.IsValid) return new(false, parsed.Issues, []);
        var importedSteps = parsed.Definition!.Deployment!.Deploy;
        for (var index = 0; index < descriptors.Count; index++)
        {
            var source = descriptors[index];
            var step = importedSteps[index];
            if ((source.Command is not null && step.Run != source.Command)
                || (source.Command is null && (step.Target != source.Target || step.Branch != $"deploy/{source.Id}")))
                return new(false,
                    [new(source.File, "deployment-import-value-changed",
                        "The generated YAML would change a legacy command or target. Edit the descriptor or import manually.")],
                    []);
        }

        var temp = definitionPath + ".migration-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, proposed);
            File.Move(temp, definitionPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return new(true, [], descriptors.Select(descriptor => descriptor.File).ToArray());
    }

    private static string? Text(JsonElement json, string name)
        => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() : null;
}
