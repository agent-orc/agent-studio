using System.Text.Json;
using AgentStudio.Cli;
using AgentStudio.Personas;
using AgentStudio.Quota;
using AgentStudio.Registry;

namespace AgentStudio.Magazine;

/// <summary>One immutable file per workspace issue. Publication is idempotent across restarts.</summary>
public sealed class WeeklyIssueStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string? _root;
    private readonly object _gate = new();
    private readonly Dictionary<string, WeeklyIssue> _memory = new(StringComparer.Ordinal);

    public WeeklyIssueStore(IConfiguration configuration) => _root =
        string.IsNullOrWhiteSpace(configuration["TaskRepository"]) ? null :
            Path.Combine(configuration["TaskRepository"]!, ".metadata", "weekly-issues");

    public IReadOnlyList<WeeklyIssue> List(string workspaceId)
    {
        var folder = Folder(workspaceId);
        lock (_gate) return folder is null
            ? _memory.Values.Where(issue => issue.WorkspaceId == workspaceId)
                .OrderByDescending(issue => issue.PublishedAtUtc).ThenBy(issue => issue.Id, StringComparer.Ordinal).ToArray()
            : !Directory.Exists(folder) ? [] :
            Directory.EnumerateFiles(folder, "*.json")
                .Select(path => JsonSerializer.Deserialize<WeeklyIssue>(File.ReadAllText(path), Json))
                .OfType<WeeklyIssue>().OrderByDescending(issue => issue.PublishedAtUtc)
                .ThenBy(issue => issue.Id, StringComparer.Ordinal).ToArray();
    }

    public WeeklyIssue? Get(string workspaceId, string issueId)
    {
        var path = PathFor(workspaceId, issueId);
        lock (_gate) return path is null ? _memory.GetValueOrDefault(workspaceId + ":" + issueId)
            : File.Exists(path) ? JsonSerializer.Deserialize<WeeklyIssue>(File.ReadAllText(path), Json) : null;
    }

    public WeeklyIssue Publish(WeeklyIssue issue)
    {
        var path = PathFor(issue.WorkspaceId, issue.Id);
        if (path is null)
        {
            lock (_gate)
            {
                var key = issue.WorkspaceId + ":" + issue.Id;
                if (!_memory.TryGetValue(key, out var stored)) _memory[key] = stored = issue;
                return stored;
            }
        }
        lock (_gate)
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<WeeklyIssue>(File.ReadAllText(path), Json)!;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(issue, Json));
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path))
                {
                    return JsonSerializer.Deserialize<WeeklyIssue>(File.ReadAllText(path), Json)!;
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return issue;
        }
    }

    private string? Folder(string workspaceId)
    {
        if (!PersonaDescriptorValidator.Slug().IsMatch(workspaceId)) throw new ArgumentException("Invalid workspace id.");
        return _root is null ? null : Path.Combine(_root, workspaceId);
    }

    private string? PathFor(string workspaceId, string issueId)
    {
        if (!(issueId.StartsWith("weekly-", StringComparison.Ordinal) || issueId.StartsWith("daily-", StringComparison.Ordinal))
            || !DateOnly.TryParseExact(issueId[(issueId.IndexOf('-') + 1)..], "yyyy-MM-dd", out _))
            throw new ArgumentException("Invalid issue id.");
        return Folder(workspaceId) is { } folder ? Path.Combine(folder, issueId + ".json") : null;
    }
}

/// <summary>The sole optional editor call for an issue. A failed call leaves titles only.</summary>
public sealed record WeeklyIssueEdit(string Text, long Tokens);

public class WeeklyIssueEditor(CliOneShotRegistry oneShots, IConfiguration configuration)
{
    public virtual async Task<WeeklyIssueEdit?> WriteAsync(WeeklyIssue issue, IReadOnlyList<PersonaItemV1> items,
        CancellationToken ct)
    {
        var sectionLeads = issue.Sections.Select(s => s.Stories.FirstOrDefault()?.Id).ToHashSet(StringComparer.Ordinal);
        var source = items.Where(i => i.Id == issue.Lead?.Id || issue.SideStories.Any(s => s.Id == i.Id)
                || sectionLeads.Contains(i.Id))
            .OrderBy(i => i.Id, StringComparer.Ordinal)
            .Select(i => new { i.Id, i.Title, i.Body }).ToArray();
        var sections = issue.Sections.Select(s => new { s.RoleId, ItemIds = s.Stories.Select(story => story.Id) });
        var prompt = "Write JSON with leadParagraph, teasers (an object keyed by item id), and " +
            "sectionTeasers (an object keyed by role id). One short lead paragraph, " +
            "one short teaser for every supplied item, and one short teaser per role section. " +
            "Every sentence must end with a citation in the form [item id]. " +
            "Use only these item ids and facts. Do not give instructions or invent facts.\n" +
            JsonSerializer.Serialize(new { Items = source, Sections = sections });
        var model = configuration["Magazine:EditorModel"] ?? ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku);
        var result = await oneShots.Require("claude").RunAsync(new CliOneShotRequest("claude", model, prompt)
        {
            Timeout = TimeSpan.FromSeconds(90), Source = "magazine-editor",
        }, ct);
        if (!result.Ok) return null;
        var usage = result.Usage;
        var tokens = usage is null ? 0 : WorkspaceQuotaPolicy.ModelTokens(
            usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheCreationTokens);
        return new WeeklyIssueEdit(string.IsNullOrWhiteSpace(result.ParsedText) ? result.Stdout : result.ParsedText,
            tokens);
    }
}

/// <summary>Workspace-time publication after Monday 07:00, with deterministic input selection.</summary>
public sealed class WeeklyIssueService(
    WeeklyIssueStore store, PersonaItemStore items, PersonaReportStore reports,
    PersonaActivationStore activations, PersonaSchedulingService scheduling,
    WorkspaceQuotaService quota, OpsPostureSnapshotStore watch,
    WorkspaceSettingsService settings, WorkspaceRegistry workspaces,
    MagazineStore magazine, WeeklyIssueEditor editor, ILogger<WeeklyIssueService> logger)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _composeLocks = new();
    public WeeklyIssue? Latest(string workspaceId) => store.List(workspaceId).FirstOrDefault();
    public IReadOnlyList<WeeklyIssue> List(string workspaceId) => store.List(workspaceId);
    public WeeklyIssue? Get(string workspaceId, string issueId) => store.Get(workspaceId, issueId);

    public async Task<WeeklyIssue?> ComposeDueAsync(string workspaceId, DateTime nowUtc,
        bool daily = false, CancellationToken ct = default)
    {
        var gate = _composeLocks.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
        var zone = Zone(workspaceId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var localDate = DateOnly.FromDateTime(local);
        var date = daily ? localDate : localDate.AddDays(-((int)localDate.DayOfWeek + 6) % 7);
        if (localDate == date && local.TimeOfDay < TimeSpan.FromHours(7)) return null;
        var id = (daily ? "daily-" : "weekly-") + date.ToString("yyyy-MM-dd");
        if (store.Get(workspaceId, id) is { } prior) return prior;
        var entries = activations.List(workspaceId).Personas.Where(p => p.Activation is not null)
            .Select(p => new IssuePersona(p.Id, p.Title, p.Activation!.RoleId ?? p.Id,
                p.Activation.State == PersonaStates.Active)).ToArray();
        var sourceItems = items.List(workspaceId);
        var active = activations.List(workspaceId).Personas.Select(p => p.Activation)
            .Where(a => a is not null).Cast<PersonaActivationV1>().ToArray();
        var budget = scheduling.Budget(workspaceId, nowUtc,
            quota.Snapshot(workspaceId).Limits?.ModelTokensPerWeek, active);
        var issue = WeeklyIssueComposer.Compose(workspaceId, date, daily, sourceItems, reports.List(workspaceId),
            entries, watch.Get(workspaceId), zone, scheduling.Receipts(workspaceId), budget.PoolTokens)
            with { PoolTokensSnapshot = budget.PoolTokens };
        if (!daily)
            issue = issue with { Articles = magazine.List(new MagazineScope(MagazineScopeKind.Workspace, workspaceId))
                .Where(article => article.UpdatedAt <= issue.PublishedAtUtc)
                .OrderBy(article => article.Slug, StringComparer.Ordinal)
                .Select(article =>
                {
                    var author = article.Authors.LastOrDefault();
                    return new IssueArticle(article.Scope, article.Slug, article.Revision, article.Title,
                        article.Summary, author?.Persona ?? author?.Principal ?? "Unknown",
                        author?.PersonaId, author?.RoleId ?? "Unassigned role");
                }).ToArray() };
        var presenter = active.FirstOrDefault(a => a.Descriptor.Kind == PersonaKinds.Presenter
            && a.State == PersonaStates.Active);
        var priorEditorTokens = store.List(workspaceId).Where(previous => previous.PeriodStart == issue.PeriodStart)
            .Sum(previous => previous.EditorTokens);
        if (issue.Lead is not null && presenter is not null &&
            budget.Personas.TryGetValue(presenter.PersonaId, out var allowance) &&
            allowance.UsedTokens + priorEditorTokens < allowance.AllowanceTokens &&
            budget.UsedTokens + priorEditorTokens < budget.PoolTokens)
        {
            try
            {
                var result = await editor.WriteAsync(issue, sourceItems, ct);
                if (result is not null)
                {
                    issue = issue with { EditorTokens = result.Tokens };
                    try { issue = WeeklyIssueComposer.WithEditor(issue, result.Text); }
                    catch (Exception ex) when (ex is ArgumentException or JsonException or KeyNotFoundException)
                    { logger.LogWarning(ex, "Magazine editor sentence refused; publishing titles only"); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Magazine editor output refused; publishing titles only");
            }
        }
        return store.Publish(issue);
        }
        finally { gate.Release(); }
    }

    public async Task ComposeDueAllAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        foreach (var workspace in workspaces.List()) await ComposeDueAsync(workspace.Id, nowUtc, ct: ct);
    }

    private TimeZoneInfo Zone(string workspaceId)
    {
        var id = settings.Get(workspaceId).UsageTimeZone;
        try { return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }
}
