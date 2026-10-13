using System.Text.RegularExpressions;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// Keeps serial test execution in OrchestratorApi.Tests deliberate (AGT-3106).
/// xUnit v2 runs a <c>DisableParallelization</c> collection only after every
/// parallel collection has finished, one test at a time. One such collection
/// had grown to 25 host classes, most without a reason, and held 62-86 % of
/// the suite's wall time. Two rules keep that from coming back:
/// <list type="number">
///   <item><b>Listed serial collections.</b> Every <c>DisableParallelization</c>
///   collection and every class in it is listed in
///   <c>Fixtures/serial-collections.txt</c> with the reason it cannot run in
///   parallel. A new collection or member fails until it is listed; an entry
///   whose code is gone fails until it is removed.</item>
///   <item><b>Environment writes stay serial.</b>
///   <c>Environment.SetEnvironmentVariable</c> changes the configuration of
///   every host booting in the same process at that moment, so it may appear
///   only inside a member of a listed serial collection. Pass host settings
///   with <c>UseSetting</c> (<see cref="TestHostSettings"/>) instead. Files
///   that predate the guard are in the same listing with their exact count;
///   that baseline only shrinks.</item>
/// </list>
/// </summary>
public sealed partial class SerialCollectionGuardTests
{
    private const string Project = "backend.Tests";
    private const string ListingPath = "backend.Tests/Architecture/Fixtures/serial-collections.txt";

    [Fact]
    public void Serial_collections_their_members_and_environment_writes_are_listed()
    {
        var root = CSharpSourceScanner.RepoRoot();
        var violations = Violations(ScanProject(root), ReadListing(root));

        Assert.True(
            violations.Count == 0,
            $"Serial test execution must be deliberate; see {ListingPath}:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Listing_only_names_what_still_needs_it()
    {
        var root = CSharpSourceScanner.RepoRoot();
        var stale = Stale(ScanProject(root), ReadListing(root));

        Assert.True(
            stale.Count == 0,
            $"The serial-collection listing only shrinks; update {ListingPath}:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Guard_flags_an_unlisted_serial_collection_member_and_environment_write()
    {
        const string definition = """
            [CollectionDefinition(Name, DisableParallelization = true)]
            public sealed class ClockSerialCollection
            {
                public const string Name = "ClockSerial";
            }
            """;
        const string member = """
            [Collection(ClockSerialCollection.Name)]
            public sealed class ClockTests
            {
            }

            public sealed class ProfileTests
            {
                [Fact]
                public void Boots_the_demo_profile()
                {
                    Environment.SetEnvironmentVariable("Security__Profile", "public-demo");
                }
            }
            """;

        var scan = Scan([("backend.Tests/ClockSerialCollection.cs", definition), ("backend.Tests/ClockTests.cs", member)]);
        var violations = Violations(scan, Listing.Parse([]));

        Assert.Equal(
            [
                "backend.Tests/ClockSerialCollection.cs:1: serial collection ClockSerialCollection (\"ClockSerial\") is not listed. "
                + "Remove DisableParallelization, or add 'collection ClockSerialCollection <reason>'.",
                "backend.Tests/ClockTests.cs:1: ClockTests runs serially in ClockSerialCollection but is not listed. "
                + "Make it parallel, or add 'member ClockSerialCollection ClockTests'.",
                "backend.Tests/ClockTests.cs: writes the process environment 1 time(s) (baseline 0) outside a serial "
                + "collection at line(s) 11. Pass the value to the host with UseSetting (TestHostSettings) instead.",
            ],
            violations);
    }

    [Fact]
    public void Guard_accepts_listed_entries_every_name_form_and_mentions_in_text()
    {
        const string definitions = """
            [CollectionDefinition("LiveCli", DisableParallelization = true)]
            public sealed class LiveCliCollection { }

            [CollectionDefinition(EnvironmentCollection.Name, DisableParallelization = true)]
            public sealed class EnvironmentCollection
            {
                public const string Name = "Environment";
            }

            [CollectionDefinition("Shared host")]
            public sealed class SharedHostCollection { }
            """;
        const string members = """
            [Collection("LiveCli")]
            public sealed class LiveTests { }

            [Collection(EnvironmentCollection.Name)]
            public sealed class EnvironmentTests
            {
                public void Inherits() => Environment.SetEnvironmentVariable("URLS", "http://127.0.0.1:5031");

                private sealed class Scope
                {
                    public Scope() => Environment.SetEnvironmentVariable("URLS", null);
                }
            }

            [Collection("Shared host")]
            public sealed class SharedHostTests
            {
                // Environment.SetEnvironmentVariable would race every host here.
                private const string Hint = "never call Environment.SetEnvironmentVariable";
            }
            """;
        var listing = Listing.Parse(
        [
            "collection LiveCliCollection the live CLIs share one session database",
            "collection EnvironmentCollection its members write the process environment",
            "member LiveCliCollection LiveTests",
            "member EnvironmentCollection EnvironmentTests",
        ]);

        var scan = Scan([("backend.Tests/Collections.cs", definitions), ("backend.Tests/Members.cs", members)]);

        Assert.Equal(["EnvironmentCollection", "LiveCliCollection"], scan.Collections.Select(c => c.Definition).Order());
        Assert.Empty(Violations(scan, listing));
        Assert.Empty(Stale(scan, listing));
    }

    [Fact]
    public void Guard_reports_stale_entries_and_a_baseline_that_shrank()
    {
        const string source = """
            public sealed class CodexHomeTests
            {
                public void Reads_the_home() => Environment.SetEnvironmentVariable("CODEX_HOME", null);
            }
            """;
        var listing = Listing.Parse(
        [
            "collection GoneCollection it used to share a port",
            "member GoneCollection GoneTests",
            "environment-baseline backend.Tests/CodexHomeTests.cs 2",
            "process-start backend.Tests/Gone.cs it used to strip the launcher's listener",
        ]);

        var stale = Stale(Scan([("backend.Tests/CodexHomeTests.cs", source)]), listing);

        Assert.Equal(
            [
                "remove 'collection GoneCollection'",
                "remove 'member GoneCollection GoneTests'",
                "lower 'environment-baseline backend.Tests/CodexHomeTests.cs 2' to 1",
                "remove 'process-start backend.Tests/Gone.cs'",
            ],
            stale);
    }

    [Fact]
    public void Listing_rejects_an_entry_without_a_reason()
    {
        var error = Assert.Throws<InvalidDataException>(() => Listing.Parse(["collection LiveCliCollection"]));

        Assert.Contains("collection LiveCliCollection", error.Message, StringComparison.Ordinal);
    }

    internal sealed record SerialCollection(string Definition, string Name, string File, int Line);

    internal sealed record SerialMember(string Collection, string TestClass, string File, int Line);

    internal sealed record EnvironmentWrites(string File, IReadOnlyList<int> Lines);

    internal sealed record ScanResult(
        IReadOnlyList<SerialCollection> Collections,
        IReadOnlyList<SerialMember> Members,
        IReadOnlyList<EnvironmentWrites> Writes,
        IReadOnlyList<string> Unresolved);

    internal sealed class Listing
    {
        public Dictionary<string, string> Collections { get; } = new(StringComparer.Ordinal);
        public HashSet<(string Collection, string TestClass)> Members { get; } = [];
        public Dictionary<string, int> EnvironmentBaseline { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ProcessStart { get; } = new(StringComparer.Ordinal);

        public static Listing Parse(IEnumerable<string> lines)
        {
            var listing = new Listing();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                switch (parts)
                {
                    case ["collection", var definition, var reason]:
                        listing.Collections[definition] = reason;
                        break;
                    case ["member", var collection, var testClass]:
                        listing.Members.Add((collection, testClass));
                        break;
                    case ["environment-baseline", var file, var count]
                        when int.TryParse(count, System.Globalization.CultureInfo.InvariantCulture, out var writes):
                        listing.EnvironmentBaseline[file] = writes;
                        break;
                    case ["process-start", var file, var reason]:
                        listing.ProcessStart[file] = reason;
                        break;
                    default:
                        throw new InvalidDataException(
                            $"{ListingPath}: unreadable line '{raw}'. A collection or process-start entry needs a reason.");
                }
            }
            return listing;
        }
    }

    private static Listing ReadListing(string root)
        => Listing.Parse(File.ReadLines(Path.Combine(root, ListingPath.Replace('/', Path.DirectorySeparatorChar))));

    private static ScanResult ScanProject(string root)
        => Scan(CSharpSourceScanner.SourceFiles(root, Project)
            .Select(file => (CSharpSourceScanner.Relative(root, file), CSharpSourceScanner.Read(file)))
            .ToList());

    private static List<string> Violations(ScanResult scan, Listing listing)
    {
        var violations = new List<string>(scan.Unresolved);
        foreach (var collection in scan.Collections.Where(c => !listing.Collections.ContainsKey(c.Definition)))
        {
            violations.Add(
                $"{collection.File}:{collection.Line}: serial collection {collection.Definition} (\"{collection.Name}\") is not listed. "
                + $"Remove DisableParallelization, or add 'collection {collection.Definition} <reason>'.");
        }
        foreach (var member in scan.Members.Where(m => !listing.Members.Contains((m.Collection, m.TestClass))))
        {
            violations.Add(
                $"{member.File}:{member.Line}: {member.TestClass} runs serially in {member.Collection} but is not listed. "
                + $"Make it parallel, or add 'member {member.Collection} {member.TestClass}'.");
        }
        foreach (var writes in scan.Writes.Where(w => !listing.ProcessStart.ContainsKey(w.File)))
        {
            var allowed = listing.EnvironmentBaseline.GetValueOrDefault(writes.File);
            if (writes.Lines.Count <= allowed) continue;
            violations.Add(
                $"{writes.File}: writes the process environment {writes.Lines.Count} time(s) (baseline {allowed}) outside a serial "
                + $"collection at line(s) {string.Join(", ", writes.Lines)}. Pass the value to the host with UseSetting "
                + "(TestHostSettings) instead.");
        }
        return violations;
    }

    private static List<string> Stale(ScanResult scan, Listing listing)
    {
        var stale = new List<string>();
        var collections = scan.Collections.Select(c => c.Definition).ToHashSet(StringComparer.Ordinal);
        var members = scan.Members.Select(m => (m.Collection, m.TestClass)).ToHashSet();
        var writes = scan.Writes.ToDictionary(w => w.File, w => w.Lines.Count, StringComparer.Ordinal);

        stale.AddRange(listing.Collections.Keys.Where(c => !collections.Contains(c)).Order(StringComparer.Ordinal)
            .Select(c => $"remove 'collection {c}'"));
        stale.AddRange(listing.Members.Where(m => !members.Contains(m)).Order()
            .Select(m => $"remove 'member {m.Collection} {m.TestClass}'"));
        foreach (var (file, count) in listing.EnvironmentBaseline.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var actual = writes.GetValueOrDefault(file);
            if (actual < count)
                stale.Add(actual == 0
                    ? $"remove 'environment-baseline {file} {count}'"
                    : $"lower 'environment-baseline {file} {count}' to {actual}");
        }
        stale.AddRange(listing.ProcessStart.Keys.Where(file => !writes.ContainsKey(file)).Order(StringComparer.Ordinal)
            .Select(file => $"remove 'process-start {file}'"));
        return stale;
    }

    private sealed record SourceFile(
        string Path,
        string Text,
        string Code,
        IReadOnlyList<CSharpSourceScanner.TypeSpan> Types);

    /// <summary>
    /// Scans sources lexically. Attributes and calls are found in code with
    /// comments and literals masked, so a sample inside a string never counts;
    /// collection names are read back from the unmasked text at the same offset.
    /// </summary>
    private static ScanResult Scan(IReadOnlyList<(string Path, string Source)> sources)
    {
        var files = sources.Select(source =>
        {
            var text = CSharpSourceScanner.MaskComments(source.Source.Replace("\r\n", "\n", StringComparison.Ordinal));
            var code = CSharpSourceScanner.MaskCommentsAndLiterals(text);
            return new SourceFile(source.Path, text, code, CSharpSourceScanner.Types(code).ToList());
        }).ToList();
        var constants = Constants(files);
        var unresolved = new List<string>();

        var collections = new List<SerialCollection>();
        foreach (var file in files)
        {
            foreach (Match match in CollectionDefinitionAttribute().Matches(file.Code))
            {
                var arguments = file.Text.Substring(match.Groups["args"].Index, match.Groups["args"].Length);
                if (!DisableParallelization().IsMatch(arguments)) continue;
                var definition = AttachedType(file, match);
                var line = CSharpSourceScanner.LineAt(file.Code, match.Index);
                var name = ResolveName(arguments.Split(',')[0], definition, constants);
                if (definition is null || name is null)
                    unresolved.Add($"{file.Path}:{line}: cannot resolve the serial collection '{arguments}'.");
                else
                    collections.Add(new SerialCollection(definition, name, file.Path, line));
            }
        }

        var byName = collections.ToDictionary(c => c.Name, c => c.Definition, StringComparer.Ordinal);
        var members = new List<SerialMember>();
        foreach (var file in files)
        {
            foreach (Match match in CollectionAttribute().Matches(file.Code))
            {
                var argument = file.Text.Substring(match.Groups["arg"].Index, match.Groups["arg"].Length);
                var testClass = AttachedType(file, match);
                var line = CSharpSourceScanner.LineAt(file.Code, match.Index);
                var name = ResolveName(argument, testClass, constants);
                if (testClass is null || name is null)
                    unresolved.Add($"{file.Path}:{line}: cannot resolve the collection '{argument}'.");
                else if (byName.TryGetValue(name, out var definition))
                    members.Add(new SerialMember(definition, testClass, file.Path, line));
            }
        }

        var serialClasses = members.Select(m => m.TestClass).ToHashSet(StringComparer.Ordinal);
        var writes = new List<EnvironmentWrites>();
        foreach (var file in files)
        {
            var lines = EnvironmentWrite().Matches(file.Code)
                .Where(match => !file.Types.Any(type =>
                    type.Start <= match.Index && match.Index < type.End && serialClasses.Contains(type.Name)))
                .Select(match => CSharpSourceScanner.LineAt(file.Code, match.Index))
                .ToList();
            if (lines.Count > 0) writes.Add(new EnvironmentWrites(file.Path, lines));
        }

        return new ScanResult(collections, members, writes, unresolved);
    }

    /// <summary>Every <c>const string</c> per declaring type, for <c>Type.Name</c> collection arguments.</summary>
    private static Dictionary<(string Type, string Constant), string> Constants(IEnumerable<SourceFile> files)
    {
        var constants = new Dictionary<(string, string), string>();
        foreach (var file in files)
        {
            foreach (Match match in StringConstant().Matches(file.Code))
            {
                var literal = LiteralAt().Match(file.Text, match.Index + match.Length);
                if (!literal.Success) continue;
                var owner = file.Types
                    .Where(type => type.Start <= match.Index && match.Index < type.End)
                    .MaxBy(type => type.Start);
                if (owner is not null) constants[(owner.Name, match.Groups["name"].Value)] = literal.Groups["value"].Value;
            }
        }
        return constants;
    }

    private static string? AttachedType(SourceFile file, Match attribute)
        => file.Types.FirstOrDefault(type => type.Start >= attribute.Index + attribute.Length)?.Name;

    private static string? ResolveName(
        string argument,
        string? attachedType,
        IReadOnlyDictionary<(string Type, string Constant), string> constants)
    {
        argument = argument.Trim();
        var literal = StringLiteral().Match(argument);
        if (literal.Success && literal.Length == argument.Length) return literal.Groups["value"].Value;
        var parts = argument.Split('.');
        var (type, constant) = parts.Length >= 2 ? (parts[^2], parts[^1]) : (attachedType, parts[0]);
        return type is not null && constants.TryGetValue((type, constant), out var value) ? value : null;
    }

    [GeneratedRegex(@"\[\s*(?:Xunit\.)?CollectionDefinition\s*\((?<args>[^\]]*)\)\s*\]")]
    private static partial Regex CollectionDefinitionAttribute();

    [GeneratedRegex(@"\[\s*(?:Xunit\.)?Collection\s*\((?<arg>[^\]\)]*)\)\s*\]")]
    private static partial Regex CollectionAttribute();

    [GeneratedRegex(@"\bDisableParallelization\s*=\s*true\b")]
    private static partial Regex DisableParallelization();

    [GeneratedRegex(@"\bEnvironment\s*\.\s*SetEnvironmentVariable\s*\(")]
    private static partial Regex EnvironmentWrite();

    [GeneratedRegex(@"\bconst\s+string\s+(?<name>\w+)\s*=")]
    private static partial Regex StringConstant();

    [GeneratedRegex("\\G\\s*\"(?<value>[^\"\\\\]*)\"")]
    private static partial Regex LiteralAt();

    [GeneratedRegex("\"(?<value>[^\"\\\\]*)\"")]
    private static partial Regex StringLiteral();
}
