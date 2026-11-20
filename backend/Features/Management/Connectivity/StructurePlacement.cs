using System.Text.Json;

namespace AgentStudio.Management;

/// <summary>
/// Placement apart from connectivity (AGT-3954). The file holds only
/// <c>machines</c>, <c>externals</c> and <c>structureLinks</c>, in the schema
/// the connectivity manifest uses for them, and is read from its own setting.
/// It never reaches <see cref="ConnectivityPolicy"/>: listeners and runner
/// links cannot change because this file is set or edited.
/// </summary>
public sealed record StructurePlacement(
    IReadOnlyList<ConnectivityMachine>? Machines = null,
    IReadOnlyList<ConnectivityExternal>? Externals = null,
    IReadOnlyList<ConnectivityStructureLink>? StructureLinks = null)
{
    public const string PathKey = "Structure:PlacementPath";

    public static StructurePlacement Parse(string json)
        => JsonSerializer.Deserialize<StructurePlacement>(json, ConnectivityPolicy.Json)
           ?? throw new InvalidOperationException("Structure placement file is empty.");

    /// <summary>Identity findings. Parents may name a machine that only reports, so unknown parents are allowed.</summary>
    public IReadOnlyList<ConnectivityFinding> Validate()
    {
        var findings = new List<ConnectivityFinding>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var machine in Machines ?? [])
        {
            if (string.IsNullOrWhiteSpace(machine.Id) || string.IsNullOrWhiteSpace(machine.Name) || !ids.Add(machine.Id))
                findings.Add(new("machine-identity", machine.Id ?? "machine",
                    "Machine id and name must be set and ids must be unique.", "Give each machine a unique id and name."));
            if (machine.ParentId is { Length: > 0 } parent && string.Equals(parent, machine.Id, StringComparison.OrdinalIgnoreCase))
                findings.Add(new("machine-parent", machine.Id, "Machine parentId must name another machine.",
                    "Use the id of the enclosing machine."));
            var components = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var component in machine.Components ?? [])
                if (string.IsNullOrWhiteSpace(component.Id) || string.IsNullOrWhiteSpace(component.Name)
                    || !components.Add(component.Id))
                    findings.Add(new("component-identity", machine.Id ?? "machine",
                        "Component id and name must be set and ids must be unique per machine.",
                        "Give each component a unique id and name."));
        }
        var byId = (Machines ?? []).Where(machine => !string.IsNullOrWhiteSpace(machine.Id))
            .GroupBy(machine => machine.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var machine in byId.Values)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { machine.Id };
            var parent = machine.ParentId;
            while (parent is { Length: > 0 } && byId.TryGetValue(parent, out var ancestor))
            {
                if (!visited.Add(parent))
                {
                    findings.Add(new("machine-cycle", machine.Id, "Machine placement contains a cycle.",
                        "Remove the cyclic parentId."));
                    break;
                }
                parent = ancestor.ParentId;
            }
        }
        var externals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var external in Externals ?? [])
            if (string.IsNullOrWhiteSpace(external.Id) || string.IsNullOrWhiteSpace(external.Name) || !externals.Add(external.Id))
                findings.Add(new("external-identity", external.Id ?? "external",
                    "External id and name must be set and ids must be unique.", "Give each external a unique id and name."));
        return findings;
    }
}

/// <summary>What the placement file currently says, or why it cannot be used.</summary>
public sealed record StructurePlacementRead(StructurePlacement? Placement, string? Error);

/// <summary>Reads the placement file once per change of its timestamp or length.</summary>
public sealed class StructurePlacementSource(IConfiguration configuration)
{
    private readonly object _gate = new();
    private (string Path, DateTime WrittenAt, long Length, StructurePlacementRead Read)? _cached;

    public StructurePlacementRead Read()
    {
        var path = configuration[StructurePlacement.PathKey]?.Trim();
        if (string.IsNullOrEmpty(path)) return new(null, null);
        try
        {
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists) return new(null, "Structure placement file is unavailable.");
            lock (_gate)
            {
                if (_cached is { } cached && cached.Path == file.FullName
                    && cached.WrittenAt == file.LastWriteTimeUtc && cached.Length == file.Length)
                    return cached.Read;
                var placement = StructurePlacement.Parse(File.ReadAllText(file.FullName));
                var read = placement.Validate() is { Count: > 0 } findings
                    ? new StructurePlacementRead(null, "Structure placement file is invalid: " + findings[0].Message)
                    : new StructurePlacementRead(placement, null);
                _cached = (file.FullName, file.LastWriteTimeUtc, file.Length, read);
                return read;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or JsonException or InvalidOperationException or ArgumentException
                                              or NotSupportedException)
        { return new(null, "Structure placement file is unavailable."); }
    }
}
