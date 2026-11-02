namespace AgentStudio.TaskServer.Contracts;

/// <summary>A region a project's placements and managed services live in.</summary>
public sealed record ProjectStructureRegion(string Name, string? Label);

/// <summary>
/// A declared placement: a cluster, namespace, VM or server, or a runner deploy
/// target kind that maps to one. <see cref="Artifacts"/> names the project
/// artifacts that run there in each listed environment.
/// </summary>
public sealed record ProjectStructurePlacement(string Name, string Kind, string? Parent, string? Region,
    IReadOnlyList<string> Environments, IReadOnlyList<string> Artifacts, int? Replicas);

/// <summary>
/// State the system depends on but the platform cannot enter. A
/// <see cref="HealthCheck"/> names an environment health check whose
/// control-plane probe observes it; without one it is declared, not observed.
/// </summary>
public sealed record ProjectManagedService(string Name, string Kind, string? Region,
    IReadOnlyList<string> Environments, string? HealthCheck);

/// <summary>The optional <c>structure</c> section of project.yml schema 2 (AGT-3349 S5).</summary>
public sealed record ProjectStructureDefinition(
    IReadOnlyList<ProjectStructureRegion> Regions,
    IReadOnlyList<ProjectStructurePlacement> Placements,
    IReadOnlyList<ProjectManagedService> ManagedServices)
{
    public static readonly ProjectStructureDefinition Empty = new([], [], []);
    public bool IsConfigured => Regions.Count > 0 || Placements.Count > 0 || ManagedServices.Count > 0;
}

/// <summary>
/// Keys and kinds of the <c>structure</c> section, shared by the reader, the
/// validator, the structure projection and the JSON schema sync test.
/// </summary>
public static class ProjectStructureVocabulary
{
    public static readonly IReadOnlyList<string> Sections = ["regions", "placements", "managedServices"];
    public static readonly IReadOnlyList<string> RegionKeys = ["name", "label"];
    public static readonly IReadOnlyList<string> PlacementKeys =
        ["name", "kind", "parent", "region", "environments", "artifacts", "replicas"];
    public static readonly IReadOnlyList<string> ManagedServiceKeys =
        ["name", "kind", "region", "environments", "healthCheck"];
    /// <summary>The runner's <c>IDeployTargetAdapter.Kind</c> values.</summary>
    public static readonly IReadOnlyList<string> DeployTargetKinds = ["ssh-docker", "kubernetes", "cloud-run"];
    public static readonly IReadOnlyList<string> PlacementKinds =
        ["server", "vm", "cluster", "namespace", .. DeployTargetKinds];
    public static readonly IReadOnlyList<string> ManagedServiceKinds =
        ["database", "queue", "object-storage", "cache", "search", "other"];
    public const int MaxReplicas = 1000;

    /// <summary>
    /// The placement kind the structure view draws. A deploy target kind maps to
    /// the box it deploys into: an SSH Docker host is a server, a Kubernetes
    /// target a cluster, Cloud Run a managed runtime.
    /// </summary>
    public static string PlacementKindFor(string kind) => kind switch
    {
        "ssh-docker" => "server",
        "kubernetes" => "cluster",
        "cloud-run" => "managed-runtime",
        _ => kind,
    };

    /// <summary>The kind line shown under a placement label; null when the kind says enough.</summary>
    public static string? PlacementDetail(string kind) => kind switch
    {
        "ssh-docker" => "SSH Docker host",
        "kubernetes" => "Kubernetes",
        "cloud-run" => "Cloud Run",
        _ => null,
    };

    public static string ManagedServiceDetail(string kind) => kind switch
    {
        "object-storage" => "object storage",
        _ => kind,
    };
}

public static partial class ProjectDefinitionReader
{
    private static ProjectStructureDefinition ParseStructure(IReadOnlyList<YamlLine> lines,
        List<ProjectDefinitionIssue> issues)
    {
        CheckKeys(Section(lines, "structure"), 2, ProjectStructureVocabulary.Sections.ToArray(), "structure", issues);
        var regions = SequenceMaps(lines, "structure.regions", 4, 6, issues)
            .Select((item, index) =>
            {
                CheckMapKeys(item, ProjectStructureVocabulary.RegionKeys, $"structure.regions[{index}]", issues);
                return new ProjectStructureRegion(Get(item, "name") ?? "", Get(item, "label"));
            }).ToArray();
        var placements = SequenceMaps(lines, "structure.placements", 4, 6, issues)
            .Select((item, index) =>
            {
                var path = $"structure.placements[{index}]";
                CheckMapKeys(item, ProjectStructureVocabulary.PlacementKeys, path, issues);
                int? replicas = null;
                if (Get(item, "replicas") is { } text)
                {
                    if (int.TryParse(text, out var parsed)) replicas = parsed;
                    else issues.Add(new($"{path}.replicas", "structure-replicas-invalid",
                        "replicas must be a whole number."));
                }
                return new ProjectStructurePlacement(Get(item, "name") ?? "", Get(item, "kind") ?? "",
                    Get(item, "parent"), Get(item, "region"), List(item, "environments"),
                    List(item, "artifacts"), replicas);
            }).ToArray();
        var managed = SequenceMaps(lines, "structure.managedServices", 4, 6, issues)
            .Select((item, index) =>
            {
                CheckMapKeys(item, ProjectStructureVocabulary.ManagedServiceKeys,
                    $"structure.managedServices[{index}]", issues);
                return new ProjectManagedService(Get(item, "name") ?? "", Get(item, "kind") ?? "",
                    Get(item, "region"), List(item, "environments"), Get(item, "healthCheck"));
            }).ToArray();
        return new(regions, placements, managed);
    }
}

public static partial class ProjectDefinitionValidator
{
    private static void ValidateStructure(ProjectStructureDefinition structure, HashSet<string> environments,
        HashSet<string> artifacts, List<ProjectDefinitionIssue> issues)
    {
        var regions = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < structure.Regions.Count; i++)
            if (!Identifier().IsMatch(structure.Regions[i].Name) || !regions.Add(structure.Regions[i].Name))
                issues.Add(new($"structure.regions[{i}].name", "structure-name-invalid",
                    "Region names must be unique lower-case identifiers."));
        void Scope(string path, string? region, IReadOnlyList<string> scoped)
        {
            if (region is not null && !regions.Contains(region))
                issues.Add(new($"{path}.region", "structure-region-unknown", "The region must be declared under structure.regions."));
            if (scoped.Any(environment => !environments.Contains(environment)))
                issues.Add(new($"{path}.environments", "structure-environment-unknown", "Structure environments must be declared."));
        }

        var placements = new Dictionary<string, ProjectStructurePlacement>(StringComparer.Ordinal);
        for (var i = 0; i < structure.Placements.Count; i++)
        {
            var placement = structure.Placements[i];
            var path = $"structure.placements[{i}]";
            if (!Identifier().IsMatch(placement.Name) || !placements.TryAdd(placement.Name, placement))
                issues.Add(new($"{path}.name", "structure-name-invalid", "Placement names must be unique lower-case identifiers."));
            if (!ProjectStructureVocabulary.PlacementKinds.Contains(placement.Kind, StringComparer.Ordinal))
                issues.Add(new($"{path}.kind", "structure-placement-kind-invalid",
                    $"Placement kind must be one of {string.Join(", ", ProjectStructureVocabulary.PlacementKinds)}."));
            if (placement.Replicas is < 1 or > ProjectStructureVocabulary.MaxReplicas)
                issues.Add(new($"{path}.replicas", "structure-replicas-invalid",
                    $"replicas must be between 1 and {ProjectStructureVocabulary.MaxReplicas}."));
            if (placement.Artifacts.Any(artifact => !artifacts.Contains(artifact)))
                issues.Add(new($"{path}.artifacts", "structure-artifact-unknown", "Placed artifacts must be declared under artifacts."));
            Scope(path, placement.Region, placement.Environments);
        }
        for (var i = 0; i < structure.Placements.Count; i++)
        {
            var placement = structure.Placements[i];
            var path = $"structure.placements[{i}]";
            if (placement.Parent is { } parentName)
            {
                if (!placements.TryGetValue(parentName, out var parent) || parentName == placement.Name)
                    issues.Add(new($"{path}.parent", "structure-parent-unknown", "The parent must be another declared placement."));
                else if (HasCycle(placement, placements))
                    issues.Add(new($"{path}.parent", "structure-parent-cycle", "Placement parents must not form a cycle."));
                else if (placement.Region is not null && EffectiveRegion(parent, placements) is { } inherited
                         && inherited != placement.Region)
                    issues.Add(new($"{path}.region", "structure-region-conflict", "A nested placement stays in its parent's region."));
            }
            if (placement.Kind == "namespace" && (placement.Parent is null || !placements.TryGetValue(placement.Parent, out var cluster)
                    || ProjectStructureVocabulary.PlacementKindFor(cluster.Kind) != "cluster"))
                issues.Add(new($"{path}.parent", "structure-namespace-parent-invalid", "A namespace needs a cluster or kubernetes parent."));
        }

        var services = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < structure.ManagedServices.Count; i++)
        {
            var service = structure.ManagedServices[i];
            var path = $"structure.managedServices[{i}]";
            if (!Identifier().IsMatch(service.Name) || !services.Add(service.Name))
                issues.Add(new($"{path}.name", "structure-name-invalid", "Managed service names must be unique lower-case identifiers."));
            if (!ProjectStructureVocabulary.ManagedServiceKinds.Contains(service.Kind, StringComparer.Ordinal))
                issues.Add(new($"{path}.kind", "structure-managed-kind-invalid",
                    $"Managed service kind must be one of {string.Join(", ", ProjectStructureVocabulary.ManagedServiceKinds)}."));
            if (service.HealthCheck is { } check && !Identifier().IsMatch(check))
                issues.Add(new($"{path}.healthCheck", "structure-health-check-invalid",
                    "healthCheck names an environment health check id."));
            Scope(path, service.Region, service.Environments);
        }
    }

    private static bool HasCycle(ProjectStructurePlacement start, IReadOnlyDictionary<string, ProjectStructurePlacement> placements)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start.Name };
        for (var current = start; current.Parent is { } parent && placements.TryGetValue(parent, out var next); current = next)
            if (!seen.Add(next.Name)) return true;
        return false;
    }

    private static string? EffectiveRegion(ProjectStructurePlacement placement,
        IReadOnlyDictionary<string, ProjectStructurePlacement> placements)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (ProjectStructurePlacement? current = placement; current is not null && seen.Add(current.Name);
             current = current.Parent is { } parent ? placements.GetValueOrDefault(parent) : null)
            if (current.Region is { } region) return region;
        return null;
    }
}
