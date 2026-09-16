using AgentStudio.Git;
using AgentStudio.Registry;

namespace AgentStudio.Tasks;

/// <summary>One swept card: what it claims, what Git says, and what was repaired.</summary>
public sealed record DeliveryClaimSweepRow(
    string Project,
    string TaskKey,
    string JobId,
    string Lane,
    string Class,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Repairs,
    string? DeliveryRef,
    string? IntegrationBranch,
    bool Integrated,
    bool? Released,
    string? CompletionBasis,
    string? Detail);

/// <summary>Containment and release membership of one attributed commit, for the card surface.</summary>
public sealed record TaskDeliveryCommitAnswer(
    string Sha,
    string ShortSha,
    bool OnIntegrationBranch,
    bool OnReleaseBranch,
    string Supersession);

/// <summary>
/// AGT-2817 - the per-card deployment answer: is this delivery integrated,
/// which merge carried it, and is it contained in the released line? Decided by
/// Git containment; the stored record is reported beside it, never instead of
/// it.
/// </summary>
public sealed record TaskDeliveryClaimAnswer(
    string TaskKey,
    string JobId,
    string Lane,
    string? DeliveryRef,
    string IntegrationBranch,
    string ReleaseBranch,
    string ContainmentStatus,
    bool Integrated,
    bool? Released,
    string? IntegratedSha,
    string? MergeCommit,
    string? MergeSubject,
    DateTime? MergedAtUtc,
    bool HasIntegrationRecord,
    string Class,
    IReadOnlyList<string> Findings,
    TaskCompletionClaim? CompletionClaim,
    IReadOnlyList<TaskDeliveryCommitAnswer> Commits,
    string? Detail);

/// <summary>Result of one sweep over a project's delivered and archived cards.</summary>
public sealed record DeliveryClaimSweepReport(
    string Project,
    DateTime RanAtUtc,
    bool Repair,
    int Scanned,
    IReadOnlyDictionary<string, int> Classes,
    IReadOnlyDictionary<string, int> Findings,
    IReadOnlyList<DeliveryClaimSweepRow> Rows);

/// <summary>
/// AGT-2817 - the sweep that answers "is everything in the delivered lane
/// actually deployed" without a script, and the reconciliation pass that
/// repairs the caches which contradict the Git answer.
///
/// <para>
/// Read mode reports every card's class and every contradiction. Repair mode
/// additionally writes the integration record a contained delivery is missing
/// and clears the <c>next-attempt</c> placeholder from commits that have
/// shipped. Both repairs move in the one direction that cannot lose
/// information: they are proposed only where containment is positive. A
/// delivery that is not contained is reported and left exactly as it is,
/// because nothing here can prove what should have happened to it.
/// </para>
/// </summary>
public sealed class DeliveryClaimSweep
{
    internal const string RepairRecordIdPrefix = "agt-2817-containment-";

    private static readonly HashSet<string> SweptLanes = new(StringComparer.Ordinal)
    {
        TaskStates.Completed,
        TaskStates.Archive,
    };

    private readonly TaskScannerService _scanner;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly TaskMutationService _mutations;
    private readonly ProjectSettingsService _settings;
    private readonly ProjectRegistry _projects;
    private readonly GitService? _git;
    private readonly TimeProvider _time;
    private readonly ILogger<DeliveryClaimSweep> _logger;

    public DeliveryClaimSweep(
        TaskScannerService scanner,
        TaskIntegrationStatusService integrationStatus,
        TaskMutationService mutations,
        ProjectSettingsService settings,
        ProjectRegistry projects,
        ILogger<DeliveryClaimSweep> logger,
        GitService? git = null,
        TimeProvider? timeProvider = null)
    {
        _scanner = scanner;
        _integrationStatus = integrationStatus;
        _mutations = mutations;
        _settings = settings;
        _projects = projects;
        _git = git;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The deployment answer for one card, for the surface that shows the
    /// state where the operator already looks. Containment decides; the merge
    /// that carried the delivery is read from the curated integration history
    /// when it can be named, and stays null rather than being guessed.
    /// </summary>
    public TaskDeliveryClaimAnswer Describe(TaskInfo card)
    {
        var status = _integrationStatus.BuildLookup([card]).GetValueOrDefault(card.TaskKey);
        var facts = BuildFacts(card, status);
        var assessment = DeliveryClaimSweepPolicy.Assess(facts);
        var integrated = CompletionContractPolicy.IsContained(status?.Status);
        var branch = status?.IntegrationBranch
            ?? TaskIntegrationBranch.Name(_settings.Get(card.ProjectName).IntegrationBranch);
        // A read endpoint must not throw on a repeated SHA across repository
        // entries; the first membership entry wins.
        var membership = (status?.Repositories ?? [])
            .SelectMany(repository => repository.Commits)
            .GroupBy(commit => commit.Sha, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var merge = integrated ? FindCarryingMerge(card, branch) : null;

        return new TaskDeliveryClaimAnswer(
            card.TaskKey,
            card.Id,
            card.State,
            status?.DeliveryRef,
            branch,
            status?.ReleaseBranch ?? BoardMergeStatusService.ReleaseBranch,
            status?.Status ?? CompletionContractPolicy.ContainmentUnknown,
            integrated,
            status?.Released,
            status?.Sha,
            merge?.Sha,
            merge?.Subject,
            merge?.CommittedAtUtc,
            card.IntegrationRecords.Count > 0,
            assessment.Class,
            assessment.Findings,
            card.CompletionClaim,
            (card.Commits ?? []).Select(commit => new TaskDeliveryCommitAnswer(
                commit.Sha,
                commit.ShortSha,
                membership.TryGetValue(commit.Sha, out var entry) && entry.OnIntegrationBranch,
                membership.TryGetValue(commit.Sha, out var release) && release.OnReleaseBranch,
                TaskCommitSupersession.State(commit))).ToList(),
            status?.Detail);
    }

    /// <summary>
    /// The curated <c>merge(KEY): ...</c> commit on the integration line that
    /// carried this card, including the operator card-scoped merges performed
    /// outside the pipeline. Null when none can be named.
    /// </summary>
    private GitIntegrationMergeCommit? FindCarryingMerge(TaskInfo card, string branch)
    {
        if (_git is null || string.IsNullOrWhiteSpace(card.Key)) return null;
        try
        {
            var root = _git.ResolveRepoRootForWatchPath(card.WatchPath);
            if (string.IsNullOrWhiteSpace(root)) return null;
            return _git.GetIntegrationMergeCommits(root, branch)
                .Where(merge => string.Equals(merge.TaskKey, card.Key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(merge => merge.CommittedAtUtc)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "carrying-merge-lookup-failed job={JobId}", card.Id);
            return null;
        }
    }

    public DeliveryClaimSweepReport Run(string? projectIdOrName, bool repair)
    {
        var watchPath = ResolveWatchPath(projectIdOrName);
        var cards = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(task => SweptLanes.Contains(task.State))
            .Where(task => watchPath is null
                || string.Equals(task.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(task => task.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var statuses = _integrationStatus.BuildLookup(cards);
        var rows = new List<DeliveryClaimSweepRow>(cards.Count);
        foreach (var card in cards)
        {
            statuses.TryGetValue(card.TaskKey, out var status);
            var facts = BuildFacts(card, status);
            var assessment = DeliveryClaimSweepPolicy.Assess(facts);
            var applied = repair && assessment.Repairs.HasWork
                ? Repair(card, status, assessment.Repairs)
                : [];
            rows.Add(new DeliveryClaimSweepRow(
                card.ProjectName,
                card.TaskKey,
                card.Id,
                card.State,
                assessment.Class,
                assessment.Findings,
                applied,
                status?.DeliveryRef,
                status?.IntegrationBranch,
                CompletionContractPolicy.IsContained(status?.Status),
                status?.Released,
                card.CompletionClaim?.Basis,
                status?.Detail));
        }

        var report = new DeliveryClaimSweepReport(
            projectIdOrName ?? "*",
            _time.GetUtcNow().UtcDateTime,
            repair,
            rows.Count,
            Tally(rows.Select(row => row.Class)),
            Tally(rows.SelectMany(row => row.Findings)),
            rows);
        _logger.LogInformation(
            "delivery-claim-sweep project={Project} repair={Repair} scanned={Scanned} divergent={Divergent} repaired={Repaired}",
            report.Project,
            repair,
            report.Scanned,
            rows.Count(row => row.Findings.Count > 0),
            rows.Count(row => row.Repairs.Count > 0));
        return report;
    }

    /// <summary>
    /// Projects one card's Git truth into the pure policy's input. Per-commit
    /// containment comes from the repository membership the status service
    /// already computed, so the sweep adds no per-card git spawn.
    /// </summary>
    internal static DeliveryClaimCardFacts BuildFacts(TaskInfo card, TaskIntegrationStatus? status)
    {
        var contained = (status?.Repositories ?? [])
            .SelectMany(repository => repository.Commits)
            .Where(commit => commit.OnIntegrationBranch)
            .Select(commit => commit.Sha)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commits = (card.Commits ?? [])
            .Select(commit => new DeliveryClaimCommitFact(
                commit.Sha,
                contained.Contains(commit.Sha),
                TaskCommitSupersession.State(commit),
                commit.FilesChanged > 0 || commit.Files.Count > 0))
            .ToList();

        return new DeliveryClaimCardFacts(
            IntegrationRequired: AcceptanceIntegrationPolicy.IsIntegrationRequired(card),
            Commits: commits,
            ContainmentStatus: status?.Status ?? CompletionContractPolicy.ContainmentUnknown,
            HasIntegrationRecord: card.IntegrationRecords.Count > 0,
            HasNamedDeliverable: NamedDeliverableReader.Exists(card));
    }

    private IReadOnlyList<string> Repair(
        TaskInfo card,
        TaskIntegrationStatus? status,
        DeliveryClaimRepairPlan plan)
    {
        var applied = new List<string>();
        if (plan.ClearPendingSupersession)
        {
            var contained = (status?.Repositories ?? [])
                .SelectMany(repository => repository.Commits)
                .Where(commit => commit.OnIntegrationBranch)
                .Select(commit => commit.Sha)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cleared = _mutations.ResolvePendingSupersessionOnFolder(
                card.FolderPath,
                commit => contained.Contains(commit.Sha));
            if (cleared.Succeeded && cleared.MarkedCommits > 0)
                applied.Add(DeliveryClaimFindings.StalePendingSupersession);
        }

        if (plan.AppendIntegrationRecord)
        {
            var branch = status?.IntegrationBranch
                ?? TaskIntegrationBranch.Name(_settings.Get(card.ProjectName).IntegrationBranch);
            var shas = (status?.Repositories ?? [])
                .SelectMany(repository => repository.Commits)
                .Where(commit => commit.OnIntegrationBranch)
                .Select(commit => commit.Sha)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var record = new TaskIntegrationRecord
            {
                Id = RepairRecordIdPrefix + card.TaskKey.ToLowerInvariant(),
                Version = 1,
                Classification = IntegrationRecordClasses.IntegratedVerified,
                RecordedAtUtc = _time.GetUtcNow().UtcDateTime,
                IntegrationBranch = branch,
                CommitShas = shas,
                Evidence = $"Reconciled by the AGT-2817 delivery-claim sweep: "
                    + $"{shas.Count} attributed commit(s) are contained in '{branch}'. "
                    + "The record was missing; containment, not the record, decided this.",
            };
            var write = _mutations.AppendIntegrationRecordOnFolder(card.FolderPath, record);
            if (write.Succeeded && write.Appended)
                applied.Add(DeliveryClaimFindings.MissingIntegrationRecord);
        }

        if (applied.Count > 0)
            _scanner.InvalidateCache();
        return applied;
    }

    private string? ResolveWatchPath(string? projectIdOrName)
    {
        if (string.IsNullOrWhiteSpace(projectIdOrName)) return null;
        var project = _projects.FindByIdOrDisplayName(projectIdOrName)
            ?? _projects.FindByStorageLocation(projectIdOrName);
        if (project is not null) return project.StorageLocation;
        return _scanner.GetWatchPaths()
            .FirstOrDefault(entry =>
                string.Equals(entry.Name, projectIdOrName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Path, projectIdOrName, StringComparison.OrdinalIgnoreCase))
            ?.Path;
    }

    private static IReadOnlyDictionary<string, int> Tally(IEnumerable<string> values)
        => values
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}
