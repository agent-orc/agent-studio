using System.Text.Json;
using AgentStudio.Runtime;
using AgentStudio.Registry;

namespace AgentStudio.Deploy;

/// <summary>A background-built read model. Reads never spawn Git or write deployment records.</summary>
public sealed class StudioReleasesService(InstanceDeployService instance, InstanceDeployStore deploys,
    ReleaseCardAttributionSource attribution, TaskScannerService scanner, GitService git,
    AgentStudio.Projects.ProjectSettingsService settings, BuildIdentity identity,
    IConfiguration configuration, TimeProvider clock, ProjectRegistry projects)
{
    private StudioReleasesView? _view;
    private const int ReachabilityLimit = 100000;
    private readonly Dictionary<string, HashSet<string>> _reachable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, string), bool> _membership = new();
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
        var revisions = deploys.Read().Releases.Where(release => release.LiveSinceUtc is not null)
            .Select(release => new StudioRevision(release.CommitSha, release.LiveSinceUtc!.Value, release.FromVersionRoute,
                release.FromVersionRoute ? "build-baseline" : "deploy"))
            .ToList();
        // Deploy reports may be absent on older installations. Remember each running
        // revision independently of the deploy scripts and their record protocol.
        var path = Path.Combine(configuration["TaskRepository"]
            ?? throw new InvalidOperationException("TaskRepository is required."), "deployments", "studio-release-observations.json");
        var observed = File.Exists(path)
            ? JsonSerializer.Deserialize<List<StudioRevision>>(File.ReadAllText(path)) ?? [] : [];
        if (!revisions.Concat(observed).Any(revision => SameRevision(revision.Revision, running)))
        {
            observed.Add(new(running, _startedAt, true));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(observed));
            File.Move(temporary, path, true);
        }
        revisions.AddRange(observed);
        revisions = revisions.Select(revision => revision with
            { Revision = git.GetBranchTip(repo, revision.Revision) ?? revision.Revision })
            .GroupBy(revision => revision.Revision, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(revision => revision.TimeBasis == "build-baseline")
                .ThenBy(revision => revision.Observed).ThenBy(revision => revision.AtUtc).First())
            .OrderBy(revision => revision.AtUtc).ThenBy(revision => revision.Revision, StringComparer.Ordinal).ToList();
        var metadata = scanner.ScanAllJobsWithArchive()
            .Where(card => string.Equals(card.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .GroupBy(card => card.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var cards = attribution.IntegratedCards(project).Select(record =>
        {
            // TaskKey includes watch path on some surfaces; release records use the human key.
            var card = metadata.Values.FirstOrDefault(candidate => string.Equals(candidate.Key, record.Key, StringComparison.OrdinalIgnoreCase));
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
        Volatile.Write(ref _view, next);
    }

    public void Unavailable(string detail)
    {
        // Stale containment is unsafe after a rollback or a newly integrated delivery.
        Volatile.Write(ref _view, new(Source ?? "", identity.Commit, clock.GetUtcNow().UtcDateTime,
            [], [], new Dictionary<string, StudioCardLiveState>(), detail));
    }

    private static bool SameRevision(string left, string right)
        => left.Length >= 7 && right.Length >= 7 && (left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left, StringComparison.OrdinalIgnoreCase));
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
