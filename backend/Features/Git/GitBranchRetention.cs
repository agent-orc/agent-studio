namespace AgentStudio.Git;

public enum BranchRetentionDecision
{
    Delete,
    UnsupportedNamespace,
    CheckedOut,
    MissingCommitTime,
    TooYoung,
    DevelopUnavailable,
    MainUnavailable,
    NotMergedIntoDevelop,
    NotMergedIntoMain,
    ChangedBeforeDelete,
    DeleteFailed,
    ResultRefNotInMain,
    ResultRefPending,
    SalvageRefTaskNotTerminal,
    SalvageRefTooYoung,
    QuarantineRefTooYoung,
    QuarantineRefReferenced,
}

public enum BranchNamespace
{
    Unknown,
    Task,
    Runner,
    Delivery,
    ResultsRef,
    SalvageRef,
    QuarantineRef,
    Protected,
}

public sealed record BranchRetentionFacts(
    string Branch,
    BranchNamespace Namespace,
    DateTimeOffset? TipCommittedAtUtc,
    bool CheckedOut,
    bool DevelopAvailable,
    bool MainAvailable,
    bool MergedIntoDevelop,
    bool MergedIntoMain,
    bool MergedIntoIntegrationBranch = false,
    string? IntegrationBranch = null,
    bool IsTaskIntegrated = false,
    bool IsTaskTerminal = false);

/// <summary>
/// Pure retention decision for managed task-delivery and build-proof refs across all namespaces.
/// Classifies refs by namespace and applies namespace-specific retention rules. Missing facts always retain.
/// </summary>
public static class BranchRetentionPolicy
{
    private const int ResultsRefRetentionDays = 0;
    private const int SalvageRefRetentionDays = 14;
    private const int QuarantineRefRetentionDays = 30;

    public static BranchRetentionDecision Evaluate(
        BranchRetentionFacts facts,
        DateTimeOffset now,
        TimeSpan minimumAge)
    {
        var ns = ClassifyNamespace(facts.Branch);

        return ns switch
        {
            BranchNamespace.Task or BranchNamespace.Runner or BranchNamespace.Delivery
                => EvaluateTaskDeliveryBranch(facts, now, minimumAge),
            BranchNamespace.ResultsRef
                => EvaluateResultsRef(facts, now),
            BranchNamespace.SalvageRef
                => EvaluateSalvageRef(facts, now),
            BranchNamespace.QuarantineRef
                => EvaluateQuarantineRef(facts, now),
            BranchNamespace.Protected or BranchNamespace.Unknown
                => BranchRetentionDecision.UnsupportedNamespace,
            _ => BranchRetentionDecision.UnsupportedNamespace
        };
    }

    private static BranchRetentionDecision EvaluateTaskDeliveryBranch(
        BranchRetentionFacts facts,
        DateTimeOffset now,
        TimeSpan minimumAge)
    {
        if (facts.CheckedOut)
            return BranchRetentionDecision.CheckedOut;
        if (facts.TipCommittedAtUtc is null)
            return BranchRetentionDecision.MissingCommitTime;
        if (facts.TipCommittedAtUtc.Value > now - minimumAge)
            return BranchRetentionDecision.TooYoung;
        if (!facts.DevelopAvailable)
            return BranchRetentionDecision.DevelopUnavailable;
        if (!facts.MainAvailable)
            return BranchRetentionDecision.MainUnavailable;
        if (!facts.MergedIntoDevelop)
            return BranchRetentionDecision.NotMergedIntoDevelop;
        if (!facts.MergedIntoMain)
            return BranchRetentionDecision.NotMergedIntoMain;
        return BranchRetentionDecision.Delete;
    }

    private static BranchRetentionDecision EvaluateResultsRef(
        BranchRetentionFacts facts,
        DateTimeOffset now)
    {
        if (facts.CheckedOut)
            return BranchRetentionDecision.CheckedOut;
        if (facts.TipCommittedAtUtc is null)
            return BranchRetentionDecision.MissingCommitTime;

        if (!facts.MainAvailable)
            return BranchRetentionDecision.MainUnavailable;

        if (!facts.MergedIntoMain)
            return BranchRetentionDecision.ResultRefNotInMain;

        return BranchRetentionDecision.Delete;
    }

    private static BranchRetentionDecision EvaluateSalvageRef(
        BranchRetentionFacts facts,
        DateTimeOffset now)
    {
        if (facts.CheckedOut)
            return BranchRetentionDecision.CheckedOut;
        if (facts.TipCommittedAtUtc is null)
            return BranchRetentionDecision.MissingCommitTime;

        var age = now - facts.TipCommittedAtUtc.Value;
        var ageDays = (int)age.TotalDays;

        if (!facts.MainAvailable)
            return BranchRetentionDecision.MainUnavailable;

        if (facts.MergedIntoMain)
            return BranchRetentionDecision.Delete;

        if (ageDays >= SalvageRefRetentionDays)
            return BranchRetentionDecision.Delete;

        if (!facts.IsTaskTerminal)
            return BranchRetentionDecision.SalvageRefTaskNotTerminal;

        return BranchRetentionDecision.SalvageRefTooYoung;
    }

    private static BranchRetentionDecision EvaluateQuarantineRef(
        BranchRetentionFacts facts,
        DateTimeOffset now)
    {
        if (facts.CheckedOut)
            return BranchRetentionDecision.CheckedOut;
        if (facts.TipCommittedAtUtc is null)
            return BranchRetentionDecision.MissingCommitTime;

        var age = now - facts.TipCommittedAtUtc.Value;
        if (age < TimeSpan.FromDays(QuarantineRefRetentionDays))
            return BranchRetentionDecision.QuarantineRefTooYoung;

        return BranchRetentionDecision.Delete;
    }

    public static BranchNamespace ClassifyNamespace(string branch)
    {
        if (branch.StartsWith("task/", StringComparison.Ordinal))
            return BranchNamespace.Task;
        if (branch.StartsWith("runner/", StringComparison.Ordinal))
            return BranchNamespace.Runner;
        if (branch.StartsWith("delivery/", StringComparison.Ordinal))
            return BranchNamespace.Delivery;
        if (branch.StartsWith("agent-studio/results/", StringComparison.Ordinal))
            return BranchNamespace.ResultsRef;
        if (branch.StartsWith("agent-studio/salvage/", StringComparison.Ordinal))
            return BranchNamespace.SalvageRef;
        if (branch.StartsWith("agent-studio/quarantine/", StringComparison.Ordinal))
            return BranchNamespace.QuarantineRef;
        if (IsProtectedRef(branch))
            return BranchNamespace.Protected;
        return BranchNamespace.Unknown;
    }

    private static bool IsProtectedRef(string branch)
        => branch == "main" || branch == "develop" || branch.StartsWith("release/", StringComparison.Ordinal)
           || branch.StartsWith("v", StringComparison.Ordinal);

    public static bool IsManagedBranch(string branch)
    {
        var ns = ClassifyNamespace(branch);
        return ns is BranchNamespace.Task or BranchNamespace.Runner or BranchNamespace.Delivery
            or BranchNamespace.ResultsRef or BranchNamespace.SalvageRef or BranchNamespace.QuarantineRef;
    }

    public static string ReasonFor(BranchRetentionDecision decision) => decision switch
    {
        BranchRetentionDecision.Delete => "Eligible for deletion; deletion policy met.",
        BranchRetentionDecision.UnsupportedNamespace => "Branch is outside managed namespaces.",
        BranchRetentionDecision.CheckedOut => "Branch is checked out in a live worktree.",
        BranchRetentionDecision.MissingCommitTime => "Tip commit time is unavailable.",
        BranchRetentionDecision.TooYoung => "Tip commit is within the retention window.",
        BranchRetentionDecision.DevelopUnavailable => "Protected develop ref is unavailable.",
        BranchRetentionDecision.MainUnavailable => "Protected main ref is unavailable.",
        BranchRetentionDecision.NotMergedIntoDevelop => "Tip is not contained in integration branch.",
        BranchRetentionDecision.NotMergedIntoMain => "Tip is not contained in main.",
        BranchRetentionDecision.ChangedBeforeDelete => "Branch changed before deletion attempt.",
        BranchRetentionDecision.DeleteFailed => "Deletion failed; branch kept.",
        BranchRetentionDecision.ResultRefNotInMain => "Result ref tip is not contained in main.",
        BranchRetentionDecision.ResultRefPending => "Result ref is pending integration.",
        BranchRetentionDecision.SalvageRefTaskNotTerminal => "Task is not terminal; ref retained for recovery.",
        BranchRetentionDecision.SalvageRefTooYoung => "Salvage ref is within 14-day retention window.",
        BranchRetentionDecision.QuarantineRefTooYoung => "Quarantine ref is within 30-day retention window.",
        BranchRetentionDecision.QuarantineRefReferenced => "Quarantine ref is referenced by an open escalation or human-review card.",
        _ => "Unknown retention decision.",
    };
}

public sealed record BranchRetentionAction(
    string Scope,
    string Branch,
    string TipSha,
    DateTimeOffset? TipCommittedAtUtc,
    BranchRetentionDecision Decision,
    bool Deleted,
    string Reason,
    BranchNamespace? Namespace = null,
    string? TaskKey = null);

public sealed record BranchRetentionProjectReport(
    string Project,
    string? RepositoryPath,
    string? DevelopRef,
    string? MainRef,
    int StaleWorktreesPruned,
    IReadOnlyList<BranchRetentionAction> Actions,
    string? Error)
{
    public int DeletedCount => Actions.Count(action => action.Deleted);
    public int KeptCount => Actions.Count - DeletedCount;
}

public sealed record BranchRetentionRunReport(
    DateTimeOffset StartedAtUtc,
    int RetentionDays,
    IReadOnlyList<BranchRetentionProjectReport> Projects)
{
    public int DeletedCount => Projects.Sum(project => project.DeletedCount);
    public int KeptCount => Projects.Sum(project => project.KeptCount);
    public int StaleWorktreesPruned => Projects.Sum(project => project.StaleWorktreesPruned);
}

/// <summary>
/// Coordinates one bounded branch-retention pass. It refreshes origin first,
/// prunes missing worktree registrations, classifies local and remote
/// <c>task/*</c> and <c>runner/*</c> refs, then rechecks the immutable candidate
/// tip against both <c>develop</c> and <c>main</c> before deletion.
/// </summary>
public sealed class GitBranchRetentionService
{
    public const int DefaultRetentionDays = 7;

    private const string OriginPrefix = "origin/";
    private static readonly string[] LocalPatterns =
    [
        "refs/heads/task",
        "refs/heads/runner",
        "refs/heads/delivery",
        "refs/heads/agent-studio/results",
        "refs/heads/agent-studio/salvage",
        "refs/heads/agent-studio/quarantine"
    ];
    private static readonly string[] RemotePatterns =
    [
        "refs/remotes/origin/task",
        "refs/remotes/origin/runner",
        "refs/remotes/origin/delivery",
        "refs/remotes/origin/agent-studio/results",
        "refs/remotes/origin/agent-studio/salvage",
        "refs/remotes/origin/agent-studio/quarantine"
    ];

    private readonly GitService _git;
    private readonly AgentStudio.Registry.ProjectRegistry _projects;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GitBranchRetentionService> _logger;
    private readonly TimeProvider _time;

    public GitBranchRetentionService(
        GitService git,
        AgentStudio.Registry.ProjectRegistry projects,
        IConfiguration configuration,
        ILogger<GitBranchRetentionService> logger,
        TimeProvider? time = null)
    {
        _git = git;
        _projects = projects;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public BranchRetentionRunReport RunOnce(CancellationToken cancellationToken = default)
        => RunOnce(dryRun: false, cancellationToken);

    public BranchRetentionRunReport RunOnce(bool dryRun, CancellationToken cancellationToken = default)
    {
        var startedAt = _time.GetUtcNow();
        var retentionDays = ResolveRetentionDays();
        var reports = new List<BranchRetentionProjectReport>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in _projects.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = _git.ResolveProjectRepoRoot(project.Id);
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string normalizedRoot;
            try { normalizedRoot = Path.GetFullPath(root); }
            catch { normalizedRoot = root; }
            if (!seenRoots.Add(normalizedRoot))
                continue;

            try
            {
                var projectReport = RunRepository(
                    project.DisplayName,
                    normalizedRoot,
                    startedAt,
                    retentionDays,
                    dryRun,
                    cancellationToken);
                reports.Add(projectReport);
                _logger.LogInformation(
                    "git-branch-retention-project project={Project} repository={Repository} deleted={Deleted} kept={Kept} staleWorktreesPruned={Pruned} error={Error}",
                    project.DisplayName, normalizedRoot, projectReport.DeletedCount,
                    projectReport.KeptCount, projectReport.StaleWorktreesPruned,
                    projectReport.Error ?? "none");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Git branch retention failed for project {Project} at {Repository}",
                    project.DisplayName, normalizedRoot);
                reports.Add(Failed(project.DisplayName, normalizedRoot, ex.Message));
            }
        }

        var report = new BranchRetentionRunReport(startedAt, retentionDays, reports);
        _logger.LogInformation(
            "git-branch-retention-complete projects={Projects} deleted={Deleted} kept={Kept} staleWorktreesPruned={Pruned} retentionDays={RetentionDays}",
            reports.Count, report.DeletedCount, report.KeptCount,
            report.StaleWorktreesPruned, retentionDays);
        return report;
    }

    public BranchRetentionProjectReport RunRepository(
        string project,
        string repositoryPath,
        DateTimeOffset now,
        int retentionDays,
        CancellationToken cancellationToken = default)
        => RunRepository(project, repositoryPath, now, retentionDays, dryRun: false, cancellationToken);

    public BranchRetentionProjectReport RunRepository(
        string project,
        string repositoryPath,
        DateTimeOffset now,
        int retentionDays,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            return Failed(project, repositoryPath, "Repository path does not exist.");

        var staleBefore = _git.ListWorktrees(repositoryPath)
            .Count(worktree => !worktree.IsPrimary && !Directory.Exists(worktree.Path));
        if (!dryRun)
            _git.WorktreePrune(repositoryPath);
        var staleAfter = dryRun ? staleBefore : _git.ListWorktrees(repositoryPath)
            .Count(worktree => !worktree.IsPrimary && !Directory.Exists(worktree.Path));
        var pruned = dryRun ? 0 : Math.Max(0, staleBefore - staleAfter);

        var fetch = _git.Fetch(repositoryPath, cancellationToken: cancellationToken);
        if (!string.IsNullOrWhiteSpace(fetch.Error))
        {
            return new BranchRetentionProjectReport(
                project, repositoryPath, null, null, pruned, [],
                $"Origin refresh failed; retention skipped: {fetch.Error}");
        }

        var develop = ResolveProtectedRef(repositoryPath, "develop");
        var main = ResolveProtectedRef(repositoryPath, "main");
        var worktrees = _git.ListWorktrees(repositoryPath);
        var checkedOut = worktrees
            .Where(worktree => Directory.Exists(worktree.Path) && !string.IsNullOrWhiteSpace(worktree.Branch))
            .Select(worktree => worktree.Branch!)
            .ToHashSet(StringComparer.Ordinal);

        var candidates = RemotePatterns
            .SelectMany(pattern => _git.ListRefs(repositoryPath, pattern))
            .Select(reference => ToCandidate(reference, remote: true))
            .Concat(LocalPatterns
                .SelectMany(pattern => _git.ListRefs(repositoryPath, pattern))
                .Select(reference => ToCandidate(reference, remote: false)))
            .OrderBy(candidate => candidate.Branch, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Remote ? 0 : 1)
            .ToList();

        var actions = new List<BranchRetentionAction>(candidates.Count);
        var minimumAge = TimeSpan.FromDays(Math.Clamp(retentionDays, 1, 3650));
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var facts = FactsFor(repositoryPath, candidate, checkedOut, develop, main);
            var decision = BranchRetentionPolicy.Evaluate(facts, now, minimumAge);
            if (decision != BranchRetentionDecision.Delete)
            {
                actions.Add(ToKeptAction(candidate, decision));
                continue;
            }

            actions.Add(DeleteAfterRecheck(
                repositoryPath, candidate, now, minimumAge, dryRun, cancellationToken));
        }

        return new BranchRetentionProjectReport(
            project,
            repositoryPath,
            develop?.ShortName,
            main?.ShortName,
            pruned,
            actions,
            null);
    }

    private BranchRetentionAction DeleteAfterRecheck(
        string root,
        RetentionCandidate candidate,
        DateTimeOffset now,
        TimeSpan minimumAge,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var current = _git.ListRefs(root, candidate.Reference.FullName)
            .SingleOrDefault(reference => string.Equals(
                reference.FullName, candidate.Reference.FullName, StringComparison.Ordinal));
        if (current is null)
            return Changed(candidate, "Candidate disappeared before deletion.");
        if (!string.Equals(current.Sha, candidate.Reference.Sha, StringComparison.OrdinalIgnoreCase))
            return Changed(candidate, "Branch tip changed after retention classification; kept.");

        var develop = ResolveProtectedRef(root, "develop");
        var main = ResolveProtectedRef(root, "main");
        var checkedOut = _git.ListWorktrees(root)
            .Any(worktree => Directory.Exists(worktree.Path)
                && string.Equals(worktree.Branch, candidate.Branch, StringComparison.Ordinal));
        var facts = FactsFor(root, candidate with { Reference = current },
            checkedOut ? new HashSet<string>(StringComparer.Ordinal) { candidate.Branch } : [],
            develop,
            main);
        var decision = BranchRetentionPolicy.Evaluate(facts, now, minimumAge);
        if (decision != BranchRetentionDecision.Delete)
            return ToKeptAction(candidate with { Reference = current }, decision);

        if (dryRun)
        {
            return new BranchRetentionAction(
                candidate.Remote ? "remote" : "local",
                candidate.Branch,
                current.Sha,
                current.CommittedAtUtc,
                BranchRetentionDecision.Delete,
                false,
                "[DRY RUN] Would be deleted after age and develop/main ancestry recheck.");
        }

        var result = candidate.Remote
            ? _git.DeleteRemoteBranchAtTip(
                root, candidate.Branch, current.Sha, cancellationToken: cancellationToken)
            : _git.DeleteBranchAtTip(root, candidate.Branch, current.Sha);
        return new BranchRetentionAction(
            candidate.Remote ? "remote" : "local",
            candidate.Branch,
            current.Sha,
            current.CommittedAtUtc,
            result.Success ? BranchRetentionDecision.Delete : BranchRetentionDecision.DeleteFailed,
            result.Success,
            result.Success
                ? "Deleted after age and develop/main ancestry recheck."
                : result.Error ?? "Deletion failed; branch kept.");
    }

    private BranchRetentionFacts FactsFor(
        string root,
        RetentionCandidate candidate,
        IReadOnlySet<string> checkedOut,
        GitRefLine? develop,
        GitRefLine? main)
        => new(
            candidate.Branch,
            BranchRetentionPolicy.ClassifyNamespace(candidate.Branch),
            candidate.Reference.CommittedAtUtc,
            checkedOut.Contains(candidate.Branch),
            develop is not null,
            main is not null,
            develop is not null && _git.IsAncestor(root, candidate.Reference.Sha, develop.Sha),
            main is not null && _git.IsAncestor(root, candidate.Reference.Sha, main.Sha));

    private GitRefLine? ResolveProtectedRef(string root, string branch)
    {
        var remote = _git.ListRefs(root, $"refs/remotes/origin/{branch}")
            .SingleOrDefault(reference => string.Equals(
                reference.FullName, $"refs/remotes/origin/{branch}", StringComparison.Ordinal));
        if (remote is not null)
            return remote;
        return _git.ListRefs(root, $"refs/heads/{branch}")
            .SingleOrDefault(reference => string.Equals(
                reference.FullName, $"refs/heads/{branch}", StringComparison.Ordinal));
    }

    private static RetentionCandidate ToCandidate(GitRefLine reference, bool remote)
    {
        var branch = remote && reference.ShortName.StartsWith(OriginPrefix, StringComparison.Ordinal)
            ? reference.ShortName[OriginPrefix.Length..]
            : reference.ShortName;
        return new RetentionCandidate(reference, branch, remote);
    }

    private static BranchRetentionAction ToKeptAction(
        RetentionCandidate candidate,
        BranchRetentionDecision decision)
        => new(
            candidate.Remote ? "remote" : "local",
            candidate.Branch,
            candidate.Reference.Sha,
            candidate.Reference.CommittedAtUtc,
            decision,
            false,
            ReasonFor(decision));

    private static BranchRetentionAction Changed(RetentionCandidate candidate, string reason)
        => new(
            candidate.Remote ? "remote" : "local",
            candidate.Branch,
            candidate.Reference.Sha,
            candidate.Reference.CommittedAtUtc,
            BranchRetentionDecision.ChangedBeforeDelete,
            false,
            reason);

    private static string ReasonFor(BranchRetentionDecision decision) => decision switch
    {
        BranchRetentionDecision.UnsupportedNamespace => "Branch is outside task/* and runner/*.",
        BranchRetentionDecision.CheckedOut => "Branch is checked out in a live worktree.",
        BranchRetentionDecision.MissingCommitTime => "Tip commit time is unavailable.",
        BranchRetentionDecision.TooYoung => "Tip commit is inside the retention window.",
        BranchRetentionDecision.DevelopUnavailable => "Protected develop ref is unavailable.",
        BranchRetentionDecision.MainUnavailable => "Protected main ref is unavailable.",
        BranchRetentionDecision.NotMergedIntoDevelop => "Tip is not contained in develop.",
        BranchRetentionDecision.NotMergedIntoMain => "Tip is not contained in main.",
        BranchRetentionDecision.ChangedBeforeDelete => "Branch changed before deletion.",
        BranchRetentionDecision.DeleteFailed => "Deletion failed; branch kept.",
        _ => "Eligible for deletion.",
    };

    private int ResolveRetentionDays()
        => Math.Clamp(
            _configuration.GetValue<int?>("GitRetention:RetentionDays") ?? DefaultRetentionDays,
            1,
            3650);

    private static BranchRetentionProjectReport Failed(
        string project,
        string? repositoryPath,
        string error)
        => new(project, repositoryPath, null, null, 0, [], error);

    private sealed record RetentionCandidate(GitRefLine Reference, string Branch, bool Remote);
}

/// <summary>
/// Recurring host-owned retention loop. It runs once at startup and then at the
/// configured bounded interval; individual project failures are reported and do
/// not stop later maintenance passes.
/// </summary>
public sealed class GitBranchRetentionHostedService : BackgroundService
{
    private readonly GitBranchRetentionService _retention;
    private readonly ArchivedResultRefPruner _archivedResultRefs;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GitBranchRetentionHostedService> _logger;

    public GitBranchRetentionHostedService(
        GitBranchRetentionService retention,
        ArchivedResultRefPruner archivedResultRefs,
        IConfiguration configuration,
        ILogger<GitBranchRetentionHostedService> logger)
    {
        _retention = retention;
        _archivedResultRefs = archivedResultRefs;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!(_configuration.GetValue<bool?>("GitRetention:Enabled") ?? true))
        {
            _logger.LogInformation("Git branch retention disabled via GitRetention:Enabled=false");
            return;
        }

        var intervalHours = Math.Clamp(
            _configuration.GetValue<int?>("GitRetention:IntervalHours") ?? 24,
            1,
            24 * 7);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Run(() => _retention.RunOnce(stoppingToken), stoppingToken);
                await Task.Run(() => _archivedResultRefs.RunOnce(stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Git branch retention sweep failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
