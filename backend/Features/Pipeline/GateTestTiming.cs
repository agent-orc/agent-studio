using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AgentStudio.Pipeline;

public sealed record GateSlowTest(string Name, double DurationMs, string Source);

/// <summary>
/// What a test process's TRX report says about the run (AGT-3232): counts,
/// failed test names, and the summed test duration per simple class name.
/// </summary>
public sealed record GateTestReport(GateTestCounts Counts, IReadOnlyList<string> FailedTests)
{
    /// <summary>Feeds the next shard plan; kept off the serialized process evidence.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, double> ClassDurationsMs { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>
    /// The simple class name of a TRX <c>className</c>: the last namespace
    /// segment, and the outer class of a nested one (<c>Ns.Outer+Inner</c>).
    /// </summary>
    internal static string ClassKey(string className)
    {
        var simple = className[(className.LastIndexOf('.') + 1)..];
        var nested = simple.IndexOf('+');
        return nested >= 0 ? simple[..nested] : simple;
    }
}

/// <summary>Retains red signals and the top ten timings independently of the output tail.</summary>
internal sealed class GateTestTiming
{
    private readonly object _sync = new();
    private readonly List<GateSlowTest> _slowest = [];
    private readonly Dictionary<string, double> _classDurations = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _failedTests = new(StringComparer.Ordinal);
    private GateTestCounts? _counts;
    private bool _failed;
    internal bool Failed { get { lock (_sync) return _failed; } }
    internal IReadOnlyList<GateSlowTest> Slowest { get { lock (_sync) return _slowest.ToArray(); } }

    /// <summary>The TRX summary, or null when no report was read.</summary>
    internal GateTestReport? Report
    {
        get
        {
            lock (_sync)
            {
                return _counts is null
                    ? null
                    : new GateTestReport(_counts, _failedTests.ToArray())
                    {
                        ClassDurationsMs = new Dictionary<string, double>(_classDurations, StringComparer.Ordinal),
                    };
            }
        }
    }

    private static readonly Regex Ansi = new(@"\x1b\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    // Keep this deliberately format-based rather than test-framework-specific.
    // A gate may be killed before its final summary or TRX is flushed, so every
    // definitive per-test and summary form emitted by our xUnit and vitest
    // commands must win over the environmental timeout classification.
    private static readonly Regex Red = new(
        @"^\s*(?:" +
        @"FAIL\s+\S" +                                      // vitest failure header
        @"|[×✕]\s+\S" +                                    // vitest failed test
        @"|❯\s+.*\([^)]*\b[1-9]\d*\s+failed\b" +         // vitest failed collection
        @"|.*\s\[FAIL\]\s*$" +                            // xUnit console suffix
        @"|Failed\s+.+\[[^\]]+\]\s*$" +                  // dotnet console prefix
        @"|Failed!.*\bFailed:\s*[1-9]\d*\b" +             // dotnet summary
        @"|(?:Tests|Test Files)\s+[1-9]\d*\s+failed\b" +  // vitest summary
        @")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex DotNetTiming = new(
        @"^\s*(?:Passed|Failed|Skipped)\s+(?<name>.+?)\s+\[(?:(?<value>[\d.,]+)\s*(?<unit>ms|s|m|h)\s*)+\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> FailedOutcomes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Failed", "Error", "Timeout", "Aborted",
    };
    private static readonly Regex VitestTiming = new(
        @"^\s*[✓✔√×❯]\s+(?<name>.+?)\s+(?<value>[\d.,]+)\s*(?<unit>ms|s)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool HasFailedTests(string output)
        => output.Split('\n').Any(line => Red.IsMatch(Ansi.Replace(line.TrimEnd('\r'), "")));

    internal void Observe(string line)
    {
        line = Ansi.Replace(line, "");
        lock (_sync)
        {
            _failed |= Red.IsMatch(line);
            var match = DotNetTiming.Match(line);
            var source = "dotnet-console";
            if (!match.Success) { match = VitestTiming.Match(line); source = "vitest-console"; }
            if (!match.Success) return;
            var duration = 0d;
            for (var i = 0; i < match.Groups["value"].Captures.Count; i++)
            {
                if (!double.TryParse(match.Groups["value"].Captures[i].Value.Replace(",", ""),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return;
                duration += value * (match.Groups["unit"].Captures[i].Value switch
                {
                    "h" => 3600000, "m" => 60000, "s" => 1000, _ => 1,
                });
            }
            Add(new(match.Groups["name"].Value, duration, source));
        }
    }

    internal void ReadTrx(string path)
    {
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 64 * 1024 * 1024,
            });
            var document = XDocument.Load(reader);
            var classes = document.Descendants()
                .Where(e => e.Name.LocalName == "UnitTest")
                .Select(test => (
                    Id: (string?)test.Attribute("id"),
                    Class: (string?)test.Elements().FirstOrDefault(e => e.Name.LocalName == "TestMethod")?.Attribute("className")))
                .Where(test => test.Id is not null && !string.IsNullOrWhiteSpace(test.Class))
                .GroupBy(test => test.Id!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => GateTestReport.ClassKey(group.First().Class!), StringComparer.Ordinal);
            lock (_sync)
            {
                var counts = _counts ?? GateTestCounts.None;
                foreach (var result in document.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
                {
                    var outcome = (string?)result.Attribute("outcome");
                    var name = (string?)result.Attribute("testName") ?? "unnamed test";
                    var failed = outcome is not null && FailedOutcomes.Contains(outcome);
                    _failed |= string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase);
                    if (failed) _failedTests.Add(name);
                    counts = counts.Add(failed ? new(1, 0, 1, 0)
                        : string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase) ? new(1, 1, 0, 0)
                        : new(1, 0, 0, 1));
                    if (TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var duration))
                    {
                        Add(new(name, duration.TotalMilliseconds, "trx"));
                        if ((string?)result.Attribute("testId") is { } testId && classes.TryGetValue(testId, out var key))
                            _classDurations[key] = _classDurations.GetValueOrDefault(key) + duration.TotalMilliseconds;
                    }
                }
                _counts = counts;
            }
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "GateTestTiming: incomplete test report; console timings retained");
        }
    }

    private void Add(GateSlowTest test)
    {
        if (!double.IsFinite(test.DurationMs) || test.DurationMs < 0) return;
        var existing = _slowest.FindIndex(item => item.Name == test.Name);
        if (existing >= 0)
        {
            if (_slowest[existing].DurationMs > test.DurationMs) return;
            _slowest.RemoveAt(existing);
        }
        _slowest.Add(test);
        _slowest.Sort((a, b) => b.DurationMs.CompareTo(a.DurationMs));
        if (_slowest.Count > 10) _slowest.RemoveAt(10);
    }
}
