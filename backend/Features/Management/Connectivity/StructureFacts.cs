namespace AgentStudio.Management;

/// <summary>
/// AGT-3954: performance, network, capability and data facts of one structure
/// node. Facts only: no ranking, no wording beyond the source detail. Every
/// field is optional so a host that reports less still shows. The shape is a
/// contract for the structure canvas and the assessment layers, see
/// docs/system/contracts/operations-structure.md.
/// </summary>
public sealed record StructureNodeFacts(
    string? HostId = null,
    StructureMachineFact? Machine = null,
    IReadOnlyList<StructureRoleFact>? Roles = null,
    int? CpuCores = null,
    double? CpuPercent = null,
    double? Load1 = null,
    double? Load5 = null,
    double? Load15 = null,
    long? MemoryUsedBytes = null,
    long? MemoryTotalBytes = null,
    IReadOnlyList<StructureMountFact>? Mounts = null,
    long? NetworkReceiveBytesPerSecond = null,
    long? NetworkTransmitBytesPerSecond = null,
    int? BusySlots = null,
    int? FreeSlots = null,
    IReadOnlyList<string>? RunningCards = null,
    string? Version = null,
    string? StableVersion = null,
    string? ReleaseState = null,
    string? ReleaseDetail = null,
    string? PendingHostUpdate = null,
    DateTime? LastSeenAt = null,
    DateTime? TelemetryObservedAt = null,
    string? AdmissionState = null,
    IReadOnlyList<StructureToolFact>? Tools = null,
    IReadOnlyList<StructureRequirementFact>? Requirements = null,
    IReadOnlyList<string>? LinksDown = null,
    IReadOnlyList<string>? HeldCards = null,
    IReadOnlyList<StructureDataLocation>? DataLocations = null,
    string? ImageDigest = null,
    string? ContainerState = null);

/// <summary>Where a host runs, as it reported. <c>ReportedParentId</c> is the configured parent machine id.</summary>
public sealed record StructureMachineFact(string Kind, string? Virtualization, string? ContainerRuntime,
    string? ContainerName, string? ReportedParentId, string Source);

/// <summary>One runner identity (coding, review or gate role) reporting from a host.</summary>
public sealed record StructureRoleFact(string RunnerId, string Role, string Status, string? Version,
    DateTime LastSeenAt, int? BusySlots, int? FreeSlots, IReadOnlyList<string> RunningCards);

/// <summary>One file system the host measured. <c>Purpose</c> names what the host keeps there.</summary>
public sealed record StructureMountFact(string Purpose, string Mount, long UsedBytes, long FreeBytes, long TotalBytes);

/// <summary>A toolchain or CLI the host reports, with its version and whether a required target is not met.</summary>
public sealed record StructureToolFact(string Key, string Name, string Status, string? Version,
    string? TargetVersion = null, bool Behind = false);

/// <summary>
/// A required capability the existing capability and readiness checks find
/// missing, not ready, held or behind on this host, with the cards waiting for it.
/// </summary>
/// <param name="State">missing, not-ready, held, indeterminate, stale or behind.</param>
public sealed record StructureRequirementFact(string Key, string State, string? Detail,
    IReadOnlyList<string>? WaitingCards = null);

/// <summary>
/// A data location that holds installation state, as a fact on the machine
/// that keeps it. The id is stable so the Backup layer and assessments attach
/// to it. Paths stay out of the response; the role names the location.
/// </summary>
/// <param name="Role">workspace-repository, project-checkouts, application-state, secrets-key, edge-certificates, or a backup source name.</param>
/// <param name="BackupCovered">A configured <c>/api/operator/backups</c> source contains the location.</param>
/// <param name="GitRemoteCovered">Every checkout below the location has a Git remote (project checkouts only).</param>
public sealed record StructureDataLocation(string Id, string Role, string Label, long? SizeBytes,
    bool BackupCovered, bool? GitRemoteCovered, DateTime? MeasuredAt, string? Detail = null);

/// <summary>Link facts: what kind of connection, its state, and the probe round trip where one is probed.</summary>
public sealed record StructureLinkFacts(string State, long? LatencyMs = null, DateTime? ProbedAt = null,
    string? Transport = null);

/// <summary>
/// A change of the workspace structure between two snapshot revisions,
/// pushed as <c>structureChanged</c> on the Studio hub. A client applies it
/// only when its revision equals <c>FromRevision</c>; otherwise it reads the
/// whole snapshot again.
/// </summary>
public sealed record StructureDelta(long FromRevision, long Revision, DateTime GeneratedAt,
    IReadOnlyList<StructureNode> UpsertNodes, IReadOnlyList<string> RemoveNodes,
    IReadOnlyList<StructureLink> UpsertLinks, IReadOnlyList<string> RemoveLinks,
    IReadOnlyList<ConnectivityFinding>? Findings = null)
{
    public bool IsEmpty => UpsertNodes.Count == 0 && RemoveNodes.Count == 0
                           && UpsertLinks.Count == 0 && RemoveLinks.Count == 0;
}

/// <summary>Stable link identity and the delta between two snapshots.</summary>
public static class StructureIdentity
{
    /// <summary><c>link:{from}|{to}|{kind}</c>, with <c>#n</c> for a repeated triple in source order.</summary>
    public static IReadOnlyList<StructureLink> AssignLinkIds(IEnumerable<StructureLink> links)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        return links.Select(link =>
        {
            if (link.Id is not null) return link;
            var key = $"link:{link.From}|{link.To}|{link.Kind}";
            var count = seen[key] = seen.GetValueOrDefault(key) + 1;
            return link with { Id = count == 1 ? key : $"{key}#{count}" };
        }).ToArray();
    }

    public static StructureDelta Diff(StructureResponse before, StructureResponse after)
    {
        var oldNodes = before.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var newNodes = after.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var oldLinks = before.Links.ToDictionary(link => link.Id!, StringComparer.Ordinal);
        var newLinks = after.Links.ToDictionary(link => link.Id!, StringComparer.Ordinal);
        return new(before.Revision, after.Revision, after.GeneratedAt ?? before.GeneratedAt ?? default,
            after.Nodes.Where(node => !oldNodes.TryGetValue(node.Id, out var old) || !Same(old, node)).ToArray(),
            before.Nodes.Where(node => !newNodes.ContainsKey(node.Id)).Select(node => node.Id).ToArray(),
            after.Links.Where(link => !oldLinks.TryGetValue(link.Id!, out var old) || !Same(old, link)).ToArray(),
            before.Links.Where(link => !newLinks.ContainsKey(link.Id!)).Select(link => link.Id!).ToArray(),
            after.Findings);
    }

    /// <summary>Records hold lists, so equality compares the serialized shape.</summary>
    public static bool Same<T>(T left, T right)
        => System.Text.Json.JsonSerializer.Serialize(left, ConnectivityPolicy.Json)
           == System.Text.Json.JsonSerializer.Serialize(right, ConnectivityPolicy.Json);
}
