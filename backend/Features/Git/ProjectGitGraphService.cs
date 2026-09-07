using System.Text.RegularExpressions;

namespace AgentStudio.Git;

/// <summary>
/// Composes read-only repository facts with task, lease, integration-presence,
/// and runtime deployment metadata for the Project Hub Git graph.
///
/// Git topology remains owned by <see cref="GitService"/>. develop/main
/// presence remains owned by <see cref="BoardMergeStatusService"/>. This class
/// only joins those cached projections by SHA and branch name.
/// </summary>
public sealed partial class ProjectGitGraphService
{
    private const int CachedHistoryLimit = 400;
    private readonly GitService _git;
    private readonly TaskScannerService _tasks;
    private readonly BoardMergeStatusService _presence;
    private readonly TaskRunnerService _runner;
    private readonly AttemptAuthorityService _attempts;
    private readonly BuildIdentity _identity;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, GitProjectInventory> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EnrichedHistorySnapshot> _historyCache =
        new(StringComparer.OrdinalIgnoreCase);

    public ProjectGitGraphService(
        GitService git,
        TaskScannerService tasks,
        BoardMergeStatusService presence,
        TaskRunnerService runner,
        AttemptAuthorityService attempts,
        IConfiguration configuration)
    {
        _git = git;
        _tasks = tasks;
        _presence = presence;
        _runner = runner;
        _attempts = attempts;
        _identity = BuildIdentity.Load(configuration);
    }

    public GitProjectInventory BuildInventory(string projectName)
    {
        var inventory = _git.GetProjectInventory(projectName);
        if (!inventory.IsRepo || string.IsNullOrWhiteSpace(inventory.RepositoryPath))
            return inventory;

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(projectName, out var cached)
                && cached.ComputedAt == inventory.ComputedAt)
                return cached;
        }

        // The raw snapshot can become visible immediately before its
        // background enrichment callback finishes. Return it as-is in that
        // narrow window so an HTTP poll never performs Git work.
        return inventory;
    }

    internal void RefreshInventory(string projectName)
    {
        var inventory = _git.GetProjectInventory(projectName);
        if (!inventory.IsRepo || string.IsNullOrWhiteSpace(inventory.RepositoryPath)) return;
        var tasks = ProjectTasks(projectName);
        var deployments = DeploymentMarkers();
        var history = _git.GetProjectHistory(projectName, 0, CachedHistoryLimit);
        var enrichedHistory = EnrichHistory(
            projectName,
            inventory.RepositoryPath,
            history,
            tasks,
            deployments);
        var firstHistory = inventory.History is null
            ? null
            : PaginateHistory(
                enrichedHistory.Commits,
                inventory.History.Offset,
                inventory.History.PageSize);
        var enriched = BuildInventorySnapshot(
            inventory with { History = firstHistory }, tasks, deployments);
        lock (_cacheGate)
        {
            _cache[projectName] = enriched;
            _historyCache[projectName] = new EnrichedHistorySnapshot(
                inventory.ComputedAt,
                enrichedHistory.Commits);
        }
    }

    private GitProjectInventory BuildInventorySnapshot(
        GitProjectInventory inventory,
        IReadOnlyList<TaskInfo> tasks,
        IReadOnlyList<GitDeploymentMarker> deployments)
    {
        var inventoryBranches = inventory.Branches
            .Concat(IndexedDeliveryBranches(
                tasks,
                _attempts.GetIndexedDeliveryRefs(
                    tasks.Select(task => task.TaskKey), includeArchived: true)))
            .DistinctBy(branch => branch.Name, StringComparer.Ordinal)
            .ToList();
        var taskByBranch = BuildTaskByBranch(tasks, inventoryBranches);
        var branches = inventoryBranches
            .Select(branch => branch with
            {
                Tasks = taskByBranch.TryGetValue(branch.Name, out var cards) ? cards : [],
            })
            .ToList();
        var worktrees = inventory.Worktrees
            .Select(worktree => worktree with
            {
                Task = worktree.Branch is not null
                    && taskByBranch.TryGetValue(worktree.Branch, out var cards)
                        ? cards.FirstOrDefault()
                        : null,
            })
            .ToList();
        var active = BuildActiveCheckouts(inventory.ProjectName, tasks, branches, worktrees);

        return inventory with
        {
            Branches = branches,
            Worktrees = worktrees,
            ActiveCheckouts = active,
            Deployments = deployments,
        };
    }

    public GitHistoryPage BuildHistory(string projectName, int offset, int pageSize)
    {
        offset = Math.Max(0, offset);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var inventory = _git.GetProjectInventory(projectName);
        lock (_cacheGate)
        {
            if (_historyCache.TryGetValue(projectName, out var cached)
                && cached.ComputedAt == inventory.ComputedAt)
                return PaginateHistory(cached.Commits, offset, pageSize);
        }

        // The raw snapshot can become visible just before background enrichment
        // completes. Serve its in-memory history without presence enrichment so
        // a ref-signature change cannot cause Git work on this request path.
        return _git.GetProjectHistory(projectName, offset, pageSize);
    }

    private static GitHistoryPage PaginateHistory(
        IReadOnlyList<GitGraphCommit> commits,
        int offset,
        int pageSize)
    {
        var page = commits.Skip(offset).Take(pageSize).ToList();
        var hasMore = offset + page.Count < commits.Count;
        return new GitHistoryPage(
            offset,
            pageSize,
            hasMore ? offset + page.Count : null,
            hasMore,
            page);
    }

    private GitHistoryPage EnrichHistory(
        string projectName,
        string repoRoot,
        GitHistoryPage page,
        IReadOnlyList<TaskInfo> tasks,
        IReadOnlyList<GitDeploymentMarker> deployments)
    {
        var presence = _presence.BuildCommitPresence(
            projectName,
            repoRoot,
            page.Commits.Select(commit => commit.Sha));
        var taskByCommit = BuildTaskByCommit(tasks);
        var taskByKey = tasks
            .Select(task => (Key: DisplayKey(task), Task: task))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Task, StringComparer.OrdinalIgnoreCase);

        var commits = page.Commits.Select(commit =>
        {
            var cards = taskByCommit.TryGetValue(commit.Sha, out var direct)
                ? new List<GitTaskBadge>(direct)
                : [];
            var mergeKey = MergeTaskKey(commit.Subject);
            if (mergeKey is not null
                && taskByKey.TryGetValue(mergeKey, out var mergedTask)
                && cards.All(card => !string.Equals(card.TaskKey, mergedTask.TaskKey, StringComparison.Ordinal)))
                cards.Add(Card(mergedTask));

            presence.TryGetValue(commit.Sha, out var commitPresence);
            return commit with
            {
                Tasks = cards,
                Presence = commitPresence is null
                    ? null
                    : new GitCommitPresence(
                        commitPresence.InIntegration,
                        commitPresence.InRelease,
                        commitPresence.IntegrationBranch,
                        commitPresence.ReleaseBranch),
                Deployments = deployments
                    .Where(marker => string.Equals(marker.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase))
                    .ToList(),
            };
        }).ToList();
        return page with { Commits = commits };
    }

    private IReadOnlyList<TaskInfo> ProjectTasks(string projectName)
        => _tasks.ScanAllJobsWithArchive()
            .Where(task => string.Equals(task.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
            .Where(task => !task.Fixture)
            .ToList();

    private IReadOnlyList<GitActiveCheckout> BuildActiveCheckouts(
        string projectName,
        IReadOnlyList<TaskInfo> tasks,
        IReadOnlyList<GitBranchEntry> branches,
        IReadOnlyList<GitWorktreeEntry> worktrees)
    {
        var status = _runner.GetStatus();
        status.Projects.TryGetValue(
            projectName,
            out var projectStatus);
        var active = new List<GitActiveCheckout>();
        foreach (var task in tasks.Where(task => task.State == TaskStates.Progress))
        {
            var facts = _runner.GetRunActivityForJob(task.Id, task.ProjectName);
            var runnerBadge = _runner.ResolveRunnerBadge(task.TaskKey);
            var isActive = facts.SlotActive
                || runnerBadge is not null
                || string.Equals(projectStatus?.ActiveJobId, task.Id, StringComparison.OrdinalIgnoreCase);
            if (!isActive) continue;

            var branch = branches.FirstOrDefault(candidate =>
                candidate.Tasks?.Any(card => card.TaskKey == task.TaskKey) == true);
            var worktree = worktrees.FirstOrDefault(candidate =>
                candidate.Task?.TaskKey == task.TaskKey
                || (branch is not null
                    && string.Equals(candidate.Branch, branch.Name, StringComparison.Ordinal)));
            var location = runnerBadge?.IsRemote == true ? "remote" : "local";
            active.Add(new GitActiveCheckout(
                Card(task),
                branch?.Name ?? task.Provenance?.Branch,
                branch?.TipSha ?? LatestCommitSha(task),
                location,
                runnerBadge?.RunnerName ?? _runner.BackendName,
                location == "local" ? worktree?.Path : null,
                runnerBadge?.AcquiredAt));
        }
        return active;
    }

    private IReadOnlyList<GitDeploymentMarker> DeploymentMarkers()
    {
        if (!ReviewSubjectStore.IsValidResultSha(_identity.Commit)) return [];
        var shortSha = _identity.Commit[..Math.Min(7, _identity.Commit.Length)];
        return
        [
            new GitDeploymentMarker("backend", _identity.Commit, shortSha),
            new GitDeploymentMarker("runner", _identity.Commit, shortSha),
            new GitDeploymentMarker("frontend", _identity.Commit, shortSha),
        ];
    }

    private sealed record EnrichedHistorySnapshot(
        DateTimeOffset? ComputedAt,
        IReadOnlyList<GitGraphCommit> Commits);

    internal static Dictionary<string, List<GitTaskBadge>> BuildTaskByCommit(
        IReadOnlyList<TaskInfo> tasks)
    {
        var result = new Dictionary<string, List<GitTaskBadge>>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            var shas = task.Commits.Select(commit => commit.Sha).ToList();
            if (task.Commit is not null) shas.Add(task.Commit.Sha);
            if (task.Provenance?.Merge?.MergeCommit is { Length: > 0 } merge) shas.Add(merge);
            var subject = ReadReviewSubject(task);
            if (subject is not null) shas.Add(subject.ResultSha);
            foreach (var sha in shas.Where(ReviewSubjectStore.IsValidResultSha)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!result.TryGetValue(sha, out var cards))
                {
                    cards = [];
                    result[sha] = cards;
                }
                if (cards.All(card => card.TaskKey != task.TaskKey)) cards.Add(Card(task));
            }
        }
        return result;
    }

    internal static Dictionary<string, List<GitTaskBadge>> BuildTaskByBranch(
        IReadOnlyList<TaskInfo> tasks,
        IReadOnlyList<GitBranchEntry> branches)
    {
        var result = new Dictionary<string, List<GitTaskBadge>>(StringComparer.Ordinal);
        foreach (var branch in branches)
        {
            foreach (var task in tasks.Where(task => BranchBelongsToTask(branch.Name, task)))
            {
                if (!result.TryGetValue(branch.Name, out var cards))
                {
                    cards = [];
                    result[branch.Name] = cards;
                }
                cards.Add(Card(task));
            }
        }
        return result;
    }

    private static bool BranchBelongsToTask(string branch, TaskInfo task)
    {
        var normalized = NormalizeBranch(branch);
        var known = new[]
        {
            task.Provenance?.Branch,
            WorktreeTaskLifecycle.BranchFor(task.Id),
            ReadReviewSubject(task)?.ResultRef,
        };
        if (known.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizeBranch(value!))
            .Any(value => string.Equals(value, normalized, StringComparison.Ordinal)))
            return true;
        if (!normalized.StartsWith("runner/", StringComparison.Ordinal)) return false;
        var tail = normalized.Split('/').LastOrDefault() ?? "";
        var candidates = new[] { task.Key, task.Id, DisplayKey(task) }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => SafeBranchSegment(value!));
        return candidates.Any(candidate =>
            string.Equals(tail, candidate, StringComparison.OrdinalIgnoreCase)
            || tail.StartsWith(candidate + "-collision-", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeBranch(string branch)
    {
        var value = branch.Trim();
        if (value.StartsWith("refs/heads/", StringComparison.Ordinal))
            return value["refs/heads/".Length..];
        if (value.StartsWith("refs/remotes/origin/", StringComparison.Ordinal))
            return value["refs/remotes/origin/".Length..];
        if (value.StartsWith("origin/", StringComparison.Ordinal))
            return value["origin/".Length..];
        return value;
    }

    private static string SafeBranchSegment(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        return new string(chars).Trim('-', '.');
    }

    private static string DisplayKey(TaskInfo task)
        => string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key;

    private static ReviewSubjectRecord? ReadReviewSubject(TaskInfo task)
        => string.IsNullOrWhiteSpace(task.FolderPath)
            ? null
            : ReviewSubjectStore.Read(task.FolderPath);

    private static IEnumerable<GitBranchEntry> IndexedDeliveryBranches(
        IReadOnlyList<TaskInfo> tasks,
        IReadOnlyList<AttemptIndexedDeliveryRef> attemptRefs)
    {
        foreach (var indexed in attemptRefs)
        {
            var value = NormalizeBranch(indexed.Ref);
            if (!IsDeliveryIndexRef(value)) continue;
            yield return IndexedBranch(value, indexed.ResultSha, indexed.RecordedAt);
        }

        foreach (var task in tasks)
        {
            var subject = ReadReviewSubject(task);
            if (subject is null || !ReviewSubjectStore.IsValidResultSha(subject.ResultSha)) continue;
            foreach (var value in new[] { subject.ImmutableResultRef, subject.ResultRef }
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Select(value => NormalizeBranch(value!))
                         .Where(IsDeliveryIndexRef)
                         .Distinct(StringComparer.Ordinal))
            {
                yield return IndexedBranch(
                    value, subject.ResultSha, subject.CompletedAtUtc.UtcDateTime);
            }
        }
    }

    private static GitBranchEntry IndexedBranch(string value, string sha, DateTime recordedAt)
        => new(
            value,
            "other",
            sha,
            sha[..Math.Min(7, sha.Length)],
            false,
            null,
            0,
            0,
            "Indexed immutable delivery",
            recordedAt,
            null,
            IsLocal: false,
            HasRemote: true,
            RemoteTipSha: sha);

    private static bool IsDeliveryIndexRef(string value)
        => value.StartsWith("agent-studio/results/", StringComparison.Ordinal)
            || value.StartsWith("agent-studio/quarantine/", StringComparison.Ordinal)
            || value.StartsWith("agent-studio/salvage/", StringComparison.Ordinal);

    private static string? LatestCommitSha(TaskInfo task)
        => task.Commits.LastOrDefault()?.Sha ?? task.Commit?.Sha;

    private static GitTaskBadge Card(TaskInfo task)
        => new(task.TaskKey, DisplayKey(task), task.Title, task.State);

    private static string? MergeTaskKey(string subject)
    {
        var match = CuratedMergeSubject().Match(subject);
        return match.Success ? match.Groups["key"].Value.Trim() : null;
    }

    [GeneratedRegex(@"^merge(?:-recut)?\((?<key>[^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CuratedMergeSubject();
}
