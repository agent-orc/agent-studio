using System.Text.RegularExpressions;
using Xunit;

namespace AgentStudio.Tests.Architecture;

// clock-independent: the guard's own samples carry fixed dates as scanner input.

/// <summary>
/// Time-bomb guard for every test project (AGT-3003). A test that dates its
/// data (a lease from 2026-09-27) while the code under test reads the real
/// clock turns red on a calendar day, on every host, without any product
/// change: <c>DurableLeaseAuthorityTests</c> did exactly that and held the
/// Windows merge gate red. Two rules keep new test code off the wall clock:
/// <list type="number">
///   <item><b>Wall clock.</b> <c>DateTime.UtcNow</c>, <c>DateTime.Now</c>,
///   <c>DateTimeOffset.UtcNow</c>/<c>Now</c>, <c>DateTime.Today</c> and
///   <c>TimeProvider.System</c> may not appear in a test file. Inject a clock
///   instead (<c>FakeTimeProvider</c>, the product's <c>TimeProvider</c> or
///   <c>utcNow</c> parameter) and read it.</item>
///   <item><b>Fixed date without a clock.</b> A test file with a literal date
///   (<c>"2026-09-27T08:00:00Z"</c>, <c>new DateTime(2026, ...)</c>) must also
///   control the clock, or say in a <c>// clock-independent: reason</c>
///   comment why its dates never meet "now" (parsers, formatters, pure policy
///   inputs).</item>
/// </list>
/// Files that predate the guard are listed with their exact use count in
/// <c>Fixtures/test-clock-baseline.txt</c>. That list only shrinks: a file
/// that grows its wall-clock uses fails, and so does a baseline entry that no
/// longer matches the file (lower or remove it). The whole suite was run with
/// the clock shifted to 2031, a de-DE culture and a +12:45 time zone when the
/// baseline was taken, so the grandfathered files are proven not to be time
/// bombs up to that horizon.
/// </summary>
public sealed partial class TestClockGuardTests
{
    private const string BaselinePath = "backend.Tests/Architecture/Fixtures/test-clock-baseline.txt";

    [Fact]
    public void Test_files_do_not_read_the_wall_clock_or_date_data_without_a_clock()
    {
        var root = CSharpSourceScanner.RepoRoot();
        var baseline = ReadBaseline(root);
        var findings = ScanTestProjects(root);
        var violations = new List<string>();

        foreach (var finding in findings)
        {
            var allowedWallClock = baseline.WallClock.GetValueOrDefault(finding.File);
            if (finding.WallClockLines.Count > allowedWallClock)
            {
                violations.Add(
                    $"{finding.File}: reads the wall clock {finding.WallClockLines.Count} time(s) "
                    + $"(baseline {allowedWallClock}) at line(s) {string.Join(", ", finding.WallClockLines)}. "
                    + "Inject a clock (FakeTimeProvider, TimeProvider or a utcNow parameter) instead.");
            }
            if (finding.FixedDateWithoutClock && !baseline.FixedDate.Contains(finding.File))
            {
                violations.Add(
                    $"{finding.File}: literal date at line {finding.FirstFixedDateLine} and no controlled clock. "
                    + "Inject a clock, or add '// clock-independent: <reason>' when the dates never meet \"now\".");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Tests must not depend on the day they run on:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Baseline_only_lists_files_that_still_need_it()
    {
        var root = CSharpSourceScanner.RepoRoot();
        var baseline = ReadBaseline(root);
        var findings = ScanTestProjects(root).ToDictionary(finding => finding.File, StringComparer.Ordinal);
        var stale = new List<string>();

        foreach (var (file, count) in baseline.WallClock)
        {
            var actual = findings.TryGetValue(file, out var finding) ? finding.WallClockLines.Count : 0;
            if (actual < count)
                stale.Add(actual == 0
                    ? $"remove 'wall-clock {file}'"
                    : $"lower 'wall-clock {file} {count}' to {actual}");
        }
        foreach (var file in baseline.FixedDate)
        {
            if (!findings.TryGetValue(file, out var finding) || !finding.FixedDateWithoutClock)
                stale.Add($"remove 'fixed-date {file}'");
        }

        Assert.True(
            stale.Count == 0,
            $"The test-clock baseline only shrinks; update {BaselinePath}:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Guard_flags_wall_clock_reads_and_dates_without_a_clock()
    {
        const string source = """
            public sealed class LeaseTests
            {
                [Fact]
                public void Renewal_keeps_authority()
                {
                    var lease = new Lease("job", new DateTimeOffset(2026, 9, 27, 8, 0, 0, TimeSpan.Zero));
                    var heartbeat = new LeaseHeartbeat(lease);
                    Assert.True(heartbeat.Renew(DateTime.UtcNow).Granted);
                }
            }
            """;

        var finding = ScanSource("runner.Tests/LeaseTests.cs", source);

        Assert.Equal([8], finding.WallClockLines);
        Assert.True(finding.FixedDateWithoutClock);
        Assert.Equal(6, finding.FirstFixedDateLine);
    }

    [Fact]
    public void Guard_accepts_an_injected_clock_an_explained_opt_out_and_mentions_in_text()
    {
        const string injected = """
            public sealed class LeaseTests
            {
                private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-27T08:00:00Z"));
                // DateTime.UtcNow would make this a time bomb.
                private const string Hint = "never use DateTime.UtcNow here";
            }
            """;
        const string parameter = """
            var heartbeat = new LeaseHeartbeat(lease, utcNow: () => new DateTimeOffset(2026, 9, 27, 8, 0, 0, TimeSpan.Zero));
            """;
        const string optOut = """
            // clock-independent: parses recorded CLI output; the timestamps are data.
            var line = Parse("{\"ts\":\"2026-09-27T08:00:00Z\"}");
            """;

        Assert.All(
            new[] { injected, parameter, optOut }.Select(source => ScanSource("backend.Tests/Sample.cs", source)),
            finding =>
            {
                Assert.Empty(finding.WallClockLines);
                Assert.False(finding.FixedDateWithoutClock);
            });
    }

    private sealed record ClockFinding(
        string File,
        IReadOnlyList<int> WallClockLines,
        bool FixedDateWithoutClock,
        int FirstFixedDateLine);

    private sealed record Baseline(IReadOnlyDictionary<string, int> WallClock, IReadOnlySet<string> FixedDate);

    private static IReadOnlyList<ClockFinding> ScanTestProjects(string root)
    {
        var projects = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => name is not null && (name.EndsWith(".Tests", StringComparison.Ordinal) || name == "testsupport"))
            .Order(StringComparer.Ordinal);
        return projects
            .SelectMany(project => CSharpSourceScanner.SourceFiles(root, project!))
            .Select(file => ScanSource(CSharpSourceScanner.Relative(root, file), CSharpSourceScanner.Read(file)))
            .Where(finding => finding.WallClockLines.Count > 0 || finding.FixedDateWithoutClock)
            .ToList();
    }

    private static ClockFinding ScanSource(string relativePath, string source)
    {
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var code = CSharpSourceScanner.MaskCommentsAndLiterals(source);
        var codeAndLiterals = CSharpSourceScanner.MaskComments(source);

        var wallClock = WallClock().Matches(code)
            .Select(match => CSharpSourceScanner.LineAt(code, match.Index))
            .ToList();
        var fixedDate = FixedDate().Match(codeAndLiterals);
        var controlsClock = ClockControl().IsMatch(code) || ClockIndependent().IsMatch(source);

        return new ClockFinding(
            relativePath,
            wallClock,
            fixedDate.Success && !controlsClock,
            fixedDate.Success ? CSharpSourceScanner.LineAt(codeAndLiterals, fixedDate.Index) : 0);
    }

    private static Baseline ReadBaseline(string root)
    {
        var wallClock = new Dictionary<string, int>(StringComparer.Ordinal);
        var fixedDate = new HashSet<string>(StringComparer.Ordinal);
        var path = Path.Combine(root, BaselinePath.Replace('/', Path.DirectorySeparatorChar));
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts)
            {
                case ["wall-clock", var file, var count]:
                    wallClock[file] = int.Parse(count, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case ["fixed-date", var file]:
                    fixedDate.Add(file);
                    break;
                default:
                    throw new InvalidDataException($"{BaselinePath}: unreadable line '{raw}'.");
            }
        }
        return new Baseline(wallClock, fixedDate);
    }

    [GeneratedRegex(@"\bDateTime(?:Offset)?\s*\.\s*(?:UtcNow|Now|Today)\b|\bTimeProvider\s*\.\s*System\b")]
    private static partial Regex WallClock();

    [GeneratedRegex(@"\b20\d\d-[01]\d-[0-3]\d|\bnew\s+(?:System\.)?Date(?:Time(?:Offset)?|Only)\s*\(\s*20\d\d\s*,")]
    private static partial Regex FixedDate();

    [GeneratedRegex(@"\bFakeTimeProvider\b|\bTimeProvider\b(?!\s*\.\s*System\b)|\b(?:utcNow|nowUtc|now|clock|asOf)\s*:|\bFunc<\s*DateTime(?:Offset)?\s*>")]
    private static partial Regex ClockControl();

    [GeneratedRegex(@"//\s*clock-independent:\s*\S")]
    private static partial Regex ClockIndependent();
}
