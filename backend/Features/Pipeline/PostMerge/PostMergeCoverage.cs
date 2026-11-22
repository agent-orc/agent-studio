using System.Collections.Concurrent;
using AgentStudio.Diagnostics;
using AgentStudio.Git;
using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Values of <see cref="PostMergeCoverage.State"/>.</summary>
public static class PostMergeCoverageStates
{
    /// <summary>Strict: the project tests before integration and expects no post-merge run.</summary>
    public const string NotExpected = "not-expected";
    /// <summary>A post-merge run covers develop, or reported within the limit.</summary>
    public const string Current = "current";
    /// <summary>Develop moved, and no post-merge run reported within the limit.</summary>
    public const string Missing = "missing";
    /// <summary>The develop tip or its history could not be read on this host.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// Whether a project on fast or balanced gets the post-merge runs its
/// strategy relies on (AGT-3965). <see cref="Message"/> is the sentence the
/// settings page, the Operations view and Needs you show.
/// </summary>
public sealed record PostMergeCoverage(
    string Project,
    string Strategy,
    string State,
    string? DevelopTipSha,
    string? LastReportedTipSha,
    DateTimeOffset? LastReportedAtUtc,
    string Message)
{
    public bool Missing => State == PostMergeCoverageStates.Missing;
}

/// <summary>The newest post-merge receipt of a project: the tip it checked and when it reported.</summary>
public sealed record PostMergeLastReport(string TipSha, DateTimeOffset AtUtc);

/// <summary>
/// Pure rule of the missing-post-merge-run warning (AGT-3965). A project on
/// fast or balanced integrates before its tests run; the post-merge timers
/// are the only check develop gets. When develop has a first-parent commit
/// that no reported run covers, that commit is at least <see cref="Limit"/>
/// old, and no run reported within <see cref="Limit"/>, the project runs
/// blind and must say so. Any receipt counts as a report, a skip and a red
/// run included: red develop has its own repair path.
/// </summary>
public static class PostMergeCoveragePolicy
{
    public static readonly TimeSpan Limit = TimeSpan.FromHours(6);

    public static PostMergeLastReport? LastReport(PostMergeProjectState state)
    {
        var reports = state.Runs
            .Select(run => new PostMergeLastReport(run.TipSha, run.FinishedAtUtc ?? run.StartedAtUtc));
        if (state.ActiveRun is { } active)
            reports = reports.Append(new PostMergeLastReport(active.TipSha, active.StartedAtUtc));
        return reports.Where(report => !string.IsNullOrWhiteSpace(report.TipSha))
            .OrderByDescending(report => report.AtUtc).FirstOrDefault();
    }

    /// <summary>
    /// The state before any history walk: null means the caller must ask
    /// whether develop holds an uncovered commit older than <see cref="Limit"/>.
    /// </summary>
    public static string? Precheck(string strategy, PostMergeLastReport? last, string? developTip,
        DateTimeOffset now)
    {
        if (!TestingStrategies.ExpectsPostMergeRuns(strategy)) return PostMergeCoverageStates.NotExpected;
        if (string.IsNullOrWhiteSpace(developTip)) return PostMergeCoverageStates.Unknown;
        if (last is not null && string.Equals(last.TipSha, developTip, StringComparison.OrdinalIgnoreCase))
            return PostMergeCoverageStates.Current;
        if (last is not null && now - last.AtUtc < Limit) return PostMergeCoverageStates.Current;
        return null;
    }

    public static string Decide(string? precheck, bool? uncoveredOlderThanLimit)
        => precheck ?? uncoveredOlderThanLimit switch
        {
            true => PostMergeCoverageStates.Missing,
            false => PostMergeCoverageStates.Current,
            null => PostMergeCoverageStates.Unknown,
        };

    public static string Message(string project, string strategy, string state, string branch,
        string? developTip, PostMergeLastReport? last)
    {
        var hours = (int)Limit.TotalHours;
        return state switch
        {
            PostMergeCoverageStates.NotExpected =>
                $"{project} tests before integration (strict); no post-merge run is expected.",
            PostMergeCoverageStates.Current => last is null
                ? $"{branch} has no unverified commit older than {hours} hours."
                : $"The last post-merge run reported at {last.AtUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC on {Short(last.TipSha)}.",
            PostMergeCoverageStates.Missing => (last is null
                    ? $"No post-merge run was ever reported for {project}"
                    : $"No post-merge run was reported for {project} in the last {hours} hours "
                        + $"(last at {last.AtUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC on {Short(last.TipSha)})")
                + $", while {branch} moved to {Short(developTip)}. Testing strategy {strategy} integrates "
                + "before the tests run, so these merges are untested. Start the post-merge window and "
                + "nightly timers for this project, or choose strict on the project settings page.",
            _ => $"Studio cannot read {branch} for {project}, so it cannot tell whether post-merge runs cover it.",
        };
    }

    private static string Short(string? sha) => string.IsNullOrWhiteSpace(sha) ? "an unknown tip"
        : sha.Length > 12 ? sha[..12] : sha;
}

/// <summary>Reads the integration branch without changing the repository.</summary>
public interface IDevelopCoverageGit
{
    /// <summary>The published tip of <paramref name="branch"/>, or null when unreadable.</summary>
    string? Tip(string project, string branch);

    /// <summary>
    /// True when a first-parent commit after <paramref name="coveredTip"/> (all
    /// of <paramref name="tip"/>'s history when null) was committed at or
    /// before <paramref name="before"/>; null when Git cannot answer.
    /// </summary>
    bool? HasUncoveredCommitBefore(string project, string tip, string? coveredTip, DateTimeOffset before);
}

/// <summary>Ref files answer the tip; one bounded <c>rev-list</c> answers the history question.</summary>
public sealed class DevelopCoverageGit(GitService git) : IDevelopCoverageGit
{
    private readonly ConcurrentDictionary<string, string?> _roots = new(StringComparer.OrdinalIgnoreCase);

    public string? Tip(string project, string branch)
    {
        if (Root(project) is not { } root) return null;
        return git.GetBranchTip(root, "refs/remotes/origin/" + branch) ?? git.GetBranchTip(root, branch);
    }

    public bool? HasUncoveredCommitBefore(string project, string tip, string? coveredTip, DateTimeOffset before)
        => Root(project) is { } root ? git.HasFirstParentCommitBefore(root, tip, coveredTip, before) : null;

    private string? Root(string project)
    {
        var root = _roots.GetOrAdd(project, git.ResolveRepoRootForProject);
        if (root is null) _roots.TryRemove(project, out _);
        return root;
    }
}

/// <summary>
/// Evaluates <see cref="PostMergeCoveragePolicy"/> per project. Only
/// <see cref="Evaluate(ProjectRecord)"/> and <see cref="RefreshAll"/> read Git;
/// <see cref="PostMergeCoverageRefresher"/> calls them off the request path.
/// Read routes and Needs you use <see cref="Read"/> and <see cref="Missing"/>,
/// which serve the last evaluation and start no Git process. The history walk
/// runs once per pair of develop tip and covered tip; a negative answer is
/// asked again after a minute, because the commit ages.
/// </summary>
public sealed class PostMergeCoverageService(ProjectRegistry projects, PostMergeRunStore runs,
    ProjectSettingsService settings, IDevelopCoverageGit git, TimeProvider clock)
{
    private static readonly TimeSpan NegativeRecheck = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (bool? Answer, DateTimeOffset AskedAt)> _walks =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PostMergeCoverage> _latest =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Evaluates one project now, Git included, and publishes the result. Not for read requests.</summary>
    public PostMergeCoverage? Evaluate(string project)
        => Find(project) is { } record ? Evaluate(record) : null;

    /// <summary>Evaluates every unarchived project; the background refresher's tick.</summary>
    public void RefreshAll()
    {
        foreach (var record in projects.List().Where(record => !record.Archived))
            Evaluate(record);
    }

    /// <summary>
    /// The published coverage of a project, without Git. Strict answers at
    /// once; a fast or balanced project not evaluated under its current
    /// strategy yet reads as unknown until the next refresh.
    /// </summary>
    public PostMergeCoverage? Read(string project)
    {
        if (Find(project) is not { } record) return null;
        var projectSettings = settings.Get(record.DisplayName);
        var strategy = TestingStrategies.Resolve(projectSettings);
        var branch = Branch(projectSettings);
        if (!TestingStrategies.ExpectsPostMergeRuns(strategy))
            return Coverage(record, strategy, PostMergeCoverageStates.NotExpected, branch, null, null);
        if (_latest.TryGetValue(record.DisplayName, out var latest) && latest.Strategy == strategy)
            return latest;
        return new PostMergeCoverage(record.DisplayName, strategy, PostMergeCoverageStates.Unknown, null, null, null,
            $"Studio has not checked {branch} for {record.DisplayName} under testing strategy {strategy} yet; "
            + "the check runs every minute.");
    }

    /// <summary>The published missing projects whose strategy still expects post-merge runs; no Git.</summary>
    public IReadOnlyList<PostMergeCoverage> Missing()
        => _latest.Values.Where(coverage => coverage.Missing
                && Find(coverage.Project) is { Archived: false } record
                && TestingStrategies.Resolve(settings.Get(record.DisplayName)) == coverage.Strategy)
            .OrderBy(coverage => coverage.Project, StringComparer.OrdinalIgnoreCase).ToArray();

    public PostMergeCoverage Evaluate(ProjectRecord record)
    {
        var projectSettings = settings.Get(record.DisplayName);
        var strategy = TestingStrategies.Resolve(projectSettings);
        var branch = Branch(projectSettings);
        var now = clock.GetUtcNow();
        PostMergeLastReport? last = null;
        string? tip = null;
        string? precheck;
        if (!TestingStrategies.ExpectsPostMergeRuns(strategy))
            precheck = PostMergeCoverageStates.NotExpected;
        else
        {
            try
            {
                last = PostMergeCoveragePolicy.LastReport(runs.Read(record.Id));
                tip = git.Tip(record.DisplayName, branch);
                precheck = PostMergeCoveragePolicy.Precheck(strategy, last, tip, now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException)
            {
                SilentCatch.Note(ex, "PostMergeCoverageService: state read failed");
                precheck = PostMergeCoverageStates.Unknown;
            }
        }
        var state = PostMergeCoveragePolicy.Decide(precheck,
            precheck is null ? Walk(record.DisplayName, tip!, last?.TipSha, now) : null);
        var coverage = Coverage(record, strategy, state, branch, tip, last);
        _latest[record.DisplayName] = coverage;
        return coverage;
    }

    private ProjectRecord? Find(string project)
        => projects.FindByIdOrDisplayName(project) ?? projects.FindByShortCode(project);

    private static string Branch(ProjectSettings projectSettings)
        => string.IsNullOrWhiteSpace(projectSettings.IntegrationBranch)
            ? "develop" : projectSettings.IntegrationBranch.Trim();

    private static PostMergeCoverage Coverage(ProjectRecord record, string strategy, string state, string branch,
        string? tip, PostMergeLastReport? last)
        => new(record.DisplayName, strategy, state, tip, last?.TipSha, last?.AtUtc,
            PostMergeCoveragePolicy.Message(record.DisplayName, strategy, state, branch, tip, last));

    private bool? Walk(string project, string tip, string? coveredTip, DateTimeOffset now)
    {
        var key = string.Join('\0', project, tip, coveredTip ?? "");
        if (_walks.TryGetValue(key, out var cached)
            && (cached.Answer == true || now - cached.AskedAt < NegativeRecheck))
            return cached.Answer;
        var answer = git.HasUncoveredCommitBefore(project, tip, coveredTip, now - PostMergeCoveragePolicy.Limit);
        foreach (var stale in _walks.Keys.Where(item => item.StartsWith(project + '\0', StringComparison.Ordinal)
                     && item != key))
            _walks.TryRemove(stale, out _);
        _walks[key] = (answer, now);
        return answer;
    }
}

/// <summary>
/// Refreshes <see cref="PostMergeCoverageService"/> once a minute off the
/// request path, so read routes and Needs you never start Git for it.
/// </summary>
public sealed class PostMergeCoverageRefresher(PostMergeCoverageService coverage,
    ILogger<PostMergeCoverageRefresher> logger, TimeProvider clock) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { coverage.RefreshAll(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "post-merge coverage refresh failed; the last published coverage stays");
            }
            try { await Task.Delay(Interval, clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
