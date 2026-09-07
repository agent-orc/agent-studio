using System.Globalization;
using AgentStudio.Review;

namespace AgentStudio.TestRuns;

/// <summary>
/// Reads deterministic test evidence that is already attached to a task
/// folder. Remote Review build-tests grades and build/test gate logs predate
/// the project-wide TestRunStore, so card projection must include both sources
/// instead of treating the absence of a project run as the absence of evidence.
///
/// <para>
/// Since AGT-2717 the review half of that evidence comes from the canonical
/// <see cref="ReviewRoundRecord"/> rather than from report Markdown. The reading
/// rule AGT-2714 established is unchanged and now lives in
/// <see cref="ReviewRoundProjectionPolicy"/>: build-tests are derived only from
/// build-tests verdict rows, a blocking semantic verdict never changes them, and
/// a missing row is <c>not-proven</c> with a reason that names the command.
/// </para>
/// </summary>
internal static class TaskScopedTestEvidenceReader
{
    private static readonly (string Pattern, string Kind, string Label)[] GatePatterns =
    [
        ("build-test-gate-*.log", "build-test-gate", "Build/test gate"),
        ("pre-develop-build-gate-*.log", "pre-develop-build-gate", "Pre-develop build gate"),
        ("pre-main-test-gate-*.log", "pre-main-test-gate", "Pre-main test gate"),
    ];

    /// <summary>Report files whose derived state now comes from the record.</summary>
    private static readonly string[] ReviewSignaturePatterns =
    [
        ReviewRoundRecordSchema.FilePattern,
        "remote-review-grade-*.md",
    ];

    public static TaskScopedTestEvidenceSnapshot Read(TaskInfo task)
    {
        if (string.IsNullOrWhiteSpace(task.FolderPath) || !Directory.Exists(task.FolderPath))
            return new([], "missing");

        var sources = new List<TaskTestEvidenceSource>();
        var signature = new List<string>();
        try
        {
            foreach (var pattern in ReviewSignaturePatterns)
            {
                foreach (var path in Directory.EnumerateFiles(
                             task.FolderPath,
                             pattern,
                             SearchOption.TopDirectoryOnly))
                {
                    AddSignature(path, signature);
                }
            }

            foreach (var round in ReadReviewRounds(task.FolderPath))
                sources.AddRange(FromReviewRound(round));

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

    /// <summary>
    /// Canonical records, plus a Markdown backfill for any round written before
    /// the record existed. Only rounds that carry review evidence of their own
    /// reach the Evidence tab; a local grade round has no commands and no
    /// aspects, so it contributes nothing here.
    /// </summary>
    private static IEnumerable<ReviewRoundRecord> ReadReviewRounds(string jobFolder)
    {
        var records = ReviewRoundRecordStore.ReadAll(jobFolder);
        var known = records
            .Select(record => $"{ReviewPlanes.Normalize(record.Plane)}:{record.AttemptId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return records.Concat(ReviewRoundMarkdownBackfill.Read(jobFolder)
            .Where(round => !known.Contains($"{ReviewPlanes.Normalize(round.Plane)}:{round.AttemptId}")));
    }

    /// <summary>
    /// One round's two independent evidence rows: the command proof and, when a
    /// semantic verdict blocks, the reason it blocked. They are kept separate so
    /// a blocked aspect never turns a passing build red.
    /// </summary>
    private static IEnumerable<TaskTestEvidenceSource> FromReviewRound(ReviewRoundRecord round)
    {
        var commit = round.SubjectSha;
        if (string.IsNullOrWhiteSpace(commit)) yield break;

        // The single-round projection reuses the shared policy, so the Evidence
        // tab and the review banner always phrase the same round identically.
        var projection = ReviewRoundProjectionPolicy.Build(
            new ReviewProjectionInputs(round.AttemptId, [round]));

        if (round.BuildTests.Count > 0)
        {
            var buildTests = projection.BuildTests;
            var stepSuffix = buildTests.Steps.Count > 0
                ? $" ({string.Join(", ", buildTests.Steps)})"
                : "";
            yield return new TaskTestEvidenceSource
            {
                Kind = "review-build-tests",
                Id = round.AttemptId,
                Commit = commit,
                Result = buildTests.Result,
                ObservedAt = round.ReceivedAt,
                Summary = $"Review build-tests {ResultLabel(buildTests.Result)} at {Short(commit)}{stepSuffix}",
                Reason = buildTests.Reason,
                ReportRef = round.ReportRef ?? "",
            };
        }

        if (projection.BlockingAspects.Count > 0)
        {
            yield return new TaskTestEvidenceSource
            {
                Kind = "review-aspects",
                Id = round.AttemptId,
                Commit = commit,
                Result = "blocked",
                ObservedAt = round.ReceivedAt,
                Summary = $"Review blocked by {NaturalList(projection.BlockingAspects.Select(a => a.Name).ToList())}",
                Reason = Sentence(string.Join("; ", projection.BlockingAspects.Select(aspect =>
                    aspect.Summary.Length > 0
                        ? $"{aspect.Name} blocked: {TrimSentence(aspect.Summary)}"
                        : $"{aspect.Name} blocked without a recorded reason"))),
                ReportRef = round.ReportRef ?? "",
            };
        }
    }

    private static string ResultLabel(string result) => result switch
    {
        ReviewBuildTestsResults.Passed => "Pass",
        ReviewBuildTestsResults.Failed => "Failed",
        _ => "Not proven",
    };

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

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static void AddSignature(string path, ICollection<string> signature)
    {
        var info = new FileInfo(path);
        signature.Add($"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
    }
}

internal sealed record TaskScopedTestEvidenceSnapshot(
    IReadOnlyList<TaskTestEvidenceSource> Sources,
    string Signature);
