using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Diagnostics;

namespace AgentStudio.Management;

public sealed record StructureNode(string Id, string Kind, string Label, string? ParentId,
    string? UnitId, string? EnvironmentId, string? Project, string Provenance, string SourceRef,
    string? Role = null, StructureObservation? Observation = null);
public sealed record StructureLink(string From, string To, string Kind, string Label, string SourceRef,
    StructureObservation? Observation = null);
public sealed record StructureObservation(string Difference, string Health, DateTime? ObservedAt = null,
    string? Version = null, string? ExpectedVersion = null, bool Behind = false,
    double? CpuPercent = null, long? MemoryUsedBytes = null, long? MemoryTotalBytes = null,
    int? ActiveGates = null, string? Detail = null);
public sealed record StructureResponse(IReadOnlyList<StructureNode> Nodes, IReadOnlyList<StructureLink> Links);

/// <summary>Read-only projection of installation and repository declarations.</summary>
public static class StructureProjection
{
    public static StructureResponse Build(InstallationConnectivityManifest? manifest,
        IReadOnlyList<ProjectRecord> projects, ProjectEnvironmentService environments,
        string? projectId, string? environmentId)
        => Build(manifest, projects, environments.DeclarationFiles, projectId, environmentId);

    public static StructureResponse Build(InstallationConnectivityManifest? manifest,
        IReadOnlyList<ProjectRecord> projects,
        Func<string, IReadOnlyList<EnvironmentDeclarationFile>?> declarationFiles,
        string? projectId, string? environmentId,
        Func<ProjectRecord, string?>? repositoryRoot = null)
    {
        var nodes = new List<StructureNode>();
        var links = new List<StructureLink>();
        const string manifestRef = "Connectivity:ManifestPath";
        void Add(string id, string kind, string label, string? parent, string? unit = null,
            string? environment = null, string? project = null, string source = manifestRef,
            string? role = null)
            => nodes.Add(new(id, kind, label, parent, unit, environment, project, "declared", source, role));

        Add("installation", "boundary", manifest?.InstallationId ?? "Installation", null);
        if (manifest is not null)
        {
            foreach (var location in (manifest.Machines ?? []).Select(m => m.Location)
                         .Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase))
                Add($"location:{location}", "location", location!, "installation");
            foreach (var machine in manifest.Machines ?? [])
                Add($"machine:{machine.Id}", machine.Kind ?? "physical", machine.Name,
                    machine.ParentId is { Length: > 0 } ? $"machine:{machine.ParentId}" :
                    machine.Location is { Length: > 0 } ? $"location:{machine.Location}" : "installation",
                    source: $"{manifestRef}#machines/{machine.Id}");
            foreach (var machine in manifest.Machines ?? [])
                foreach (var component in machine.Components ?? [])
                    Add($"component:{machine.Id}:{component.Id}", component.Kind, component.Name,
                        $"machine:{machine.Id}", component.Id,
                        source: $"{manifestRef}#machines/{machine.Id}/components/{component.Id}", role: component.Role);
            foreach (var external in manifest.Externals ?? [])
                Add($"external:{external.Id}", external.Kind ?? "external", external.Name, null,
                    source: $"{manifestRef}#externals/{external.Id}");
            foreach (var link in manifest.StructureLinks ?? [])
            {
                static string Resolve(string id) => id.Contains(':') ? id : $"machine:{id}";
                links.Add(new(Resolve(link.From), Resolve(link.To), link.Kind,
                    link.Label ?? link.Kind, $"{manifestRef}#structureLinks/{links.Count}"));
            }
        }

        foreach (var project in projects.Where(p => !p.Archived && (projectId is null || p.Id == projectId)))
        {
            var root = repositoryRoot?.Invoke(project) ?? project.RepositoryPath ?? project.RootPath;
            if (string.IsNullOrWhiteSpace(root)) continue;
            var definitionPath = Path.Combine(root, ProjectPreparationPaths.Definition);
            ProjectDefinitionReadResult? definition = null;
            if (File.Exists(definitionPath))
            {
                try { definition = ProjectDefinitionReader.Parse(File.ReadAllText(definitionPath)); }
                catch (IOException ex) { SilentCatch.Warn(ex, "StructureProjection: project definition unavailable"); }
            }
            if ((definition?.Definition ?? definition?.Parsed) is { SchemaVersion: 2, Deployment: { } deployment })
                foreach (var artifact in deployment.Artifacts)
                    Add($"unit:{project.Id}:{artifact.Name}", "unit", artifact.Name, "installation",
                        artifact.Name, null, project.Id, $"{project.Id}/.agent-studio/project.yml#artifacts/{artifact.Name}");

            foreach (var file in declarationFiles(project.DisplayName) ?? [])
            {
                var declaration = file.Declaration;
                if (declaration is null || environmentId is not null && declaration.Id != environmentId) continue;
                var source = $"{project.Id}/{ProjectEnvironmentService.DeclarationDirectory}/{file.EnvironmentId}.json";
                var environmentNode = $"environment:{project.Id}:{declaration.Id}";
                Add(environmentNode, "environment", declaration.Name, "installation", null,
                    declaration.Id, project.Id, source);
                var host = $"machine:{declaration.TargetHostId}";
                var parent = nodes.Any(n => n.Id == host) ? host : environmentNode;
                foreach (var artifact in declaration.Artifacts)
                    Add($"instance:{project.Id}:{declaration.Id}:{artifact.Name}",
                        artifact.Type == "container" ? "container" : "process", artifact.Name, parent,
                        artifact.Name, declaration.Id, project.Id, source);
            }
        }
        if (environmentId is not null)
        {
            var byId = nodes.ToDictionary(n => n.Id);
            var keep = new HashSet<string> { "installation" };
            foreach (var matching in nodes.Where(n => n.EnvironmentId == environmentId))
            {
                StructureNode? current = matching;
                while (current is not null && keep.Add(current.Id))
                    current = current.ParentId is { } parent && byId.TryGetValue(parent, out var ancestor)
                        ? ancestor : null;
                if (matching.Kind is "container" or "process" && matching.Project is { } project
                    && matching.UnitId is { } unitId)
                    keep.Add($"unit:{project}:{unitId}");
            }
            nodes.RemoveAll(n => !keep.Contains(n.Id));
        }
        return new(nodes, links.Where(l => nodes.Any(n => n.Id == l.From)
                                       && nodes.Any(n => n.Id == l.To)).ToArray());
    }
}
