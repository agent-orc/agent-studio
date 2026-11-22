using System.Text.RegularExpressions;
using AgentStudio.Runtime;

namespace AgentStudio.Deploy;

/// <summary>
/// The running Studio instance as the Production environment of its source
/// project (AGT-3879). Operator scripts report each deploy (start, steps, end)
/// with a deploy reporter token; a release goes live when its server switch,
/// or its whole run, succeeded. Read paths serve the stored records and the
/// <see cref="InstanceDeployFacts"/> that <see cref="Refresh"/> derives from
/// git in the background, so a read request never starts git.
/// </summary>
public sealed partial class InstanceDeployService
{
    public const string EnvironmentId = "studio-instance";
    public const string EnvironmentName = "Production";
    public const int StalledAfterMinutes = 30;
    public const string ReleaseIdPrefix = "instance-";
    private const int RangeWindow = 5000;
    private const int ReachableWindow = 50000;
    private const int MaxComponents = 16;

    private readonly InstanceDeployStore _store;
    private readonly GitService _git;
    private readonly TaskScannerService _scanner;
    private readonly ReleaseCardAttributionSource _attribution;
    private readonly AgentStudio.Projects.ProjectSettingsService? _settings;
    private readonly IConfiguration _configuration;
    private readonly BuildIdentity? _identity;
    private readonly TimeProvider _clock;
    private readonly ILogger<InstanceDeployService> _logger;
    private readonly object _gate = new();
    private readonly object _refreshGate = new();
    private readonly SemaphoreSlim _refreshSignal = new(0, int.MaxValue);
    private InstanceDeployState? _state;
    private InstanceDeployFacts? _facts;
    private (string Commit, HashSet<string> Shas)? _reachable;

    public InstanceDeployService(InstanceDeployStore store, GitService git, TaskScannerService scanner,
        ReleaseCardAttributionSource attribution, IConfiguration configuration, TimeProvider clock,
        ILogger<InstanceDeployService> logger, AgentStudio.Projects.ProjectSettingsService? settings = null,
        BuildIdentity? identity = null)
    {
        _store = store;
        _git = git;
        _scanner = scanner;
        _attribution = attribution;
        _configuration = configuration;
        _clock = clock;
        _logger = logger;
        _settings = settings;
        _identity = identity;
    }

    /// <summary>The project whose repository the instance is built from, once known.</summary>
    public string? SourceProject
    {
        get { lock (_gate) return Configured() ?? State().SourceProject; }
    }

    public bool IsSource(string project)
        => SourceProject is { } source && string.Equals(source, project, StringComparison.OrdinalIgnoreCase);

    // ---- Record API ---------------------------------------------------------

    public DeployOutcome<InstanceDeployRunView> Start(InstanceDeployStartRequest request, string reporter)
    {
        var runId = request.RunId?.Trim() ?? "";
        var commit = request.TargetCommit?.Trim().ToLowerInvariant() ?? "";
        var version = request.Version?.Trim() ?? "";
        if (!RunIdPattern().IsMatch(runId))
            return DeployOutcome<InstanceDeployRunView>.Invalid("runId must be 1 to 80 letters, digits, '-' or '_'.");
        if (!FullCommitPattern().IsMatch(commit))
            return DeployOutcome<InstanceDeployRunView>.Invalid("targetCommit must be a full 40-character commit SHA.");
        if (!VersionPattern().IsMatch(version))
            return DeployOutcome<InstanceDeployRunView>.Invalid("version must be 1 to 80 characters of letters, digits, '.', '_', '+' or '-'.");
        var components = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in request.Components ?? new Dictionary<string, string>())
        {
            if (components.Count >= MaxComponents)
                return DeployOutcome<InstanceDeployRunView>.Invalid($"At most {MaxComponents} components.");
            if (!ComponentPattern().IsMatch(name ?? "") || !VersionPattern().IsMatch(value?.Trim() ?? ""))
                return DeployOutcome<InstanceDeployRunView>.Invalid($"Component '{name}' needs a name of lowercase letters, digits, '.', '_' or '-' and a version.");
            components[name!] = value!.Trim();
        }
        EnsureSourceProject(commit);
        lock (_gate)
        {
            var state = State();
            var now = Now;
            if (state.Runs.FirstOrDefault(item => item.Id == runId) is { } existing)
                return existing.TargetCommit == commit && existing.Version == version
                    ? DeployOutcome<InstanceDeployRunView>.Ok(ToView(existing, state, now))
                    : DeployOutcome<InstanceDeployRunView>.Conflict(
                        $"Run '{runId}' already started for {existing.Version} at {Short(existing.TargetCommit)}.");
            var run = new InstanceDeployRun
            {
                Id = runId, TargetCommit = commit, Version = version, Components = components,
                StartedAtUtc = request.StartedAtUtc?.ToUniversalTime() ?? now, LastReportAtUtc = now,
                ReportedBy = reporter,
            };
            var releases = state.Releases.ToList();
            if (FindRelease(releases, commit) is null)
                releases.Add(new InstanceRelease
                {
                    Id = ReleaseId(commit, version), Version = version, CommitSha = commit, CreatedAtUtc = now,
                    Components = components, RunId = runId,
                });
            var next = state with { Releases = releases, Runs = [.. state.Runs, run] };
            Save(next);
            return DeployOutcome<InstanceDeployRunView>.Ok(ToView(run, next, now));
        }
    }

    public DeployOutcome<InstanceDeployRunView> ReportStep(string runId, string stepName, InstanceDeployStepRequest request)
    {
        var name = stepName?.Trim() ?? "";
        if (!StepPattern().IsMatch(name))
            return DeployOutcome<InstanceDeployRunView>.Invalid("Step names are 1 to 80 lowercase letters, digits, ':', '.', '_' or '-', such as build:server or host:runner-1.");
        var state = request.State?.Trim().ToLowerInvariant();
        if (!InstanceDeployStates.IsStepState(state))
            return DeployOutcome<InstanceDeployRunView>.Invalid("state must be running, succeeded, failed or skipped.");
        var kind = string.IsNullOrWhiteSpace(request.Kind) ? InstanceDeployStepKinds.FromName(name) : request.Kind.Trim();
        if (!InstanceDeployStepKinds.IsKnown(kind))
            return DeployOutcome<InstanceDeployRunView>.Invalid("kind must be build, server-switch, host, review-host or other.");
        if (Message(request.Message) is { } invalid) return DeployOutcome<InstanceDeployRunView>.Invalid(invalid);
        InstanceRelease? wentLive = null;
        DeployOutcome<InstanceDeployRunView> outcome;
        lock (_gate)
        {
            var current = State();
            var now = Now;
            var run = current.Runs.FirstOrDefault(item => item.Id == runId);
            if (run is null) return DeployOutcome<InstanceDeployRunView>.NotFound($"Unknown deploy run '{runId}'.");
            var existing = run.Steps.FirstOrDefault(item => item.Name == name);
            var step = Merge(existing, name, kind, state!, request, now);
            if (step == existing) return DeployOutcome<InstanceDeployRunView>.Ok(ToView(run, current, now));
            var steps = run.Steps.Where(item => item.Name != name).Append(step)
                .OrderBy(item => item.StartedAtUtc ?? item.ReportedAtUtc).ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToList();
            var updated = run with { Steps = steps, LastReportAtUtc = now };
            var next = Replace(current, updated);
            if (step.Kind == InstanceDeployStepKinds.ServerSwitch && step.State == InstanceDeployStates.Succeeded)
                (next, wentLive) = GoLive(next, updated, step.FinishedAtUtc ?? now);
            Save(next);
            outcome = DeployOutcome<InstanceDeployRunView>.Ok(ToView(updated, next, now));
        }
        AfterLive(wentLive);
        return outcome;
    }

    public DeployOutcome<InstanceDeployRunView> Finish(string runId, InstanceDeployFinishRequest request)
    {
        var outcomeState = request.Outcome?.Trim().ToLowerInvariant();
        if (outcomeState is not (InstanceDeployStates.Succeeded or InstanceDeployStates.Failed))
            return DeployOutcome<InstanceDeployRunView>.Invalid("outcome must be succeeded or failed.");
        if (Message(request.Message) is { } invalid) return DeployOutcome<InstanceDeployRunView>.Invalid(invalid);
        InstanceRelease? wentLive = null;
        DeployOutcome<InstanceDeployRunView> result;
        lock (_gate)
        {
            var current = State();
            var now = Now;
            var run = current.Runs.FirstOrDefault(item => item.Id == runId);
            if (run is null) return DeployOutcome<InstanceDeployRunView>.NotFound($"Unknown deploy run '{runId}'.");
            if (run.Outcome is not null)
                return run.Outcome == outcomeState
                    ? DeployOutcome<InstanceDeployRunView>.Ok(ToView(run, current, now))
                    : DeployOutcome<InstanceDeployRunView>.Conflict($"Run '{runId}' already ended {run.Outcome}.");
            var updated = run with
            {
                Outcome = outcomeState, FinishedAtUtc = request.FinishedAtUtc?.ToUniversalTime() ?? now,
                LastReportAtUtc = now, Message = Clean(request.Message) ?? run.Message,
            };
            var next = Replace(current, updated);
            if (outcomeState == InstanceDeployStates.Succeeded)
                (next, wentLive) = GoLive(next, updated, updated.FinishedAtUtc!.Value);
            Save(next);
            result = DeployOutcome<InstanceDeployRunView>.Ok(ToView(updated, next, now));
        }
        AfterLive(wentLive);
        return result;
    }

    // ---- First start --------------------------------------------------------

    /// <summary>
    /// Records the running version (the version route's build identity) as the
    /// live release when no instance release exists yet. Older history is not
    /// backfilled, so that release brings no cards. Returns whether it recorded.
    /// </summary>
    public bool RecordRunningVersionIfEmpty()
    {
        if (_identity is not { Legacy: false } identity || !ShortCommitPattern().IsMatch(identity.Commit ?? "")
            || !VersionPattern().IsMatch(identity.Version ?? "")) return false;
        lock (_gate)
        {
            if (State().Releases.Count > 0 || State().Runs.Count > 0) return false;
        }
        var commit = identity.Commit.ToLowerInvariant();
        EnsureSourceProject(commit);
        if (SourceProject is { } project && _git.ResolveRepoRootForProject(project) is { } repo
            && _git.GetBranchTip(repo, commit) is { Length: 40 } full)
            commit = full.ToLowerInvariant();
        lock (_gate)
        {
            var state = State();
            if (state.Releases.Count > 0 || state.Runs.Count > 0) return false;
            var now = Now;
            var at = identity.BuiltAt?.UtcDateTime is { } built && built <= now ? built : now;
            var runId = "version-route-" + Short(commit);
            var run = new InstanceDeployRun
            {
                Id = runId, TargetCommit = commit, Version = identity.Version, StartedAtUtc = at,
                LastReportAtUtc = at, FinishedAtUtc = at, Outcome = InstanceDeployStates.Succeeded,
                ReportedBy = "version-route", FromVersionRoute = true,
                Message = "Recorded from the version route on first start; earlier deploys are not recorded.",
            };
            var release = new InstanceRelease
            {
                Id = ReleaseId(commit, identity.Version), Version = identity.Version, CommitSha = commit,
                CreatedAtUtc = at, LiveSinceUtc = at, RunId = runId, CardsResolved = true, FromVersionRoute = true,
            };
            Save(state with { Releases = [release], Runs = [run] });
            _logger.LogInformation("Instance deploy: recorded running version {Version} at {Commit} as the live release",
                identity.Version, Short(commit));
            return true;
        }
    }

    // ---- Background facts ---------------------------------------------------

    /// <summary>Asks the background worker for an early refresh.</summary>
    public void RequestRefresh()
    {
        // A refresh already waits; one pending signal is enough.
        if (_refreshSignal.CurrentCount == 0) _refreshSignal.Release();
    }

    internal Task<bool> WaitForRefreshRequest(TimeSpan timeout, CancellationToken token)
        => _refreshSignal.WaitAsync(timeout, token);

    /// <summary>
    /// Resolves the cards of live releases that do not have them yet, then the
    /// cards integrated after the live commit, in landing order, and which
    /// cards the live commit contains. Runs git; never call it from a read.
    /// </summary>
    public InstanceDeployFacts? Refresh()
    {
        lock (_refreshGate)
        {
            if (SourceProject is null && _identity is { Legacy: false } identity
                && ShortCommitPattern().IsMatch(identity.Commit ?? ""))
                EnsureSourceProject(identity.Commit.ToLowerInvariant());
            var project = SourceProject;
            if (project is null) return null;
            var repo = _git.ResolveRepoRootForProject(project);
            if (repo is null) return null;
            var cards = _attribution.IntegratedCards(project);
            ResolveReleaseCards(repo, cards);
            InstanceDeployState state;
            lock (_gate) state = State();
            var live = LiveReleases(state);
            var current = live.LastOrDefault();
            var developRef = _git.ResolveIntegrationReadRef(repo, _settings?.Get(project).IntegrationBranch);
            var developTip = string.IsNullOrWhiteSpace(developRef) ? null : _git.GetBranchTip(repo, developRef);
            var released = live.SelectMany(item => item.CardKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<InstanceCardView> pending = [];
            if (current is not null && developTip is not null)
            {
                var range = _git.ListRangeCommits(repo, developTip, current.CommitSha, RangeWindow);
                pending = InstanceRangeCards.Map(range, developTip, cards, released)
                    .Select(item => new InstanceCardView(item.Key, item.Title, item.LandedSha)).ToArray();
            }
            var liveCards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var release in live)
                foreach (var key in release.CardKeys)
                    liveCards.TryAdd(key, release.Id);
            if (current is not null)
            {
                var reachable = Reachable(repo, current.CommitSha);
                var waiting = pending.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var card in cards.Where(card => !waiting.Contains(card.Key)
                             && card.Shas.Any(reachable.Contains)))
                    liveCards.TryAdd(card.Key, live[0].Id);
            }
            var facts = new InstanceDeployFacts(project, developRef, developTip, current?.CommitSha, pending,
                liveCards, Now);
            lock (_gate) _facts = facts;
            return facts;
        }
    }

    public InstanceDeployFacts? Facts
    {
        get { lock (_gate) return _facts; }
    }

    // ---- Reads --------------------------------------------------------------

    public InstanceDeployView? View(string project)
    {
        if (!IsSource(project)) return null;
        lock (_gate)
        {
            var state = State();
            var now = Now;
            var facts = FactsFor(project);
            var live = LiveReleases(state).LastOrDefault();
            var latest = state.Runs.OrderBy(item => item.StartedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal)
                .LastOrDefault();
            return new InstanceDeployView(SourceProject!, EnvironmentId, EnvironmentName,
                live is null ? null : ToView(live), latest is null ? null : ToView(latest, state, now),
                facts?.NotLiveYet ?? [], facts?.DevelopRef, facts?.DevelopCommit, facts?.ComputedAtUtc,
                StalledAfterMinutes);
        }
    }

    /// <summary>Cards of releases that went live in the window, the flow counter <c>deployed</c>; null without a source project.</summary>
    public int? CardsWentLive(DateTime fromUtc, DateTime toUtc)
    {
        if (SourceProject is null) return null;
        lock (_gate)
            return LiveReleases(State()).Where(item => item.LiveSinceUtc >= fromUtc && item.LiveSinceUtc <= toUtc)
                .SelectMany(item => item.CardKeys).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    /// <summary>The instance as deploy records for <see cref="DeployService"/>; null for any other project.</summary>
    internal InstanceDeployOverlay? Overlay(string project)
    {
        if (!IsSource(project)) return null;
        lock (_gate)
        {
            var state = State();
            var now = Now;
            var facts = FactsFor(project);
            var environment = new DeployEnvironment(EnvironmentId, SourceProject!, EnvironmentName, 0,
                EnvironmentKinds.Production) { Protected = true, Automatic = false };
            var records = state.Releases.Select(item => new ReleaseRecord(ToRelease(item, SourceProject!), [],
                item.CardTitles)).ToArray();
            var runs = state.Runs.Select(item => ToRecord(item, state, now)).ToArray();
            var live = LiveReleases(state);
            var latest = state.Runs.OrderBy(item => item.StartedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal)
                .LastOrDefault();
            var health = latest is null ? "unknown" : RunState(latest, now) switch
            {
                InstanceDeployStates.Succeeded => "healthy",
                InstanceDeployStates.Failed or InstanceDeployStates.Stalled => "failed",
                _ => live.Count > 0 ? "healthy" : "unknown",
            };
            var liveSince = live.ToDictionary(item => item.Id, item => item.LiveSinceUtc!.Value, StringComparer.Ordinal);
            return new InstanceDeployOverlay(environment, records, runs, health,
                live.Select(item => ToRelease(item, SourceProject!)).ToArray(),
                facts?.NotLiveYet, facts?.LiveCardReleases ?? new Dictionary<string, string>(),
                liveSince, state.Runs.ToDictionary(item => item.Id, item => (IReadOnlyList<InstanceDeployStep>)item.Steps,
                    StringComparer.Ordinal))
            { LatestRun = latest is null ? null : ToView(latest, state, now) };
        }
    }

    // ---- Internals ----------------------------------------------------------

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private string? Configured() => _configuration["InstanceDeploy:SourceProject"] is { Length: > 0 } value
        ? value.Trim() : null;

    private InstanceDeployState State() => _state ??= _store.Read();

    private void Save(InstanceDeployState state)
    {
        _store.Save(state);
        _state = state;
    }

    private InstanceDeployFacts? FactsFor(string project)
        => _facts is { } facts && string.Equals(facts.Project, project, StringComparison.OrdinalIgnoreCase) ? facts : null;

    /// <summary>
    /// The configured source project, or the first project whose repository
    /// holds the commit. Stored, so reads know it without git.
    /// </summary>
    private void EnsureSourceProject(string commit)
    {
        if (SourceProject is not null) return;
        var match = _scanner.GetWatchPaths().Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(name => _git.ResolveRepoRootForProject(name) is { } repo
                && _git.TryIsAncestor(repo, commit, commit) == true);
        if (match is null) return;
        lock (_gate)
        {
            if (State().SourceProject is not null) return;
            Save(State() with { SourceProject = match });
        }
        _logger.LogInformation("Instance deploy: source project is {Project}", match);
    }

    private (InstanceDeployState State, InstanceRelease? WentLive) GoLive(InstanceDeployState state,
        InstanceDeployRun run, DateTime at)
    {
        var release = FindRelease(state.Releases, run.TargetCommit);
        if (release?.LiveSinceUtc is not null) return (state, null);
        var live = release is null
            ? new InstanceRelease
            {
                Id = ReleaseId(run.TargetCommit, run.Version), Version = run.Version, CommitSha = run.TargetCommit,
                CreatedAtUtc = run.StartedAtUtc, Components = run.Components, RunId = run.Id, LiveSinceUtc = at,
            }
            : release with { LiveSinceUtc = at, RunId = run.Id, CardsResolved = false };
        var releases = state.Releases.Where(item => item.Id != live.Id).Append(live).ToList();
        return (state with { Releases = releases }, live);
    }

    /// <summary>A write that took a release live resolves its cards now and asks for fresh facts.</summary>
    private void AfterLive(InstanceRelease? release)
    {
        if (release is null) return;
        try
        {
            if (SourceProject is { } project && _git.ResolveRepoRootForProject(project) is { } repo)
                ResolveReleaseCards(repo, _attribution.IntegratedCards(project));
        }
        catch (Exception exception) when (exception is ReleaseAttributionUnavailableException or IOException)
        {
            _logger.LogWarning(exception, "Instance deploy: cards of release {Version} wait for the next refresh",
                release.Version);
        }
        RequestRefresh();
    }

    private void ResolveReleaseCards(string repo, IReadOnlyList<IntegratedCardRecord> cards)
    {
        InstanceDeployState state;
        lock (_gate) state = State();
        var live = LiveReleases(state);
        var resolved = new Dictionary<string, InstanceRelease>(StringComparer.Ordinal);
        var prior = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < live.Count; index++)
        {
            var release = live[index];
            if (!release.CardsResolved)
            {
                var previous = index == 0 ? null : live[index - 1];
                if (previous is null)
                    resolved[release.Id] = release with { CardsResolved = true };
                else if (_git.TryIsAncestor(repo, release.CommitSha, release.CommitSha) == true)
                {
                    var range = _git.ListRangeCommits(repo, release.CommitSha, previous.CommitSha, RangeWindow);
                    var landed = InstanceRangeCards.Map(range, release.CommitSha, cards, prior);
                    resolved[release.Id] = release with
                    {
                        CardsResolved = true, CardKeys = landed.Select(item => item.Key).ToList(),
                        CardTitles = landed.ToDictionary(item => item.Key, item => item.Title, StringComparer.OrdinalIgnoreCase),
                    };
                }
            }
            foreach (var key in (resolved.GetValueOrDefault(release.Id) ?? release).CardKeys) prior.Add(key);
        }
        if (resolved.Count == 0) return;
        lock (_gate)
        {
            var current = State();
            Save(current with
            {
                Releases = current.Releases.Select(item => resolved.TryGetValue(item.Id, out var update)
                    && item.LiveSinceUtc == update.LiveSinceUtc ? update : item).ToList(),
            });
        }
    }

    private HashSet<string> Reachable(string repo, string commit)
    {
        if (_reachable is { } cached && cached.Commit == commit) return cached.Shas;
        var shas = _git.ListRevisions(repo, commit, null, ReachableWindow).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _reachable = (commit, shas);
        return shas;
    }

    private static List<InstanceRelease> LiveReleases(InstanceDeployState state)
        => state.Releases.Where(item => item.LiveSinceUtc is not null)
            .OrderBy(item => item.LiveSinceUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();

    private static InstanceRelease? FindRelease(IEnumerable<InstanceRelease> releases, string commit)
        => releases.FirstOrDefault(item => SameCommit(item.CommitSha, commit));

    private static bool SameCommit(string left, string right)
        => left.Length >= 7 && right.Length >= 7
           && (left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
               || right.StartsWith(left, StringComparison.OrdinalIgnoreCase));

    private static InstanceDeployState Replace(InstanceDeployState state, InstanceDeployRun run)
        => state with { Runs = state.Runs.Select(item => item.Id == run.Id ? run : item).ToList() };

    /// <summary>
    /// The same report twice is a no-op. A running report never reopens a
    /// finished step; a finished report replaces the step, so a retried step
    /// that now succeeded clears its failure. The first start time stays.
    /// </summary>
    private static InstanceDeployStep Merge(InstanceDeployStep? existing, string name, string kind, string state,
        InstanceDeployStepRequest request, DateTime now)
    {
        var message = Clean(request.Message);
        if (existing is not null)
        {
            if (state == InstanceDeployStates.Running && InstanceDeployStates.IsTerminal(existing.State)) return existing;
            var startedAt = existing.StartedAtUtc ?? request.StartedAtUtc?.ToUniversalTime() ?? now;
            DateTime? finishedAt = InstanceDeployStates.IsTerminal(state)
                ? request.FinishedAtUtc?.ToUniversalTime()
                  ?? (existing.State == state ? existing.FinishedAtUtc : null) ?? now
                : null;
            var candidate = existing with
            {
                Kind = kind, State = state, StartedAtUtc = startedAt, FinishedAtUtc = finishedAt,
                Message = message ?? existing.Message,
            };
            return candidate with { ReportedAtUtc = existing.ReportedAtUtc } == existing
                ? existing : candidate with { ReportedAtUtc = now };
        }
        return new InstanceDeployStep
        {
            Name = name, Kind = kind, State = state,
            StartedAtUtc = request.StartedAtUtc?.ToUniversalTime() ?? now,
            FinishedAtUtc = InstanceDeployStates.IsTerminal(state) ? request.FinishedAtUtc?.ToUniversalTime() ?? now : null,
            Message = message, ReportedAtUtc = now,
        };
    }

    /// <summary>Ended runs keep their outcome; a failed step fails an open run; silence for 30 minutes stalls it.</summary>
    public static string RunState(InstanceDeployRun run, DateTime now)
    {
        if (run.Outcome is { } outcome) return outcome;
        if (run.Steps.Any(item => item.State == InstanceDeployStates.Failed)) return InstanceDeployStates.Failed;
        return now - run.LastReportAtUtc >= TimeSpan.FromMinutes(StalledAfterMinutes)
            ? InstanceDeployStates.Stalled : InstanceDeployStates.Running;
    }

    private static string? LastStep(InstanceDeployRun run)
        => run.Steps.OrderBy(item => item.ReportedAtUtc).ThenBy(item => item.FinishedAtUtc ?? item.StartedAtUtc)
            .LastOrDefault()?.Name;

    private static string? FailedStep(InstanceDeployRun run)
        => run.Steps.Where(item => item.State == InstanceDeployStates.Failed)
            .OrderBy(item => item.ReportedAtUtc).LastOrDefault()?.Name;

    private static InstanceDeployRunView ToView(InstanceDeployRun run, InstanceDeployState state, DateTime now)
        => new(run.Id, FindRelease(state.Releases, run.TargetCommit)?.Id ?? ReleaseId(run.TargetCommit, run.Version),
            run.Version, run.TargetCommit, RunState(run, now), run.StartedAtUtc, run.FinishedAtUtc, run.LastReportAtUtc,
            LastStep(run), FailedStep(run), run.Components, run.Steps, run.FromVersionRoute, run.Message);

    private static InstanceReleaseView ToView(InstanceRelease release)
        => new(release.Id, release.Version, release.CommitSha, release.LiveSinceUtc,
            release.CardKeys.Select(key => new InstanceCardView(key, release.CardTitles.GetValueOrDefault(key), null)).ToArray(),
            release.CardsResolved, release.FromVersionRoute, release.Components);

    private static Release ToRelease(InstanceRelease release, string project)
        => new(release.Id, project, release.Version, release.CommitSha,
            release.Components.Select(item => item.Key + "@" + item.Value).ToArray(), "instance", "instance",
            release.CardKeys, release.LiveSinceUtc ?? release.CreatedAtUtc, ReleaseSnapshot.DefaultChannel);

    /// <summary>The generic deploy contract has no stalled state: a stalled run reads as failed at its last step.</summary>
    private static DeploymentRecord ToRecord(InstanceDeployRun run, InstanceDeployState state, DateTime now)
    {
        var runState = RunState(run, now);
        var release = FindRelease(state.Releases, run.TargetCommit);
        var contractState = runState switch
        {
            InstanceDeployStates.Succeeded => DeploymentStates.Succeeded,
            InstanceDeployStates.Running => DeploymentStates.Running,
            _ => DeploymentStates.Failed,
        };
        return new DeploymentRecord
        {
            Id = run.Id, ReleaseId = release?.Id ?? ReleaseId(run.TargetCommit, run.Version),
            EnvironmentId = EnvironmentId, State = contractState, Trigger = DeploymentTriggers.Deploy,
            DeployStep = "instance", RequestedBy = run.ReportedBy, CreatedAtUtc = run.StartedAtUtc,
            StartedAtUtc = run.StartedAtUtc,
            FinishedAtUtc = run.FinishedAtUtc ?? (runState == InstanceDeployStates.Running ? null : run.LastReportAtUtc),
            FailedStep = runState is InstanceDeployStates.Failed or InstanceDeployStates.Stalled
                ? FailedStep(run) ?? LastStep(run) : null,
            Steps = run.Steps.Select(item => new DeploymentStepRecord(item.Name,
                item.State == InstanceDeployStates.Skipped ? DeploymentStepStates.Skipped : item.State,
                item.StartedAtUtc, item.FinishedAtUtc)).ToList(),
        };
    }

    private static string ReleaseId(string commit, string version) => ReleaseIdPrefix + DeployIds.For(commit + "|" + version);

    private static string Short(string commit) => commit.Length > 9 ? commit[..9] : commit;

    private static string? Message(string? message)
        => message is { Length: > 512 } ? "message must be at most 512 characters." : null;

    private static string? Clean(string? message)
        => string.IsNullOrWhiteSpace(message) ? null
            : new string(message.Trim().Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$")]
    private static partial Regex RunIdPattern();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex FullCommitPattern();

    [GeneratedRegex("^[0-9a-fA-F]{7,40}$")]
    private static partial Regex ShortCommitPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,79}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,39}$")]
    private static partial Regex ComponentPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9:._-]{0,79}$")]
    private static partial Regex StepPattern();
}

/// <param name="LiveReleases">Releases that went live, oldest first.</param>
/// <param name="NotLiveYet">Null until the background refresh computed the facts.</param>
/// <param name="LiveCardReleases">Card key to the live release that carries it.</param>
/// <param name="LiveSince">Live release id to the time it went live.</param>
internal sealed record InstanceDeployOverlay(DeployEnvironment Environment, IReadOnlyList<ReleaseRecord> Records,
    IReadOnlyList<DeploymentRecord> Runs, string Health, IReadOnlyList<Release> LiveReleases,
    IReadOnlyList<InstanceCardView>? NotLiveYet, IReadOnlyDictionary<string, string> LiveCardReleases,
    IReadOnlyDictionary<string, DateTime> LiveSince, IReadOnlyDictionary<string, IReadOnlyList<InstanceDeployStep>> Steps)
{
    /// <summary>
    /// The release whose server switch, or whole run, went through last. A run
    /// that failed at a host after the switch keeps its failed state; the
    /// server already runs its release.
    /// </summary>
    public Release? LiveRelease => LiveReleases.Count == 0 ? null : LiveReleases[^1];

    /// <summary>The latest reported run with its derived state, stalled included.</summary>
    public InstanceDeployRunView? LatestRun { get; init; }

    /// <summary>
    /// The server switch decides what runs. A run that failed before its switch
    /// left the live release in place, and one that failed after it already
    /// took its release live. Only a latest run that failed or stalled with
    /// its switch reported but not succeeded leaves the running release open.
    /// </summary>
    public bool ReleaseUncertain => LatestRun is { State: InstanceDeployStates.Failed or InstanceDeployStates.Stalled } run
        && run.Steps.Any(step => step.Kind == InstanceDeployStepKinds.ServerSwitch
            && step.State is InstanceDeployStates.Running or InstanceDeployStates.Failed);
}
