namespace AgentStudio.Management;

/// <summary>
/// Facts of one structure node (AGT-3954). Facts only: no ranking and no
/// wording. Assessments and the canvas layers attach to nodes, links and data
/// locations by id, so every id here is stable across reports and restarts.
/// Every member is optional; a host that does not report a value omits it.
/// </summary>
/// <param name="HostId">Reporting host id this node joins on.</param>
/// <param name="MachineKind">Kind the host detected for the place it runs in: container, vm, physical or unknown.</param>
/// <param name="Virtualization">Detected technology, for example kvm or docker.</param>
/// <param name="Roles">Runner roles that report from this node: coding, review, gate.</param>
public sealed record StructureNodeFacts(
    string? HostId = null,
    string? MachineKind = null,
    string? Virtualization = null,
    string? ContainerName = null,
    IReadOnlyList<string>? Roles = null,
    DateTime? LastSeenAt = null,
    StructurePerformanceFacts? Performance = null,
    StructureCapacityFacts? Capacity = null,
    StructureReleaseFacts? Release = null,
    IReadOnlyList<StructureDataLocation>? DataLocations = null,
    StructureWarningFacts? Warnings = null);

/// <summary>Performance and network of one machine or host, as last reported.</summary>
public sealed record StructurePerformanceFacts(
    DateTime ObservedAt,
    double? CpuPercent = null,
    int? CpuCores = null,
    double? Load1 = null,
    double? Load5 = null,
    double? Load15 = null,
    long? MemoryUsedBytes = null,
    long? MemoryTotalBytes = null,
    IReadOnlyList<StructureDiskFact>? Disks = null,
    long? NetworkReceiveBytesPerSecond = null,
    long? NetworkTransmitBytesPerSecond = null);

/// <summary>One mounted file system. <c>Purpose</c> names what the installation keeps there.</summary>
public sealed record StructureDiskFact(string Mount, string Purpose, long UsedBytes, long FreeBytes, long TotalBytes);

/// <summary>Slots and the cards that run on the node right now.</summary>
public sealed record StructureCapacityFacts(int BusySlots, int? FreeSlots, int? TotalSlots,
    IReadOnlyList<string> RunningCards);

/// <param name="State">current, behind, differs or unknown.</param>
/// <param name="PendingInstall">A host install or CLI update is requested and not finished.</param>
public sealed record StructureReleaseFacts(string? Version, string? StableVersion, string State, bool Behind,
    bool PendingInstall, string? Detail = null);

/// <summary>
/// A location that holds state. The absolute path stays on the host;
/// <c>SourceRef</c> names the setting that selects it.
/// </summary>
/// <param name="Id">Stable: <c>data:{machineId}:{role}</c>.</param>
/// <param name="Role">workspace-repository, project-checkouts, application-state, secrets-key or edge-certificates.</param>
/// <param name="SizeComplete">False when the bounded size walk stopped early.</param>
/// <param name="BackupCovered">A source of <c>/api/operator/backups</c> contains the location and backups are configured.</param>
/// <param name="GitRemoteCovered">The location is a git checkout with a remote, or every checkout in it has one.</param>
/// <param name="Coverage">backup, git-remote, both or none.</param>
public sealed record StructureDataLocation(string Id, string Role, string Label, string SourceRef, bool Exists,
    long? SizeBytes, bool SizeComplete, DateTime? MeasuredAt, bool BackupCovered, bool GitRemoteCovered,
    string Coverage);

/// <summary>What a warnings layer needs, unranked.</summary>
/// <param name="MissingRequired">Capability keys waiting work needs that this node does not offer.</param>
/// <param name="BehindRequired">Tools below the version the server targets.</param>
/// <param name="LinksDown">Ids of links of this node that are down.</param>
public sealed record StructureWarningFacts(
    IReadOnlyList<StructureToolFact> Tools,
    IReadOnlyList<string> MissingRequired,
    IReadOnlyList<string> BehindRequired,
    bool ReleaseBehind,
    bool PendingHostInstall,
    IReadOnlyList<string> LinksDown,
    IReadOnlyList<StructureBlockedCard> BlockedCards);

/// <param name="Kind">cli or toolchain.</param>
/// <param name="State">ok, behind, missing or unhealthy.</param>
public sealed record StructureToolFact(string Name, string Kind, string? Version, string? TargetVersion, string State);

/// <param name="State">parked (waiting for a capability this node holds back) or refused (a claim was refused).</param>
/// <param name="Reason">The capability key behind it.</param>
public sealed record StructureBlockedCard(string Key, string State, string Reason);

/// <param name="State">up, down, stale or not observed.</param>
/// <param name="LatencyMs">Round trip of the last probe, where the link is probed.</param>
public sealed record StructureLinkFacts(string State, double? LatencyMs = null, DateTime? ProbedAt = null,
    string? Transport = null);

/// <summary>
/// Changes of one structure scope since <see cref="BaseRevision"/>, sent on the
/// Studio hub as <c>structureDelta</c>. A client whose revision differs from
/// the base reads the whole structure again.
/// </summary>
public sealed record StructureDelta(string Scope, string? Environment, long BaseRevision, long Revision,
    DateTime GeneratedAt, IReadOnlyList<StructureNode> Nodes, IReadOnlyList<string> RemovedNodes,
    IReadOnlyList<StructureLink> Links, IReadOnlyList<string> RemovedLinks)
{
    public bool IsEmpty => Nodes.Count == 0 && RemovedNodes.Count == 0 && Links.Count == 0 && RemovedLinks.Count == 0;

    /// <summary>Changed and removed nodes and links by id. Nodes keep the order of <paramref name="current"/>.</summary>
    public static StructureDelta Between(string scope, string? environment, StructureResponse previous,
        StructureResponse current)
    {
        var before = previous.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var nodes = current.Nodes.Where(node => !before.TryGetValue(node.Id, out var old) || !Same(old, node)).ToArray();
        var ids = current.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var beforeLinks = previous.Links.Where(link => link.Id is not null)
            .GroupBy(link => link.Id!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var links = current.Links.Where(link => link.Id is null
            || !beforeLinks.TryGetValue(link.Id, out var old) || !Same(old, link)).ToArray();
        var linkIds = current.Links.Select(link => link.Id).ToHashSet(StringComparer.Ordinal);
        return new(scope, environment, previous.Revision, current.Revision, current.GeneratedAt ?? default, nodes,
            previous.Nodes.Where(node => !ids.Contains(node.Id)).Select(node => node.Id).ToArray(), links,
            beforeLinks.Keys.Where(id => !linkIds.Contains(id)).ToArray());
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    // Records with list members compare by reference; the serialized form is the value.
    private static bool Same<T>(T left, T right)
        => System.Text.Json.JsonSerializer.Serialize(left, Json) == System.Text.Json.JsonSerializer.Serialize(right, Json);
}
