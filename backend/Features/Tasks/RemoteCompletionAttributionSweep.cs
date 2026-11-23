using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;

namespace AgentStudio.Tasks;

/// <summary>
/// One-time conservative repair for recent remote deliveries that predate the
/// pickup-base attribution and durable token-receipt fixes. Only a fenced
/// review subject, an exact ancestor base, and a non-empty task-owned range can
/// produce commit attribution.
/// </summary>
public sealed partial class RemoteCompletionAttributionSweep
{
    public const string ReportFileName = "remote-completion-attribution-v1.json";
    public const string SalvageSubjectReportFileName = "salvage-subject-attribution-v1.json";
    public const string IntegrationHistoryReportFileName = "integration-history-attribution-v3.json";
    public const string CommitSubjectKeyReportFileName = "commit-subject-key-attribution-v1.json";
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(8);
    // AGT-3968: the integration-history pass also repairs merge-only and
    // unchanged continuation deliveries parked since 16 November.
    private static readonly TimeSpan IntegrationHistoryLookback = TimeSpan.FromDays(14);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly TaskScannerService _scanner;
    private readonly GitService _git;
    private readonly TaskMutationService _mutations;
    private readonly RemoteTokenReceiptService _tokens;
    private readonly ILogger<RemoteCompletionAttributionSweep> _logger;
    private readonly string _reportPath;
    private readonly DateTimeOffset _now;
    private readonly TaskStateMachine? _states;
    private readonly TimelineLog? _timeline;
    private readonly AttemptAuthorityService? _authority;

    public RemoteCompletionAttributionSweep(
        TaskScannerService scanner,
        GitService git,
        TaskMutationService mutations,
        RemoteTokenReceiptService tokens,
        IConfiguration configuration,
        ILogger<RemoteCompletionAttributionSweep> logger,
        TaskStateMachine states,
        TimelineLog timeline,
        AttemptAuthorityService authority)
        : this(
            scanner,
            git,
            mutations,
            tokens,
            Path.Combine(
                Path.GetFullPath(configuration["TaskRepository"]
                    ?? Path.Combine(AppContext.BaseDirectory, "workspace")),
                ".metadata",
                "migrations",
                ReportFileName),
            DateTimeOffset.UtcNow,
            logger, states, timeline, authority)
    {
    }

    internal RemoteCompletionAttributionSweep(
        TaskScannerService scanner,
        GitService git,
        TaskMutationService mutations,
        RemoteTokenReceiptService tokens,
        string reportPath,
        DateTimeOffset now,
        ILogger<RemoteCompletionAttributionSweep> logger,
        TaskStateMachine? states = null,
        TimelineLog? timeline = null,
        AttemptAuthorityService? authority = null)
    {
        _scanner = scanner;
        _git = git;
        _mutations = mutations;
        _tokens = tokens;
        _reportPath = reportPath;
        _now = now;
        _logger = logger;
        _states = states;
        _timeline = timeline;
        _authority = authority;
    }

    public RemoteCompletionAttributionSweepReport RunOnce()
    {
        if (File.Exists(_reportPath))
        {
            var prior = JsonSerializer.Deserialize<RemoteCompletionAttributionSweepReport>(
                File.ReadAllText(_reportPath),
                Json);
            return prior is null
                ? RemoteCompletionAttributionSweepReport.CompletedEarlier()
                : prior with { AlreadyCompleted = true };
        }

        var rows = new List<RemoteCompletionAttributionSweepTaskReport>();
        foreach (var task in _scanner.ScanAllJobsWithArchive()
                     .Where(task => TaskIntegrationStatusService.DeliveredLanes.Contains(task.State)))
        {
            var subject = ReviewSubjectStore.Read(task.FolderPath);
            if (subject is null || subject.CompletedAtUtc < _now - Lookback) continue;

            var commitCount = 0;
            var tokenCalls = 0;
            string? warning = null;
            if (task.Commits.Count == 0)
            {
                var repair = RepairCommits(task, subject);
                commitCount = repair.Commits;
                warning = repair.Warning;
            }

            if (!HasTokenSummary(task.FolderPath))
            {
                var receipt = _tokens.PersistFromLog(
                    task,
                    subject.RunAttemptId,
                    string.IsNullOrWhiteSpace(subject.Executor) ? "remote-runner-backfill" : subject.Executor);
                tokenCalls = receipt.Persisted ? receipt.Calls : 0;
                warning = JoinWarnings(warning, receipt.Warning);
            }

            if (commitCount == 0 && tokenCalls == 0 && string.IsNullOrWhiteSpace(warning)) continue;
            rows.Add(new RemoteCompletionAttributionSweepTaskReport(
                task.ProjectName,
                task.Key ?? task.Id,
                subject.ResultSha,
                commitCount,
                tokenCalls,
                warning));
        }

        var report = new RemoteCompletionAttributionSweepReport(
            Version: 1,
            CompletedAtUtc: _now,
            AlreadyCompleted: false,
            RepairedCommitTasks: rows.Count(row => row.Commits > 0),
            RepairedTokenTasks: rows.Count(row => row.TokenCalls > 0),
            UnresolvedTasks: rows.Count(row => !string.IsNullOrWhiteSpace(row.Warning)),
            Tasks: rows);
        WriteReport(report, _reportPath);
        _logger.LogInformation(
            "remote-completion-attribution-sweep completed commitTasks={CommitTasks} tokenTasks={TokenTasks} unresolved={Unresolved} report={Report}",
            report.RepairedCommitTasks,
            report.RepairedTokenTasks,
            report.UnresolvedTasks,
            _reportPath);
        return report;
    }

    /// <summary>
    /// AGT-3717: one-time repair for deliveries of the Deploy 38 runner. Its
    /// salvage commit subject carried the card title, and a title naming a
    /// related card made the guard reject the whole range. Only a current
    /// generation without attributed commits, whose verified range attributes
    /// solely because the guard now exempts this card's own salvage commit,
    /// is written. Any other rejection stays rejected.
    /// </summary>
    public RemoteCompletionAttributionSweepReport RunSalvageSubjectRepairOnce()
    {
        var reportPath = Path.Combine(Path.GetDirectoryName(_reportPath)!, SalvageSubjectReportFileName);
        if (File.Exists(reportPath))
        {
            var prior = JsonSerializer.Deserialize<RemoteCompletionAttributionSweepReport>(
                File.ReadAllText(reportPath),
                Json);
            return prior is null
                ? RemoteCompletionAttributionSweepReport.CompletedEarlier()
                : prior with { AlreadyCompleted = true };
        }

        var rows = new List<RemoteCompletionAttributionSweepTaskReport>();
        foreach (var task in _scanner.ScanAllJobsWithArchive()
                     .Where(task => TaskIntegrationStatusService.DeliveredLanes.Contains(task.State)))
        {
            var subject = ReviewSubjectStore.Read(task.FolderPath);
            if (subject is null || subject.CompletedAtUtc < _now - Lookback) continue;
            if (task.Commits.Any(commit => string.Equals(
                    commit.RunAttemptId, subject.RunAttemptId, StringComparison.OrdinalIgnoreCase)))
                continue;

            var key = task.Key ?? task.Id;
            var repair = RepairCommits(task, subject, range => range.Any(commit =>
                RunnerSalvageCommit.IsTeardownFor(commit.Body, key)
                && RemoteCommitAttributionGuard.OtherKeyInSubject(commit.Subject, key) is not null));
            if (repair.Commits == 0 && repair.Warning is null) continue;
            rows.Add(new RemoteCompletionAttributionSweepTaskReport(
                task.ProjectName,
                key,
                subject.ResultSha,
                repair.Commits,
                0,
                repair.Warning));
        }

        var report = new RemoteCompletionAttributionSweepReport(
            Version: 1,
            CompletedAtUtc: _now,
            AlreadyCompleted: false,
            RepairedCommitTasks: rows.Count(row => row.Commits > 0),
            RepairedTokenTasks: 0,
            UnresolvedTasks: rows.Count(row => !string.IsNullOrWhiteSpace(row.Warning)),
            Tasks: rows);
        WriteReport(report, reportPath);
        _logger.LogInformation(
            "salvage-subject-attribution-repair completed commitTasks={CommitTasks} unresolved={Unresolved} report={Report}",
            report.RepairedCommitTasks,
            report.UnresolvedTasks,
            reportPath);
        return report;
    }

    /// <summary>
    /// Repairs a passing reviewed delivery escalated solely because a commit
    /// imported from the integration line was mistaken for task work, or
    /// because a continuation round added only integration merges or nothing
    /// at all (AGT-3947, AGT-3968). For the last case the pickup range is
    /// empty: the runner's develop merge is the pickup base, or the round
    /// re-delivered the earlier SHA. The integration merge-base range is then
    /// attributed under the strict continuation rule.
    /// </summary>
    public RemoteCompletionAttributionSweepReport RunIntegrationHistoryRepairOnce()
    {
        var reportPath = Path.Combine(Path.GetDirectoryName(_reportPath)!, IntegrationHistoryReportFileName);
        if (File.Exists(reportPath))
        {
            var prior = JsonSerializer.Deserialize<RemoteCompletionAttributionSweepReport>(
                File.ReadAllText(reportPath), Json);
            if (prior is { UnresolvedTasks: 0 }) return prior with { AlreadyCompleted = true };
        }

        var rows = new List<RemoteCompletionAttributionSweepTaskReport>();
        if (_states is not null && _timeline is not null && _authority is not null)
        foreach (var task in _scanner.ScanAllJobsWithArchive().Where(task => task.State == TaskStates.Escalated))
        {
            var subject = ReviewSubjectStore.Read(task.FolderPath);
            if (subject is null || subject.CompletedAtUtc < _now - IntegrationHistoryLookback) continue;
            var key = task.Key ?? task.Id;
            if (!IsCurrentPassingReview(key, subject)) continue;
            if (RemoteCommitAttributionGuard.CoversDelivery(task.Commits, subject.RunAttemptId, subject.ResultSha))
                continue;
            var warning = RunFinishedAttributionWarning(task, subject);
            var match = Regex.Match(warning ?? "", @"commit (?<sha>[0-9a-fA-F]{7,40}) names foreign task");
            var repository = _git.ResolveRepoRootForWatchPath(task.WatchPath);
            var deliveryRef = subject.ImmutableResultRef ?? subject.ResultRef;
            if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(deliveryRef)) continue;
            var integrationBranch = subject.IntegrationBranch ?? task.IntegrationBranch;
            var range = _git.InspectRemoteDeliveryCommitRange(repository, deliveryRef,
                subject.ResultSha, integrationBranch, subject.BaseSha, taskKey: key);
            var continuationRange = false;
            if (!match.Success && task.Commits.Count > 0 && (!range.Success || range.Commits.Count == 0))
            {
                range = _git.InspectRemoteDeliveryCommitRange(repository, deliveryRef,
                    subject.ResultSha, integrationBranch, expectedBaseSha: null, taskKey: key);
                continuationRange = true;
            }
            if (!range.Success || range.Commits.Count == 0) continue;
            var integrationRef = "refs/remotes/origin/" + TaskIntegrationBranch.Name(range.IntegrationBranch);
            var importedForeignCommit = match.Success
                && _git.IsAncestor(repository, match.Groups["sha"].Value, integrationRef);
            var newDeliveryCommits = range.Commits.Where(commit => !task.Commits.Any(existing =>
                string.Equals(existing.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase))).ToList();
            var mergeOnlyRecovery = !match.Success
                && (newDeliveryCommits.Count > 0 || continuationRange)
                && newDeliveryCommits.All(commit => commit.IsMerge);
            if (!importedForeignCommit && !mergeOnlyRecovery) continue;

            var repair = continuationRange
                ? AttributeRange(task, subject, repository, deliveryRef, range, continuationRange: true)
                : RepairCommits(task, subject);
            if (repair.Commits == 0)
            {
                var unresolved = repair.Warning ?? "No commit could be attributed.";
                if (unresolved.StartsWith(RejectedPrefix, StringComparison.Ordinal))
                    unresolved = unresolved[RejectedPrefix.Length..];
                rows.Add(new RemoteCompletionAttributionSweepTaskReport(task.ProjectName, key,
                    subject.ResultSha, 0, 0, unresolved));
                continue;
            }
            var details = new Dictionary<string, string>
            {
                ["reason"] = importedForeignCommit
                    ? "imported-integration-history"
                    : continuationRange
                        ? "continuation-without-own-commit"
                        : "merge-only-recovery-delivery",
            };
            if (importedForeignCommit) details["foreignCommit"] = match.Groups["sha"].Value;
            rows.Add(RestoreToAutoReview(task, subject, repair.Commits,
                "integration-history-attribution-repaired",
                TimelineEventKinds.IntegrationHistoryAttributionRepaired,
                "Restored the passing reviewed delivery after excluding integration history from attribution.",
                details));
        }

        var report = new RemoteCompletionAttributionSweepReport(1, _now, false,
            rows.Count(row => row.Commits > 0 && row.Warning is null), 0,
            rows.Count(row => row.Warning is not null), rows);
        WriteReport(report, reportPath);
        return report;
    }

    /// <summary>
    /// AGT-3854: repairs a passing reviewed delivery escalated because the
    /// subject guard read a finding id (<c>AGT-3794-F13</c>), a key the card
    /// references, or a subject that also names the card's own key as foreign
    /// work. The range is attributed through the current guard; a delivery
    /// whose subjects still name an unreferenced foreign key stays escalated.
    /// </summary>
    public RemoteCompletionAttributionSweepReport RunCommitSubjectKeyRepairOnce()
    {
        var reportPath = Path.Combine(Path.GetDirectoryName(_reportPath)!, CommitSubjectKeyReportFileName);
        if (File.Exists(reportPath))
        {
            var prior = JsonSerializer.Deserialize<RemoteCompletionAttributionSweepReport>(
                File.ReadAllText(reportPath), Json);
            if (prior is { UnresolvedTasks: 0 }) return prior with { AlreadyCompleted = true };
        }

        var rows = new List<RemoteCompletionAttributionSweepTaskReport>();
        if (_states is not null && _timeline is not null && _authority is not null)
        foreach (var task in _scanner.ScanAllJobsWithArchive().Where(task => task.State == TaskStates.Escalated))
        {
            var subject = ReviewSubjectStore.Read(task.FolderPath);
            if (subject is null || subject.CompletedAtUtc < _now - Lookback) continue;
            var key = task.Key ?? task.Id;
            if (!IsCurrentPassingReview(key, subject)) continue;
            var warning = RunFinishedAttributionWarning(task, subject);
            var match = ForeignSubjectWarning().Match(warning ?? "");
            if (!match.Success) continue;
            if (task.Commits.Any(commit => string.Equals(commit.RunAttemptId,
                    subject.RunAttemptId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(commit.ResultSha, subject.ResultSha, StringComparison.OrdinalIgnoreCase)))
                continue;

            var repair = RepairCommits(task, subject);
            if (repair.Commits == 0)
            {
                // A subject that still names an unreferenced foreign key keeps
                // the escalation; that is the guard working, not a failed repair.
                if (!ForeignSubjectWarning().IsMatch(repair.Warning ?? ""))
                    rows.Add(new RemoteCompletionAttributionSweepTaskReport(task.ProjectName, key,
                        subject.ResultSha, 0, 0, repair.Warning));
                continue;
            }
            rows.Add(RestoreToAutoReview(task, subject, repair.Commits,
                "commit-subject-key-attribution-repaired",
                TimelineEventKinds.CommitSubjectKeyAttributionRepaired,
                $"Restored the passing reviewed delivery: '{match.Groups["key"].Value}' in a commit subject is no longer read as another card's work.",
                new Dictionary<string, string>
                {
                    ["reason"] = "commit-subject-key-allowed",
                    ["foreignCommit"] = match.Groups["sha"].Value,
                    ["namedKey"] = match.Groups["key"].Value,
                }));
        }

        var report = new RemoteCompletionAttributionSweepReport(1, _now, false,
            rows.Count(row => row.Commits > 0 && row.Warning is null), 0,
            rows.Count(row => row.Warning is not null), rows);
        WriteReport(report, reportPath);
        _logger.LogInformation(
            "commit-subject-key-attribution-repair completed commitTasks={CommitTasks} unresolved={Unresolved} report={Report}",
            report.RepairedCommitTasks,
            report.UnresolvedTasks,
            reportPath);
        return report;
    }

    [GeneratedRegex(@"commit (?<sha>[0-9a-fA-F]{7,40}) names foreign task '(?<key>[^']+)'",
        RegexOptions.CultureInvariant)]
    private static partial Regex ForeignSubjectWarning();

    private bool IsCurrentPassingReview(string key, ReviewSubjectRecord subject)
    {
        var projection = _authority!.GetTaskProjection(key);
        var review = projection.CurrentReviewAttempt;
        return review?.Outcome == ReviewTerminalOutcome.Pass
            && string.Equals(projection.CurrentRunAttempt?.AttemptId, subject.RunAttemptId,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(review.SourceRunAttemptId, subject.RunAttemptId,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(review.TestedResultSha, subject.ResultSha,
                StringComparison.OrdinalIgnoreCase);
    }

    private string? RunFinishedAttributionWarning(TaskInfo task, ReviewSubjectRecord subject)
        => _timeline!.ReadAll(task.FolderPath).LastOrDefault(evt =>
                evt.Kind == TimelineEventKinds.AgentRunFinished
                && string.Equals(evt.RunId, subject.RunAttemptId, StringComparison.OrdinalIgnoreCase))
            ?.Details?.GetValueOrDefault("commitAttributionWarning");

    // Moves a reattributed escalated delivery back to Auto Review and records
    // why; it starts no coding round.
    private RemoteCompletionAttributionSweepTaskReport RestoreToAutoReview(
        TaskInfo task,
        ReviewSubjectRecord subject,
        int commits,
        string transitionDetail,
        string eventKind,
        string summary,
        Dictionary<string, string> details)
    {
        var key = task.Key ?? task.Id;
        var moved = _states!.MoveJob(task.Id, TaskStates.AutoReview, task.WatchPath,
            cause: TimelineActors.System, expectedSourceState: TaskStates.Escalated,
            transitionCause: LaneChangeCauses.ReviewVerdict,
            transitionDetail: transitionDetail);
        if (moved.Status != MoveJobStatus.Success)
            return new RemoteCompletionAttributionSweepTaskReport(task.ProjectName, key,
                subject.ResultSha, commits, 0, $"Lane restoration failed: {moved.Status}.");
        details["resultSha"] = subject.ResultSha;
        details["attributedCommits"] = commits.ToString();
        _timeline!.Append(moved.NewFolderPath ?? task.FolderPath, eventKind, TimelineActors.System,
            summary, subject.RunAttemptId, details: details);
        return new RemoteCompletionAttributionSweepTaskReport(task.ProjectName, key,
            subject.ResultSha, commits, 0, null);
    }

    internal static IReadOnlyList<string> BaseCandidates(
        TaskInfo task,
        ReviewSubjectRecord subject)
    {
        var values = new List<string?> { subject.BaseSha, task.Provenance?.Base };
        values.AddRange((task.Provenance?.Transitions ?? [])
            .Where(transition => transition.AtUtc <= subject.CompletedAtUtc.UtcDateTime)
            .OrderByDescending(transition => transition.AtUtc)
            .Select(transition => transition.WorkBranchHead));
        return values
            .Where(ReviewSubjectStore.IsValidResultSha)
            .Select(value => value!.Trim())
            .Where(value => !string.Equals(value, subject.ResultSha, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private (int Commits, string? Warning) RepairCommits(
        TaskInfo task,
        ReviewSubjectRecord subject,
        Func<IReadOnlyList<GitCommitInfo>, bool>? rangeQualifies = null)
    {
        var repository = _git.ResolveRepoRootForWatchPath(task.WatchPath);
        var deliveryRef = subject.ImmutableResultRef ?? subject.ResultRef;
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(deliveryRef))
            return (0, "Repository or immutable delivery ref is unavailable.");

        var warnings = new List<string>();
        foreach (var baseSha in BaseCandidates(task, subject))
        {
            var range = _git.InspectRemoteDeliveryCommitRange(
                repository,
                deliveryRef,
                subject.ResultSha,
                subject.IntegrationBranch ?? task.IntegrationBranch,
                baseSha,
                taskKey: task.Key ?? task.Id);
            if (!range.Success)
            {
                if (!string.IsNullOrWhiteSpace(range.Warning)) warnings.Add(range.Warning);
                continue;
            }
            if (range.Commits.Count == 0)
            {
                warnings.Add($"Base {baseSha[..8]} produced an empty delivery range.");
                continue;
            }

            // A range the narrower pass does not own is left as it is.
            if (rangeQualifies is not null && !rangeQualifies(range.Commits))
                return (0, null);

            var attributed = AttributeRange(task, subject, repository, deliveryRef, range,
                continuationRange: false);
            if (attributed.Commits == 0 && attributed.Warning is { } rejected
                && rejected.StartsWith(RejectedPrefix, StringComparison.Ordinal))
            {
                warnings.Add(rejected[RejectedPrefix.Length..]);
                continue;
            }
            return attributed;
        }

        return (0, warnings.Count == 0
            ? "No unambiguous pickup base is recorded for the remote delivery."
            : string.Join(" ", warnings.Distinct(StringComparer.Ordinal)));
    }

    private const string RejectedPrefix = "rejected:";

    // Attributes one verified range to the subject's run attempt. A guard
    // rejection is returned with RejectedPrefix so RepairCommits can try the
    // next base candidate.
    private (int Commits, string? Warning) AttributeRange(
        TaskInfo task,
        ReviewSubjectRecord subject,
        string repository,
        string deliveryRef,
        RemoteDeliveryCommitRange range,
        bool continuationRange)
    {
        var integrationRef = "refs/remotes/origin/" + TaskIntegrationBranch.Name(range.IntegrationBranch);
        var attribution = RemoteCommitAttributionGuard.Attribute(
            task.Key ?? task.Id,
            deliveryRef,
            range.Commits,
            subject.Repository,
            RemoteCommitAttributionGuard.CardReferencedKeys(task),
            task.Commits,
            parent => _git.IsAncestor(repository, parent, integrationRef),
            continuationRange);
        if (!attribution.Accepted)
            return (0, RejectedPrefix + (attribution.Warning ?? "attribution rejected"));

        _mutations.SetRunIntegrationBranchOnFolder(task.FolderPath, range.IntegrationBranch!);
        var written = _mutations.SetRemoteCommitAttributionOnFolder(
            task.FolderPath,
            subject.RunAttemptId,
            string.IsNullOrWhiteSpace(subject.Executor) ? "remote-runner-backfill" : subject.Executor,
            subject.ResultSha,
            attribution.Commits);
        if (!written) return (0, "The verified delivery range could not be persisted.");
        var current = _scanner.FindJob(task.Key ?? task.Id, task.WatchPath);
        // Commits this run produced; an unchanged re-delivery produced none and
        // counts the inherited commits its delivery generation now carries.
        var producedForRun = current?.Commits.Count(commit =>
            string.Equals(commit.RunAttemptId, subject.RunAttemptId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(commit.ResultSha, subject.ResultSha, StringComparison.OrdinalIgnoreCase)) ?? 0;
        var persistedForRun = producedForRun > 0 ? producedForRun : current?.Commits.Count(commit =>
            string.Equals(commit.DeliveryAttemptId, subject.RunAttemptId, StringComparison.OrdinalIgnoreCase)) ?? 0;
        return persistedForRun > 0
               && current is not null
               && RemoteCommitAttributionGuard.CoversDelivery(current.Commits, subject.RunAttemptId, subject.ResultSha)
            ? (persistedForRun, null)
            : (0, "The verified range contained only commits attributed to an earlier run.");
    }

    private static bool HasTokenSummary(string folderPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folderPath, "task.json")));
            return document.RootElement.TryGetProperty("tokenSummary", out var value)
                   && value.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static string? JoinWarnings(string? left, string? right)
        => string.IsNullOrWhiteSpace(left) ? right
            : string.IsNullOrWhiteSpace(right) ? left
            : left + " " + right;

    private static void WriteReport(RemoteCompletionAttributionSweepReport report, string reportPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var temporary = reportPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, report, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, reportPath, overwrite: true);
    }
}

public sealed record RemoteCompletionAttributionSweepTaskReport(
    string Project,
    string TaskKey,
    string ResultSha,
    int Commits,
    int TokenCalls,
    string? Warning);

public sealed record RemoteCompletionAttributionSweepReport(
    int Version,
    DateTimeOffset CompletedAtUtc,
    bool AlreadyCompleted,
    int RepairedCommitTasks,
    int RepairedTokenTasks,
    int UnresolvedTasks,
    IReadOnlyList<RemoteCompletionAttributionSweepTaskReport> Tasks)
{
    public static RemoteCompletionAttributionSweepReport CompletedEarlier()
        => new(1, DateTimeOffset.MinValue, true, 0, 0, 0, []);
}
