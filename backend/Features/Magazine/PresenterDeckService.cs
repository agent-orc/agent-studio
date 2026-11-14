using System.Text.Json;
using AgentStudio.Cli;
using AgentStudio.Personas;
using AgentStudio.Quota;
using AgentStudio.Registry;

namespace AgentStudio.Magazine;

/// <summary>Immutable deck.json files beside immutable issue snapshots.</summary>
public sealed class PresenterDeckStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string? _root;
    private readonly object _gate = new();
    private readonly Dictionary<string, PresenterDeck> _memory = new(StringComparer.Ordinal);
    public PresenterDeckStore(IConfiguration configuration) => _root =
        string.IsNullOrWhiteSpace(configuration["TaskRepository"]) ? null :
        Path.Combine(configuration["TaskRepository"]!, ".metadata", "presenter-decks");

    public IReadOnlyList<PresenterDeck> List(string workspaceId)
    {
        var folder = Folder(workspaceId);
        lock (_gate) return folder is null
            ? _memory.Values.Where(d => d.WorkspaceId == workspaceId).OrderByDescending(d => d.CreatedAtUtc).ToArray()
            : !Directory.Exists(folder) ? [] : Directory.EnumerateFiles(folder, "deck.json", SearchOption.AllDirectories)
                .Select(path => JsonSerializer.Deserialize<PresenterDeck>(File.ReadAllText(path), Json))
                .OfType<PresenterDeck>().OrderByDescending(d => d.CreatedAtUtc).ToArray();
    }

    public PresenterDeck? Get(string workspaceId, string deckId)
    {
        var path = PathFor(workspaceId, deckId);
        lock (_gate) return path is null ? _memory.GetValueOrDefault(workspaceId + ":" + deckId)
            : File.Exists(path) ? JsonSerializer.Deserialize<PresenterDeck>(File.ReadAllText(path), Json) : null;
    }

    public PresenterDeck Publish(PresenterDeck deck)
    {
        var path = PathFor(deck.WorkspaceId, deck.Id);
        lock (_gate)
        {
            if (path is null)
            {
                var key = deck.WorkspaceId + ":" + deck.Id;
                if (!_memory.TryGetValue(key, out var stored)) _memory[key] = stored = deck;
                return stored;
            }
            if (File.Exists(path)) return JsonSerializer.Deserialize<PresenterDeck>(File.ReadAllText(path), Json)!;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(deck, Json));
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path))
                { return JsonSerializer.Deserialize<PresenterDeck>(File.ReadAllText(path), Json)!; }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return deck;
        }
    }

    private string? Folder(string workspaceId)
    {
        if (!PersonaDescriptorValidator.Slug().IsMatch(workspaceId)) throw new ArgumentException("Invalid workspace id.");
        return _root is null ? null : Path.Combine(_root, workspaceId);
    }
    private string? PathFor(string workspaceId, string deckId)
    {
        if (!(deckId.StartsWith("weekly-", StringComparison.Ordinal) || deckId.StartsWith("monthly-", StringComparison.Ordinal)) ||
            !DateOnly.TryParseExact(deckId[(deckId.IndexOf('-') + 1)..], "yyyy-MM-dd", out _))
            throw new ArgumentException("Invalid deck id.");
        return Folder(workspaceId) is { } folder ? Path.Combine(folder, deckId, "deck.json") : null;
    }
}

public sealed record PresenterDeckEdit(string Text, long Tokens);

public class PresenterDeckEditor(CliOneShotRegistry oneShots, IConfiguration configuration)
{
    public virtual async Task<PresenterDeckEdit?> WriteAsync(PresenterDeck deck, CancellationToken ct)
    {
        var source = deck.Slides.Select(s => new { s.Id, s.Layout, s.Title,
            Items = s.Blocks.Where(b => b.ItemId is not null).Select(b => new { b.ItemId, b.Label }) });
        var prompt = "Return JSON with slides array in the supplied order, each with title and speakerNotes. " +
            "Keep each supplied title, or add a supplied [item id] citation to a changed title. Do not put numbers in titles. Every sentence of speakerNotes " +
            "must cite a supplied item id as [item id]. Use no unsupplied facts. " +
            "For slides with no items, speakerNotes must be empty.\n" + JsonSerializer.Serialize(source);
        var model = configuration["Presenter:EditorModel"] ?? ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku);
        var result = await oneShots.Require("claude").RunAsync(new CliOneShotRequest("claude", model, prompt)
        { Timeout = TimeSpan.FromSeconds(90), Source = "presenter-deck" }, ct);
        if (!result.Ok) return null;
        var usage = result.Usage;
        var tokens = usage is null ? 0 : WorkspaceQuotaPolicy.ModelTokens(
            usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheCreationTokens);
        return new PresenterDeckEdit(string.IsNullOrWhiteSpace(result.ParsedText) ? result.Stdout : result.ParsedText,
            tokens);
    }
}

public sealed class PresenterDeckService(PresenterDeckStore store, WeeklyIssueStore issues,
    PersonaActivationStore activations, PersonaSchedulingService scheduling, WorkspaceQuotaService quota,
    WorkspaceRegistry workspaces, PresenterDeckEditor editor, ILogger<PresenterDeckService> logger)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    public IReadOnlyList<PresenterDeck> List(string workspaceId) => store.List(workspaceId);
    public PresenterDeck? Get(string workspaceId, string deckId) => store.Get(workspaceId, deckId);

    public async Task ComposeDueAllAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        foreach (var workspace in workspaces.List()) await ComposeDueAsync(workspace.Id, nowUtc, ct);
    }

    public async Task<IReadOnlyList<PresenterDeck>> ComposeDueAsync(string workspaceId, DateTime nowUtc,
        CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var weekly = issues.List(workspaceId).Where(i => i.Kind == "weekly")
                .OrderBy(i => i.PeriodStart).ToArray();
            var published = new List<PresenterDeck>();
            for (var index = 0; index < weekly.Length; index++)
            {
                var issue = weekly[index];
                var id = "weekly-" + issue.PeriodStart.ToString("yyyy-MM-dd");
                if (store.Get(workspaceId, id) is null)
                    published.Add(await PublishAsync(weekly[Math.Max(0, index - 3)..(index + 1)], "weekly", workspaceId, nowUtc, ct));
            }
            // Each fourth issue in a consecutive run closes one monthly review window.
            var consecutive = 0;
            for (var index = 0; index < weekly.Length; index++)
            {
                consecutive = index > 0 && weekly[index].PeriodStart.DayNumber - weekly[index - 1].PeriodStart.DayNumber == 7
                    ? consecutive + 1 : 1;
                if (consecutive < 4 || consecutive % 4 != 0) continue;
                var last = weekly[(index - 3)..(index + 1)];
                var id = "monthly-" + last[^1].PeriodStart.ToString("yyyy-MM-dd");
                if (store.Get(workspaceId, id) is null)
                    published.Add(await PublishAsync(last, "monthly", workspaceId, nowUtc, ct));
            }
            return published;
        }
        finally { gate.Release(); }
    }

    private async Task<PresenterDeck> PublishAsync(IReadOnlyList<WeeklyIssue> source, string kind,
        string workspaceId, DateTime nowUtc, CancellationToken ct)
    {
        var deck = PresenterDeckComposer.Compose(source, kind);
        var active = activations.List(workspaceId).Personas.Select(p => p.Activation)
            .Where(a => a is not null).Cast<PersonaActivationV1>().ToArray();
        var presenter = active.Where(a => a.Descriptor.Kind == PersonaKinds.Presenter && a.State == PersonaStates.Active)
            .OrderBy(a => a.PersonaId, StringComparer.Ordinal).FirstOrDefault();
        deck = deck with { PresenterPersonaId = presenter?.PersonaId,
            PresenterRoleId = presenter?.RoleId ?? presenter?.Descriptor.Id ?? "presenter" };
        if (presenter is not null)
        {
            var budget = scheduling.Budget(workspaceId, nowUtc,
                quota.Snapshot(workspaceId).Limits?.ModelTokensPerWeek, active);
            var editorTokens = store.List(workspaceId).Where(previous => previous.CreatedAtUtc >= budget.StartUtc &&
                    previous.CreatedAtUtc < budget.EndUtc).Sum(previous => previous.EditorTokens)
                + issues.List(workspaceId).Where(issue => issue.PublishedAtUtc >= budget.StartUtc &&
                    issue.PublishedAtUtc < budget.EndUtc).Sum(issue => issue.EditorTokens);
            if (budget.Personas.TryGetValue(presenter.PersonaId, out var allowance) &&
                deck.CreatedAtUtc >= budget.StartUtc && deck.CreatedAtUtc < budget.EndUtc &&
                allowance.UsedTokens + editorTokens < allowance.AllowanceTokens &&
                budget.UsedTokens + editorTokens < budget.PoolTokens)
                try
                {
                    var edit = await editor.WriteAsync(deck, ct);
                    if (edit is not null)
                    {
                        deck = deck with { EditorTokens = edit.Tokens };
                        if (!string.IsNullOrWhiteSpace(edit.Text))
                            deck = PresenterDeckValidator.WithNotes(deck, edit.Text, source);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { logger.LogWarning(ex, "Presenter notes refused; publishing deterministic deck"); }
        }
        return store.Publish(deck);
    }
}
