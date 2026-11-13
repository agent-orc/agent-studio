using System.Text.Json;
using AgentStudio.Watcher;

namespace AgentStudio.Personas;

public static class PersonaItemKinds
{
    public const string Finding = "finding";
    public const string Proposal = "proposal";
    public const string DecisionRequest = "decision-request";
    public const string ReportLine = "report-line";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Finding, Proposal, DecisionRequest, ReportLine], StringComparer.Ordinal);
}

/// <summary>Item states. Open, draft and deferred items still take an owner decision.</summary>
public static class PersonaItemStates
{
    public static bool IsDecidable(string state) => state is "open" or "draft" or "deferred";
}

public sealed record PersonaEvidenceV1(string Ref, bool Measured = false);
public sealed record PersonaItemDraftV1(string Kind, string Title, string Body, string? Project,
    IReadOnlyList<PersonaEvidenceV1> Evidence, int Impact, int Urgency, double Confidence,
    string FingerprintKey, DateTime? DeadlineUtc = null, bool DraftCard = false,
    IReadOnlyList<string>? Options = null, string? RecommendedOption = null,
    string? DossierKey = null, string? DossierSection = null,
    string? DraftTitle = null, string? DraftPrompt = null, string? Area = null);
public sealed record PersonaItemChangeV1(DateTime AtUtc, int PreviousImportance, int Importance,
    string Reason, string OccurrenceKey);
public sealed record PersonaItemV1(string Id, string PersonaId, string RoleId, string Kind,
    string Title, string Body, string? Project, IReadOnlyList<PersonaEvidenceV1> Evidence,
    int Impact, int Urgency, double Confidence, int Floor, int Importance,
    string Fingerprint, string State, DateTime CreatedAtUtc, DateTime UpdatedAtUtc,
    IReadOnlyList<PersonaItemChangeV1> History, DateTime? DeadlineUtc = null,
    bool DraftCard = false, IReadOnlyList<string>? Options = null, string? RecommendedOption = null,
    string? DossierKey = null, string? DossierSection = null,
    string? DraftTitle = null, string? DraftPrompt = null, string? Area = null,
    DateTime? DeferredUntilUtc = null, string? PromotedTaskKey = null);

public static class PersonaImportanceService
{
    public static double CapConfidence(double requested, IReadOnlyList<PersonaEvidenceV1> evidence)
    {
        var distinct = evidence.Select(e => e.Ref).Distinct(StringComparer.Ordinal).Count();
        var cap = distinct == 0 ? 0.5 : distinct == 1 ? 0.7 : evidence.Any(e => e.Measured) ? 1.0 : 0.5;
        return Math.Min(requested, cap);
    }

    public static int Score(int impact, int urgency, double confidence, int floor = 0)
    {
        if (impact is < 1 or > 5 || urgency is < 1 or > 5 ||
            !double.IsFinite(confidence) || confidence is < 0 or > 1 || floor is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(impact), "Importance factors are out of range.");
        var computed = (int)Math.Round(100d * impact * urgency / 25d * (0.5 + 0.5 * confidence),
            MidpointRounding.AwayFromZero);
        return Math.Clamp(Math.Max(computed, floor), 0, 100);
    }

    public static int Urgency(DateTime now, DateTime created, DateTime? deadline, int stated)
    {
        if (deadline is { } due)
        {
            var remaining = due - now;
            return Math.Max(stated, remaining <= TimeSpan.Zero ? 5
                : remaining <= TimeSpan.FromDays(1) ? 4
                : remaining <= TimeSpan.FromDays(7) ? 3
                : remaining <= TimeSpan.FromDays(30) ? 2 : 1);
        }
        return Math.Min(5, Math.Max(stated, 1 + (int)((now - created).TotalDays / 7)));
    }

    public static int DescriptorFloor(PersonaDescriptorV1 descriptor, string fact)
    {
        foreach (var rule in descriptor.Floors)
        {
            var split = rule.LastIndexOf(':');
            if (split > 0 && rule[..split] == fact && int.TryParse(rule[(split + 1)..], out var value))
                return Math.Clamp(value, 0, 100);
        }
        return 0;
    }
}

public static class PersonaFingerprint
{
    public static string Compute(PersonaItemDraftV1 draft, PersonaGroundsV1? grounds = null)
    {
        if (draft.FingerprintKey.StartsWith("ops:", StringComparison.Ordinal))
            return draft.FingerprintKey;
        var caseFingerprint = grounds?.Sources.FirstOrDefault(source =>
            source.Kind == "watcher-case" && source.Fingerprint is not null &&
            draft.Evidence.Any(e => e.Ref == source.Ref))?.Fingerprint;
        return caseFingerprint ?? WatcherFingerprint.Compute("persona", draft.Project ?? "_workspace",
            draft.FingerprintKey.Trim().ToLowerInvariant());
    }
}

/// <summary>Application-owned items. One fingerprint has one lifecycle and an append-only score history.</summary>
public sealed class PersonaItemStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string? _root;
    private readonly Dictionary<string, List<PersonaItemV1>> _states = new(StringComparer.Ordinal);

    public PersonaItemStore(IConfiguration configuration) : this(
        string.IsNullOrWhiteSpace(configuration["TaskRepository"]) ? null
            : Path.Combine(configuration["TaskRepository"]!, ".metadata", "persona-items")) { }
    public PersonaItemStore(string? root) => _root = root;

    public IReadOnlyList<PersonaItemV1> List(string workspaceId)
    {
        lock (_gate) return [.. Load(workspaceId)];
    }

    public PersonaItemV1 Upsert(string workspaceId, string personaId, string roleId,
        string occurrenceKey, PersonaItemDraftV1 draft, int floor, DateTime now,
        PersonaGroundsV1? grounds = null)
    {
        var fingerprint = PersonaFingerprint.Compute(draft, grounds);
        lock (_gate)
        {
            var items = Load(workspaceId);
            var priorIndex = items.FindIndex(i => i.PersonaId == personaId && i.Fingerprint == fingerprint
                && PersonaItemStates.IsDecidable(i.State));
            var confidence = PersonaImportanceService.CapConfidence(draft.Confidence, draft.Evidence);
            var score = PersonaImportanceService.Score(draft.Impact, draft.Urgency, confidence, floor);
            PersonaItemV1 item;
            if (priorIndex >= 0)
            {
                var previous = items[priorIndex];
                score = Math.Max(score, previous.Floor);
                item = previous with { Title = draft.Title, Body = draft.Body, Evidence = draft.Evidence,
                    Impact = draft.Impact, Urgency = draft.Urgency, Confidence = confidence,
                    Floor = Math.Max(floor, previous.Floor), Importance = score, UpdatedAtUtc = now,
                    History = [.. previous.History, new PersonaItemChangeV1(now, previous.Importance,
                        score, "finding-updated", occurrenceKey)], DossierKey = draft.DossierKey,
                    DossierSection = draft.DossierSection, DraftTitle = draft.DraftTitle,
                    DraftPrompt = draft.DraftPrompt, Area = draft.Area };
                items[priorIndex] = item;
            }
            else
            {
                item = new PersonaItemV1("pit_" + Guid.CreateVersion7().ToString("N"), personaId, roleId,
                    draft.Kind, draft.Title, draft.Body, draft.Project, draft.Evidence, draft.Impact,
                    draft.Urgency, confidence, floor, score, fingerprint,
                    draft.Kind == PersonaItemKinds.Proposal ? "draft" : draft.Kind == PersonaItemKinds.ReportLine ? "published" : "open",
                    now, now, [new PersonaItemChangeV1(now, 0, score, "created", occurrenceKey)],
                    draft.DeadlineUtc, draft.DraftCard, draft.Options, draft.RecommendedOption,
                    draft.DossierKey, draft.DossierSection, draft.DraftTitle, draft.DraftPrompt,
                    draft.Area);
                items.Add(item);
            }
            Save(workspaceId, items);
            return item;
        }
    }

    public PersonaItemV1? Find(string workspaceId, string itemId)
    {
        lock (_gate) return Load(workspaceId).FirstOrDefault(item => item.Id == itemId);
    }

    /// <summary>
    /// Records an owner decision that <see cref="PersonaProposalLifecycle"/> has already applied.
    /// Returns null when another decision closed the item first.
    /// </summary>
    public PersonaItemV1? RecordDecision(string workspaceId, string itemId, string outcome, string reason,
        DateTime now, DateTime? deferredUntil = null, string? promotedTaskKey = null)
    {
        lock (_gate)
        {
            var items = Load(workspaceId);
            var index = items.FindIndex(item => item.Id == itemId);
            if (index < 0) throw new KeyNotFoundException("Persona item not found.");
            var current = items[index];
            if (!PersonaItemStates.IsDecidable(current.State)) return null;
            var updated = current with { State = outcome, UpdatedAtUtc = now,
                DeferredUntilUtc = deferredUntil, PromotedTaskKey = promotedTaskKey ?? current.PromotedTaskKey,
                History = [.. current.History, new PersonaItemChangeV1(now, current.Importance,
                    current.Importance, "owner-" + outcome, reason)] };
            items[index] = updated;
            Save(workspaceId, items);
            return updated;
        }
    }

    public IReadOnlyList<PersonaItemV1> RecomputeOpen(string workspaceId, DateTime now)
    {
        lock (_gate)
        {
            var items = Load(workspaceId);
            var changed = new List<PersonaItemV1>();
            for (var n = 0; n < items.Count; n++)
            {
                var item = items[n];
                if (item.State is not ("open" or "draft")) continue;
                var urgency = PersonaImportanceService.Urgency(now, item.CreatedAtUtc, item.DeadlineUtc, item.Urgency);
                var score = PersonaImportanceService.Score(item.Impact, urgency, item.Confidence, item.Floor);
                if (score == item.Importance && urgency == item.Urgency) continue;
                items[n] = item with { Urgency = urgency, Importance = score, UpdatedAtUtc = now,
                    History = [.. item.History, new PersonaItemChangeV1(now, item.Importance, score,
                        "urgency-recomputed", "sweep")] };
                changed.Add(items[n]);
            }
            if (changed.Count > 0) Save(workspaceId, items);
            return changed;
        }
    }

    private List<PersonaItemV1> Load(string workspaceId)
    {
        if (_states.TryGetValue(workspaceId, out var state)) return state;
        var path = PathFor(workspaceId);
        state = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<List<PersonaItemV1>>(File.ReadAllText(path), Json) ?? [] : [];
        return _states[workspaceId] = state;
    }

    private void Save(string workspaceId, List<PersonaItemV1> items)
    {
        if (PathFor(workspaceId) is not { } path) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(items, Json));
        File.Move(temp, path, true);
    }

    private string? PathFor(string id)
    {
        if (_root is null) return null;
        if (!PersonaDescriptorValidator.Slug().IsMatch(id)) throw new ArgumentException("Invalid workspace id.");
        return Path.Combine(_root, id + ".json");
    }
}
