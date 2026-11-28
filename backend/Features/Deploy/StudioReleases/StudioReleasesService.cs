using System.Text.Json;
using AgentStudio.Runtime;
using AgentStudio.Registry;

namespace AgentStudio.Deploy;

/// <summary>A background-built read model. Reads never spawn Git or write deployment records.</summary>
public sealed class StudioReleasesService(InstanceDeployService instance, InstanceDeployStore deploys,
    ReleaseCardAttributionSource attribution, TaskScannerService scanner, GitService git,
    AgentStudio.Projects.ProjectSettingsService settings, BuildIdentity identity,
    IConfiguration configuration, TimeProvider clock, ProjectRegistry projects, AgentStudio.Areas.AreaRegistryService? areas = null)
{
    private StudioReleasesView? _view;
    private const int ReachabilityLimit = 100000;
    private readonly Dictionary<string, HashSet<string>> _reachable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, string), bool> _membership = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _changedPaths = new(StringComparer.Ordinal);
    private string? _source;
    private readonly DateTime _startedAt = clock.GetUtcNow().UtcDateTime;
    public string? Source => Volatile.Read(ref _source) ?? instance.SourceProject;
    public StudioReleasesView? View => Volatile.Read(ref _view);

    public StudioCardLiveState? Card(string project, string key)
        => Source is null ? new("unknown")
            : !IsSource(project) ? null
            : View?.CardStates.GetValueOrDefault(key) ?? new StudioCardLiveState("unknown");

    public bool IsSource(string project)
        => Source is { } source && (string.Equals(source, project, StringComparison.OrdinalIgnoreCase)
            || projects.FindByIdOrDisplayName(source) is { } sourceRecord
                && projects.FindByIdOrDisplayName(project) is { } projectRecord
                && string.Equals(sourceRecord.Id, projectRecord.Id, StringComparison.OrdinalIgnoreCase));

    public void Refresh()
    {
        var project = Source;
        if (project is null) return;
        Volatile.Write(ref _source, project);
        var repo = git.ResolveRepoRootForProject(project);
        if (repo is null) throw new IOException("The Studio source repository is unavailable.");
        var running = git.GetBranchTip(repo, identity.Commit);
        if (running is null) throw new IOException("The running Studio revision is unavailable in the source repository.");
        var branch = git.ResolveIntegrationReadRef(repo, settings.Get(project).IntegrationBranch);
        var target = branch is null ? null : git.GetBranchTip(repo, branch);
        if (target is null) throw new IOException("The integration branch is unavailable.");
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Resolve(string revision)
        {
            if (!resolved.TryGetValue(revision, out var sha))
                resolved[revision] = sha = git.GetBranchTip(repo, revision) ?? revision;
            return sha;
        }
        var revisions = deploys.Read().Releases.Where(release => release.LiveSinceUtc is not null)
            .Select(release => new StudioRevision(Resolve(release.CommitSha), release.LiveSinceUtc!.Value, release.FromVersionRoute,
                release.FromVersionRoute ? "build-baseline" : "deploy", "deploy:" + release.Id))
            .ToList();
        // Deploy reports may be absent or retain only the first deployment of a SHA.
        // Observe transitions, including returns to old revisions, rather than restarts.
        var path = Path.Combine(configuration["TaskRepository"]
            ?? throw new InvalidOperationException("TaskRepository is required."), "deployments", "studio-release-observations.json");
        var observed = File.Exists(path)
            ? JsonSerializer.Deserialize<List<StudioRevision>>(File.ReadAllText(path)) ?? [] : [];
        observed = observed.Select((revision, index) => revision with {
            Revision = Resolve(revision.Revision),
            OccurrenceId = revision.OccurrenceId ?? "observed:legacy:" + index,
        }).ToList();
        revisions = Occurrences(revisions.Concat(observed));
        if (revisions.Count == 0 || !SameRevision(revisions[^1].Revision, running))
        {
            // A report received after process startup must precede a newly observed transition.
            var at = revisions.Count == 0 || _startedAt >= revisions[^1].AtUtc
                ? _startedAt : clock.GetUtcNow().UtcDateTime;
            var observation = new StudioRevision(running, at, true, OccurrenceId: "observed:" + Guid.NewGuid().ToString("N"));
            observed.Add(observation);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(observed));
            File.Move(temporary, path, true);
            revisions.Add(observation);
        }
        var metadata = scanner.ScanAllJobsWithArchive()
            .Where(card => string.Equals(card.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .GroupBy(card => card.Key ?? card.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var cards = attribution.IntegratedCards(project).Select(record =>
        {
            // TaskKey includes watch path on some surfaces; release records use the human key.
            var card = metadata.GetValueOrDefault(record.Key);
            var integratedAt = card?.IntegrationRecords.Where(row => row.RecordedAtUtc != default)
                .OrderByDescending(row => row.RecordedAtUtc).FirstOrDefault()?.RecordedAtUtc;
            if (integratedAt is null && card is not null && !attribution.ReadsTaskServer)
                integratedAt = AgentStudio.Pipeline.IntegrationVerificationStore.Read(card.FolderPath)?.RecordedAtUtc.UtcDateTime;
            if (integratedAt == default(DateTime)) integratedAt = null;
            return new StudioReleaseCard(record.Key, record.Title,
                card is null ? null : card.Mode == TaskModes.Concept ? "concept" : card.TaskType,
                "#/tasks/" + Uri.EscapeDataString(record.Key),
                record.Shas.Count > 1 ? record.Shas.Skip(1).ToArray() : Array.Empty<string>(), integratedAt,
                record.Shas.FirstOrDefault());
        }).GroupBy(card => card.Key, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        bool? Ancestor(string from, string to)
        {
            if (_membership.TryGetValue((from, to), out var cached)) return cached;
            if (!_reachable.TryGetValue(to, out var reachable))
            {
                var shas = git.ListRevisions(repo, to, null, ReachabilityLimit);
                // An incomplete graph cannot support a negative answer.
                if (shas.Count >= ReachabilityLimit || shas.Count == 0) return null;
                if (_reachable.Count >= 8) _reachable.Remove(_reachable.Keys.First());
                _reachable[to] = reachable = shas.ToHashSet(StringComparer.OrdinalIgnoreCase);
                _known.UnionWith(reachable);
            }
            if (reachable.Contains(from)) return _membership[(from, to)] = true;
            if (!_known.Contains(from))
            {
                if (git.TryIsAncestor(repo, from, from) != true) return null;
                _known.Add(from);
            }
            return _membership[(from, to)] = false;
        }
        var next = StudioReleaseProjection.Build(project, running, target, clock.GetUtcNow().UtcDateTime,
            revisions, cards, Ancestor);
        var vocabulary = areas?.List(project) ?? AgentStudio.Areas.AreaTaxonomy.ProductDefaults;
        var needed = next.Pending.SelectMany(DeliveryRevisions).ToHashSet(StringComparer.Ordinal);
        foreach (var sha in _changedPaths.Keys.Where(sha => !needed.Contains(sha)).ToArray()) _changedPaths.Remove(sha);
        foreach (var sha in needed.Where(sha => !_changedPaths.ContainsKey(sha)))
        {
            if (git.GetFirstParent(repo, sha) is { } parent
                && git.ChangedPathsAgainstMergeBase(repo, parent, sha) is { } paths)
                _changedPaths[sha] = paths;
        }
        var waiting = next.Pending.Select(card =>
        {
            var revisions = DeliveryRevisions(card);
            var labels = vocabulary.Where(area => metadata.GetValueOrDefault(card.Key)?.Tags.Contains(area.Id) == true)
                .Select(area => area.Label);
            var flags = revisions.Count > 0 && revisions.All(_changedPaths.ContainsKey)
                ? WaitingDelivery.PathFlags(revisions.SelectMany(sha => _changedPaths[sha]).ToArray()) : null;
            return new WaitingDelivery(card.Key, card.Title, string.Join(" / ", labels), flags, card.IntegratedAtUtc);
        }).ToArray();
        Volatile.Write(ref _view, next with { Branch = branch, Tip = target, Waiting = waiting });
    }

    private static IReadOnlyList<string> DeliveryRevisions(StudioReleaseCard card) =>
        card.Shas.Count > 0 ? card.Shas : card.IntegrationSha is { } sha ? [sha] : [];

    public void Unavailable(string detail)
    {
        // Stale containment is unsafe after a rollback or a newly integrated delivery.
        Volatile.Write(ref _view, new(Source ?? "", identity.Commit, clock.GetUtcNow().UtcDateTime,
            [], [], new Dictionary<string, StudioCardLiveState>(), detail));
    }

    private static bool SameRevision(string left, string right)
        => left.Length >= 7 && right.Length >= 7 && (left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left, StringComparison.OrdinalIgnoreCase));

    private static List<StudioRevision> Occurrences(IEnumerable<StudioRevision> revisions)
    {
        var ordered = revisions.OrderBy(revision => revision.AtUtc).ToArray();
        var occurrences = new List<StudioRevision>();
        for (var start = 0; start < ordered.Length;)
        {
            var end = start + 1;
            while (end < ordered.Length && SameRevision(ordered[start].Revision, ordered[end].Revision)) end++;
            var interval = ordered[start..end];
            var reports = interval.Where(revision => revision.TimeBasis == "deploy").ToArray();
            // Reports and observations of the same uninterrupted revision describe
            // one interval. Explicit deployments still retain their own identities.
            if (reports.Length > 0) occurrences.AddRange(reports);
            else occurrences.Add(interval.FirstOrDefault(revision => revision.TimeBasis != "build-baseline") ?? interval[0]);
            start = end;
        }
        return occurrences;
    }
}

public sealed class StudioReleasesWorker(StudioReleasesService releases, ILogger<StudioReleasesWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { releases.Refresh(); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                releases.Unavailable("Release facts are unavailable. The background refresh will retry.");
                logger.LogWarning(exception, "Studio release read model could not be refreshed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
