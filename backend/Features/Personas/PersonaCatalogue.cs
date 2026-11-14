namespace AgentStudio.Personas;

public sealed record PersonaCatalogueEntry(PersonaDescriptorV1 Descriptor, string Digest);

/// <summary>
/// The built-in persona catalogue (PT-D1, PT-D12): the descriptors shipped as
/// <c>backend/Policies/personas/&lt;id&gt;.persona.json</c> and embedded in the
/// assembly. The catalogue is read-only product content; a workspace changes
/// how a persona runs through its activation, or forks it into a custom
/// persona, and never edits these files.
/// </summary>
public sealed class PersonaCatalogue
{
    public const string ResourcePrefix = "Policies.personas.";
    public const string ResourceSuffix = ".persona.json";

    private readonly IReadOnlyDictionary<string, PersonaCatalogueEntry> _entries;

    public PersonaCatalogue() : this(LoadEmbedded()) { }

    /// <summary>Builds a catalogue from descriptors; refuses an invalid descriptor or a duplicate id.</summary>
    public PersonaCatalogue(IEnumerable<PersonaDescriptorV1> descriptors)
    {
        var entries = new Dictionary<string, PersonaCatalogueEntry>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            var errors = PersonaDescriptorValidator.Validate(descriptor);
            if (errors.Count > 0)
                throw new InvalidOperationException($"Persona descriptor '{descriptor.Id}' is invalid: {string.Join(" ", errors)}");
            if (!entries.TryAdd(descriptor.Id, new PersonaCatalogueEntry(descriptor, PersonaDescriptorDigest.Compute(descriptor))))
                throw new InvalidOperationException($"Persona descriptor '{descriptor.Id}' is defined twice.");
        }
        _entries = entries;
    }

    public IReadOnlyList<PersonaCatalogueEntry> Entries
        => _entries.Values.OrderBy(entry => entry.Descriptor.Id, StringComparer.Ordinal).ToList();

    public PersonaCatalogueEntry? Find(string id) => _entries.GetValueOrDefault(id);

    private static IEnumerable<PersonaDescriptorV1> LoadEmbedded()
    {
        var assembly = typeof(PersonaCatalogue).Assembly;
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                                    && name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var descriptor = PersonaDescriptorJson.Parse(reader.ReadToEnd());
            var stem = name[ResourcePrefix.Length..^ResourceSuffix.Length];
            if (descriptor.Id != stem)
                throw new InvalidOperationException($"Persona descriptor '{descriptor.Id}' must live in '{descriptor.Id}{ResourceSuffix}', not '{stem}{ResourceSuffix}'.");
            yield return descriptor;
        }
    }
}
