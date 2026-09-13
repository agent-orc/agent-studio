namespace AgentStudio.Git;

public enum BranchNamespace
{
    Task,
    Runner,
    Delivery,
    Results,
    Salvage,
    Quarantine,
    Unknown,
}

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
    NotMergedIntoMainForResults,
    SalvageRefTooYoung,
    QuarantineRefTooYoung,
    QuarantineRefReferenced,
    ChangedBeforeDelete,
    DeleteFailed,
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
    string? TaskKeyForRef = null,
    bool IsIntegrated = false,
    bool IsReferenced = false);

/// <summary>
/// Pure retention decision for managed task-delivery refs. Classifies branches by
/// namespace and applies namespace-specific rules:
/// - task/*, runner/*, delivery/*: delete when integrated and in main
/// - results/*: delete when in main (record proof before deletion)
/// - salvage/*: delete when task terminal and (in main OR >14 days old)
/// - quarantine/*: keep 30 days, delete unless referenced
/// - Never delete: main, develop, release/*, v*, checked-out refs
/// </summary>
public static class BranchRetentionPolicy
{
    public const int SalvageRetentionDays = 14;
    public const int QuarantineRetentionDays = 30;

    public static BranchRetentionDecision Evaluate(
        BranchRetentionFacts facts,
        DateTimeOffset now,
        TimeSpan minimumAge)
    {
        if (facts.Namespace == BranchNamespace.Unknown)
            return BranchRetentionDecision.UnsupportedNamespace;

        if (facts.CheckedOut)
            return BranchRetentionDecision.CheckedOut;

        if (facts.TipCommittedAtUtc is null)
            return BranchRetentionDecision.MissingCommitTime;

        return facts.Namespace switch
        {
            BranchNamespace.Task or BranchNamespace.Runner or BranchNamespace.Delivery
                => EvaluateTaskDeliveryBranch(facts, now, minimumAge),
            BranchNamespace.Results
                => EvaluateResultsBranch(facts, now, minimumAge),
            BranchNamespace.Salvage
                => EvaluateSalvageBranch(facts, now),
            BranchNamespace.Quarantine
                => EvaluateQuarantineBranch(facts, now),
            _ => BranchRetentionDecision.UnsupportedNamespace,
        };
    }

    private static BranchRetentionDecision EvaluateTaskDeliveryBranch(
        BranchRetentionFacts facts,
        DateTimeOffset now,
        TimeSpan minimumAge)
    {
        if (facts.TipCommittedAtUtc is null || facts.TipCommittedAtUtc.Value > now - minimumAge)
            return BranchRetentionDecision.TooYoung;
        if (!facts.DevelopAvailable)
            return BranchRetentionDecision.DevelopUnavailable;
        if (!facts.MainAvailable)
            return BranchRetentionDecision.MainUnavailable;
        if (!facts.MergedIntoDevelop)
            return BranchRetentionDecision.NotMergedIntoDevelop;
        if (!facts.MergedIntoMain)
            return BranchRetentionDecision.NotMergedIntoMain;
        if (!facts.IsIntegrated)
            return BranchRetentionDecision.NotMergedIntoMain;
        return BranchRetentionDecision.Delete;
    }

    private static BranchRetentionDecision EvaluateResultsBranch(
        BranchRetentionFacts facts,
        DateTimeOffset now,
        TimeSpan minimumAge)
    {
        if (facts.TipCommittedAtUtc is null || facts.TipCommittedAtUtc.Value > now - minimumAge)
            return BranchRetentionDecision.TooYoung;
        if (!facts.MainAvailable)
            return BranchRetentionDecision.MainUnavailable;
        if (!facts.MergedIntoMain)
            return BranchRetentionDecision.NotMergedIntoMainForResults;
        return BranchRetentionDecision.Delete;
    }

    private static BranchRetentionDecision EvaluateSalvageBranch(
        BranchRetentionFacts facts,
        DateTimeOffset now)
    {
        if (!facts.MainAvailable)
            return BranchRetentionDecision.MainUnavailable;

        if (facts.TipCommittedAtUtc is null)
            return BranchRetentionDecision.MissingCommitTime;

        var salvageAge = TimeSpan.FromDays(SalvageRetentionDays);
        var isOldEnough = facts.TipCommittedAtUtc.Value <= now - salvageAge;
        var isInMain = facts.MergedIntoMain;

        if (!isOldEnough && !isInMain)
            return BranchRetentionDecision.SalvageRefTooYoung;

        return BranchRetentionDecision.Delete;
    }

    private static BranchRetentionDecision EvaluateQuarantineBranch(
        BranchRetentionFacts facts,
        DateTimeOffset now)
    {
        if (facts.IsReferenced)
            return BranchRetentionDecision.QuarantineRefReferenced;

        if (facts.TipCommittedAtUtc is null)
            return BranchRetentionDecision.MissingCommitTime;

        var quarantineAge = TimeSpan.FromDays(QuarantineRetentionDays);
        if (facts.TipCommittedAtUtc.Value > now - quarantineAge)
            return BranchRetentionDecision.QuarantineRefTooYoung;

        return BranchRetentionDecision.Delete;
    }

    public static BranchNamespace ClassifyBranch(string branch)
    {
        return branch switch
        {
            _ when branch.StartsWith("task/", StringComparison.Ordinal) => BranchNamespace.Task,
            _ when branch.StartsWith("runner/", StringComparison.Ordinal) => BranchNamespace.Runner,
            _ when branch.StartsWith("delivery/", StringComparison.Ordinal) => BranchNamespace.Delivery,
            _ when branch.StartsWith("agent-studio/results/", StringComparison.Ordinal) => BranchNamespace.Results,
            _ when branch.StartsWith("agent-studio/salvage/", StringComparison.Ordinal) => BranchNamespace.Salvage,
            _ when branch.StartsWith("agent-studio/quarantine/", StringComparison.Ordinal) => BranchNamespace.Quarantine,
            _ => BranchNamespace.Unknown,
        };
    }

    public static bool IsManagedBranch(string branch)
        => ClassifyBranch(branch) != BranchNamespace.Unknown;
}

public sealed record BranchRetentionAction(
    string Scope,
    string Branch,
    string TipSha,
    DateTimeOffset? TipCommittedAtUtc,
    BranchRetentionDecision Decision,
    bool Deleted,
    string Reason);

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
/// prunes missing worktree registrations, classifies local and remote refs by
/// namespace (task, runner, delivery, results, salvage, quarantine), then rechecks
/// the immutable candidate tip against protected branches before deletion.
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
        "refs/heads/agent-studio/quarantine",
    ];
    private static readonly string[] RemotePatterns =
    [
        "refs/remotes/origin/task",
        "refs/remotes/origin/runner",
        "refs/remotes/origin/delivery",
        "refs/remotes/origin/agent-studio/results",
        "refs/remotes/origin/agent-studio/salvage",
        "refs/remotes/origin/agent-studio/quarantine",
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
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            return Failed(project, repositoryPath, "Repository path does not exist.");

        var staleBefore = _git.ListWorktrees(repositoryPath)
            .Count(worktree => !worktree.IsPrimary && !Directory.Exists(worktree.Path));
        _git.WorktreePrune(repositoryPath);
        var staleAfter = _git.ListWorktrees(repositoryPath)
            .Count(worktree => !worktree.IsPrimary && !Directory.Exists(worktree.Path));
        var pruned = Math.Max(0, staleBefore - staleAfter);

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
                repositoryPath, candidate, now, minimumAge, cancellationToken));
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
    {
        var ns = BranchRetentionPolicy.ClassifyBranch(candidate.Branch);
        return new(
            candidate.Branch,
            ns,
            candidate.Reference.CommittedAtUtc,
            checkedOut.Contains(candidate.Branch),
            develop is not null,
            main is not null,
            develop is not null && _git.IsAncestor(root, candidate.Reference.Sha, develop.Sha),
            main is not null && _git.IsAncestor(root, candidate.Reference.Sha, main.Sha),
            TaskKeyForRef(candidate.Branch, ns),
            false,
            false);
    }

    private static string? TaskKeyForRef(string branch, BranchNamespace ns)
    {
        return ns switch
        {
            BranchNamespace.Task => ExtractKeyFromTaskBranch(branch),
            BranchNamespace.Runner => ExtractKeyFromRunnerBranch(branch),
            BranchNamespace.Salvage => ExtractKeyFromSalvageBranch(branch),
            BranchNamespace.Quarantine => ExtractKeyFromQuarantineBranch(branch),
            _ => null,
        };
    }

    private static string? ExtractKeyFromTaskBranch(string branch)
    {
        var prefix = "task/";
        return branch.StartsWith(prefix, StringComparison.Ordinal)
            ? branch[prefix.Length..].Split('/')[0]
            : null;
    }

    private static string? ExtractKeyFromRunnerBranch(string branch)
    {
        var parts = branch.Split('/');
        return parts.Length >= 3 ? parts[2] : null;
    }

    private static string? ExtractKeyFromSalvageBranch(string branch)
    {
        var parts = branch.Split('/');
        return parts.Length >= 4 ? parts[3] : null;
    }

    private static string? ExtractKeyFromQuarantineBranch(string branch)
    {
        var parts = branch.Split('/');
        return parts.Length >= 4 ? parts[3] : null;
    }

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
        BranchRetentionDecision.UnsupportedNamespace => "Branch is outside managed namespaces (task, runner, delivery, results, salvage, quarantine).",
        BranchRetentionDecision.CheckedOut => "Branch is checked out in a live worktree.",
        BranchRetentionDecision.MissingCommitTime => "Tip commit time is unavailable.",
        BranchRetentionDecision.TooYoung => "Tip commit is inside the retention window.",
        BranchRetentionDecision.DevelopUnavailable => "Protected develop ref is unavailable.",
        BranchRetentionDecision.MainUnavailable => "Protected main ref is unavailable.",
        BranchRetentionDecision.NotMergedIntoDevelop => "Tip is not contained in develop.",
        BranchRetentionDecision.NotMergedIntoMain => "Tip is not contained in main or task not integrated.",
        BranchRetentionDecision.NotMergedIntoMainForResults => "Results ref tip is not contained in main.",
        BranchRetentionDecision.SalvageRefTooYoung => "Salvage ref is too young and not yet in main.",
        BranchRetentionDecision.QuarantineRefTooYoung => "Quarantine ref is less than 30 days old.",
        BranchRetentionDecision.QuarantineRefReferenced => "Quarantine ref is referenced by an open escalation or review card.",
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
