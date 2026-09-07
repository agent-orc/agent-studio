using System.Globalization;

namespace AgentStudio.TestRuns;

/// <summary>
/// Reads deterministic test evidence that is already attached to a task
/// folder. Remote Review build-tests grades and build/test gate logs predate
/// the project-wide TestRunStore, so card projection must include both sources
/// instead of treating the absence of a project run as the absence of evidence.
/// </summary>
internal static class TaskScopedTestEvidenceReader
{
    private static readonly (string Pattern, string Kind, string Label)[] GatePatterns =
    [
        ("build-test-gate-*.log", "build-test-gate", "Build/test gate"),
        ("pre-develop-build-gate-*.log", "pre-develop-build-gate", "Pre-develop build gate"),
        ("pre-main-test-gate-*.log", "pre-main-test-gate", "Pre-main test gate"),
    ];

    public static TaskScopedTestEvidenceSnapshot Read(TaskInfo task)
    {
        if (string.IsNullOrWhiteSpace(task.FolderPath) || !Directory.Exists(task.FolderPath))
            return new([], "missing");

        var sources = new List<TaskTestEvidenceSource>();
        var signature = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         task.FolderPath,
                         "remote-review-grade-*.md",
                         SearchOption.TopDirectoryOnly))
            {
                AddSignature(path, signature);
                sources.AddRange(ReadRemoteReview(path));
            }

            var postSteps = Path.Combine(task.FolderPath, "post-steps");
            if (Directory.Exists(postSteps))
            {
                foreach (var (pattern, kind, label) in GatePatterns)
                {
                    foreach (var path in Directory.EnumerateFiles(postSteps, pattern, SearchOption.TopDirectoryOnly))
                    {
                        AddSignature(path, signature);
                        if (ReadGate(path, kind, label) is { } gate) sources.Add(gate);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            signature.Add(ex.GetType().Name);
        }

        return new(
            sources.OrderByDescending(source => source.ObservedAt ?? DateTime.MinValue).ToList(),
            string.Join('|', signature.OrderBy(value => value, StringComparer.Ordinal)));
    }

    private static IReadOnlyList<TaskTestEvidenceSource> ReadRemoteReview(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        var frontmatter = ReadFrontmatter(text);
        if (!string.Equals(frontmatter.GetValueOrDefault("type"), "remote-review-grade", StringComparison.OrdinalIgnoreCase))
            return [];

        var commit = frontmatter.GetValueOrDefault("actualHead")
                     ?? frontmatter.GetValueOrDefault("expectedResultSha")
                     ?? "";
        if (string.IsNullOrWhiteSpace(commit)) return [];

        var observedAt = ParseDate(frontmatter.GetValueOrDefault("receivedAt"))
                         ?? File.GetLastWriteTimeUtc(path);
        var attemptId = frontmatter.GetValueOrDefault("attemptId") ?? Path.GetFileNameWithoutExtension(path);
        var reportRef = Path.GetFileName(path);
        var verdictRows = ReadTable(text, "## Aspect verdicts")
            .Where(columns => columns.Count >= 4 && !IsTableHeader(columns))
            .Select(columns => new ReviewVerdictRow(columns[0], columns[1], columns[2], columns[3]))
            .ToList();
        var commandSteps = ReadTable(text, "## Command evidence")
            .Where(columns => columns.Count >= 3 && !IsTableHeader(columns))
            .Select(columns => columns[2])
            .Where(IsBuildVerifyStep)
            .ToList();
        var buildRows = verdictRows
            .Where(row => row.Aspect.Equals("build-tests", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var buildStepByRow = buildRows
            .Select((row, index) => StepId(row.Summary)
                                    ?? (index < commandSteps.Count ? commandSteps[index] : null))
            .ToList();
        var buildSteps = buildStepByRow
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var buildResult = buildRows.Count == 0
            ? "not-proven"
            : buildRows.All(row => row.Status.Equals("pass", StringComparison.OrdinalIgnoreCase))
                ? "passed"
                : "failed";
        var buildResultLabel = buildResult switch
        {
            "passed" => "Pass",
            "failed" => "Failed",
            _ => "Not proven",
        };
        var buildReason = buildResult switch
        {
            "passed" when buildSteps.Count > 0 => Sentence($"{NaturalList(buildSteps)} passed"),
            "passed" => "All build-tests verdicts passed.",
            "failed" => FailedBuildReason(buildRows, buildStepByRow),
            _ when commandSteps.Count > 0 => Sentence(
                $"Build-tests verdict is missing for {NaturalList(commandSteps)}"),
            _ => "Build-tests command is missing from the Remote Review report.",
        };
        var stepSuffix = buildSteps.Count > 0 ? $" ({string.Join(", ", buildSteps)})" : "";
        var sources = new List<TaskTestEvidenceSource>
        {
            new()
            {
                Kind = "review-build-tests",
                Id = attemptId,
                Commit = commit,
                Result = buildResult,
                ObservedAt = observedAt,
                Summary = $"Review build-tests {buildResultLabel} at {Short(commit)}{stepSuffix}",
                Reason = buildReason,
                ReportRef = reportRef,
            },
        };

        var blockedAspects = verdictRows
            .Where(row => !row.Aspect.Equals("build-tests", StringComparison.OrdinalIgnoreCase)
                          && IsBlocking(row.Status))
            .ToList();
        if (blockedAspects.Count > 0)
        {
            var aspectNames = blockedAspects.Select(row => row.Aspect).ToList();
            sources.Add(new TaskTestEvidenceSource
            {
                Kind = "review-aspects",
                Id = attemptId,
                Commit = commit,
                Result = "blocked",
                ObservedAt = observedAt,
                Summary = $"Review blocked by {NaturalList(aspectNames)}",
                Reason = Sentence(string.Join("; ", blockedAspects.Select(row =>
                    $"{row.Aspect} blocked: {TrimSentence(row.Summary)}"))),
                ReportRef = reportRef,
            });
        }

        return sources;
    }

    private static TaskTestEvidenceSource? ReadGate(string path, string kind, string label)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        var verdict = ReadToken(text, "verdict");
        var reason = ReadLineValue(text, "reason");
        var commit = ReadToken(text, "testedSha");
        if (string.IsNullOrWhiteSpace(commit) || commit.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            commit = ReadToken(text, "expectedSha");
        if (string.IsNullOrWhiteSpace(verdict)
            || string.IsNullOrWhiteSpace(commit)
            || commit.Equals("n/a", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalizedVerdict = verdict.ToLowerInvariant();
        var legacyNotApplicable = normalizedVerdict == "skipped"
                                  && string.Equals(
                                      reason,
                                      "no verify commands derivable",
                                      StringComparison.OrdinalIgnoreCase);
        var result = normalizedVerdict switch
        {
            "ok" or "warn" => "passed",
            "fail" => "failed",
            "notapplicable" or "not-applicable" => "not-applicable",
            "skipped" when legacyNotApplicable => "not-applicable",
            _ => "not-proven",
        };
        var resultLabel = result switch
        {
            "passed" => "green",
            "failed" => "failed",
            "not-applicable" => "not applicable",
            _ => "skipped",
        };
        var observedAt = ParseDate(ReadToken(text, "completedAtUtc"))
                         ?? File.GetLastWriteTimeUtc(path);
        var id = ReadToken(text, "gateRunId");
        if (string.IsNullOrWhiteSpace(id) || id.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            id = Path.GetFileNameWithoutExtension(path);

        return new TaskTestEvidenceSource
        {
            Kind = kind,
            Id = id,
            Commit = commit,
            Result = result,
            ObservedAt = observedAt,
            Summary = result == "not-applicable" && kind == "build-test-gate"
                ? "No build/test defined"
                : $"{label} {resultLabel} at {Short(commit)}",
            Reason = Sentence(string.IsNullOrWhiteSpace(reason)
                ? $"{label} reported verdict {verdict}"
                : reason),
            ReportRef = $"post-steps/{Path.GetFileName(path)}",
        };
    }

    private static Dictionary<string, string> ReadFrontmatter(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text);
        if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal)) return values;
        while (reader.ReadLine() is { } line)
        {
            if (string.Equals(line.Trim(), "---", StringComparison.Ordinal)) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            values[line[..colon].Trim()] = Unquote(line[(colon + 1)..].Trim());
        }
        return values;
    }

    private static IReadOnlyList<string> ParseTableRow(string line) =>
        line.Trim('|').Split('|').Select(value => value.Trim()).ToList();

    private static IEnumerable<IReadOnlyList<string>> ReadTable(string text, string heading)
    {
        var inSection = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!inSection)
            {
                inSection = line.Equals(heading, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal)) yield break;
            if (!line.StartsWith('|')) continue;
            yield return ParseTableRow(line);
        }
    }

    private static bool IsTableHeader(IReadOnlyList<string> columns) =>
        columns.Count == 0
        || columns[0].Equals("Aspect", StringComparison.OrdinalIgnoreCase)
        || columns[0].Equals("Phase", StringComparison.OrdinalIgnoreCase)
        || columns.All(column => column.Length > 0 && column.All(ch => ch is '-' or ':'));

    private static bool IsBlocking(string status) =>
        status.Equals("block", StringComparison.OrdinalIgnoreCase)
        || status.Equals("blocked", StringComparison.OrdinalIgnoreCase)
        || status.Equals("fail", StringComparison.OrdinalIgnoreCase)
        || status.Equals("failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildVerifyStep(string step) =>
        step.Length > "verify-".Length
        && step.StartsWith("verify-", StringComparison.OrdinalIgnoreCase)
        && step["verify-".Length..].All(char.IsDigit);

    private static string? StepId(string summary)
    {
        const string marker = "Review command '";
        var start = summary.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += marker.Length;
        var end = summary.IndexOf('\'', start);
        return end > start ? summary[start..end] : null;
    }

    private static string FailedBuildReason(
        IReadOnlyList<ReviewVerdictRow> rows,
        IReadOnlyList<string?> steps)
    {
        var failures = rows
            .Select((row, index) => (Row: row, Step: index < steps.Count ? steps[index] : null))
            .Where(item => !item.Row.Status.Equals("pass", StringComparison.OrdinalIgnoreCase))
            .Select(item => $"{item.Step ?? "build-tests"} failed: {TrimSentence(item.Row.Summary)}")
            .ToList();
        return Sentence(failures.Count > 0
            ? string.Join("; ", failures)
            : "A build-tests verdict failed");
    }

    private static string NaturalList(IReadOnlyList<string> values) => values.Count switch
    {
        0 => "",
        1 => values[0],
        2 => $"{values[0]} and {values[1]}",
        _ => string.Join(", ", values.Take(values.Count - 1)) + $", and {values[^1]}",
    };

    private static string Sentence(string value) => TrimSentence(value) + ".";

    private static string TrimSentence(string value) => value.Trim().TrimEnd('.', ';', ':');

    private static string? ReadToken(string text, string key)
    {
        var marker = key + "=";
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var value = line[(index + marker.Length)..];
            var end = value.IndexOfAny([' ', '\r', '\n']);
            return (end >= 0 ? value[..end] : value).Trim();
        }
        return null;
    }

    private static string? ReadLineValue(string text, string key)
    {
        var marker = key + "=";
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
            return line[marker.Length..].Trim();
        }
        return null;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal)
            : value;

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static void AddSignature(string path, ICollection<string> signature)
    {
        var info = new FileInfo(path);
        signature.Add($"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
    }

    private sealed record ReviewVerdictRow(
        string Aspect,
        string Status,
        string Classification,
        string Summary);
}

internal sealed record TaskScopedTestEvidenceSnapshot(
    IReadOnlyList<TaskTestEvidenceSource> Sources,
    string Signature);
