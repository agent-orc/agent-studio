using System.Text.Json;
using AgentStudio.Pipeline;

namespace AgentStudio.Tasks;

/// <summary>
/// One-time conservative repair for recent remote deliveries that predate the
/// pickup-base attribution and durable token-receipt fixes. Only a fenced
/// review subject, an exact ancestor base, and a non-empty task-owned range can
/// produce commit attribution.
/// </summary>
public sealed class RemoteCompletionAttributionSweep
{
    public const string ReportFileName = "remote-completion-attribution-v1.json";
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(8);
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

    public RemoteCompletionAttributionSweep(
        TaskScannerService scanner,
        GitService git,
        TaskMutationService mutations,
        RemoteTokenReceiptService tokens,
        IConfiguration configuration,
        ILogger<RemoteCompletionAttributionSweep> logger)
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
            logger)
    {
    }

    internal RemoteCompletionAttributionSweep(
        TaskScannerService scanner,
        GitService git,
        TaskMutationService mutations,
        RemoteTokenReceiptService tokens,
        string reportPath,
        DateTimeOffset now,
        ILogger<RemoteCompletionAttributionSweep> logger)
    {
        _scanner = scanner;
        _git = git;
        _mutations = mutations;
        _tokens = tokens;
        _reportPath = reportPath;
        _now = now;
        _logger = logger;
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
        WriteReport(report);
        _logger.LogInformation(
            "remote-completion-attribution-sweep completed commitTasks={CommitTasks} tokenTasks={TokenTasks} unresolved={Unresolved} report={Report}",
            report.RepairedCommitTasks,
            report.RepairedTokenTasks,
            report.UnresolvedTasks,
            _reportPath);
        return report;
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
        ReviewSubjectRecord subject)
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
                baseSha);
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

            var attribution = RemoteCommitAttributionGuard.Attribute(
                task.Key ?? task.Id,
                deliveryRef,
                range.Commits,
                subject.Repository);
            if (!attribution.Accepted)
            {
                if (!string.IsNullOrWhiteSpace(attribution.Warning)) warnings.Add(attribution.Warning);
                continue;
            }

            _mutations.SetRunIntegrationBranchOnFolder(task.FolderPath, range.IntegrationBranch!);
            var written = _mutations.SetRemoteCommitAttributionOnFolder(
                task.FolderPath,
                subject.RunAttemptId,
                string.IsNullOrWhiteSpace(subject.Executor) ? "remote-runner-backfill" : subject.Executor,
                subject.ResultSha,
                attribution.Commits);
            return written
                ? (attribution.Commits.Count, null)
                : (0, "The verified delivery range could not be persisted.");
        }

        return (0, warnings.Count == 0
            ? "No unambiguous pickup base is recorded for the remote delivery."
            : string.Join(" ", warnings.Distinct(StringComparer.Ordinal)));
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

    private void WriteReport(RemoteCompletionAttributionSweepReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_reportPath)!);
        var temporary = _reportPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, report, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _reportPath, overwrite: true);
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
