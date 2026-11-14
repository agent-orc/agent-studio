using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentStudio.Personas;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The built-in persona catalogue (persona team PT-1, decisions PT-D1 and
/// PT-D12). Every <c>backend/Policies/personas/*.persona.json</c> validates
/// against <c>docs/app/schemas/persona.schema.json</c> and the in-code rules,
/// the embedded catalogue matches the files, and
/// <c>revisions.lock.json</c> pins the digest of every published revision, so
/// a file whose content changed without a new <c>revision</c> fails here.
/// </summary>
public sealed class PersonaCatalogueTests
{
    private static readonly string[] StarterIds =
    [
        "budget-steward", "chief-of-staff", "cost-controller", "design-lead", "documentation-editor", "improvement-scout", "operations-warden",
        "presenter", "product-manager", "quality-lead", "reliability-engineer", "security-officer",
    ];

    [Fact]
    public void Every_starter_descriptor_validates_against_the_schema()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaPath()));
        var files = DescriptorFiles();

        Assert.Equal(StarterIds, files.Select(file => Path.GetFileName(file)[..^".persona.json".Length]));
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var errors = JsonSchemaSubsetValidator.Validate(schema.RootElement, document.RootElement);
            Assert.True(errors.Count == 0, $"{Path.GetFileName(file)}:\n  " + string.Join("\n  ", errors));
            var descriptor = PersonaDescriptorJson.Parse(File.ReadAllText(file));
            Assert.Equal(Path.GetFileName(file), descriptor.Id + ".persona.json");
            Assert.Empty(PersonaDescriptorValidator.Validate(descriptor));
        }
    }

    [Fact]
    public void The_embedded_catalogue_is_the_set_of_descriptor_files()
    {
        var catalogue = new PersonaCatalogue();
        var files = DescriptorFiles().Select(file => PersonaDescriptorJson.Parse(File.ReadAllText(file))).ToList();

        Assert.Equal(StarterIds, catalogue.Entries.Select(entry => entry.Descriptor.Id));
        Assert.Equal(files.Select(PersonaDescriptorDigest.Compute), catalogue.Entries.Select(entry => entry.Digest));
        Assert.All(catalogue.Entries, entry => Assert.Matches("^sha256:[0-9a-f]{64}$", entry.Digest));
    }

    [Fact]
    public void Starter_defaults_follow_the_dossier_catalogue()
    {
        var entries = new PersonaCatalogue().Entries.Select(entry => entry.Descriptor).ToList();

        // The scout joins the catalogue with its former 110,000-token Watcher subceiling.
        Assert.Equal(1_220_000, entries.Sum(descriptor => descriptor.Budget.TokensPerWeek));
        Assert.Equal(["cost-controller", "operations-warden"],
            entries.Where(descriptor => descriptor.StartsActive == true).Select(descriptor => descriptor.Id));
        Assert.Equal((9, 1, 2), (entries.Count(d => d.Kind == PersonaKinds.Persona), entries.Count(d => d.Kind == PersonaKinds.Presenter),
            entries.Count(d => d.Kind == PersonaKinds.Manager)));
        Assert.All(entries.Where(d => (d.Kind is PersonaKinds.Persona or PersonaKinds.Presenter) &&
            d.Id != "operations-warden"), d => Assert.NotNull(PersonaReportTemplates.Find(d.Report.Template)));
        Assert.All(entries.Where(d => d.Kind == PersonaKinds.Manager), d => Assert.Equal(PersonaAutonomy.Decide, d.Attributes.Autonomy));

        var warden = entries.Single(descriptor => descriptor.Id == "operations-warden");
        Assert.Equal(("2026.11.1", "operations", "balanced", "high"),
            (warden.Revision, warden.Aspect, warden.Model.CapabilityClass, warden.Model.Thinking));
        Assert.Equal(new PersonaBudgetV1(140_000, 10, 4, 2, 15), warden.Budget);
        Assert.Equal(("P1D", "06:30", "P7D"), (warden.Cadence.Report, warden.Cadence.DueAtLocal, warden.Cadence.Window));
        Assert.Equal(["backups=40", "credentials=25", "exposure=20", "patch-level=15"],
            warden.Attributes.Focus!.Select(pair => $"{pair.Key}={pair.Value}"));
        Assert.Subset(warden.Floors.ToHashSet(), new HashSet<string>
        {
            "backup.none-scheduled:95", "backup.last-good-age>48h:90", "credential.expired:95", "restore-test.age>35d:70",
            "escrow.unconfirmed:85",
        });
    }

    [Fact]
    public void The_schema_and_the_validator_refuse_the_same_broken_descriptor()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaPath()));
        var warden = WardenFromFile();
        var broken = warden with { Charter = new string('x', 601), Kind = "robot" };

        var schemaErrors = JsonSchemaSubsetValidator.Validate(schema.RootElement,
            JsonSerializer.SerializeToElement(broken, PersonaDescriptorJson.Options));
        Assert.Contains(schemaErrors, error => error.StartsWith("$.charter:", StringComparison.Ordinal));
        Assert.Contains(schemaErrors, error => error.StartsWith("$.kind:", StringComparison.Ordinal));
        var errors = PersonaDescriptorValidator.Validate(broken);
        Assert.Contains(errors, error => error.StartsWith("charter", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("kind", StringComparison.Ordinal));
        Assert.Throws<JsonException>(() => PersonaDescriptorJson.Parse(
            File.ReadAllText(Path.Combine(PersonasFolder(), "operations-warden.persona.json")).Replace("\"kind\"", "\"kinds\"")));
    }

    [Fact]
    public void Every_descriptor_revision_is_recorded_with_its_digest()
    {
        var descriptors = DescriptorFiles().Select(file => PersonaDescriptorJson.Parse(File.ReadAllText(file)));
        var violations = RevisionViolations(LockedRevisions(), descriptors);
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void A_content_change_without_a_new_revision_fails_the_revision_check()
    {
        var locked = LockedRevisions();
        var warden = WardenFromFile();

        var unversioned = warden with { Charter = warden.Charter + " Also watch the weather." };
        Assert.Contains(RevisionViolations(locked, [unversioned]),
            violation => violation.Contains("changed without a new revision", StringComparison.Ordinal));

        var versioned = unversioned with { Revision = "2026.11.2" };
        var pending = Assert.Single(RevisionViolations(locked, [versioned]));
        Assert.Contains("is not recorded", pending, StringComparison.Ordinal);
        var recorded = locked.ToDictionary(pair => pair.Key, pair => pair.Value);
        recorded[warden.Id] = new Dictionary<string, string>(locked[warden.Id])
        {
            ["2026.11.2"] = PersonaDescriptorDigest.Compute(versioned),
        };
        Assert.Empty(RevisionViolations(recorded, [versioned]));

        Assert.Contains("must be newer", Assert.Single(RevisionViolations(locked, [versioned with { Revision = "2026.10.7" }])),
            StringComparison.Ordinal);
        // Formatting and key order are not content: the digest reads the values only.
        Assert.Equal(PersonaDescriptorDigest.Compute(warden),
            PersonaDescriptorDigest.Compute(PersonaDescriptorJson.Parse(JsonSerializer.Serialize(warden, PersonaDescriptorJson.Options))));
    }

    /// <summary>
    /// A file's revision must be locked with exactly its digest. A new revision
    /// must be newer than every locked one and is added to the lock in the same
    /// change; the lock only grows.
    /// </summary>
    internal static IReadOnlyList<string> RevisionViolations(
        IReadOnlyDictionary<string, Dictionary<string, string>> locked, IEnumerable<PersonaDescriptorV1> descriptors)
    {
        var violations = new List<string>();
        foreach (var descriptor in descriptors)
        {
            var digest = PersonaDescriptorDigest.Compute(descriptor);
            var entry = $"\"{descriptor.Id}\": {{ \"{descriptor.Revision}\": \"{digest}\" }}";
            if (!locked.TryGetValue(descriptor.Id, out var revisions))
            {
                violations.Add($"{descriptor.Id}: revision {descriptor.Revision} is not recorded in revisions.lock.json; add {entry}.");
                continue;
            }
            if (revisions.TryGetValue(descriptor.Revision, out var lockedDigest))
            {
                if (lockedDigest != digest)
                    violations.Add($"{descriptor.Id}: content changed without a new revision; {descriptor.Revision} is locked as "
                        + $"{lockedDigest}, the file now hashes to {digest}. Raise 'revision' and lock the new one.");
                continue;
            }
            var newest = revisions.Keys.MaxBy(RevisionKey)!;
            violations.Add(RevisionKey(descriptor.Revision).CompareTo(RevisionKey(newest)) > 0
                ? $"{descriptor.Id}: revision {descriptor.Revision} is not recorded in revisions.lock.json; add \"{descriptor.Revision}\": \"{digest}\"."
                : $"{descriptor.Id}: revision {descriptor.Revision} must be newer than the locked {newest}.");
        }
        return violations;
    }

    private static (int, int, int) RevisionKey(string revision)
    {
        var parts = revision.Split('.').Select(int.Parse).ToArray();
        return (parts[0], parts[1], parts[2]);
    }

    private static IReadOnlyDictionary<string, Dictionary<string, string>> LockedRevisions()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(PersonasFolder(), "revisions.lock.json")));
        return document.RootElement.GetProperty("revisions").EnumerateObject().ToDictionary(
            persona => persona.Name,
            persona => persona.Value.EnumerateObject().ToDictionary(revision => revision.Name, revision => revision.Value.GetString()!));
    }

    private static PersonaDescriptorV1 WardenFromFile()
        => PersonaDescriptorJson.Parse(File.ReadAllText(Path.Combine(PersonasFolder(), "operations-warden.persona.json")));

    private static IReadOnlyList<string> DescriptorFiles()
        => Directory.GetFiles(PersonasFolder(), "*.persona.json").Order(StringComparer.Ordinal).ToList();

    private static string PersonasFolder() => Path.Combine(RepoRoot(), "backend", "Policies", "personas");

    private static string SchemaPath() => Path.Combine(RepoRoot(), "docs", "app", "schemas", "persona.schema.json");

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, ".."));
}
