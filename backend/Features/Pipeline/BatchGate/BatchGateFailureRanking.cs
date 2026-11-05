using System.Text.Json;
using AgentStudio.Diagnostics;

namespace AgentStudio.Pipeline;

public sealed record BatchTestFailure(string TestName, string BatchId, string RunId,
    IReadOnlyList<string> Members, DateTimeOffset AtUtc, string ErrorExcerpt, bool FlakyRerunPassed);

public sealed record BatchTestFailureRank(string TestName, int Batches, int Runs,
    int DistinctMemberSets, DateTimeOffset FirstFailureUtc, DateTimeOffset LastFailureUtc,
    string LatestErrorExcerpt, bool FlakyRerunPassed, bool FlakySuspect,
    IReadOnlyList<string> BatchIds, IReadOnlyList<BatchTestFailure> Failures);

public sealed record BatchTestFailureReport(string Project, int WindowDays,
    IReadOnlyList<BatchTestFailureRank> Tests, int PendingEvidenceRuns);

/// <summary>Pure window and attribution policy. A common member can explain repeated reds.</summary>
public static class BatchGateFailureRanking
{
    public static IReadOnlyList<BatchTestFailureRank> Rank(IEnumerable<BatchTestFailure> failures,
        DateTimeOffset now, int windowDays = 7)
    {
        var cutoff = now.AddDays(-windowDays);
        return failures.Where(f => f.AtUtc >= cutoff && f.AtUtc <= now)
            .GroupBy(f => f.TestName, StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group.OrderBy(f => f.AtUtc).ToArray();
                var batches = ordered.GroupBy(f => f.BatchId, StringComparer.Ordinal).ToArray();
                var sets = batches.Select(batch => batch.SelectMany(f => f.Members)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()).ToArray();
                var suspect = false;
                for (var i = 0; i < sets.Length && !suspect; i++)
                for (var j = i + 1; j < sets.Length && !suspect; j++)
                for (var k = j + 1; k < sets.Length && !suspect; k++)
                    suspect = sets[i].Length > 0 && sets[j].Length > 0 && sets[k].Length > 0
                        && Disjoint(sets[i], sets[j]) && Disjoint(sets[i], sets[k])
                        && Disjoint(sets[j], sets[k]);
                return new BatchTestFailureRank(group.Key, batches.Length,
                    ordered.Select(f => f.RunId).Distinct(StringComparer.Ordinal).Count(),
                    sets.Select(set => string.Join("\n", set)).Distinct(StringComparer.Ordinal).Count(),
                    ordered[0].AtUtc, ordered[^1].AtUtc,
                    ordered.LastOrDefault(f => f.ErrorExcerpt.Length > 0)?.ErrorExcerpt ?? "",
                    ordered.Any(f => f.FlakyRerunPassed), suspect,
                    batches.OrderByDescending(batch => batch.Max(f => f.AtUtc)).Select(batch => batch.Key).ToArray(),
                    ordered.Reverse().ToArray());
            })
            .OrderByDescending(row => row.FlakySuspect).ThenByDescending(row => row.Batches)
            .ThenByDescending(row => row.Runs).ThenByDescending(row => row.LastFailureUtc).ToArray();
    }

    private static bool Disjoint(string[] a, string[] b)
        => !a.Intersect(b, StringComparer.Ordinal).Any();

    public static IReadOnlyList<BatchTestFailure> ReadEvidence(BatchGateExecutionFact fact)
    {
        if (fact.BatchId is null || fact.BatchRunId is null) return [];
        if (!File.Exists(fact.EvidencePath))
            throw new FileNotFoundException("Batch gate failure ranking evidence is unavailable.", fact.EvidencePath);
        var found = new List<BatchTestFailure>();
        var lines = File.ReadLines(fact.EvidencePath).ToArray();
        var selected = lines.Select((line, index) => (line, index, distance: Distance(line, fact)))
            .OrderBy(item => item.distance).ThenByDescending(item => item.index).FirstOrDefault();
        if (selected.line is null) return [];
        foreach (var line in new[] { selected.line })
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var names = new HashSet<string>(StringComparer.Ordinal);
                AddStrings(root, "recordedFailedTests", names);
                AddStrings(root, "flakyQuarantinedFailures", names);
                if (TryGet(root, "quarantinedFailures", out var quarantine) && quarantine.ValueKind == JsonValueKind.Array)
                    foreach (var hit in quarantine.EnumerateArray())
                        if (String(hit, "test") is { Length: > 0 } name) names.Add(name);
                if (TryGet(root, "processes", out var processes) && processes.ValueKind == JsonValueKind.Array)
                    foreach (var process in processes.EnumerateArray())
                        if (TryGet(process, "testReport", out var report)
                            && report.ValueKind == JsonValueKind.Object)
                            AddStrings(report, "failedTests", names);
                if (TryGet(root, "testShards", out var suites) && suites.ValueKind == JsonValueKind.Array)
                    foreach (var suite in suites.EnumerateArray())
                        if (TryGet(suite, "shards", out var shards) && shards.ValueKind == JsonValueKind.Array)
                            foreach (var shard in shards.EnumerateArray()) AddStrings(shard, "failedTests", names);
                var cleared = new HashSet<string>(StringComparer.Ordinal);
                AddStrings(root, "flakyQuarantinedFailures", cleared);
                var output = String(root, "output") ?? "";
                foreach (var name in GateFlakyRerunPolicy.ParseFailedTests(output)) names.Add(name);
                if (TryGet(root, "processes", out processes) && processes.ValueKind == JsonValueKind.Array)
                    foreach (var process in processes.EnumerateArray())
                        foreach (var name in GateFlakyRerunPolicy.ParseFailedTests(
                                     (String(process, "standardOutput") ?? "") + "\n" +
                                     (String(process, "standardError") ?? ""))) names.Add(name);
                var processOutput = ProcessOutput(root);
                foreach (var name in names)
                {
                    var excerpt = Excerpt(output, name);
                    if (excerpt.Length == 0) excerpt = Excerpt(processOutput, name);
                    found.Add(new BatchTestFailure(name, fact.BatchId, fact.BatchRunId,
                        fact.MemberKeys ?? [], fact.CompletedAtUtc, excerpt, cleared.Contains(name)));
                }
            }
            catch (JsonException exception)
            {
                SilentCatch.Note(exception, "Batch gate failure ranking skipped an invalid evidence line");
            }
        }
        return found;
    }

    private static string? String(JsonElement element, string name)
        => TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && (element.TryGetProperty(name, out value)
                || element.TryGetProperty(char.ToUpperInvariant(name[0]) + name[1..], out value));
    }

    private static void AddStrings(JsonElement element, string name, HashSet<string> target)
    {
        if (!TryGet(element, name, out var values) || values.ValueKind != JsonValueKind.Array) return;
        foreach (var value in values.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                target.Add(text);
    }

    private static string Excerpt(string output, string test)
    {
        var lines = output.Split('\n');
        var first = -1;
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            if (!lines[index].Contains(test, StringComparison.Ordinal)) continue;
            first = Array.FindIndex(lines, index, Math.Min(12, lines.Length - index),
                line => line.Contains("Error Message:", StringComparison.OrdinalIgnoreCase));
            if (first >= 0) break;
        }
        if (first < 0) first = Array.FindLastIndex(lines,
            line => line.Contains("Error Message:", StringComparison.OrdinalIgnoreCase));
        if (first < 0) return "";
        var excerpt = string.Join("\n", lines.Skip(first).Take(4)).Trim();
        return excerpt[..Math.Min(500, excerpt.Length)];
    }

    private static string ProcessOutput(JsonElement root)
    {
        if (!TryGet(root, "processes", out var processes) || processes.ValueKind != JsonValueKind.Array)
            return "";
        return string.Join("\n", processes.EnumerateArray().Select(process =>
            (String(process, "standardOutput") ?? "") + "\n" +
            (String(process, "standardError") ?? "")));
    }

    private static double Distance(string line, BatchGateExecutionFact fact)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (String(root, "gateRunId") == fact.BatchRunId) return -1;
            if (DateTimeOffset.TryParse(String(root, "gateCompletedAtUtc"), out var completed))
                return Math.Abs((completed - fact.CompletedAtUtc).TotalSeconds);
        }
        catch (JsonException exception)
        {
            SilentCatch.Note(exception, "Batch gate failure ranking skipped an invalid evidence timestamp");
        }
        return double.MaxValue;
    }
}
