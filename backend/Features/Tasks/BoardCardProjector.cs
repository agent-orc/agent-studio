using static AgentStudio.Tasks.TaskEndpointHelpers;
using AgentStudio.Prompts;

namespace AgentStudio.Tasks;

/// <summary>
/// The board card enrichment: runtime overlay, tokens, orchestrator verdicts,
/// dependency projections, live status, the published Git signals and the
/// review queue, folded onto the scanned cards. The two board routes and the
/// persisted-board writer (<see cref="BoardSummaryPersister"/>) share it, so a
/// restored card is built exactly like a live one. Every lookup is a cache
/// read; nothing here starts Git or walks the workspace. Inside a pinned board
/// read, index lookups answer from the pinned generation.
/// </summary>
public sealed class BoardCardProjector(
    ITokenPricing pricing,
    TaskScannerService scanner,
    CliRouter router,
    TaskRunnerService runners,
    AttemptAuthorityService attemptAuthority,
    ITokenAggregator tokens,
    IConfiguration configuration,
    TaskListGitProjectionCache gitProjection,
    TaskLiveStatusProjection liveStatus,
    ProjectSettingsService projectSettings,
    BetterCandidateService betterCandidates)
{
    /// <summary>
    /// Enriches <paramref name="raw"/> in its order. Transitive waiters are
    /// limited to <paramref name="eligibleWaiters"/>, by default the cards
    /// themselves, so a reader never learns of a waiter it cannot see.
    /// </summary>
    public List<TaskInfo> Project(IReadOnlyList<TaskInfo> raw, IEnumerable<TaskInfo>? eligibleWaiters = null,
        IReadOnlyDictionary<string, TaskTokenSummary>? cachedTokens = null)
    {
        var tokenLookup = cachedTokens ?? BuildTokenLookup(raw, tokens);
        var verdictLookup = BuildOrchestratorVerdictLookup(raw, configuration);
        var dependencyLookups = BuildDependencyGraphLookups(raw, scanner, eligibleWaiters,
            attemptAuthority: attemptAuthority);
        var gitLookup = gitProjection.ReadCacheOnly(raw);
        var liveLookup = liveStatus.BuildLookup(raw);
        var reviewQueue = attemptAuthority.ReadReviewQueue();
        return raw.Select(job => betterCandidates.Attach(
                          WithRuntime(pricing, job, router, runners, tokenLookup, verdictLookup, dependencyLookups.WaitsOn, dependencyLookups.TransitiveWaiters),
                          projectSettings.Get(job.ProjectName)))
                      .WithLiveStatus(liveLookup)
                      .WithMergeSignal(gitLookup.Merge)
                      .WithIntegrationStatus(gitLookup.Integration)
                      .WithPublishSignal(gitLookup.Publish)
                      .WithTestRunEvidence(gitLookup.TestRuns)
                      .Select(job => WithReviewQueue(job, reviewQueue))
                      .ToList();
    }

    /// <summary>
    /// F35: each lane is sorted using a per-project strategy. The kanban mixes
    /// projects inside one lane, so the sort groups by project, applies that
    /// project's resolved strategy, then concatenates the groups in
    /// alphabetical project order for a deterministic global result. Settings
    /// are cached in memory, so the per-project lookup is essentially free.
    /// </summary>
    public Func<string, IReadOnlyList<TaskInfo>> LaneSorter(IReadOnlyList<TaskInfo> jobs)
    {
        var settingsByProject = new Dictionary<string, ProjectSettings>(StringComparer.OrdinalIgnoreCase);
        ProjectSettings SettingsFor(string projectName)
        {
            if (!settingsByProject.TryGetValue(projectName, out var settings))
            {
                settings = projectSettings.Get(projectName);
                settingsByProject[projectName] = settings;
            }
            return settings;
        }
        return lane => LaneSortApplier.Sort(jobs.Where(job => job.State == lane), lane, SettingsFor).ToList();
    }

    private static TaskInfo WithReviewQueue(TaskInfo job, IReadOnlyDictionary<string, ReviewQueueEntry> queue)
    {
        if (job.State != TaskStates.AutoReview || !queue.TryGetValue(TaskIdentity.AuthorityKey(job), out var entry)) return job;
        return job with { ReviewPriority = entry.Priority, ReviewQueuePosition = entry.Position };
    }
}
