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
    string? Detail)
{
    public CompletionContentFacts? ContentReview { get; init; }

    /// <summary>
    /// State of the published Git facts the answer joined (Git facts S3):
    /// <c>ready</c>, <c>restored</c>, <c>stale</c>, <c>warming</c> or
    /// <c>unavailable</c>. Containment reads <c>unknown</c> unless a verdict was published.
    /// </summary>
    public string FactsState { get; init; } = "ready";

    /// <summary>The generation of the card facts the answer joined; 0 while warming.</summary>
    public long FactsGeneration { get; init; }
}

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
    private readonly TaskListGitProjectionCache? _projection;
    private readonly GitCardFactReader? _cardFacts;
    private readonly TimeProvider _time;
    private readonly ILogger<DeliveryClaimSweep> _logger;

    public DeliveryClaimSweep(
        TaskScannerService scanner,
        TaskIntegrationStatusService integrationStatus,
        TaskMutationService mutations,
        ProjectSettingsService settings,
        ProjectRegistry projects,
        ILogger<DeliveryClaimSweep> logger,
        TaskListGitProjectionCache? projection = null,
        GitCardFactReader? cardFacts = null,
        TimeProvider? timeProvider = null)
    {
        _scanner = scanner;
        _integrationStatus = integrationStatus;
        _mutations = mutations;
        _settings = settings;
        _projects = projects;
        _projection = projection;
        _cardFacts = cardFacts;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The deployment answer for one card, for the surface that shows the
    /// state where the operator already looks. Containment decides; the merge
    /// that carried the delivery is read from the curated integration history
    /// when it can be named, and stays null rather than being guessed.
    ///
    /// <para>
    /// Git facts S3 (AGT-3323): a read path. Containment is the card's
    /// integration verdict the Git fact producer published with its
    /// generation, and the carrying merge comes from the curated merge index
    /// the producer keeps per integration ref. Neither a live lookup nor a
    /// <c>git log</c> runs here. A card whose verdict is not published reads
    /// <c>unknown</c>, never a false "not integrated".
    /// </para>
    /// </summary>
    public TaskDeliveryClaimAnswer Describe(TaskInfo card)
    {
        var snapshot = _projection?.ReadTask(card);
        var status = snapshot?.Data?.Integration;
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
        var published = _cardFacts?.Read(card.WatchPath).Facts;
        var merge = integrated ? CarryingMerge(card, branch, published) : null;

        return new TaskDeliveryClaimAnswer(
            card.TaskKey,
            card.Id,
            card.State,
            status?.DeliveryRef,
            branch,
            status?.ReleaseBranch ?? BoardMergeStatusService.ReleaseBranch,
            status?.ReachUnavailable == true
                ? CompletionContractPolicy.ContainmentUnknown
                : status?.Status ?? CompletionContractPolicy.ContainmentUnknown,
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
            status?.Detail)
        {
            FactsState = snapshot?.State ?? GitFactsResults.WarmingState,
            FactsGeneration = published?.Generation ?? snapshot?.Generation ?? 0,
        };
    }

    /// <summary>
    /// The curated <c>merge(KEY): ...</c> commit on the integration line that
    /// carried this card, including the operator card-scoped merges performed
    /// outside the pipeline, joined by task key from the published merge
    /// index. Null when none can be named; a ref the index does not cover yet
    /// is queued for the producer.
    /// </summary>
    private GitIntegrationMergeCommit? CarryingMerge(TaskInfo card, string branch, GitPublishedFacts? published)
    {
        if (published is null || string.IsNullOrWhiteSpace(card.Key)) return null;
        if (!published.Merges.TryGetValue(branch, out var merges))
        {
            _cardFacts?.Request(card.WatchPath, [new GitMergesWork(branch)]);
            return null;
        }
        return merges
            .Where(merge => string.Equals(merge.TaskKey, card.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(merge => merge.CommittedAtUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// Sweeps one project, or every project when <paramref name="projectIdOrName"/>
    /// is null or blank. Returns null, and reads or repairs nothing, when a
    /// named project cannot be resolved: an unknown scope must never widen to
    /// every project.
    /// </summary>
    public DeliveryClaimSweepReport? Run(string? projectIdOrName, bool repair)
    {
        var watchPath = ResolveWatchPath(projectIdOrName);
        if (!string.IsNullOrWhiteSpace(projectIdOrName) && watchPath is null)
        {
            _logger.LogWarning(
                "delivery-claim-sweep-unknown-project project={Project} repair={Repair}",
                projectIdOrName,
                repair);
            return null;
        }

        var cards = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(task => SweptLanes.Contains(task.State))
            .Where(task => watchPath is null
                || string.Equals(task.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(task => task.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Read mode joins the verdicts the Git fact producer published (Git
        // facts S3); only a repair, a POST, computes containment live before it
        // writes anything.
        var statuses = repair || _projection is null
            ? _integrationStatus.BuildLookup(cards)
            : PublishedStatuses(cards);
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

    private Dictionary<string, TaskIntegrationStatus> PublishedStatuses(IEnumerable<TaskInfo> cards)
    {
        var statuses = new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal);
        foreach (var card in cards)
            if (_projection!.ReadTask(card).Data?.Integration is { } status) statuses[card.TaskKey] = status;
        return statuses;
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
                CarriesFiles(commit)))
            .ToList();

        return new DeliveryClaimCardFacts(
            IntegrationRequired: AcceptanceIntegrationPolicy.IsIntegrationRequired(card),
            Commits: commits,
            ContainmentStatus: status?.ReachUnavailable == true
                ? CompletionContractPolicy.ContainmentUnknown
                : status?.Status ?? CompletionContractPolicy.ContainmentUnknown,
            HasIntegrationRecord: card.IntegrationRecords.Count > 0,
            HasNamedDeliverable: NamedDeliverableReader.Exists(card));
    }

    /// <summary>
    /// A commit that changed at least one file. Zero-file lifecycle markers
    /// are not deliveries, so their containment never proves or repairs one.
    /// </summary>
    internal static bool CarriesFiles(TaskCommitInfo commit)
        => commit.FilesChanged > 0 || commit.Files.Count > 0;

    private IReadOnlyList<string> Repair(
        TaskInfo card,
        TaskIntegrationStatus? status,
        DeliveryClaimRepairPlan plan)
    {
        var applied = new List<string>();
        var deliveries = (card.Commits ?? [])
            .Where(CarriesFiles)
            .Select(commit => commit.Sha)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var containedDeliveries = (status?.Repositories ?? [])
            .SelectMany(repository => repository.Commits)
            .Where(commit => commit.OnIntegrationBranch && deliveries.Contains(commit.Sha))
            .Select(commit => commit.Sha)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (plan.ClearPendingSupersession)
        {
            var contained = containedDeliveries.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cleared = _mutations.ResolvePendingSupersessionOnFolder(
                card.FolderPath,
                commit => CarriesFiles(commit) && contained.Contains(commit.Sha));
            if (cleared.Succeeded && cleared.MarkedCommits > 0)
                applied.Add(DeliveryClaimFindings.StalePendingSupersession);
        }

        if (plan.AppendIntegrationRecord)
        {
            var branch = status?.IntegrationBranch
                ?? TaskIntegrationBranch.Name(_settings.Get(card.ProjectName).IntegrationBranch);
            var record = new TaskIntegrationRecord
            {
                Id = RepairRecordIdPrefix + card.TaskKey.ToLowerInvariant(),
                Version = 1,
                Classification = IntegrationRecordClasses.IntegratedVerified,
                RecordedAtUtc = _time.GetUtcNow().UtcDateTime,
                IntegrationBranch = branch,
                CommitShas = containedDeliveries,
                Evidence = $"Reconciled by the AGT-2817 delivery-claim sweep: "
                    + $"{containedDeliveries.Count} attributed commit(s) are contained in '{branch}'. "
                    + "The record was missing; containment, not the record, decided this.",
            };
            var write = _mutations.AppendIntegrationRecordOnFolder(card.FolderPath, record);
            if (write.Succeeded && write.Appended)
                applied.Add(DeliveryClaimFindings.MissingIntegrationRecord);
        }

        if (applied.Count > 0)
        {
            _scanner.InvalidateCache();
            _scanner.PublishCoreFromKnownFolder(card.FolderPath);
        }
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
