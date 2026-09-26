using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading.Channels;
using AgentStudio.Areas;
using AgentStudio.Prompts;
using AgentStudio.Projects;
using AgentStudio.Runner;

namespace AgentStudio.Tags;

public sealed record AutoTagResult(string Kind, string Id, string Status, string[] Tags,
    double Confidence, DateTimeOffset At)
{
    public string[] AreaTags { get; init; } = [];
    public string[] FacetTags { get; init; } = [];
    public string ThinkingLevel { get; init; } = "low";
}
public sealed record AutoTagReport(string Project, bool Applied, int Eligible, int AlreadyTagged, int Classified,
    Dictionary<string, int> CountsPerArea, List<AutoTagResult> LowConfidence,
    TagGoldenSetReport GoldenSet, List<AutoTagResult> Items);
public sealed record AutoTagBackfillJob(string Id, string Project, bool Apply, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt = null, string? Error = null);

public static class AutoTaggingPolicy
{
    public const double ConfidenceThreshold = 0.8;
    public static bool EligibleCard(TaskInfo task) => !task.Fixture && task.State != TaskStates.Archive;
    public static bool IsValid(TagClassificationPrediction prediction, TagMaintenanceItem item,
        IReadOnlySet<string> allowed, IReadOnlySet<string> areas) =>
        prediction.Kind == item.Kind && prediction.Id == item.Id &&
        prediction.Confidence is >= 0 and <= 1 &&
        prediction.Tags.Length is >= 1 and <= 8 &&
        prediction.Tags.Distinct(StringComparer.Ordinal).Count() == prediction.Tags.Length &&
        prediction.Tags.All(allowed.Contains) && prediction.Tags.Any(areas.Contains);

    public static string Status(double confidence) =>
        confidence >= ConfidenceThreshold ? "tagged" : "tags-proposed";
}

public interface IAutoTagClassifier
{
    Task<TagClassificationPrediction> ClassifyAsync(string project, TagMaintenanceItem item,
        TagMaintenanceSnapshot snapshot, string thinkingLevel, CancellationToken ct);
}

/// <summary>A separate classification route, never routed through coding task qualification.</summary>
public sealed class AutoTagClassifier(CliOneShotRegistry oneShots, RuntimePromptService prompts,
    AreaRegistryService areas) : IAutoTagClassifier
{
    public const string StepId = AgentStudio.Pipeline.PipelineCatalogue.AutoTagStepId;
    public const int MaxPromptCharacters = 16000;
    public const int MaxResponseCharacters = 2048;
    public const int MaxOutputTokens = 512;
    public async Task<TagClassificationPrediction> ClassifyAsync(string project, TagMaintenanceItem item,
        TagMaintenanceSnapshot snapshot, string thinkingLevel, CancellationToken ct)
    {
        var model = ModelFamilyResolver.Resolve(ModelFamilies.ClaudeSonnet);
        var registry = areas.EffectiveTags(project);
        var areaIds = areas.List(project).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var allowed = registry.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var input = TagMaintenancePolicy.Encode(new
        {
            item = new { item.Kind, item.Id, item.Title, body = item.Text[..Math.Min(item.Text.Length, 4000)] },
            registry = registry.Select(t => new { t.Id, t.Label, t.Description, t.Kind }),
            glossaries = snapshot.Glossaries.ToDictionary(x => x.Key,
                x => x.Value.Take(20).Select(t => new { t.Term, t.Definition, t.Synonyms })),
        });
        var prompt = prompts.Render("auto-tag.md", new Dictionary<string, string?> { ["input"] = input },
            new PromptCallContext(project, StepId, model));
        if (prompt.Length > MaxPromptCharacters)
            throw new InvalidOperationException("Auto-tag context exceeds the prompt cap.");
        var cli = oneShots.Get(CliTypes.Claude)
            ?? throw new InvalidOperationException("Sonnet classification CLI unavailable.");
        var result = await cli.RunAsync(new(CliTypes.Claude, model, prompt)
        {
            ThinkingLevel = thinkingLevel, Timeout = TimeSpan.FromMinutes(2),
            Project = project, JobId = item.Kind == "card" ? item.Id : null,
            Source = StepId, StepId = StepId,
            ExtraArgs = ["--tools", "", "--max-budget-usd", "0.10"],
        }, ct);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "Auto-tag classification failed.");
        if (result.Usage?.OutputTokens > MaxOutputTokens)
            throw new InvalidOperationException("Auto-tag response exceeds the token cap.");
        if (result.ParsedText.Length > MaxResponseCharacters)
            throw new InvalidOperationException("Auto-tag response exceeds the output cap.");
        var prediction = JsonSerializer.Deserialize<TagClassificationPrediction>(result.ParsedText,
            TagMaintenancePolicy.Json) ?? throw new InvalidOperationException("Auto-tag response is empty.");
        if (!AutoTaggingPolicy.IsValid(prediction, item, allowed, areaIds))
            throw new InvalidOperationException("Auto-tag response violates the closed registry or confidence contract.");
        return prediction;
    }
}

internal static class ProposedGlossaryContext
{
    public static TagMaintenanceSnapshot Apply(string project, TagMaintenanceSnapshot snapshot)
    {
        if (!string.Equals(project, "Agent Studio", StringComparison.OrdinalIgnoreCase)) return snapshot;
        string? path = null;
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "quality", "tagging-golden-set", "glossaries.json");
            if (File.Exists(candidate)) { path = candidate; break; }
        }
        if (path == null) return snapshot;
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var source = json.RootElement.GetProperty("areas");
        var merged = snapshot.Glossaries.ToDictionary(x => x.Key, x => x.Value.ToList());
        foreach (var area in source.EnumerateObject())
        {
            if (!merged.TryGetValue(area.Name, out var current) || current.Count != 0) continue;
            merged[area.Name] = JsonSerializer.Deserialize<List<GlossaryTerm>>(area.Value,
                TagMaintenancePolicy.Json) ?? [];
        }
        return snapshot with { Glossaries = merged };
    }
}

/// <summary>One project-scoped dry-run/apply coordinator. All writes use the existing tag boundaries.</summary>
public sealed class AutoTaggingService(ITagMaintenanceWorkspace workspace, IAutoTagClassifier classifier,
    TagGoldenSetEvaluator goldenSets, ProjectSettingsService settings, TaskScannerService scanner,
    TaskMutationService mutations, WorkbenchTagService dossierTags, ProjectDocsService docs,
    AreaRegistryService areaRegistry,
    TimelineLog timeline, OrchestratorLog activity, IConfiguration configuration)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TagGoldenSetReport> _goldenReports = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Projects() => workspace.Projects();
    public bool Enabled(string project) => settings.Get(project).AutoTag;
    private string StatePath(string project)
    {
        if (!Projects().Contains(project)) throw new ArgumentException("Unknown project.");
        var root = configuration["TaskRepository"]
            ?? throw new InvalidOperationException("TaskRepository is required for auto-tag state.");
        return Path.Combine(root, "auto-tag", TagMaintenancePolicy.Fingerprint(project) + ".json");
    }
    public IReadOnlyList<AutoTagResult> Read(string project)
    {
        var path = StatePath(project);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<AutoTagResult>>(File.ReadAllText(path), TagMaintenancePolicy.Json) ?? []
            : [];
    }
    private void Save(string project, IReadOnlyList<AutoTagResult> results)
    {
        var path = StatePath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, TagMaintenancePolicy.Encode(results));
        File.Move(temp, path, overwrite: true);
    }
    private void SaveReport(string project, AutoTagReport report)
    {
        var path = Path.ChangeExtension(StatePath(project), ".report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, TagMaintenancePolicy.Encode(report));
        File.Move(temp, path, overwrite: true);
    }
    public AutoTagReport? ReadReport(string project)
    {
        var path = Path.ChangeExtension(StatePath(project), ".report.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<AutoTagReport>(File.ReadAllText(path), TagMaintenancePolicy.Json)
            : null;
    }
    public async Task<AutoTagReport> BackfillAsync(string project, bool apply, CancellationToken ct,
        IReadOnlySet<string>? onlyKeys = null)
    {
        var gate = _gates.GetOrAdd(project, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var snapshot = ProposedGlossaryContext.Apply(project, workspace.CaptureForClassification(project));
            if (!_goldenReports.TryGetValue(project, out var golden))
            {
                golden = await goldenSets.EvaluateAsync(project, snapshot, ct);
                if (golden.Status.StartsWith("evaluated", StringComparison.Ordinal))
                    _goldenReports[project] = golden;
            }
            var tier = golden.SelectedTier == 2 ? "high" : "low";
            var prior = Read(project).ToDictionary(x => (x.Kind, x.Id));
            var nonArchivedCards = scanner.ScanAllJobsWithArchive()
                .Where(t => t.ProjectName == project && AutoTaggingPolicy.EligibleCard(t))
                .Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            bool Eligible(TagMaintenanceItem item) => item.Kind == "card"
                ? nonArchivedCards.Contains(item.Id) : item.Active;
            var eligible = snapshot.Items.Where(i => i.Project == project && Eligible(i)).ToList();
            var candidates = eligible.Where(i => i.Tags.Length == 0
                && (onlyKeys == null || (onlyKeys.Contains(i.Kind + ":" + i.Id)
                    && !prior.ContainsKey((i.Kind, i.Id))))).ToList();
            var results = new List<AutoTagResult>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var low = new List<AutoTagResult>();
            var areas = areaRegistry.List(project).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            var watchPath = apply ? scanner.GetWatchPaths().Single(p => p.Name == project).Path : null;
            foreach (var existing in eligible)
                foreach (var area in existing.Tags.Where(areas.Contains))
                    counts[area] = counts.GetValueOrDefault(area) + 1;
            foreach (var item in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var thinkingLevel = tier;
                var prediction = await classifier.ClassifyAsync(project, item, snapshot, thinkingLevel, ct);
                if (prediction.Confidence < AutoTaggingPolicy.ConfidenceThreshold && thinkingLevel == "low")
                {
                    thinkingLevel = "high";
                    prediction = await classifier.ClassifyAsync(project, item, snapshot, thinkingLevel, ct);
                }
                var accepted = AutoTaggingPolicy.Status(prediction.Confidence) == "tagged";
                var result = new AutoTagResult(item.Kind, item.Id,
                    accepted ? "tagged" : "tags-proposed", prediction.Tags,
                    prediction.Confidence, DateTimeOffset.UtcNow)
                {
                    AreaTags = prediction.Tags.Where(areas.Contains).ToArray(),
                    FacetTags = prediction.Tags.Where(tag => !areas.Contains(tag)).ToArray(),
                    ThinkingLevel = thinkingLevel,
                };
                results.Add(result);
                if (!accepted) low.Add(result);
                if (accepted)
                    foreach (var area in prediction.Tags.Where(areas.Contains))
                        counts[area] = counts.GetValueOrDefault(area) + 1;
                if (!apply) continue;
                if (item.Kind == "dossier")
                {
                    var write = dossierTags.Set(project, item.Id, new SetWorkbenchTagsRequest
                    {
                        Tags = accepted ? prediction.Tags.ToList() : item.Tags.ToList(),
                        TaggingStatus = result.Status,
                    });
                    if (!write.Success) throw new InvalidOperationException(write.Error);
                }
                else if (accepted)
                {
                    var change = new TagMaintenanceChange(item.Kind, project, item.Id,
                        TagMaintenancePolicy.Encode(item.Tags), TagMaintenancePolicy.Encode(prediction.Tags));
                    workspace.Write(change);
                }
                prior[(item.Kind, item.Id)] = result;
                if (item.Kind == "card")
                {
                    var watch = scanner.GetWatchPaths().Single(p => p.Name == project);
                    var task = scanner.FindJob(item.Id, watch.Path);
                    if (task != null)
                    {
                        mutations.SetTaggingStatus(item.Id, result.Status, watch.Path);
                        timeline.Append(task.FolderPath, "auto-tag", "system",
                            accepted ? $"Auto-tagged: {string.Join(", ", prediction.Tags)}" :
                                $"Tags proposed: {string.Join(", ", prediction.Tags)}");
                    }
                }
                if (item.Kind == "wiki")
                {
                    var file = docs.ReadWikiFile(project, item.Id)
                        ?? throw new InvalidOperationException("Wiki article disappeared before its tagging status was written.");
                    var updated = TagMaintenanceWorkspace.RewriteTaggingStatus(file.Content, result.Status);
                    var write = docs.WriteWikiFile(project, item.Id, updated);
                    if (!write.Success) throw new InvalidOperationException(write.Error);
                }
                Save(project, prior.Values.OrderBy(x => x.Kind).ThenBy(x => x.Id).ToList());
                activity.Append(watchPath!, new OrchestratorLogEntry
                {
                    Kind = accepted ? OrchestratorLogKinds.Action : OrchestratorLogKinds.Observation,
                    Topic = AutoTagClassifier.StepId,
                    Summary = accepted
                        ? $"Auto-tagged {item.Kind} {item.Id}: {string.Join(", ", prediction.Tags)}"
                        : $"Tags proposed for {item.Kind} {item.Id}: {string.Join(", ", prediction.Tags)}",
                    JobId = item.Kind == "card" ? item.Id : null,
                });
            }
            var report = new AutoTagReport(project, apply, eligible.Count,
                eligible.Count(i => i.Tags.Length > 0), candidates.Count, counts, low, golden, results);
            SaveReport(project, report);
            return report;
        }
        finally { gate.Release(); }
    }
}

/// <summary>Durable queue for project backfills. Interrupted jobs resume after a restart.</summary>
public sealed class AutoTagBackfillQueue(AutoTaggingService service, IConfiguration configuration,
    ILogger<AutoTagBackfillQueue> logger) : BackgroundService
{
    private readonly Channel<AutoTagBackfillJob> _queue = Channel.CreateUnbounded<AutoTagBackfillJob>();
    private readonly ConcurrentDictionary<string, AutoTagBackfillJob> _jobs = new(StringComparer.Ordinal);
    private string DirectoryPath => Path.Combine(configuration["TaskRepository"]
        ?? throw new InvalidOperationException("TaskRepository is required for auto-tag jobs."), "auto-tag", "jobs");

    public AutoTagBackfillJob Enqueue(string project, bool apply)
    {
        if (!service.Projects().Contains(project)) throw new ArgumentException("Unknown project.");
        if (_jobs.Values.Count(job => job.Status is "queued" or "running") >= 32)
            throw new InvalidOperationException("The auto-tag backfill queue is full.");
        var job = new AutoTagBackfillJob(Guid.NewGuid().ToString("N"), project, apply, "queued", DateTimeOffset.UtcNow);
        Save(job);
        if (!_queue.Writer.TryWrite(job)) throw new InvalidOperationException("The auto-tag backfill queue is closed.");
        return job;
    }

    public AutoTagBackfillJob? Read(string project, string id)
    {
        if (_jobs.TryGetValue(id, out var cached)) return cached.Project == project ? cached : null;
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        var path = Path.Combine(DirectoryPath, id + ".json");
        if (!File.Exists(path)) return null;
        var job = JsonSerializer.Deserialize<AutoTagBackfillJob>(File.ReadAllText(path), TagMaintenancePolicy.Json);
        return job?.Project == project ? job : null;
    }

    private void Save(AutoTagBackfillJob job)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, job.Id + ".json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, TagMaintenancePolicy.Encode(job));
        File.Move(temp, path, overwrite: true);
        _jobs[job.Id] = job;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Directory.Exists(DirectoryPath))
        {
            foreach (var path in Directory.GetFiles(DirectoryPath, "*.json"))
            {
                try
                {
                    var job = JsonSerializer.Deserialize<AutoTagBackfillJob>(File.ReadAllText(path), TagMaintenancePolicy.Json);
                    if (job == null) continue;
                    if (_jobs.ContainsKey(job.Id)) continue;
                    _jobs[job.Id] = job;
                    if (job.Status is "queued" or "running") await _queue.Writer.WriteAsync(job, stoppingToken);
                }
                catch (Exception ex) { logger.LogWarning(ex, "auto-tag-job-read-failed path={Path}", path); }
            }
        }
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                Save(job with { Status = "running", Error = null });
                await service.BackfillAsync(job.Project, job.Apply, stoppingToken);
                Save(job with { Status = "completed", CompletedAt = DateTimeOffset.UtcNow, Error = null });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "auto-tag-backfill-failed project={Project} job={Job}", job.Project, job.Id);
                Save(job with { Status = "failed", CompletedAt = DateTimeOffset.UtcNow, Error = ex.Message });
            }
        }
    }
}

/// <summary>Creation detector. First start records the existing inventory; later scans classify new active items.</summary>
public sealed class AutoTagCreationWorker(AutoTaggingService service, ITagMaintenanceWorkspace workspace,
    TaskScannerService scanner, IConfiguration configuration, ILogger<AutoTagCreationWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    public void Wake()
    {
        if (_wake.CurrentCount != 0) return;
        try { _wake.Release(); }
        catch (SemaphoreFullException ex) { SilentCatch.Note(ex, "An auto-tag scan is already queued."); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ScanOnceAsync(stoppingToken);
            await _wake.WaitAsync(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }

    internal async Task ScanOnceAsync(CancellationToken ct)
    {
        foreach (var project in service.Projects())
        {
            if (!service.Enabled(project)) continue;
            try
            {
                // A durable baseline keeps a deployment from silently applying a full backfill.
                var root = configuration["TaskRepository"];
                if (string.IsNullOrWhiteSpace(root)) continue;
                var path = Path.Combine(root, "auto-tag", TagMaintenancePolicy.Fingerprint(project) + ".observed.json");
                var nonArchivedCards = scanner.ScanAllJobsWithArchive()
                    .Where(task => task.ProjectName == project && AutoTaggingPolicy.EligibleCard(task))
                    .Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
                var items = workspace.CaptureForClassification(project).Items
                    .Where(item => item.Project == project && (item.Kind == "card"
                        ? nonArchivedCards.Contains(item.Id) : item.Active)).ToList();
                var now = items.Select(i => i.Kind + ":" + i.Id).ToHashSet(StringComparer.Ordinal);
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, TagMaintenancePolicy.Encode(now));
                    continue;
                }
                var previous = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path), TagMaintenancePolicy.Json) ?? [];
                if (now.Except(previous).Any())
                {
                    await service.BackfillAsync(project, apply: true, ct,
                        now.Except(previous).ToHashSet(StringComparer.Ordinal));
                    File.WriteAllText(path, TagMaintenancePolicy.Encode(now));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "auto-tag-failed project={Project}", project); }
        }
    }
}

public static class AutoTaggingEndpoints
{
    public static void MapAutoTaggingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{project}/auto-tag");
        group.MapGet("/", (string project, AutoTaggingService service) => Results.Ok(service.Read(project)));
        group.MapGet("/report", (string project, AutoTaggingService service) =>
            service.ReadReport(project) is { } report ? Results.Ok(report) : Results.NotFound());
        group.MapPost("/backfill", async (string project, bool apply, AutoTaggingService service, CancellationToken ct) =>
            Results.Ok(await service.BackfillAsync(project, apply, ct)));
        group.MapPost("/backfill-jobs", (string project, bool apply, AutoTagBackfillQueue jobs) =>
        {
            var job = jobs.Enqueue(project, apply);
            return Results.Accepted($"/api/projects/{Uri.EscapeDataString(project)}/auto-tag/backfill-jobs/{job.Id}", job);
        });
        group.MapGet("/backfill-jobs/{id}", (string project, string id, AutoTagBackfillQueue jobs) =>
            jobs.Read(project, id) is { } job ? Results.Ok(job) : Results.NotFound());
    }
}
