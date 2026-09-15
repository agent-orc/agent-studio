using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using AgentStudio.Diagnostics;
using AgentStudio.Git;
using AgentStudio.Registry;

namespace AgentStudio.Docs;

/// <summary>
/// Advances the hosted wiki to the accepted documentation revision without any
/// manual file copying, and keeps the previous revision online whenever that
/// cannot be done safely.
///
/// <para>The flow is deliberately one-directional: validate the boundary
/// (project, repository, ref), collect facts (fetch, resolve the candidate
/// commit), let <see cref="WikiPublicationPolicy"/> decide, and only then apply
/// bounded side effects. The promotion itself is a single reference swap of an
/// immutable <see cref="WikiPublishedRevision"/> performed AFTER the new docs
/// snapshot is completely materialized, so a concurrent reader observes either
/// the whole old tree or the whole new one. A failed fetch, a missing revision,
/// or a snapshot that does not validate never reaches the swap, which is why
/// every failure path leaves the published revision untouched.</para>
///
/// <para>Publication does not make the hosted wiki writable. The published
/// source is a read-only git revision, so <c>ProjectDocsService</c> keeps
/// rejecting mutations exactly as it does for any branch-backed wiki.</para>
/// </summary>
public sealed class WikiPublicationService
{
    private static readonly Regex ShaDirectoryPattern =
        new("^[0-9a-f]{40}$", RegexOptions.Compiled);

    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly GitService? _git;
    private readonly ILogger<WikiPublicationService> _logger;
    private readonly ConcurrentDictionary<string, PublicationSlot> _slots =
        new(StringComparer.OrdinalIgnoreCase);
    private WikiContentCache? _cache;

    public WikiPublicationOptions Options { get; }

    public WikiPublicationService(
        TaskScannerService scanner,
        ProjectRegistry registry,
        ILogger<WikiPublicationService> logger,
        GitService? git = null,
        IConfiguration? configuration = null)
    {
        _scanner = scanner;
        _registry = registry;
        _logger = logger;
        _git = git;
        Options = WikiPublicationOptions.FromConfiguration(configuration);
    }

    /// <summary>
    /// Binds the process-wide wiki cache after DI construction. The cache is
    /// built over <c>ProjectDocsService</c>, which in turn reads the published
    /// revision from this service, so the edge is closed here rather than in a
    /// constructor.
    /// </summary>
    public void SetWikiContentCache(WikiContentCache cache) => _cache = cache;

    /// <summary>The revision currently online for a project, or null when none is published.</summary>
    public WikiPublishedRevision? GetPublished(ProjectRecord? project) =>
        project == null ? null : GetPublishedByKey(project.Id);

    public WikiPublishedRevision? GetPublishedByKey(string projectKey) =>
        _slots.TryGetValue(projectKey, out var slot) ? slot.Published : null;

    /// <summary>
    /// Watched project names whose wiki source ref is configured. The sync
    /// worker iterates these; a project without a source ref stays
    /// checkout-backed and is never fetched for.
    /// </summary>
    public IReadOnlyList<string> PublicationProjectNames()
    {
        var names = new List<string>();
        foreach (var entry in _scanner.GetWatchPaths())
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            var project = ProjectWikiSourceResolver.ResolveProject(entry.Name, _scanner, _registry);
            if (!string.IsNullOrWhiteSpace(project?.WikiSourceBranch)) names.Add(entry.Name);
        }
        return names;
    }

    /// <summary>
    /// Runs one supervised publication attempt. Safe to call concurrently: a
    /// per-project gate serializes promotions so two triggers cannot interleave
    /// their swaps.
    ///
    /// <para><paramref name="force"/> releases a rollback hold and is reserved
    /// for the operator trigger. The scheduled trigger must never set it, or a
    /// rollback would be re-promoted on the next tick.</para>
    /// </summary>
    public WikiPublicationOutcome Synchronize(
        string projectName,
        string trigger = "scheduled",
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var project = ProjectWikiSourceResolver.ResolveProject(projectName, _scanner, _registry);
        var key = project?.Id ?? projectName;
        var sourceRef = project?.WikiSourceBranch;
        var slot = _slots.GetOrAdd(key, _ => new PublicationSlot());

        lock (slot.Gate)
        {
            if (slot.Held && !force)
                return Record(slot, new WikiPublicationOutcome(
                    key, sourceRef, WikiPublicationStatus.Held, WikiPublicationFailure.None,
                    "A rollback pinned this project. Force a sync to resume publication.",
                    slot.Published?.Sha, slot.Published?.Sha,
                    DateTime.UtcNow, started.ElapsedMilliseconds, trigger));
            if (force) slot.Held = false;

            var repoRoot = _git?.ResolveRepoRootForProject(projectName);
            var repositoryAvailable =
                _git != null && !string.IsNullOrWhiteSpace(repoRoot) && Directory.Exists(repoRoot);

            var refAccepted = WikiPublicationPolicy.IsPublishableRef(
                sourceRef, Options.Remote, Options.AcceptedRefs);

            string? fetchError = null;
            if (repositoryAvailable && refAccepted
                && WikiPublicationPolicy.RequiresFetch(sourceRef, Options.Remote))
            {
                var fetched = _git!.Fetch(repoRoot!, Options.Remote, cancellationToken);
                fetchError = fetched.Success
                    ? fetched.Error
                    : fetched.Error ?? "The repository could not be fetched.";
            }

            // Resolved fresh, never through the short read cache: this call has
            // to observe the commit the fetch above just wrote. rev-parse also
            // covers a branch, a remote-tracking ref, a tag, and a pinned commit
            // SHA with one call, so a published release revision needs no
            // separate lookup path.
            string? candidate = null;
            if (repositoryAvailable && refAccepted && fetchError == null)
                candidate = _git!.GetRefShaFresh(repoRoot!, sourceRef!);

            var published = slot.Published;
            var decision = WikiPublicationPolicy.Decide(new WikiPublicationFacts(
                SourceRef: sourceRef,
                RepositoryAvailable: repositoryAvailable,
                RefAccepted: refAccepted,
                FetchError: fetchError,
                CandidateSha: candidate,
                PublishedSha: published?.Sha,
                PublishedSnapshotUsable: SnapshotUsable(published)));

            if (decision.Action == WikiPublicationAction.Promote)
                return Promote(slot, projectName, key, repoRoot!, sourceRef!, decision, trigger, started);

            var status = decision.Action switch
            {
                WikiPublicationAction.Disabled => WikiPublicationStatus.Disabled,
                WikiPublicationAction.NoOp => WikiPublicationStatus.NoOp,
                _ => WikiPublicationStatus.Failed,
            };
            if (status == WikiPublicationStatus.Failed)
                _logger.LogError(
                    "wiki-publication-failed project={Project} ref={Ref} published={Published} failure={Failure} trigger={Trigger} reason={Reason}",
                    key, sourceRef, decision.FromSha, decision.Failure, trigger, decision.Reason);

            return Record(slot, new WikiPublicationOutcome(
                key, sourceRef, status, decision.Failure, decision.Reason,
                decision.FromSha, decision.ToSha,
                DateTime.UtcNow, started.ElapsedMilliseconds, trigger));
        }
    }

    /// <summary>
    /// Republishes the revision that was online before the last promotion. The
    /// rehearsed recovery path: it reuses the retained snapshot, so it performs
    /// no fetch and no archive work and cannot fail on a network problem.
    /// </summary>
    public WikiPublicationOutcome Rollback(string projectName, string trigger = "operator")
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var project = ProjectWikiSourceResolver.ResolveProject(projectName, _scanner, _registry);
        var key = project?.Id ?? projectName;
        var slot = _slots.GetOrAdd(key, _ => new PublicationSlot());

        lock (slot.Gate)
        {
            var current = slot.Published;
            var target = slot.Previous;
            if (target == null || !SnapshotUsable(target))
                return Record(slot, new WikiPublicationOutcome(
                    key, project?.WikiSourceBranch, WikiPublicationStatus.Failed,
                    WikiPublicationFailure.RollbackUnavailable,
                    "No previous published revision is still readable on this host.",
                    current?.Sha, current?.Sha,
                    DateTime.UtcNow, started.ElapsedMilliseconds, trigger));

            var restored = target with
            {
                PromotedAtUtc = DateTime.UtcNow,
                PreviousSha = current?.Sha,
            };
            // The restored revision becomes the only retained one and the
            // project is pinned. Keeping the rolled-back-from revision as
            // "previous" would turn a second rollback into a silent roll
            // forward, and leaving the hold off would let the next scheduled
            // tick re-publish exactly what the operator removed.
            Swap(slot, projectName, restored, previous: null);
            slot.Held = true;

            _logger.LogWarning(
                "wiki-publication-rolled-back project={Project} ref={Ref} from={From} to={To} trigger={Trigger}",
                key, restored.SourceRef, current?.Sha, restored.Sha, trigger);

            return Record(slot, new WikiPublicationOutcome(
                key, restored.SourceRef, WikiPublicationStatus.RolledBack, WikiPublicationFailure.None,
                "The previous published revision was restored.",
                current?.Sha, restored.Sha,
                DateTime.UtcNow, started.ElapsedMilliseconds, trigger));
        }
    }

    /// <summary>
    /// Publication state and last deployment evidence for the diagnostics
    /// surface. Null for a project this host does not watch or know, which is
    /// the endpoint's 404 condition.
    /// </summary>
    public WikiPublicationReport? GetReport(string projectName)
    {
        var project = ProjectWikiSourceResolver.ResolveProject(projectName, _scanner, _registry);
        if (project == null && !IsWatchedProject(projectName)) return null;
        var key = project?.Id ?? projectName;
        _slots.TryGetValue(key, out var slot);
        var published = slot?.Published;
        return new WikiPublicationReport(
            ProjectName: projectName,
            SourceRef: project?.WikiSourceBranch,
            Enabled: Options.Enabled && !string.IsNullOrWhiteSpace(project?.WikiSourceBranch),
            PublishedSha: published?.Sha,
            PublishedShortSha: published?.ShortSha,
            PublishedAtUtc: published?.PromotedAtUtc,
            PreviousSha: slot?.Previous?.Sha,
            RollbackAvailable: slot?.Previous != null && SnapshotUsable(slot.Previous),
            Held: slot?.Held == true,
            LastOutcome: slot?.LastOutcome,
            LastFailure: slot?.LastFailure,
            IntervalSeconds: Options.IntervalSeconds);
    }

    private WikiPublicationOutcome Promote(
        PublicationSlot slot,
        string projectName,
        string key,
        string repoRoot,
        string sourceRef,
        WikiPublicationDecision decision,
        string trigger,
        System.Diagnostics.Stopwatch started)
    {
        var sha = decision.ToSha!;
        var snapshot = _git!.GetWikiSnapshotForShaCached(repoRoot, sourceRef, sha);
        var docsPresent = !string.IsNullOrWhiteSpace(snapshot.RootPath)
            && Directory.Exists(Path.Combine(snapshot.RootPath, "docs"));
        var failure = WikiPublicationPolicy.ValidateMaterialization(snapshot.Error, docsPresent);
        if (failure != WikiPublicationFailure.None)
        {
            var reason = string.IsNullOrWhiteSpace(snapshot.Error)
                ? $"Revision {Short(sha)} has no readable docs/ tree."
                : snapshot.Error!;
            _logger.LogError(
                "wiki-publication-failed project={Project} ref={Ref} published={Published} candidate={Candidate} failure={Failure} trigger={Trigger} reason={Reason}",
                key, sourceRef, decision.FromSha, sha, failure, trigger, reason);
            return Record(slot, new WikiPublicationOutcome(
                key, sourceRef, WikiPublicationStatus.Failed, failure, reason,
                decision.FromSha, decision.FromSha,
                DateTime.UtcNow, started.ElapsedMilliseconds, trigger));
        }

        var previous = slot.Published;
        var promoted = new WikiPublishedRevision(
            ProjectKey: key,
            SourceRef: sourceRef,
            Sha: sha,
            ShortSha: snapshot.ShortSha,
            SnapshotRoot: snapshot.RootPath,
            PromotedAtUtc: DateTime.UtcNow,
            PreviousSha: previous?.Sha);

        Swap(slot, projectName, promoted, previous);
        PruneSnapshots(promoted, previous);

        _logger.LogInformation(
            "wiki-publication-promoted project={Project} ref={Ref} from={From} to={To} trigger={Trigger} elapsedMs={ElapsedMs}",
            key, sourceRef, previous?.Sha, sha, trigger, started.ElapsedMilliseconds);

        return Record(slot, new WikiPublicationOutcome(
            key, sourceRef, WikiPublicationStatus.Promoted, WikiPublicationFailure.None,
            decision.Reason, previous?.Sha, sha,
            DateTime.UtcNow, started.ElapsedMilliseconds, trigger));
    }

    /// <summary>
    /// The promotion boundary. The reference assignment publishes the new
    /// revision to every reader in one step; the cache rebuild that follows
    /// only refreshes the derived projection. A reader arriving between the two
    /// still sees a complete tree, just the previous one.
    /// </summary>
    private void Swap(
        PublicationSlot slot,
        string projectName,
        WikiPublishedRevision next,
        WikiPublishedRevision? previous)
    {
        slot.Previous = previous;
        slot.Published = next;
        RefreshCache(projectName);
    }

    private void RefreshCache(string projectName)
    {
        try
        {
            _cache?.Invalidate(projectName, WikiContentCache.InvalidationSource.Mutation);
        }
        catch (Exception ex)
        {
            // The published revision is already online; a rebuild problem must
            // not roll it back. The next reader pays a cold fill instead.
            _logger.LogWarning(ex, "wiki-publication-cache-refresh-failed project={Project}", projectName);
        }
    }

    private bool IsWatchedProject(string projectName) =>
        _scanner.GetWatchPaths().Any(entry =>
            string.Equals(entry.Name, projectName, StringComparison.OrdinalIgnoreCase));

    private static bool SnapshotUsable(WikiPublishedRevision? revision) =>
        revision != null
        && !string.IsNullOrWhiteSpace(revision.SnapshotRoot)
        && Directory.Exists(Path.Combine(revision.SnapshotRoot, "docs"));

    /// <summary>
    /// Bounds snapshot disk use. The published and previous revisions are never
    /// removed, so the rehearsed rollback always has its tree, and readers that
    /// still hold the previous projection keep reading a complete directory.
    /// </summary>
    private void PruneSnapshots(WikiPublishedRevision current, WikiPublishedRevision? previous)
    {
        try
        {
            var baseDir = Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.SnapshotRoot)));
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) return;

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.Sha };
            if (previous != null) keep.Add(previous.Sha);

            var superseded = Directory.EnumerateDirectories(baseDir)
                .Where(dir => ShaDirectoryPattern.IsMatch(Path.GetFileName(dir)))
                .Where(dir => !keep.Contains(Path.GetFileName(dir)))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .Skip(Math.Max(0, Options.SnapshotRetention - keep.Count))
                .ToList();

            foreach (var dir in superseded)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SilentCatch.Note(ex, "A superseded wiki snapshot could not be removed.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SilentCatch.Note(ex, "Wiki snapshot retention could not enumerate the snapshot folder.");
        }
    }

    private static WikiPublicationOutcome Record(PublicationSlot slot, WikiPublicationOutcome outcome)
    {
        slot.LastOutcome = outcome;
        if (outcome.Status == WikiPublicationStatus.Failed) slot.LastFailure = outcome;
        return outcome;
    }

    private static string Short(string sha) => sha[..Math.Min(8, sha.Length)];

    private sealed class PublicationSlot
    {
        public readonly Lock Gate = new();
        public volatile WikiPublishedRevision? Published;
        public volatile WikiPublishedRevision? Previous;
        public volatile WikiPublicationOutcome? LastOutcome;
        public volatile WikiPublicationOutcome? LastFailure;

        /// <summary>Set by a rollback, cleared only by a forced operator sync.</summary>
        public volatile bool Held;
    }
}
