namespace AgentStudio.TestRuns;

public sealed class TestRunService
{
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan ShortFallbackTtl = TimeSpan.FromSeconds(5);

    private readonly TestRunStore _store;
    private readonly ProjectRegistry _projects;
    private readonly TaskScannerService _scanner;
    private readonly GitService _git;
    private readonly GenerationSingleFlightCache<Dictionary<string, TaskTestRunEvidence>> _evidenceCache = new();

    public TestRunService(TestRunStore store, ProjectRegistry projects, TaskScannerService scanner, GitService git)
    {
        _store = store;
        _projects = projects;
        _scanner = scanner;
        _git = git;
    }

    public ProjectRecord? ResolveProject(string handle) =>
        _projects.FindByShortCode(handle) ?? _projects.FindByIdOrDisplayName(handle);

    public IReadOnlyList<TestRunRecord>? List(string projectHandle)
    {
        var project = ResolveProject(projectHandle);
        return project is null ? null : Ordered(_store.List(project.Id));
    }

    public TestRunRecord? Create(string projectHandle, CreateTestRunRequest request)
    {
        var project = ResolveProject(projectHandle);
        if (project is null) return null;
        ValidateCreate(request);
        var now = DateTime.UtcNow;
        var state = NormalizeState(request.State);
        var runs = _store.List(project.Id);
        var run = new TestRunRecord
        {
            Id = $"TR-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..25],
            ProjectId = project.Id,
            Trigger = request.Trigger.Trim(),
            Commit = request.Commit.Trim(),
            Branch = request.Branch.Trim(),
            Scope = new TestRunScope
            {
                Level = request.Scope!.Level.Trim(),
                TestSet = request.Scope.TestSet.Trim(),
            },
            State = state,
            Result = state == TestRunStates.Completed ? NormalizeResult(request.Result) : null,
            DurationSeconds = state == TestRunStates.Planned
                ? null
                : request.DurationSeconds ?? (state == TestRunStates.Completed ? 0 : null),
            Host = Clean(request.Host),
            PlannedOrder = request.PlannedOrder ?? (runs.Count == 0 ? 1 : runs.Max(item => item.PlannedOrder) + 1),
            CreatedAt = now,
            StartedAt = state is TestRunStates.Running or TestRunStates.Completed ? now : null,
            CompletedAt = state == TestRunStates.Completed ? now : null,
        };
        return _store.Add(project.Id, run);
    }

    public TestRunRecord? Update(string projectHandle, string runId, UpdateTestRunRequest request)
    {
        var project = ResolveProject(projectHandle);
        if (project is null) return null;
        var state = NormalizeState(request.State);
        if (!TestRunStates.IsValid(state)) throw new TestRunValidationException("state must be planned, running, or completed.");
        if (state == TestRunStates.Completed && !TestRunResults.IsTerminal(NormalizeResult(request.Result)))
            throw new TestRunValidationException("A completed test run requires result passed, failed, or canceled.");
        if (request.DurationSeconds is < 0) throw new TestRunValidationException("durationSeconds cannot be negative.");

        return _store.Update(project.Id, runId, current =>
        {
            if (Rank(state) < Rank(current.State))
                throw new TestRunValidationException($"Test run state cannot move backward from {current.State} to {state}.");
            if (current.State == TestRunStates.Completed)
            {
                var changed = state != current.State
                    || NormalizeResult(request.Result) != current.Result
                    || (request.DurationSeconds is not null && request.DurationSeconds != current.DurationSeconds)
                    || (Clean(request.Host) is { } host && host != current.Host);
                if (changed) throw new TestRunValidationException("A completed test run is immutable.");
                return current;
            }
            var now = DateTime.UtcNow;
            DateTime? startedAt = state is TestRunStates.Running or TestRunStates.Completed
                ? current.StartedAt ?? now
                : null;
            var duration = request.DurationSeconds ?? current.DurationSeconds;
            if (state == TestRunStates.Completed && duration is null)
                duration = Math.Max(0, (now - startedAt!.Value).TotalSeconds);
            return current with
            {
                State = state,
                Result = state == TestRunStates.Completed ? NormalizeResult(request.Result) : null,
                DurationSeconds = duration,
                Host = Clean(request.Host) ?? current.Host,
                StartedAt = startedAt,
                CompletedAt = state == TestRunStates.Completed ? current.CompletedAt ?? now : null,
            };
        });
    }

    public ProjectTestRunsResponse? BuildProjectView(string projectHandle)
    {
        var project = ResolveProject(projectHandle);
        if (project is null) return null;
        var runs = Ordered(_store.List(project.Id));
        var jobs = _scanner.ScanAllAutomationJobs()
            .Where(job => SamePath(job.WatchPath, project.StorageLocation))
            .ToList();
        var evidence = BuildLookup(jobs);
        var jobsByKey = jobs.ToDictionary(job => job.TaskKey, StringComparer.Ordinal);
        var attachments = evidence
            .Where(pair => pair.Value.RunId is not null)
            .GroupBy(pair => pair.Value.RunId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TestRunAttachedTask>)group.Select(pair =>
                {
                    var job = jobsByKey[pair.Key];
                    return new TestRunAttachedTask(job.Key ?? job.Id, job.Title);
                }).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var repo = _git.ResolveRepoRootForProject(project.DisplayName);
        return new ProjectTestRunsResponse
        {
            Project = project.DisplayName,
            HeadCommit = string.IsNullOrWhiteSpace(repo) ? null : _git.GetBranchTip(repo, "HEAD"),
            Runs = runs.Select(run => new ProjectTestRunItem
            {
                Run = run,
                AttachedTasks = attachments.GetValueOrDefault(run.Id) ?? [],
            }).ToList(),
        };
    }

    public Dictionary<string, TaskTestRunEvidence> BuildLookup(IReadOnlyCollection<TaskInfo> jobs)
    {
        var result = new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal);
        foreach (var projectJobs in jobs.GroupBy(job => job.WatchPath, StringComparer.OrdinalIgnoreCase))
        {
            var groupedJobs = projectJobs.ToList();
            var taskScoped = groupedJobs.ToDictionary(
                job => job.TaskKey,
                job => IsSettledCard(job.State)
                    ? TaskScopedTestEvidenceReader.Read(job)
                    : new TaskScopedTestEvidenceSnapshot([], "not-settled"),
                StringComparer.Ordinal);
            var project = _projects.FindByStorageLocation(projectJobs.Key);
            var repo = _git.ResolveRepoRootForWatchPath(projectJobs.Key);
            if (project is null || string.IsNullOrWhiteSpace(repo))
            {
                foreach (var job in groupedJobs)
                {
                    result[job.TaskKey] = Match(
                        job,
                        [],
                        new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase),
                        new Dictionary<string, IReadOnlyList<GitIntegrationMerge>>(StringComparer.OrdinalIgnoreCase),
                        taskScoped[job.TaskKey].Sources);
                }
                continue;
            }
            var runs = _store.List(project.Id);
            var signature = Signature(groupedJobs, runs, taskScoped);
            var refFingerprint = ReadOnlyGitRefFingerprint.CaptureDetailed(
                repo,
                runs.Select(run => run.Branch)
                    .Concat(groupedJobs.Select(job => job.IntegrationBranch))
                    .OfType<string>()
                    .Where(branch => !string.IsNullOrWhiteSpace(branch)));
            var cached = _evidenceCache.GetOrCreateVersioned(
                $"{repo}\0{project.Id}",
                $"{signature}\0{refFingerprint.Value}",
                refFingerprint.RequiresShortFallback ? ShortFallbackTtl : CacheTtl,
                () =>
                {
                    var refs = runs.Select(run => run.Commit)
                        .Concat(groupedJobs.Select(BoardMergeStatusService.AnchorFor).OfType<string>())
                        .Concat(taskScoped.Values.SelectMany(snapshot => snapshot.Sources).Select(source => source.Commit))
                        .Where(reference => !string.IsNullOrWhiteSpace(reference))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var graph = _git.GetCommitParentGraph(repo, refs);
                    var distances = refs.ToDictionary(
                        reference => reference,
                        reference => AncestorDistances(graph, reference),
                        StringComparer.OrdinalIgnoreCase);
                    var integrationMerges = _git.GetIntegrationMergesByKey(
                        repo,
                        runs.Select(run => run.Commit).ToArray());
                    var value = groupedJobs.ToDictionary(
                        job => job.TaskKey,
                        job => Match(job, runs, distances, integrationMerges, taskScoped[job.TaskKey].Sources),
                        StringComparer.Ordinal);
                    return value;
                });
            foreach (var pair in cached) result[pair.Key] = pair.Value;
        }
        return result;
    }

    public DeploymentTestRunReference? LastGreenForDeployment(string projectHandle, string? headCommit)
    {
        var project = ResolveProject(projectHandle);
        if (project is null) return null;
        var run = _store.List(project.Id)
            .Where(item => item.State == TestRunStates.Completed && item.Result == TestRunResults.Passed)
            .OrderByDescending(item => item.CompletedAt ?? item.CreatedAt)
            .FirstOrDefault();
        if (run is null) return null;
        var repo = _git.ResolveRepoRootForProject(project.DisplayName);
        var refs = new[] { run.Commit, headCommit }.OfType<string>().ToArray();
        var graph = string.IsNullOrWhiteSpace(repo) ? null : _git.GetCommitParentGraph(repo, refs);
        var (distance, direction) = Distance(graph, run.Commit, headCommit);
        return new DeploymentTestRunReference
        {
            Id = run.Id,
            Commit = run.Commit,
            Branch = run.Branch,
            Scope = run.Scope,
            CompletedAt = run.CompletedAt,
            DistanceToHead = distance,
            HeadDirection = direction,
        };
    }

    private static TaskTestRunEvidence Match(
        TaskInfo job,
        IReadOnlyList<TestRunRecord> runs,
        IReadOnlyDictionary<string, Dictionary<string, int>> distances,
        IReadOnlyDictionary<string, IReadOnlyList<GitIntegrationMerge>> integrationMerges,
        IReadOnlyList<TaskTestEvidenceSource> taskScopedSources)
    {
        var anchor = BoardMergeStatusService.AnchorFor(job);
        if (string.IsNullOrWhiteSpace(anchor)) return None("No test evidence assigned: card has no commit");
        var anchorCommittedAt = CurrentCommitAt(job);
        var integratedAnchors = string.IsNullOrWhiteSpace(job.Key) || anchorCommittedAt is null
            ? []
            : (integrationMerges.GetValueOrDefault(job.Key) ?? [])
                .Where(merge => merge.CommittedAtUtc > anchorCommittedAt.Value)
                .Select(merge => merge.Sha)
                .ToArray();
        var scopedMatches = MatchTaskScoped(anchor, taskScopedSources, distances, integratedAnchors);

        var candidates = new List<(TestRunRecord Run, string Quality, string Direction, int Distance, bool Contains)>();
        foreach (var run in runs)
        {
            if (string.Equals(anchor, run.Commit, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add((run, "perfect", "exact", 0, true));
                continue;
            }
            if (distances.GetValueOrDefault(run.Commit)?.TryGetValue(anchor, out var afterDistance) == true)
            {
                candidates.Add((run, "contains-diff", "after", afterDistance, true));
                continue;
            }
            var integrationDistance = integratedAnchors
                .Select(merge =>
                    distances.GetValueOrDefault(run.Commit) is { } runDistances
                    && runDistances.TryGetValue(merge, out var distance)
                        ? (int?)distance
                        : null)
                .Where(distance => distance is not null)
                .Min();
            if (integrationDistance is not null)
            {
                candidates.Add((run, "contains-diff", "after", integrationDistance.Value, true));
                continue;
            }
            if (distances.GetValueOrDefault(anchor)?.TryGetValue(run.Commit, out var beforeDistance) == true)
                candidates.Add((run, "does-not-contain-diff", "before", beforeDistance, false));
        }
        var selected = candidates
            .OrderBy(candidate => CandidateRank(candidate.Run, candidate.Quality))
            .ThenBy(candidate => candidate.Distance)
            .ThenByDescending(candidate => candidate.Run.CreatedAt)
            .FirstOrDefault();
        if (selected.Run is null)
            return scopedMatches.Count > 0
                ? FromTaskScoped(scopedMatches)
                : None(runs.Count == 0 ? "No test evidence assigned" : "No matching test run");

        var evidenceState = !selected.Contains
            ? "not-proven"
            : selected.Run.State is TestRunStates.Planned or TestRunStates.Running
                ? "pending"
                : selected.Run.Result == TestRunResults.Passed ? "proven" : "failed";
        var waiting = IsSettledCard(job.State) && evidenceState == "pending";
        var summary = selected.Quality switch
        {
            "perfect" => "Perfect match",
            "contains-diff" => $"{selected.Distance} commit(s) after, diff included",
            _ => $"{selected.Distance} commit(s) before, diff not included",
        };
        var projected = new TaskTestRunEvidence
        {
            RunId = selected.Run.Id,
            RunCommit = selected.Run.Commit,
            RunState = selected.Run.State,
            RunResult = selected.Run.Result,
            MatchQuality = selected.Quality,
            Direction = selected.Direction,
            Distance = selected.Distance,
            DiffContained = selected.Contains,
            EvidenceState = evidenceState,
            AwaitingEvidence = waiting,
            Summary = waiting ? $"Evidence pending: {summary}" : summary,
            Sources = scopedMatches.Select(match => match.Source).ToList(),
        };
        return evidenceState == "not-proven" && scopedMatches.Count > 0
            ? FromTaskScoped(scopedMatches)
            : projected;
    }

    private static IReadOnlyList<ScopedEvidenceMatch> MatchTaskScoped(
        string anchor,
        IReadOnlyList<TaskTestEvidenceSource> sources,
        IReadOnlyDictionary<string, Dictionary<string, int>> distances,
        IReadOnlyList<string> integratedAnchors)
    {
        var matches = new List<ScopedEvidenceMatch>();
        foreach (var source in sources)
        {
            if (string.Equals(anchor, source.Commit, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new(source, "perfect", "exact", 0));
                continue;
            }
            if (distances.GetValueOrDefault(source.Commit)?.TryGetValue(anchor, out var afterDistance) == true)
            {
                matches.Add(new(source, "contains-diff", "after", afterDistance));
                continue;
            }
            var integrationDistance = integratedAnchors
                .Select(merge =>
                    distances.GetValueOrDefault(source.Commit) is { } sourceDistances
                    && sourceDistances.TryGetValue(merge, out var distance)
                        ? (int?)distance
                        : null)
                .Where(distance => distance is not null)
                .Min();
            if (integrationDistance is not null)
                matches.Add(new(source, "contains-diff", "after", integrationDistance.Value));
        }
        return matches
            .OrderByDescending(match => match.Source.ObservedAt ?? DateTime.MinValue)
            .ToList();
    }

    private static TaskTestRunEvidence FromTaskScoped(IReadOnlyList<ScopedEvidenceMatch> matches)
    {
        var selected = matches.FirstOrDefault(match => match.Source.Kind != "review-aspects")
                       ?? matches[0];
        var evidenceState = selected.Source.Result switch
        {
            "passed" => "proven",
            "failed" => "failed",
            "not-applicable" => "not-applicable",
            _ => "not-proven",
        };
        return new TaskTestRunEvidence
        {
            MatchQuality = selected.Quality,
            Direction = selected.Direction,
            Distance = selected.Distance,
            DiffContained = true,
            EvidenceState = evidenceState,
            AwaitingEvidence = false,
            Summary = selected.Source.Summary,
            Sources = matches.Select(match => match.Source).ToList(),
        };
    }

    private static TaskTestRunEvidence None(string summary) => new()
    {
        AwaitingEvidence = false,
        Summary = summary,
    };

    private static bool IsSettledCard(string state) => state is TaskStates.AutoReview or TaskStates.HumanReview
        or TaskStates.Escalated or TaskStates.Completed or TaskStates.Archive;

    private static (int? Distance, string Direction) Distance(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? graph,
        string from,
        string? to)
    {
        if (graph is null || string.IsNullOrWhiteSpace(to)) return (null, "unknown");
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return (0, "exact");
        var fromHead = AncestorDistances(graph, to);
        if (fromHead.TryGetValue(from, out var ahead)) return (ahead, "head-ahead");
        var fromRun = AncestorDistances(graph, from);
        if (fromRun.TryGetValue(to, out var behind)) return (behind, "head-behind");
        return (null, "diverged");
    }

    private static Dictionary<string, int> AncestorDistances(
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph,
        string tip)
    {
        var distances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [tip] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(tip);
        while (queue.TryDequeue(out var current))
        {
            if (!graph.TryGetValue(current, out var parents)) continue;
            foreach (var parent in parents)
            {
                var distance = distances[current] + 1;
                if (distances.TryGetValue(parent, out var existing) && existing <= distance) continue;
                distances[parent] = distance;
                queue.Enqueue(parent);
            }
        }
        return distances;
    }

    private static string Signature(
        IReadOnlyCollection<TaskInfo> jobs,
        IReadOnlyCollection<TestRunRecord> runs,
        IReadOnlyDictionary<string, TaskScopedTestEvidenceSnapshot> taskScoped)
        => string.Join('|', jobs.OrderBy(job => job.TaskKey).Select(job =>
            $"{job.TaskKey}:{job.State}:{BoardMergeStatusService.AnchorFor(job)}:{CurrentCommitAt(job):O}:{taskScoped[job.TaskKey].Signature}"))
           + "||" + string.Join('|', runs.OrderBy(run => run.Id).Select(run => $"{run.Id}:{run.Commit}:{run.State}:{run.Result}"));

    private static DateTime? CurrentCommitAt(TaskInfo job)
    {
        var value = job.Commits.Count > 0 ? job.Commits[^1].At : job.Commit?.At;
        if (value is null || value == default) return null;
        return value.Value.Kind == DateTimeKind.Utc ? value : value.Value.ToUniversalTime();
    }

    private static IReadOnlyList<TestRunRecord> Ordered(IEnumerable<TestRunRecord> runs) => runs
        .OrderBy(run => run.State == TestRunStates.Planned ? 0 : run.State == TestRunStates.Running ? 1 : 2)
        .ThenBy(run => run.State == TestRunStates.Completed ? -(run.CompletedAt ?? run.CreatedAt).Ticks : run.PlannedOrder)
        .ToList();

    private static void ValidateCreate(CreateTestRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Commit)) throw new TestRunValidationException("commit is required.");
        if (string.IsNullOrWhiteSpace(request.Branch)) throw new TestRunValidationException("branch is required.");
        if (string.IsNullOrWhiteSpace(request.Trigger)) throw new TestRunValidationException("trigger is required.");
        if (request.Scope is null
            || string.IsNullOrWhiteSpace(request.Scope.Level)
            || string.IsNullOrWhiteSpace(request.Scope.TestSet))
            throw new TestRunValidationException("scope.level and scope.testSet are required.");
        var state = NormalizeState(request.State);
        if (!TestRunStates.IsValid(state)) throw new TestRunValidationException("state must be planned, running, or completed.");
        if (state == TestRunStates.Completed && !TestRunResults.IsTerminal(NormalizeResult(request.Result)))
            throw new TestRunValidationException("A completed test run requires result passed, failed, or canceled.");
        if (request.DurationSeconds is < 0) throw new TestRunValidationException("durationSeconds cannot be negative.");
        if (request.PlannedOrder is <= 0) throw new TestRunValidationException("plannedOrder must be positive.");
    }

    private static int Rank(string state) => state switch { TestRunStates.Planned => 0, TestRunStates.Running => 1, _ => 2 };
    private static int CandidateRank(TestRunRecord run, string quality)
    {
        if (quality == "does-not-contain-diff") return 8;
        var exactOffset = quality == "perfect" ? 0 : 1;
        if (run.State == TestRunStates.Completed && run.Result == TestRunResults.Passed) return exactOffset;
        if (run.State == TestRunStates.Running) return 2 + exactOffset;
        if (run.State == TestRunStates.Planned) return 4 + exactOffset;
        return 6 + exactOffset;
    }
    private static string NormalizeState(string? value) => Clean(value)?.ToLowerInvariant() ?? "";
    private static string? NormalizeResult(string? value) => Clean(value)?.ToLowerInvariant();
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record ScopedEvidenceMatch(
        TaskTestEvidenceSource Source,
        string Quality,
        string Direction,
        int Distance);
}
