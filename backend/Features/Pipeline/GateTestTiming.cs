using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AgentStudio.Pipeline;

public sealed record GateSlowTest(string Name, double DurationMs, string Source);

/// <summary>Retains red signals and the top ten timings independently of the output tail.</summary>
internal sealed class GateTestTiming
{
    private readonly object _sync = new();
    private readonly List<GateSlowTest> _slowest = [];
    private bool _failed;
    internal bool Failed { get { lock (_sync) return _failed; } }
    internal IReadOnlyList<GateSlowTest> Slowest { get { lock (_sync) return _slowest.ToArray(); } }

    private static readonly Regex Ansi = new(@"\x1b\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex Red = new(
        @"^\s*(?:FAIL\s+|[×✕]\s+|[❯]\s+.*\(.*[1-9]\d* failed)|\[FAIL\]\s*$|^\s*Failed\s+.+\[[^\]]+\]\s*$|^\s*Failed!.*Failed:\s*[1-9]\d*|^\s*(?:Tests|Test Files)\s+[1-9]\d* failed",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DotNetTiming = new(
        @"^\s*(?:Passed|Failed|Skipped)\s+(?<name>.+?)\s+\[(?:(?<value>[\d.,]+)\s*(?<unit>ms|s|m|h)\s*)+\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
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
            lock (_sync)
            {
                foreach (var result in document.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
                {
                    _failed |= string.Equals((string?)result.Attribute("outcome"), "Failed", StringComparison.OrdinalIgnoreCase);
                    if (TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var duration))
                        Add(new((string?)result.Attribute("testName") ?? "unnamed test", duration.TotalMilliseconds, "trx"));
                }
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
