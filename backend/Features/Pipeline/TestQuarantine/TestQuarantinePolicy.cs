using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Pipeline;

/// <summary>
/// One quarantined test as the project declares it in
/// <see cref="TestQuarantineFile.RelativePath"/>. The owning card and the
/// expiry date are mandatory: an entry without them never quarantines anything.
/// </summary>
/// <param name="Test">Fully qualified test name, exactly as the test logger prints it.</param>
/// <param name="Card">The card that owns the flake and must resolve it.</param>
/// <param name="ExpiresOn">Last day the quarantine holds; the test is armed again on the next day.</param>
/// <param name="ResolvedOn">Set by the owning card when it fixed the test; the test is armed from then on.</param>
/// <param name="Reason">Free-text note on why the test was quarantined.</param>
public sealed record TestQuarantineEntry(
    string Test,
    string Card,
    DateOnly ExpiresOn,
    DateOnly? ResolvedOn = null,
    string? Reason = null);

/// <summary>Where one quarantine entry stands on a given day.</summary>
public enum TestQuarantineStatus
{
    /// <summary>Failures are ignored for the delivery verdict and still reported.</summary>
    Active,

    /// <summary>The expiry passed without a resolution; the test is armed again.</summary>
    Expired,

    /// <summary>The owning card resolved the flake; the test is armed again.</summary>
    Resolved,
}

/// <summary>A quarantined failure the gate observed and did not count against the delivery.</summary>
public sealed record TestQuarantineHit(string Test, string Card, DateOnly ExpiresOn);

/// <summary>
/// The failed tests of one red step split by the quarantine: <see cref="Ignored"/>
/// do not block the delivery, <see cref="Blocking"/> do.
/// </summary>
public sealed record TestQuarantinePartition(
    IReadOnlyList<TestQuarantineHit> Ignored,
    IReadOnlyList<string> Blocking)
{
    /// <summary>Every failure was quarantined, so the step does not block.</summary>
    public bool AllQuarantined => Ignored.Count > 0 && Blocking.Count == 0;
}

/// <summary>
/// AGT-W57 D4 option A (decided 2026-09-25): flaky tests are quarantined with an
/// expiry date and an owning card. Pure decisions only; the gate supplies the
/// failed names and the day.
/// </summary>
public static class TestQuarantinePolicy
{
    /// <summary>
    /// Active until and including <see cref="TestQuarantineEntry.ExpiresOn"/>
    /// unless the card resolved it first. Both other states arm the test again;
    /// they differ only in what the report says.
    /// </summary>
    public static TestQuarantineStatus Evaluate(TestQuarantineEntry entry, DateOnly today)
    {
        if (entry.ResolvedOn is { } resolved && resolved <= today) return TestQuarantineStatus.Resolved;
        return today <= entry.ExpiresOn ? TestQuarantineStatus.Active : TestQuarantineStatus.Expired;
    }

    /// <summary>
    /// Splits the failed names of one red step. A name matches an entry only by
    /// exact, ordinal equality, so a quarantine never widens to a neighbour.
    /// </summary>
    public static TestQuarantinePartition Partition(
        IReadOnlyList<string> failedTests,
        IReadOnlyList<TestQuarantineEntry> entries,
        DateOnly today)
    {
        var active = new Dictionary<string, TestQuarantineEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (Evaluate(entry, today) == TestQuarantineStatus.Active) active.TryAdd(entry.Test, entry);
        }

        var ignored = new List<TestQuarantineHit>();
        var blocking = new List<string>();
        foreach (var test in failedTests)
        {
            if (active.TryGetValue(test, out var entry))
                ignored.Add(new TestQuarantineHit(test, entry.Card, entry.ExpiresOn));
            else
                blocking.Add(test);
        }
        return new TestQuarantinePartition(ignored, blocking);
    }
}

/// <summary>Reads the per-project quarantine list from the exact gate workspace.</summary>
public static class TestQuarantineFile
{
    /// <summary>Repository-relative location; versioned with the project, so a removal is visible in history.</summary>
    public const string RelativePath = ".agent-studio/test-quarantine.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The valid entries plus one issue per rejected entry. A missing file is an
    /// empty quarantine; an unreadable file quarantines nothing and says so.
    /// </summary>
    public static TestQuarantineReadResult Read(string repositoryPath)
    {
        var path = Path.Combine(repositoryPath, RelativePath);
        if (!File.Exists(path)) return new TestQuarantineReadResult([], []);
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new TestQuarantineReadResult([], [$"{RelativePath} unreadable: {exception.Message}"]);
        }
    }

    public static TestQuarantineReadResult Parse(string json)
    {
        RawFile? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawFile>(json, Options);
        }
        catch (JsonException exception)
        {
            return new TestQuarantineReadResult([], [$"{RelativePath} is not valid JSON: {exception.Message}"]);
        }

        var entries = new List<TestQuarantineEntry>();
        var issues = new List<string>();
        foreach (var (item, index) in (raw?.Tests ?? []).Select((item, index) => (item, index)))
        {
            var at = $"tests[{index}]";
            if (string.IsNullOrWhiteSpace(item.Test)) { issues.Add($"{at}: test is required"); continue; }
            if (string.IsNullOrWhiteSpace(item.Card)) { issues.Add($"{at} {item.Test}: card is required"); continue; }
            if (!TryDate(item.ExpiresOn, out var expires))
            {
                issues.Add($"{at} {item.Test}: expiresOn must be a yyyy-MM-dd date");
                continue;
            }
            DateOnly? resolved = null;
            if (!string.IsNullOrWhiteSpace(item.ResolvedOn))
            {
                if (!TryDate(item.ResolvedOn, out var resolvedOn))
                {
                    issues.Add($"{at} {item.Test}: resolvedOn must be a yyyy-MM-dd date");
                    continue;
                }
                resolved = resolvedOn;
            }
            entries.Add(new TestQuarantineEntry(
                item.Test.Trim(), item.Card.Trim(), expires, resolved, item.Reason));
        }
        return new TestQuarantineReadResult(entries, issues);
    }

    private static bool TryDate(string? value, out DateOnly date)
        => DateOnly.TryParseExact(
            value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private sealed record RawFile([property: JsonPropertyName("tests")] List<RawEntry>? Tests);

    private sealed record RawEntry(
        string? Test,
        string? Card,
        string? ExpiresOn,
        string? ResolvedOn,
        string? Reason);
}

public sealed record TestQuarantineReadResult(
    IReadOnlyList<TestQuarantineEntry> Entries,
    IReadOnlyList<string> Issues);

/// <summary>One row of the run report or the weekly fleet report.</summary>
public sealed record TestQuarantineReportRow(
    string Project,
    string Test,
    string Card,
    DateOnly ExpiresOn,
    TestQuarantineStatus Status,
    int IgnoredFailures);

/// <summary>
/// The report side of the quarantine: ignored failures stay visible. Every
/// declared entry appears, including expired and resolved ones, so nothing
/// leaves the report silently.
/// </summary>
public static class TestQuarantineReport
{
    /// <summary>The run report: one row per declared entry with this run's ignored failures.</summary>
    public static IReadOnlyList<TestQuarantineReportRow> ProjectRun(
        string project,
        IReadOnlyList<TestQuarantineEntry> entries,
        IReadOnlyList<TestQuarantineHit> hits,
        DateOnly today)
        => ProjectFleetWeek([new TestQuarantineProjectWeek(project, entries, hits)], today);

    /// <summary>
    /// The weekly fleet report: every project's entries with the failures the
    /// gates ignored during the week, active rows first, then by ignored count.
    /// </summary>
    public static IReadOnlyList<TestQuarantineReportRow> ProjectFleetWeek(
        IEnumerable<TestQuarantineProjectWeek> projects,
        DateOnly weekEnd)
    {
        var rows = new List<TestQuarantineReportRow>();
        foreach (var project in projects)
        {
            var counts = project.Hits
                .GroupBy(hit => hit.Test, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            foreach (var entry in project.Entries)
            {
                rows.Add(new TestQuarantineReportRow(
                    project.Project,
                    entry.Test,
                    entry.Card,
                    entry.ExpiresOn,
                    TestQuarantinePolicy.Evaluate(entry, weekEnd),
                    counts.GetValueOrDefault(entry.Test)));
            }
        }
        return rows
            .OrderBy(row => row.Status)
            .ThenByDescending(row => row.IgnoredFailures)
            .ThenBy(row => row.Project, StringComparer.Ordinal)
            .ThenBy(row => row.Test, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>One project's declared quarantine and the hits its gates recorded in the window.</summary>
public sealed record TestQuarantineProjectWeek(
    string Project,
    IReadOnlyList<TestQuarantineEntry> Entries,
    IReadOnlyList<TestQuarantineHit> Hits);
