using System.Text.Json;

namespace AgentStudio.Management;

/// <summary>
/// AGT-3954: the placement file. It declares only where things run
/// (<c>machines</c>, <c>externals</c>, <c>structureLinks</c>, the same schema
/// as the connectivity manifest sections) and is read from
/// <see cref="StructurePlacementLoader.PathKey"/>. It never feeds
/// <see cref="ConnectivityPolicy"/>: listeners, origins and runner links stay
/// owned by the connectivity manifest alone.
/// </summary>
public sealed record StructurePlacementFile(
    int SchemaVersion = 1,
    IReadOnlyList<ConnectivityMachine>? Machines = null,
    IReadOnlyList<ConnectivityExternal>? Externals = null,
    IReadOnlyList<ConnectivityStructureLink>? StructureLinks = null)
{
    public static StructurePlacementFile Parse(string json)
        => JsonSerializer.Deserialize<StructurePlacementFile>(json, ConnectivityPolicy.Json)
           ?? throw new JsonException("Placement file is empty.");
}

/// <summary>Declared placement after merging the placement file with any manifest placement sections.</summary>
/// <param name="Sources">Which configuration key declared each machine, external and link, keyed by their source reference.</param>
public sealed record StructurePlacement(
    IReadOnlyList<ConnectivityMachine> Machines,
    IReadOnlyList<ConnectivityExternal> Externals,
    IReadOnlyList<ConnectivityStructureLink> StructureLinks,
    IReadOnlyDictionary<object, string> Sources)
{
    public static StructurePlacement Empty { get; } = new([], [], [], new Dictionary<object, string>());

    public string SourceOf(object item, string fallback)
        => Sources.TryGetValue(item, out var source) ? source : fallback;
}

/// <summary>The placement read for one projection: what was declared, and why a file could not be used.</summary>
public sealed record StructurePlacementRead(StructurePlacement Placement, IReadOnlyList<ConnectivityFinding> Findings);

public static class StructurePlacementLoader
{
    public const string PathKey = "Structure:PlacementPath";

    /// <summary>
    /// Reads the placement file and merges it with the manifest's placement
    /// sections. The file wins on equal ids; manifest entries with other ids
    /// stay. An unreadable or invalid file is a finding, never an exception:
    /// observed hosts still show without it.
    /// </summary>
    public static StructurePlacementRead Read(IConfiguration configuration, InstallationConnectivityManifest? manifest)
    {
        var findings = new List<ConnectivityFinding>();
        StructurePlacementFile? file = null;
        var path = configuration[PathKey]?.Trim();
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                file = StructurePlacementFile.Parse(File.ReadAllText(Path.GetFullPath(path)));
                var invalid = Validate(file);
                if (invalid.Count > 0)
                {
                    findings.AddRange(invalid);
                    file = null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                           or NotSupportedException or ArgumentException)
            {
                findings.Add(new("placement-unreadable", PathKey, "The placement file could not be read.",
                    "Check that Structure:PlacementPath names a readable JSON file; see docs/operations/setup/structure-placement.md."));
            }
        }
        return new(Merge(file, manifest), findings);
    }

    public static StructurePlacement Merge(StructurePlacementFile? file, InstallationConnectivityManifest? manifest)
    {
        var sources = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        var machines = new List<ConnectivityMachine>();
        var externals = new List<ConnectivityExternal>();
        var links = new List<ConnectivityStructureLink>();
        void Take(IEnumerable<ConnectivityMachine>? fromMachines, IEnumerable<ConnectivityExternal>? fromExternals,
            IEnumerable<ConnectivityStructureLink>? fromLinks, string source)
        {
            foreach (var machine in fromMachines ?? [])
                if (!machines.Any(item => string.Equals(item.Id, machine.Id, StringComparison.OrdinalIgnoreCase)))
                { machines.Add(machine); sources[machine] = source; }
            foreach (var external in fromExternals ?? [])
                if (!externals.Any(item => string.Equals(item.Id, external.Id, StringComparison.OrdinalIgnoreCase)))
                { externals.Add(external); sources[external] = source; }
            foreach (var link in fromLinks ?? [])
                if (!links.Any(item => item.From == link.From && item.To == link.To && item.Kind == link.Kind))
                { links.Add(link); sources[link] = source; }
        }
        Take(file?.Machines, file?.Externals, file?.StructureLinks, PathKey);
        Take(manifest?.Machines, manifest?.Externals, manifest?.StructureLinks, ConnectivityManifestLoader.ManifestPathKey);
        return new(machines, externals, links, sources);
    }

    /// <summary>The machine identity, parent and cycle rules the manifest applies to its own placement sections.</summary>
    public static IReadOnlyList<ConnectivityFinding> Validate(StructurePlacementFile file)
    {
        var findings = new List<ConnectivityFinding>();
        if (file.SchemaVersion != 1)
            findings.Add(new("placement-schema-version", PathKey, $"Schema version {file.SchemaVersion} is not 1.",
                "Write schemaVersion 1 or leave it out."));
        ConnectivityPolicy.CheckMachines(file.Machines,
            (code, subject, message, remediation) => findings.Add(new(code, subject, message, remediation)));
        foreach (var external in file.Externals ?? [])
            if (string.IsNullOrWhiteSpace(external.Id) || string.IsNullOrWhiteSpace(external.Name))
                findings.Add(new("external-identity", external.Id ?? "external", "External id and name must be set.",
                    "Give each external a unique id and name."));
        foreach (var link in file.StructureLinks ?? [])
            if (string.IsNullOrWhiteSpace(link.From) || string.IsNullOrWhiteSpace(link.To) || string.IsNullOrWhiteSpace(link.Kind))
                findings.Add(new("structure-link", link.From ?? "structureLinks", "A structure link needs from, to and kind.",
                    "Name both ends by machine id or node id and give the link a kind."));
        return findings;
    }
}
