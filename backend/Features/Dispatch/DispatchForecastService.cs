using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Projects;

namespace AgentStudio.Dispatch;

/// <summary>Collects Git and card facts off the request path. Never orders or admits cards.</summary>
public sealed class DispatchForecastService(TaskScannerService scanner, TaskIndexCache tasks,
    TaskChangeNotifier changes, TaskWatcherService watcher, ProjectSettingsService settings, GitService git, DispatchStore store,
    ILogger<DispatchForecastService> logger) : BackgroundService
{
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly ConcurrentDictionary<string, bool> _requested = new(StringComparer.Ordinal);
    private int _reconcile = 1;
    private DateTime _lastRecoveryDate;
    private void Wake() => _wake.Writer.TryWrite(true);
    public void Refresh()
    {
        Interlocked.Exchange(ref _reconcile, 1);
        Wake();
    }
    internal void RequestCardRefresh(string watchPath, string jobId, bool enteredReady = false)
    {
        _requested.AddOrUpdate(TaskIdentity.CreateKey(watchPath, jobId), enteredReady,
            (_, previous) => previous || enteredReady);
        Wake();
    }

    // Drain before capturing the publication. Events arriving during collection stay
    // queued for the next pass, including a second Ready entry of the same card.
    internal IReadOnlyList<(TaskInfo Card, bool EnteredReady, bool Requested)> CollectRefreshCards(DateTimeOffset now, out bool reconcile)
    {
        reconcile = Interlocked.Exchange(ref _reconcile, 0) != 0;
        reconcile |= _lastRecoveryDate != now.UtcDateTime.Date;
        var requested = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var key in _requested.Keys)
            if (_requested.TryRemove(key, out var ready)) requested[key] = ready;
        if (!reconcile && requested.Count == 0) return [];
        if (reconcile) _lastRecoveryDate = now.UtcDateTime.Date;
        IEnumerable<TaskInfo> cards;
        if (reconcile)
        {
            var partitions = tasks.GetCleanSnapshotPartitions();
            cards = partitions.Live.Concat(partitions.Archive);
        }
        else
        {
            cards = requested.Keys.SelectMany(key => tasks.GetMatchingCoreFacts(key)
                .Where(card => card.TaskKey == key));
        }
        return cards
            .Select(card => (card, requested.GetValueOrDefault(card.TaskKey), requested.ContainsKey(card.TaskKey))).ToArray();
    }
    private void Changed(TaskChangeEvent change) => RequestCardRefresh(change.WatchPath, change.JobId);
    private void Moved(TaskMoveEvent change) => RequestCardRefresh(change.WatchPath, change.JobId,
        change.ToState == TaskStates.Ready);

    internal void PathChanged(string path)
    {
        var name = Path.GetFileName(path);
        if (name is not ("prompt.md" or ReviewSubjectStore.FileName)) return;
        var folder = Path.GetDirectoryName(path);
        if (folder is null) return;
        if (name == ReviewSubjectStore.FileName)
        {
            folder = Path.GetDirectoryName(folder);
            if (folder is null) return;
        }
        var card = tasks.GetResidentTaskByFolder(folder);
        if (card is not null) RequestCardRefresh(card.WatchPath, card.Id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        changes.TaskCreated += Changed;
        changes.TaskUpdated += Changed;
        changes.TaskMoved += Moved;
        changes.JobsBulkChanged += Refresh;
        watcher.OnPathChanged += PathChanged;
        using var timer = new Timer(_ => Wake(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        try
        {
            // Startup must not block a worker on the first workspace scan.
            // Keep subscribed events queued until that publication is ready.
            await tasks.WaitForFirstPublicationAsync(stoppingToken).ConfigureAwait(false);
            await foreach (var signal in _wake.Reader.ReadAllAsync(stoppingToken))
            {
                var now = DateTimeOffset.UtcNow;
                var repositories = new DispatchRepositoryRefresh(store, logger);
                var cards = CollectRefreshCards(now, out var reconcile);
                // Unavailable projects also need probes when their Ready lane is empty.
                foreach (var project in scanner.GetWatchPaths())
                {
                    var projectCards = cards.Where(entry => entry.Card.ProjectName.Equals(project.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (projectCards.Length == 0 && !reconcile && store.ReadRepositoryState(project.Name) is not { Available: false }) continue;
                        var repository = repositories.Probe(project.Name, now, () =>
                        {
                            var root = git.ResolveRepositoryRoot(project);
                            if (root is null) return null;
                            var branch = TaskIntegrationBranch.Name(settings.Get(project.Name).IntegrationBranch);
                            var sha = TryRun(root, stoppingToken, "rev-parse", "--verify", $"refs/remotes/origin/{branch}^{{commit}}")
                                ?? TryRun(root, stoppingToken, "rev-parse", "--verify", $"refs/heads/{branch}^{{commit}}");
                            return sha is null ? null : (root, sha.Trim());
                        });
                        if (repository is null)
                        {
                            foreach (var (card, enteredReady, _) in projectCards)
                                _requested.AddOrUpdate(card.TaskKey, enteredReady, (_, ready) => ready || enteredReady);
                            continue;
                        }
                        var (repo, tip) = repository.Value;
                        var index = store.IndexAsOf(project.Name, now);
                        if (index is null || index.AsOf.UtcDateTime.Date < now.UtcDateTime.Date)
                        {
                            index = BuildIndex(repo, tip, now, stoppingToken);
                            store.WriteIndex(project.Name, index);
                        }
                        var hubs = settings.Get(project.Name).DispatchHubFiles ?? TouchForecast.DefaultHubFiles;
                        string[]? tracked = null;
                        foreach (var (card, enteredReady, requested) in projectCards)
                        {
                            try
                            {
                                var key = card.TaskKey;
                                var previous = store.Read(project.Name, key);
                                if (previous is not null)
                                {
                                    var subject = ReviewSubjectStore.Read(card.FolderPath);
                                    if (subject is not null && !previous.Deliveries.Any(delivery => delivery.ResultSha == subject.ResultSha))
                                    {
                                        var delivery = CollectDelivery(repo, project.Name, card.Key ?? card.Id,
                                            tip, subject, previous, now, stoppingToken);
                                        previous = previous with { Deliveries = [.. previous.Deliveries, delivery] };
                                        store.Write(project.Name, key, previous);
                                    }
                                }
                                if (card.State == TaskStates.Ready || previous is not null || requested)
                                {
                                    var promptPath = Path.Combine(card.FolderPath, "prompt.md");
                                    var prompt = File.Exists(promptPath) ? File.ReadAllText(promptPath) : "";
                                    var text = card.Title + "\n" + prompt + "\n" + string.Join("\n", card.CitedPaths);
                                    var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                                    if (previous is null || enteredReady || previous.PromptRevision != revision || !previous.HubFiles.SequenceEqual(hubs)
                                        || (card.State == TaskStates.Ready && previous.ReadyEntryAt != card.EnteredLaneAt))
                                    {
                                        // ls-tree reads exactly the integration tip's tracked inventory. The live index
                                        // may belong to a different branch and must not leak files into the forecast.
                                        tracked ??= Run(repo, stoppingToken, "ls-tree", "-r", "--name-only", "-z", tip).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                                        previous = TouchForecast.Compute(text, index with { IntegrationTip = tip, TrackedFiles = tracked }, hubs) with
                                        { PromptRevision = revision, ComputedAt = now, ReadyEntryAt = card.State == TaskStates.Ready ? card.EnteredLaneAt : previous?.ReadyEntryAt ?? default,
                                            Deliveries = previous?.Deliveries ?? [] };
                                        store.Write(project.Name, key, previous);
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
                            {
                                _requested.AddOrUpdate(card.TaskKey, enteredReady, (_, ready) => ready || enteredReady);
                                logger.LogWarning(ex, "Dispatch forecast refresh failed for {Card}; retry on the next refresh", card.TaskKey);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
                    {
                        foreach (var (card, enteredReady, requested) in projectCards)
                            _requested.AddOrUpdate(card.TaskKey, enteredReady, (_, ready) => ready || enteredReady);
                        logger.LogWarning(ex, "Dispatch forecast refresh failed for {Project}; retry on the next refresh", project.Name);
                    }
                }
            }
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        { logger.LogDebug(ex, "Dispatch forecast collector stopped"); }
        finally
        {
            changes.TaskCreated -= Changed;
            changes.TaskUpdated -= Changed;
            changes.TaskMoved -= Moved;
            changes.JobsBulkChanged -= Refresh;
            watcher.OnPathChanged -= PathChanged;
        }
    }

    internal TouchDelivery CollectDelivery(string repo, string project, string card, string integrationTip,
        ReviewSubjectRecord subject, TouchForecastV1 forecast, DateTimeOffset now, CancellationToken token)
    {
        git.TryBackfillDispatchConflicts(repo, project, now, subject.BaseSha, subject.ResultSha, card);
        return ReadDelivery(repo, integrationTip, subject, forecast, now, token);
    }

    internal static TouchDelivery ReadDelivery(string repo, string integrationTip, ReviewSubjectRecord subject,
        TouchForecastV1 forecast, DateTimeOffset now, CancellationToken token)
    {
        var mergeBase = Run(repo, token, "merge-base", integrationTip, subject.ResultSha).Trim();
        // A recovery pass may observe an already integrated result. Never turn it into
        // an empty delivery merely because the integration branch advanced first.
        if (mergeBase.Equals(subject.ResultSha, StringComparison.OrdinalIgnoreCase))
            mergeBase = Run(repo, token, "merge-base", subject.BaseSha ?? forecast.IntegrationTip, subject.ResultSha).Trim();
        var real = Run(repo, token, "diff", "--name-only", "-z", mergeBase, subject.ResultSha, "--")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(TouchForecast.IsCode).Order(StringComparer.Ordinal).ToArray();
        var modules = TouchForecast.Modules(real, forecast.HubFiles);
        return new(subject.ResultSha, mergeBase, now, real, modules,
            TouchForecast.Accuracy(forecast.ForecastFiles.Where(TouchForecast.IsCode), real),
            TouchForecast.Accuracy(forecast.Modules, modules))
        { Forecast = forecast with { Deliveries = [] } };
    }

    internal static CoChangeIndex BuildIndex(string repo, string tip, DateTimeOffset asOf, CancellationToken token)
    {
        var tracked = Run(repo, token, "ls-tree", "-r", "--name-only", "-z", tip).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var history = Run(repo, token, "log", "--no-merges", $"--since={asOf.AddDays(-60):O}", $"--until={asOf:O}",
            "--format=%x00COMMIT:%H%x00", "--name-only", "-z", tip, "--");
        var commits = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        foreach (var item in history.Split('\0'))
        {
            var path = item.TrimStart('\n');
            if (path.StartsWith("COMMIT:", StringComparison.Ordinal))
            {
                if (current.Count > 0) commits.Add(current);
                current = [];
            }
            else if (path.Length > 0) current.Add(path);
        }
        if (current.Count > 0) commits.Add(current);
        return TouchForecast.BuildIndex(tip, asOf, tracked, commits);
    }

    private static string Run(string repo, CancellationToken token, params string[] args) => TryRun(repo, token, args)
        ?? throw new IOException($"Dispatch Git fact collection failed: {args[0]}.");
    private static string? TryRun(string repo, CancellationToken token, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = repo, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var result = GitProcessBoundary.Run(start, timeout: TimeSpan.FromMinutes(2), cancellationToken: token);
        token.ThrowIfCancellationRequested();
        return result.Success ? result.StandardOutput : null;
    }
}
