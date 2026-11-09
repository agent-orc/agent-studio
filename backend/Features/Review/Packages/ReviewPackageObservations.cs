using System.Text.Json;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Review;

/// <summary>
/// One member's package verdict for one aspect, stored as a review observation
/// with level <c>package</c>. The member's owned and assigned findings travel
/// with it. Token counts are the aspect call's share per member.
/// </summary>
public sealed record ReviewPackageObservationRow(
    ReviewObservation Review,
    string PackageId,
    string MembershipDigest,
    string DossierPath,
    string? CardCheckAttemptId,
    string Status,
    string Classification,
    IReadOnlyList<ReviewPackageFindingDto> Findings,
    IReadOnlyList<ReviewPackageCriterionDto>? AcceptanceCriteria);

/// <summary>
/// Projects an admitted package report into each member's
/// <c>results/package-review-observations.jsonl</c>. The journal is separate
/// from <c>review-observations.jsonl</c> on purpose: per-card miss-rate cohorts,
/// review depth and the Token Economy export never read shadow package rows.
/// Replays append nothing twice.
/// </summary>
public static class ReviewPackageObservations
{
    public const string FileName = "package-review-observations.jsonl";
    public const string Level = "package";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string PathFor(string taskFolder) => Path.Combine(TaskPaths.ResultsDir(taskFolder), FileName);

    public static IReadOnlyList<ReviewPackageObservationRow> Rows(
        ReviewPackageSubjectDto subject, ReviewPackageReportRequest report, string attemptId, DateTime reviewedAt)
    {
        if (report.Outcome != ReviewPackageOutcomes.Completed) return [];
        var members = subject.Members.Count;
        var rows = new List<ReviewPackageObservationRow>();
        foreach (var verdict in report.Verdicts)
        {
            if (subject.Members.FirstOrDefault(item => item.TaskKey == verdict.TaskKey) is not { } member) continue;
            var command = report.Commands.FirstOrDefault(item => item.Aspect == verdict.Aspect);
            var findings = report.Findings
                .Where(finding => finding.OwnerTaskKey == member.TaskKey
                    && (verdict.Aspect == ReviewPackageAspects.Grade || finding.Aspect == verdict.Aspect))
                .ToArray();
            rows.Add(new ReviewPackageObservationRow(
                new ReviewObservation(
                    $"{subject.PackageId}:{verdict.Aspect}:{member.TaskKey}", member.TaskId ?? member.TaskKey, Level,
                    member.ResultSha, reviewedAt, command?.Model, command?.ThinkingLevel,
                    Share(command?.InputTokens, members), Share(command?.OutputTokens, members),
                    Share(command?.CacheReadTokens, members), Share(command?.CacheCreationTokens, members),
                    command is null ? 0 : (long)Math.Max(0, (command.FinishedAt - command.StartedAt).TotalMilliseconds),
                    verdict.Status,
                    new ReviewFindingCounts(
                        findings.Count(finding => finding.Severity == "block"),
                        findings.Count(finding => finding.Severity == "concern"),
                        findings.Count(finding => finding.Severity == "note")),
                    null, $"review-packages/{subject.PackageId}/report", Cli: command?.CliType,
                    Aspect: verdict.Aspect, FilesReviewed: report.MaterialFiles, ReviewRoundId: attemptId),
                subject.PackageId, subject.MembershipDigest, subject.DossierPath, member.CardCheckAttemptId,
                verdict.Status, verdict.Classification, findings, verdict.AcceptanceCriteria));
        }
        return rows;
    }

    /// <summary>Appends the rows whose id the member's journal does not hold yet.</summary>
    public static int Append(string taskFolder, IEnumerable<ReviewPackageObservationRow> rows)
    {
        var path = PathFor(taskFolder);
        var known = Read(taskFolder).Select(row => row.Review.Id).ToHashSet(StringComparer.Ordinal);
        var fresh = rows.Where(row => known.Add(row.Review.Id)).ToArray();
        if (fresh.Length == 0) return 0;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        foreach (var row in fresh)
        {
            JsonSerializer.Serialize(stream, row, Json);
            stream.WriteByte((byte)'\n');
        }
        stream.Flush(flushToDisk: true);
        return fresh.Length;
    }

    public static IReadOnlyList<ReviewPackageObservationRow> Read(string taskFolder)
    {
        var path = PathFor(taskFolder);
        if (!File.Exists(path)) return [];
        var rows = new List<ReviewPackageObservationRow>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<ReviewPackageObservationRow>(line, Json) is { } row) rows.Add(row);
            }
            catch (JsonException)
            {
                // A torn last line from an interrupted append is skipped. Its id
                // is then unknown, so a replayed report appends the row again.
                continue;
            }
        }
        return rows;
    }

    private static long Share(long? tokens, int members)
        => tokens is not { } value || members <= 0 ? 0 : (long)Math.Round(value / (double)members);
}
