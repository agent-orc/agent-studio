using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Diagnostics;

namespace AgentStudio.Management;

public sealed record StructureNode(string Id, string Kind, string Label, string? ParentId,
    string? UnitId, string? EnvironmentId, string? Project, string Provenance, string SourceRef,
    string? Role = null, string? OwnerLabel = null, bool OtherProject = false,
    string? SystemBoundaryId = null, string? Detail = null, int? Replicas = null,
    string? ProbeObservation = null, string? Probe = null, string? Status = null,
    StructureObservation? Observation = null);
public sealed record StructureLink(string From, string To, string Kind, string Label, string SourceRef,
    StructureObservation? Observation = null);
public sealed record StructureObservation(string Difference, string Health, DateTime? ObservedAt = null,
    string? Version = null, string? ExpectedVersion = null, bool Behind = false,
    double? CpuPercent = null, long? MemoryUsedBytes = null, long? MemoryTotalBytes = null,
    int? ActiveGates = null, string? Detail = null);
public sealed record StructureResponse(IReadOnlyList<StructureNode> Nodes, IReadOnlyList<StructureLink> Links);
/// <summary>The control-plane health check that observes a managed service: its kind and current status.</summary>
public sealed record StructureProbe(string Kind, string Status);

/// <summary>Read-only projection of installation and repository declarations.</summary>
public static class StructureProjection
{
    public static StructureResponse Build(InstallationConnectivityManifest? manifest,
        IReadOnlyList<ProjectRecord> projects, ProjectEnvironmentService environments,
        string? projectId, string? environmentId, IReadOnlyList<WorkspaceSystem>? systems = null)
        => Build(manifest, projects, environments.DeclarationFiles, projectId, environmentId, null, systems);

    /// <param name="probes">Looks up the health check a managed service names, by project, environment and check id.</param>
    public static StructureResponse Build(InstallationConnectivityManifest? manifest,
        IReadOnlyList<ProjectRecord> projects,
        Func<string, IReadOnlyList<EnvironmentDeclarationFile>?> declarationFiles,
        string? projectId, string? environmentId,
        Func<ProjectRecord, string?>? repositoryRoot = null,
        IReadOnlyList<WorkspaceSystem>? systems = null,
        Func<ProjectRecord, string, string, StructureProbe?>? probes = null)
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
                        $"machine:{machine.Id}", component.Id, component.EnvironmentId, component.ProjectId,
                        $"{manifestRef}#machines/{machine.Id}/components/{component.Id}", component.Role);
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

        foreach (var project in projects.Where(p => !p.Archived))
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
            var deployment = (definition?.Definition ?? definition?.Parsed) is { SchemaVersion: 2, Deployment: { } declared }
                ? declared : null;
            foreach (var artifact in deployment?.Artifacts ?? [])
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
            if (deployment is { Structure.IsConfigured: true })
                AddStructure(nodes, project, deployment, probes);
        }
        // A system is a workspace record. Its boundary is repeated per environment;
        // placement remains shared and instance ownership remains on the project.
        foreach (var system in systems ?? [])
        {
            var memberUnits = system.Units.Select(unit => (unit.ProjectId, unit.UnitId)).ToHashSet();
            var environmentsInSystem = nodes.Where(node => node.EnvironmentId is not null &&
                node.Project is not null && node.UnitId is not null &&
                memberUnits.Contains((node.Project, node.UnitId)))
                .Select(node => node.EnvironmentId!).Distinct().ToArray();
            foreach (var environment in environmentsInSystem)
                Add($"system:{system.Id}:{environment}", "system", $"{system.Name} · {environment}", "installation",
                    environment: environment, source: $"workspace#systems/{system.Id}");
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.Project is not null && node.UnitId is not null && node.EnvironmentId is not null &&
                    memberUnits.Contains((node.Project, node.UnitId)))
                    nodes[i] = node with { SystemBoundaryId = $"system:{system.Id}:{node.EnvironmentId}" };
            }
        }
        if (projectId is not null)
        {
            var own = nodes.Where(node => node.Project == projectId &&
                node.Kind is not "unit" and not "environment").Select(node => node.Id).ToHashSet();
            var byId = nodes.ToDictionary(node => node.Id);
            var placements = own.Select(id => byId[id].ParentId).Where(id => id is not null).ToHashSet();
            var neighbors = nodes.Where(node => node.Project is not null && node.Project != projectId &&
                node.Kind is not "unit" and not "environment" &&
                (placements.Contains(node.ParentId) || links.Any(link =>
                    link.From == node.Id && own.Contains(link.To) ||
                    link.To == node.Id && own.Contains(link.From))))
                .Select(node => node.Id).ToHashSet();
            var keep = new HashSet<string>(own);
            keep.UnionWith(neighbors);
            // Externals are terminal: a link to one may show it, but its links
            // never discover another project's object.
            foreach (var link in links.Where(link => own.Contains(link.From) || own.Contains(link.To)))
            {
                var peer = own.Contains(link.From) ? link.To : link.From;
                if (byId.TryGetValue(peer, out var node) && node.Project is null &&
                    node.Kind is ("external" or "registry")) keep.Add(peer);
            }
            foreach (var node in nodes.Where(node => node.Kind is ("unit" or "environment") && node.Project == projectId))
                keep.Add(node.Id);
            foreach (var node in nodes.Where(node => node.Kind == "system" &&
                nodes.Any(candidate => candidate.Project == projectId &&
                    candidate.SystemBoundaryId == node.Id))) keep.Add(node.Id);
            foreach (var id in keep.ToArray())
            {
                var parent = byId[id].ParentId;
                while (parent is not null && keep.Add(parent)) parent = byId.GetValueOrDefault(parent)?.ParentId;
            }
            nodes.RemoveAll(node => !keep.Contains(node.Id));
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.Project is not null)
                    nodes[i] = node with { OwnerLabel = projects.FirstOrDefault(project => project.Id == node.Project)?.DisplayName,
                        OtherProject = node.Project != projectId };
            }
        }
        else
            for (var i = 0; i < nodes.Count; i++)
                if (nodes[i].Project is { } owner)
                    nodes[i] = nodes[i] with { OwnerLabel = projects.FirstOrDefault(project => project.Id == owner)?.DisplayName };
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

    /// <summary>
    /// Projects the project.yml <c>structure</c> section per environment:
    /// environment, region, placement (deploy target kinds mapped to placement
    /// kinds), the artifacts placed there, and managed services. A managed
    /// service is observed only through the control-plane health check it names.
    /// </summary>
    private static void AddStructure(List<StructureNode> nodes, ProjectRecord project,
        ProjectDeploymentDefinition deployment, Func<ProjectRecord, string, string, StructureProbe?>? probes)
    {
        var structure = deployment.Structure;
        var source = $"{project.Id}/.agent-studio/project.yml#structure";
        var declared = deployment.Environments.Select(environment => environment.Name).ToArray();
        var placements = structure.Placements.GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var types = deployment.Artifacts.GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Type, StringComparer.Ordinal);
        var regionLabels = structure.Regions.GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Label ?? group.Key, StringComparer.Ordinal);
        var ids = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        void Add(StructureNode node) { if (ids.Add(node.Id)) nodes.Add(node); }
        // Nested placements inherit environments and region from their parent chain.
        IReadOnlyList<string> Environments(ProjectStructurePlacement placement, int depth = 0)
            => placement.Environments.Count > 0 ? placement.Environments
                : depth < placements.Count && placement.Parent is { } parent && placements.TryGetValue(parent, out var up)
                    ? Environments(up, depth + 1) : declared;
        string Environment(string environment)
        {
            var id = $"environment:{project.Id}:{environment}";
            Add(new(id, "environment", environment.Length == 0 ? environment
                : char.ToUpperInvariant(environment[0]) + environment[1..], "installation", null, environment,
                project.Id, "declared", source));
            return id;
        }
        string Region(string environment, string? region)
        {
            var parent = Environment(environment);
            if (region is null) return parent;
            var id = $"region:{project.Id}:{environment}:{region}";
            Add(new(id, "region", regionLabels.GetValueOrDefault(region, region), parent, null, environment,
                project.Id, "declared", $"{source}/regions/{region}"));
            return id;
        }
        string? Namespace(ProjectStructurePlacement placement)
        {
            var current = placement;
            for (var depth = 0; depth <= placements.Count; depth++)
            {
                if (current.Kind == "namespace") return current.Name;
                if (current.Parent is not { } parent || !placements.TryGetValue(parent, out var next)) return null;
                current = next;
            }
            return null;
        }

        foreach (var placement in structure.Placements)
            foreach (var environment in Environments(placement))
            {
                var parent = placement.Parent is { } parentName && placements.TryGetValue(parentName, out var up)
                             && Environments(up).Contains(environment, StringComparer.Ordinal)
                    ? $"placement:{project.Id}:{environment}:{parentName}" : Region(environment, placement.Region);
                var id = $"placement:{project.Id}:{environment}:{placement.Name}";
                Add(new(id, ProjectStructureVocabulary.PlacementKindFor(placement.Kind), placement.Name, parent, null,
                    environment, project.Id, "declared", $"{source}/placements/{placement.Name}",
                    Detail: ProjectStructureVocabulary.PlacementDetail(placement.Kind)));
                foreach (var artifact in placement.Artifacts)
                {
                    // A placed artifact is more specific than the environment declaration's instance.
                    nodes.RemoveAll(node => node.Id == $"instance:{project.Id}:{environment}:{artifact}");
                    Add(new($"instance:{project.Id}:{environment}:{placement.Name}:{artifact}",
                        types.GetValueOrDefault(artifact) == "static" ? "process" : "container", artifact, id, artifact,
                        environment, project.Id, "declared", $"{source}/placements/{placement.Name}",
                        Namespace(placement), Replicas: placement.Replicas));
                }
            }
        foreach (var service in structure.ManagedServices)
            foreach (var environment in service.Environments.Count > 0 ? service.Environments : declared)
            {
                var probe = service.HealthCheck is { } check ? probes?.Invoke(project, environment, check) : null;
                Add(new($"managed:{project.Id}:{environment}:{service.Name}", "managed", service.Name,
                    Region(environment, service.Region), null, environment, project.Id, "declared",
                    $"{source}/managedServices/{service.Name}",
                    Detail: ProjectStructureVocabulary.ManagedServiceDetail(service.Kind),
                    ProbeObservation: probe is null ? "not-observed" : "probed", Probe: probe?.Kind, Status: probe?.Status));
            }
    }
}
