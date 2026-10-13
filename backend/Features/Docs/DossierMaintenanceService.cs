namespace AgentStudio.Docs;

public sealed record DossierMaintenanceTarget(
    string Id,
    string? Key,
    string Title,
    string EntryPath);

public sealed record DossierMaintenanceReview(
    bool Required,
    bool IsComplete,
    IReadOnlyList<DossierMaintenanceTarget> Targets,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string>? Notes = null)
{
    public string Summary => !Required
        ? "No Dossier reference is attached to this card."
        : IsComplete
            ? string.Join(" ", new[] { $"Implementation status appended to {Targets.Count} referenced Dossier(s)." }
                .Concat(Notes ?? []))
            : string.Join(" ", Findings);
}

/// <summary>
/// One Git revision the maintenance gate may read a Dossier at, with the
/// source that named it (<c>attributed commit</c>, <c>run result</c>, or a
/// full ref name such as <c>refs/remotes/origin/develop</c>).
/// </summary>
public sealed record DossierRevision(string Sha, string Source)
{
    public const string AttributedCommitSource = "attributed commit";
    public const string RunResultSource = "run result";

    public string ShortSha => Sha.Length > 9 ? Sha[..9] : Sha;

    public string Describe() => Source.StartsWith("refs/", StringComparison.Ordinal)
        ? $"{Source}@{ShortSha}"
        : $"{Source} {ShortSha}";
}

/// <summary>Pure timeline projection for the mandatory maintenance step.</summary>
public static class DossierMaintenanceStepPolicy
{
    public static PipelineStepExecution ToExecution(
        DossierMaintenanceReview review,
        DateTime startedAt,
        DateTime completedAt) => new()
    {
        StepId = PipelineCatalogue.DossierMaintenanceStepId,
        Kind = StepKind.Tool,
        Status = !review.Required
            ? PipelineStepStatus.NotApplicable
            : review.IsComplete
                ? PipelineStepStatus.Passed
                : PipelineStepStatus.Failed,
        StartedAt = startedAt,
        CompletedAt = completedAt,
        DurationMs = Math.Max(0, (long)(completedAt - startedAt).TotalMilliseconds),
        Verdict = !review.Required
            ? "not-referenced"
            : review.IsComplete ? "appended" : "missing-update",
        VerdictSummary = review.Summary,
        Reason = review.IsComplete ? null : review.Summary,
    };
}

/// <summary>
/// Resolves the existing Workbench/Dossier reference graph for CORE prompt
/// framing and validates the delivered revision against the append-only HTML
/// contract. It never rewrites the Dossier itself.
/// </summary>
public sealed class DossierMaintenanceService
{
    private readonly WorkbenchCatalogueService _catalogue;
    private readonly GitService _git;

    public DossierMaintenanceService(WorkbenchCatalogueService catalogue, GitService git)
    {
        _catalogue = catalogue;
        _git = git;
    }

    public IReadOnlyList<DossierMaintenanceTarget> ResolveTargets(string projectName, TaskInfo task)
    {
        var taskKeys = new[] { task.Key, task.TaskKey, task.Id }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workbenchKeys = task.References.Workbenches
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (_catalogue.List(projectName, includeHistory: true)?.Items ?? [])
            .Where(item => item.Valid
                && (workbenchKeys.Contains(item.Id)
                    || item.Key != null && workbenchKeys.Contains(item.Key)
                    || item.DescriptorSourceTaskKeys.Any(taskKeys.Contains)))
            .Select(item => new DossierMaintenanceTarget(
                item.Id,
                item.Key,
                item.Title,
                item.EntryPath))
            .DistinctBy(target => target.EntryPath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(target => target.EntryPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Newer revisions that may already carry the card's Dossier entry, in
    /// check order: the run result recorded for review, then the integration
    /// branch tip as last fetched (<c>refs/remotes/origin/&lt;branch&gt;</c>), then
    /// the local branch. Reads existing refs only; it never fetches.
    /// </summary>
    public IReadOnlyList<DossierRevision> ResolveRevisionCandidates(
        string repositoryRoot,
        TaskInfo task,
        string? subjectResultSha,
        string? configuredIntegrationBranch = null)
    {
        var candidates = new List<DossierRevision>();
        if (ReviewSubjectStore.IsValidResultSha(subjectResultSha))
            candidates.Add(new DossierRevision(subjectResultSha!.Trim(), DossierRevision.RunResultSource));

        var branch = BranchName(task.IntegrationBranch);
        if (branch == null && !string.IsNullOrWhiteSpace(repositoryRoot) && Directory.Exists(repositoryRoot))
            branch = BranchName(_git.ResolveIntegrationBranch(repositoryRoot, configuredIntegrationBranch));
        if (branch != null)
        {
            foreach (var refName in new[] { $"refs/remotes/origin/{branch}", $"refs/heads/{branch}" })
            {
                var tip = _git.GetBranchTip(repositoryRoot, refName);
                if (!string.IsNullOrWhiteSpace(tip))
                    candidates.Add(new DossierRevision(tip, refName));
            }
        }

        return candidates
            .DistinctBy(candidate => candidate.Sha, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Validates every referenced Dossier against the append-only contract on
    /// the attributed delivery range. A pure absence (no file, section, log,
    /// or entry) is re-checked once on the first newer revision candidate, so
    /// an entry that already landed through another round is accepted as
    /// idempotent instead of reopening the card. Append-only violations are
    /// never cleared by a newer revision.
    /// </summary>
    public DossierMaintenanceReview Review(
        string projectName,
        string repositoryRoot,
        TaskInfo task,
        IReadOnlyList<DossierRevision>? revisionCandidates = null)
    {
        var targets = ResolveTargets(projectName, task);
        if (targets.Count == 0)
            return new DossierMaintenanceReview(false, true, targets, Array.Empty<string>());

        IReadOnlyList<DossierRevision> Candidates() =>
            revisionCandidates ??= ResolveRevisionCandidates(repositoryRoot, task, subjectResultSha: null);
        var taskKey = string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key.Trim();
        var findings = new List<string>();
        var notes = new List<string>();
        foreach (var target in targets)
        {
            var touchingCommits = task.Commits
                .Where(commit => commit.Files.Any(file =>
                    string.Equals(NormalizePath(file), target.EntryPath, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(commit => commit.At)
                .ToList();

            if (touchingCommits.Count == 0)
            {
                // Nothing attributed touched the Dossier: the entry can only be
                // present already, so read the newest resolved revision.
                var revision = Candidates().FirstOrDefault();
                if (revision == null)
                {
                    findings.Add($"{target.EntryPath}: The Dossier entrypoint is missing from the delivery revision; "
                        + "no attributed commit touches it and no run result or integration branch revision resolved.");
                    continue;
                }
                var content = _git.GetFileAtCommit(repositoryRoot, revision.Sha, target.EntryPath);
                var existing = DossierImplementationContract.Review(content, content, taskKey);
                if (existing.IsComplete)
                    notes.Add(PresentNote(target, taskKey, revision));
                else
                    findings.AddRange(Describe(target, revision, existing));
                continue;
            }

            var first = touchingCommits[0];
            var last = touchingCommits[^1];
            var attributed = new DossierRevision(
                string.IsNullOrWhiteSpace(last.ResultSha) ? last.Sha : last.ResultSha!,
                DossierRevision.AttributedCommitSource);
            var before = _git.GetFileAtParentOfCommit(repositoryRoot, first.Sha, target.EntryPath);
            var after = _git.GetFileAtCommit(repositoryRoot, attributed.Sha, target.EntryPath);
            var review = DossierImplementationContract.Review(before, after, taskKey);
            if (review.IsComplete) continue;

            var newer = review.IsAbsence
                ? Candidates().FirstOrDefault(candidate =>
                    !string.Equals(candidate.Sha, attributed.Sha, StringComparison.OrdinalIgnoreCase))
                : null;
            if (newer == null)
            {
                findings.AddRange(Describe(target, attributed, review));
                continue;
            }

            var newerContent = _git.GetFileAtCommit(repositoryRoot, newer.Sha, target.EntryPath);
            var newerReview = DossierImplementationContract.Review(newerContent, newerContent, taskKey);
            if (newerReview.IsComplete)
            {
                notes.Add(PresentNote(target, taskKey, newer));
                continue;
            }
            findings.AddRange(Describe(target, attributed, review));
            findings.AddRange(Describe(target, newer, newerReview));
        }

        return new DossierMaintenanceReview(
            true,
            findings.Count == 0,
            targets,
            findings,
            notes);
    }

    public static string RenderTargetList(IReadOnlyList<DossierMaintenanceTarget> targets) =>
        string.Join(Environment.NewLine, targets.Select(target =>
            $"- `{target.EntryPath}` ({target.Key ?? target.Id}, {target.Title})"));

    private static IEnumerable<string> Describe(
        DossierMaintenanceTarget target,
        DossierRevision revision,
        DossierImplementationReview review) =>
        review.Findings.Select(finding => $"{target.EntryPath} at {revision.Describe()}: {finding}");

    private static string PresentNote(DossierMaintenanceTarget target, string taskKey, DossierRevision revision) =>
        $"{target.EntryPath}: implementation entry for {taskKey} already present on {revision.Describe()}.";

    private static string? BranchName(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        var branch = configured.Trim();
        foreach (var prefix in new[] { "refs/heads/", "refs/remotes/origin/", "origin/" })
        {
            if (branch.StartsWith(prefix, StringComparison.Ordinal))
            {
                branch = branch[prefix.Length..];
                break;
            }
        }
        return branch.Length == 0 ? null : branch;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
}
