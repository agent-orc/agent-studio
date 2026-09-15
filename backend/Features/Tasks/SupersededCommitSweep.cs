using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// Pure, conservative decision policy for the one-time superseded-delivery
/// repair. It only replaces a missing runner fence when a later, integrated
/// card commit from a different generation has comparable breadth and at
/// least 90 percent changed-file overlap, with no more than three omissions.
/// Anything outside those bounds remains unchanged and is reported for review.
/// </summary>
public static class SupersededCommitSweepPolicy
{
    /// <param name="commits">The card's attributed commits, oldest to newest.</param>
    /// <param name="isIntegrated">Target-branch ancestry check for one SHA.</param>
    /// <param name="isCandidate">
    /// Which non-integrated commits may be treated as missing-and-superseded.
    /// Defaults to <see cref="IsRunnerFence"/> for the historical one-time
    /// migration; the live integration projection passes a permissive
    /// predicate so any attributed commit can be recognized as superseded by
    /// a later, integrated delivery of the same card, not only a runner
    /// lifecycle fence.
    /// </param>
    public static SupersededCommitSweepTaskDecision Evaluate(
        IReadOnlyList<TaskCommitInfo> commits,
        Func<string, bool> isIntegrated,
        Func<TaskCommitInfo, bool>? isCandidate = null)
    {
        isCandidate ??= IsRunnerFence;
        var replacements = new List<SupersededCommitReplacement>();
        var ambiguous = new List<SupersededCommitAmbiguity>();

        for (var index = 0; index < commits.Count; index++)
        {
            var fence = commits[index];
            if (TaskCommitSupersession.IsSuperseded(fence)
                || !isCandidate(fence)
                || isIntegrated(fence.Sha))
            {
                continue;
            }

            var laterIntegrated = commits.Skip(index + 1)
                .Where(commit => !TaskCommitSupersession.IsSuperseded(commit))
                .Where(commit => !IsLifecycleMarker(commit)
                    || commit.FilesChanged > 0
                    || commit.Files.Count > 0)
                .Where(commit => isIntegrated(commit.Sha))
                .ToList();
            if (laterIntegrated.Count == 0)
            {
                ambiguous.Add(new SupersededCommitAmbiguity(
                    fence.Sha,
                    "No later integrated commit is available as a replacement.",
                    []));
                continue;
            }

            var fenceFiles = NormalizeFiles(fence.Files);
            if (fenceFiles.Count == 0)
            {
                ambiguous.Add(new SupersededCommitAmbiguity(
                    fence.Sha,
                    "The missing fence has no changed-file metadata for a breadth comparison.",
                    laterIntegrated.Select(commit => commit.Sha).ToList()));
                continue;
            }

            var replacement = laterIntegrated
                .Select(candidate => new
                {
                    Commit = candidate,
                    Breadth = CompareBreadth(fenceFiles, NormalizeFiles(candidate.Files)),
                })
                .FirstOrDefault(candidate =>
                    IsDifferentGeneration(fence, candidate.Commit)
                    && candidate.Breadth.IsMatch);
            if (replacement is null)
            {
                var hasDifferentGeneration = laterIntegrated.Any(candidate =>
                    IsDifferentGeneration(fence, candidate));
                ambiguous.Add(new SupersededCommitAmbiguity(
                    fence.Sha,
                    hasDifferentGeneration
                        ? "Later integrated commits do not cover the full changed-file breadth of the missing fence."
                        : "The later integrated commit is not proven to belong to a different delivery generation.",
                    laterIntegrated.Select(commit => commit.Sha).ToList()));
                continue;
            }

            replacements.Add(new SupersededCommitReplacement(
                fence.Sha,
                replacement.Commit.Sha,
                ReplacementAttempt(replacement.Commit),
                replacement.Breadth.CoveragePercent,
                replacement.Breadth.CommonFiles,
                replacement.Breadth.MissingFiles));
        }

        return new SupersededCommitSweepTaskDecision(replacements, ambiguous);
    }

    internal static bool IsRunnerFence(TaskCommitInfo commit)
        => Subject(commit).StartsWith(
            "wip(runner): salvage before teardown",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsLifecycleMarker(TaskCommitInfo commit)
    {
        var subject = Subject(commit);
        return IsRunnerFence(commit)
            || subject.StartsWith("chore: snapshot for review", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDifferentGeneration(TaskCommitInfo older, TaskCommitInfo newer)
    {
        if (!string.IsNullOrWhiteSpace(older.RunAttemptId)
            && !string.IsNullOrWhiteSpace(newer.RunAttemptId))
        {
            return !string.Equals(
                older.RunAttemptId,
                newer.RunAttemptId,
                StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(older.ResultSha)
            && !string.IsNullOrWhiteSpace(newer.ResultSha))
        {
            return !string.Equals(
                older.ResultSha,
                newer.ResultSha,
                StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(older.Branch)
            && !string.IsNullOrWhiteSpace(newer.Branch))
        {
            return !string.Equals(
                older.Branch,
                newer.Branch,
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string ReplacementAttempt(TaskCommitInfo replacement)
        => !string.IsNullOrWhiteSpace(replacement.RunAttemptId)
            ? replacement.RunAttemptId!
            : !string.IsNullOrWhiteSpace(replacement.ResultSha)
                ? replacement.ResultSha!
                : replacement.Sha;

    private static HashSet<string> NormalizeFiles(IReadOnlyCollection<string> files)
        => files.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/').TrimStart('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static BreadthComparison CompareBreadth(
        HashSet<string> fenceFiles,
        HashSet<string> candidateFiles)
    {
        var common = fenceFiles.Intersect(candidateFiles, StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missing = fenceFiles.Except(candidateFiles, StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var coverage = common.Count / (double)fenceFiles.Count;
        var relativeBreadth = candidateFiles.Count / (double)fenceFiles.Count;
        var comparableUpperBreadth = fenceFiles.Count < 10
            ? candidateFiles.Count <= fenceFiles.Count + 3
            : relativeBreadth <= 1.25;
        return new BreadthComparison(
            IsMatch: coverage >= 0.90
                && missing.Count <= 3
                && relativeBreadth >= 0.80
                && comparableUpperBreadth,
            CoveragePercent: Math.Round(coverage * 100, 1),
            CommonFiles: common,
            MissingFiles: missing);
    }

    private static string Subject(TaskCommitInfo commit)
        => commit.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;
}

/// <summary>
/// One-time startup migration over delivered and archived cards. The report is
/// also the completion marker, so later starts do not silently widen or repeat
/// the historical classification.
/// </summary>
public sealed class SupersededCommitSweep
{
    public const string ReportFileName = "superseded-commits-v1.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly ILogger<SupersededCommitSweep> _logger;
    private readonly string _reportPath;

    public SupersededCommitSweep(
        TaskScannerService scanner,
        TaskMutationService mutations,
        GitService git,
        ProjectSettingsService settings,
        IConfiguration configuration,
        ILogger<SupersededCommitSweep> logger)
        : this(
            scanner,
            mutations,
            git,
            settings,
            Path.Combine(
                Path.GetFullPath(configuration["TaskRepository"]
                    ?? Path.Combine(AppContext.BaseDirectory, "workspace")),
                ".metadata",
                "migrations",
                ReportFileName),
            logger)
    {
    }

    internal SupersededCommitSweep(
        TaskScannerService scanner,
        TaskMutationService mutations,
        GitService git,
        ProjectSettingsService settings,
        string reportPath,
        ILogger<SupersededCommitSweep> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _git = git;
        _settings = settings;
        _reportPath = reportPath;
        _logger = logger;
    }

    public SupersededCommitSweepReport RunOnce()
    {
        if (File.Exists(_reportPath))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<SupersededCommitSweepReport>(
                    File.ReadAllText(_reportPath),
                    Json);
                return existing is null ? SupersededCommitSweepReport.CompletedEarlier()
                    : existing with { AlreadyCompleted = true };
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The superseded-commit sweep report '{_reportPath}' is unreadable.",
                    ex);
            }
        }

        var rows = new List<SupersededCommitSweepTaskReport>();
        var repairedTasks = 0;
        var repairedCommits = 0;
        var unresolvedTasks = 0;
        foreach (var task in _scanner.ScanAllJobsWithArchive()
                     .Where(task => TaskIntegrationStatusService.DeliveredLanes.Contains(task.State))
                     .Where(task => task.Commits.Count > 1)
                     .Where(task => task.Commits.Any(commit =>
                         !TaskCommitSupersession.IsSuperseded(commit)
                         && SupersededCommitSweepPolicy.IsRunnerFence(commit))))
        {
            try
            {
                var root = _git.ResolveRepoRootForWatchPath(task.WatchPath);
                if (string.IsNullOrWhiteSpace(root))
                {
                    rows.Add(Unresolved(task, "The project repository could not be resolved."));
                    unresolvedTasks++;
                    continue;
                }

                var configuredBranch = _settings.Get(task.ProjectName).IntegrationBranch;
                var integrationRef = _git.ResolveIntegrationReadRef(root, configuredBranch);
                if (!_git.TryGetAncestorShaSet(
                        root,
                        [integrationRef, _git.ResolveOriginReadRef(integrationRef)],
                        out var ancestors))
                {
                    rows.Add(Unresolved(task, $"The integration graph for '{integrationRef}' could not be read."));
                    unresolvedTasks++;
                    continue;
                }

                var enriched = task.Commits.Select(commit => EnrichFiles(task, commit)).ToList();
                var decision = SupersededCommitSweepPolicy.Evaluate(
                    enriched,
                    sha => TaskIntegrationStatusService.AncestorSetContains(ancestors, sha));
                if (decision.Replacements.Count == 0 && decision.Ambiguous.Count == 0) continue;

                var write = _mutations.MarkCommitsSupersededOnFolder(
                    task.FolderPath,
                    decision.Replacements.ToDictionary(
                        replacement => replacement.SupersededSha,
                        replacement => replacement.ReplacementAttempt,
                        StringComparer.OrdinalIgnoreCase));
                if (!write.Succeeded)
                {
                    rows.Add(new SupersededCommitSweepTaskReport(
                        task.Key ?? task.Id,
                        task.State,
                        integrationRef,
                        [],
                        decision.Ambiguous,
                        "The task mutation failed; no commits were marked."));
                    unresolvedTasks++;
                    continue;
                }

                if (write.MarkedCommits > 0)
                {
                    repairedTasks++;
                    repairedCommits += write.MarkedCommits;
                }
                if (decision.Ambiguous.Count > 0) unresolvedTasks++;
                rows.Add(new SupersededCommitSweepTaskReport(
                    task.Key ?? task.Id,
                    task.State,
                    integrationRef,
                    decision.Replacements,
                    decision.Ambiguous,
                    null));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "superseded-commit-sweep failed project={Project} task={Task}",
                    task.ProjectName,
                    task.Key ?? task.Id);
                rows.Add(Unresolved(task, ex.Message));
                unresolvedTasks++;
            }
        }

        var report = new SupersededCommitSweepReport(
            Version: 1,
            CompletedAtUtc: DateTime.UtcNow,
            AlreadyCompleted: false,
            RepairedTasks: repairedTasks,
            RepairedCommits: repairedCommits,
            UnresolvedTasks: unresolvedTasks,
            Tasks: rows);
        WriteReport(report);
        _logger.LogInformation(
            "superseded-commit-sweep completed repairedTasks={Tasks} repairedCommits={Commits} unresolvedTasks={Unresolved} report={Report}",
            repairedTasks,
            repairedCommits,
            unresolvedTasks,
            _reportPath);
        return report;
    }

    private TaskCommitInfo EnrichFiles(TaskInfo task, TaskCommitInfo commit)
    {
        if (commit.Files.Count > 0) return commit;
        var files = _git.GetCommitFiles(task.Id, task.WatchPath, commit.Sha);
        return files.Count == 0 ? commit : commit with
        {
            FilesChanged = files.Count,
            Files = files.Select(file => file.Path).ToList(),
        };
    }

    private static SupersededCommitSweepTaskReport Unresolved(TaskInfo task, string error)
        => new(task.Key ?? task.Id, task.State, null, [], [], error);

    private void WriteReport(SupersededCommitSweepReport report)
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

public sealed record SupersededCommitSweepTaskDecision(
    IReadOnlyList<SupersededCommitReplacement> Replacements,
    IReadOnlyList<SupersededCommitAmbiguity> Ambiguous);

public sealed record SupersededCommitReplacement(
    string SupersededSha,
    string ReplacementSha,
    string ReplacementAttempt,
    double CoveragePercent,
    IReadOnlyList<string> CoveredFiles,
    IReadOnlyList<string> MissingFiles);

internal sealed record BreadthComparison(
    bool IsMatch,
    double CoveragePercent,
    IReadOnlyList<string> CommonFiles,
    IReadOnlyList<string> MissingFiles);

public sealed record SupersededCommitAmbiguity(
    string FenceSha,
    string Reason,
    IReadOnlyList<string> LaterIntegratedShas);

public sealed record SupersededCommitSweepTaskReport(
    string TaskKey,
    string Lane,
    string? IntegrationRef,
    IReadOnlyList<SupersededCommitReplacement> Replacements,
    IReadOnlyList<SupersededCommitAmbiguity> Ambiguous,
    string? Error);

public sealed record SupersededCommitSweepReport(
    int Version,
    DateTime CompletedAtUtc,
    bool AlreadyCompleted,
    int RepairedTasks,
    int RepairedCommits,
    int UnresolvedTasks,
    IReadOnlyList<SupersededCommitSweepTaskReport> Tasks)
{
    public static SupersededCommitSweepReport CompletedEarlier()
        => new(1, default, true, 0, 0, 0, []);
}
