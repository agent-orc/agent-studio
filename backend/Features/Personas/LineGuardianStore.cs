using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Personas;

public sealed record GuardianOwnerItem(string Id, string Incident, string Operation, string Recommended,
    DateTimeOffset ExpiresAt, int UseCap = 1);
public sealed record GuardianUsage(string RunId, string Incident, DateTimeOffset At, double CoreMinutes);
public sealed record GuardianModelAttempt(string Incident, DateTimeOffset At, long ReservedTokens,
    GuardianClassification? Result = null)
{
    public long? TokensUsed { get; init; }
}
public sealed record GuardianShadowRecord(PostMergeIncidentLogEntry Log, bool Shadow,
    RedTestTriageDecision Guardian, RedTestTriageDecision Applied);
public sealed record GuardianProjectState(bool Live, DateTimeOffset? ShadowSince, string? ChangedBy,
    IReadOnlyList<GuardianUsage> Receipts, IReadOnlyList<GuardianModelAttempt> Models,
    IReadOnlyList<GuardianShadowRecord> Decisions, IReadOnlyList<GuardianOwnerItem> OwnerItems,
    IReadOnlyList<string> ClosedIncidents)
{
    public IReadOnlyDictionary<string, double> ReviewWeights { get; init; } = GuardianReviewProjection.DefaultWeights;
    public IReadOnlyList<GuardianReviewEvidence> ReviewEvidence { get; init; } = [];
}

/// <summary>Project-scoped authority, reservations and comparison journal. No task workspace writes.</summary>
public sealed class LineGuardianStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _root;
    private readonly ILineGuardianClassifier? _classifier;
    public LineGuardianStore(string? root = null, ILineGuardianClassifier? classifier = null)
    {
        _root = root ?? LocalStateRoot.Combine("agentstudio", "line-guardian");
        _classifier = classifier;
    }
    public static string Principal(string project) => "persona:line-guardian:" + project;
    public GuardianProjectState Read(string project)
    {
        lock (_gate) return File.Exists(PathFor(project))
            ? JsonSerializer.Deserialize<GuardianProjectState>(File.ReadAllText(PathFor(project)), Json)!
            : new(false, null, null, [], [], [], [], []);
    }
    public void SetLive(string project, bool live, string owner, bool isOwner, DateTimeOffset now)
    {
        if (!isOwner) throw new UnauthorizedAccessException("Only the owner may change guardian authority.");
        lock (_gate)
        {
            var state = Read(project);
            if (live && !state.Live && !MonolithTransitionAdapter.GuardianShadowWeekComplete(state.ShadowSince, now))
                throw new InvalidOperationException("The guardian requires one complete shadow week before going live.");
            Save(project, state with { Live = live, ChangedBy = owner, ShadowSince = state.ShadowSince ?? now });
        }
    }
    public void SetReviewWeights(string project, IReadOnlyDictionary<string, double> weights)
    {
        if (weights.Count != GuardianReviewProjection.DefaultWeights.Count
            || GuardianReviewProjection.DefaultWeights.Keys.Any(key => !weights.ContainsKey(key))
            || weights.Values.Any(value => !double.IsFinite(value) || value < 0) || weights.Values.Sum() <= 0)
            throw new ArgumentException("Provide six finite, non-negative weights with a positive total.");
        lock (_gate) Save(project, Read(project) with { ReviewWeights = weights });
    }
    public void RecordReviewEvidence(string project, GuardianReviewEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.Id) || string.IsNullOrWhiteSpace(evidence.Source)
            || string.IsNullOrWhiteSpace(evidence.Incident) || !double.IsFinite(evidence.Value) || evidence.Value < 0
            || evidence.End < evidence.At
            || (evidence.Kind is "held-wait" or "unrelated-refusal") && evidence.End is null
            || evidence.Kind == "outcome" && (string.IsNullOrWhiteSpace(evidence.DecisionId) || string.IsNullOrWhiteSpace(evidence.Outcome))
            || evidence.Kind is not ("false-blame" or "unrelated-hold" or "escape" or "unrelated-refusal" or "held-wait" or "outcome"))
            throw new ArgumentException("Evidence requires an id, incident, source, supported kind and valid non-negative value or interval.");
        lock (_gate)
        {
            var state = Read(project);
            var old = state.ReviewEvidence.FirstOrDefault(e => e.Id == evidence.Id);
            if (old is not null && old != evidence) throw new InvalidOperationException("Evidence ids are immutable.");
            if (old is null) Save(project, state with { ReviewEvidence = [.. state.ReviewEvidence, evidence] });
        }
    }
    public GuardianOwnerItem? Request(string project, string incident, GuardianOperationFacts facts)
    {
        lock (_gate)
        {
            var permission = MonolithTransitionAdapter.GuardianOperation(facts);
            if (permission == GuardianPermission.Refused) throw new InvalidOperationException("Operation is not grantable within guardian policy.");
            if (permission == GuardianPermission.Allowed) return null;
            var state = Read(project);
            var item = Item(incident, facts.Operation, facts.Now);
            Save(project, state with { OwnerItems = AddItem(state.OwnerItems, item) });
            return item;
        }
    }
    public GuardianShadowRecord Evaluate(string project, PostMergeRunRecord run, RedTestTriageFacts facts,
        RedTestTriageDecision system, DateTimeOffset now, string? evaluationId = null, PostMergeEnvelopeFingerprint? referenceEnvelope = null, string? incidentId = null)
    {
        lock (_gate)
        {
            var state = Read(project);
            var id = evaluationId ?? run.Id + ":" + facts.TestId;
            if (state.Decisions.FirstOrDefault(d => d.Log.DecisionId == id) is { } old) return old;
            var incident = incidentId ?? facts.TestId + ":" + facts.IncidentFingerprint;
            // FocusedRuns is a run-level ledger: never sum each test's duplicate attempts.
            var cores = Math.Max(1, run.Envelope?.ProcessorCount ?? 1);
            var minutes = (run.FocusedRuns ?? []).Where(r => r.Phase is PostMergeRunPhases.Repeat
                or PostMergeRunPhases.Control or PostMergeRunPhases.Bisect
                or PostMergeRunPhases.ControlBuild or PostMergeRunPhases.BisectBuild).Sum(r => Math.Max(0, r.DurationMs)) * cores / 60000d;
            var receiptAt = run.FinishedAtUtc ?? run.StartedAtUtc;
            if (!state.Receipts.Any(r => r.RunId == run.Id && r.Incident == incident))
                state = state with { Receipts = [.. state.Receipts, new(run.Id, incident, receiptAt, minutes)] };
            else
                state = state with { Receipts = state.Receipts.Select(r => r.RunId == run.Id && r.Incident == incident
                    ? r with { At = receiptAt, CoreMinutes = Math.Max(r.CoreMinutes, minutes) } : r).ToArray() };
            var week = now.UtcDateTime.Date.AddDays(-((int)now.UtcDateTime.DayOfWeek + 6) % 7);
            var attempt = state.Models.FirstOrDefault(a => a.Incident == incident);
            var budget = new GuardianBudgetFacts(attempt is not null,
                state.Models.Where(m => m.At >= week).Sum(m => (long)m.ReservedTokens),
                state.Receipts.Where(r => r.Incident == incident).Sum(r => r.CoreMinutes),
                state.Receipts.Where(r => r.At >= week).GroupBy(r => r.RunId).Sum(group => group.Max(r => r.CoreMinutes)));
            var guardian = MonolithTransitionAdapter.RedTestTriage(facts);
            if (guardian.Decisions.Any(a => a.Action == RedTestAction.RequestClassification))
            {
                if (MonolithTransitionAdapter.GuardianMayClassify(budget) && _classifier is not null)
                {
                    attempt = new(incident, now, LineGuardianPolicy.ClassificationTokens);
                    state = state with { Models = [.. state.Models, attempt] };
                    Save(project, state); // Reserve before the call, including on crash or invalid output.
                    GuardianClassificationCall? call = null;
                    try
                    {
                        call = _classifier.Classify(project, new(Clip(AgentStudio.Security.CredentialRedactor.Redact(run.Failure?.LogExcerpt ?? ""), 2000),
                            JsonSerializer.Serialize(new { Current = run.Envelope, Reference = referenceEnvelope, facts.FingerprintMatchesReference }),
                            Clip(JsonSerializer.Serialize(new { run.Failure?.FailingPaths, run.Failure?.SuspectMerges }), 4000)));
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or OperationCanceledException
                        or HttpRequestException or IOException or TimeoutException)
                    { AgentStudio.Diagnostics.SilentCatch.Note(ex, "Line guardian classification failed; rules continue."); }
                    attempt = attempt with { Result = call?.Result, TokensUsed = call?.TokensUsed, ReservedTokens = Math.Max(attempt.ReservedTokens, call?.TokensUsed ?? 0) };
                    state = state with { Models = state.Models.Select(m => m.Incident == incident ? attempt : m).ToArray() };
                }
                else if (attempt is null && !MonolithTransitionAdapter.GuardianMayClassify(budget))
                    state = state with { OwnerItems = AddItem(state.OwnerItems, Item(incident, "budget", now)) };
                facts = facts with { AmbiguousClassificationAttempted = true,
                    ModelClassification = attempt?.Result is { } prediction
                        ? Enum.Parse<RedTestClassification>(prediction.Class) : null };
                guardian = MonolithTransitionAdapter.RedTestTriage(facts);
            }
            if (MonolithTransitionAdapter.GuardianComputeExhausted(budget))
                state = state with { OwnerItems = AddItem(state.OwnerItems, Item(incident, "budget", now)) };
            var applied = state.Live ? guardian : system;
            var reason = string.Join(" ", guardian.Decisions.Select(a => a.Reason));
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { run.Id, run.TipSha, facts, run.Envelope })));
            var log = new PostMergeIncidentLogEntry(id, nameof(RedTestTriagePolicy), "rt-8.v1", digest,
                guardian.Classification.ToString(), attempt?.Result?.Confidence,
                guardian.Decisions.Select(a => a.Action.ToString()).ToArray(), [],
                "Two consecutive covering passes on tips containing the repair in the standard envelope.",
                Clip(reason, 280), run.Id, run.TipSha, facts.TestId, facts.IncidentFingerprint, Principal(project), now);
            var record = new GuardianShadowRecord(log, !state.Live, guardian, applied);
            Save(project, state with { ShadowSince = state.ShadowSince ?? now, Decisions = [.. state.Decisions, record] });
            return record;
        }
    }
    public bool Close(string project, string incident)
    {
        lock (_gate)
        {
            var state = Read(project);
            if (state.ClosedIncidents.Contains(incident)) return false;
            Save(project, state with { ClosedIncidents = [.. state.ClosedIncidents, incident] });
            return true;
        }
    }
    private static GuardianOwnerItem Item(string incident, string operation, DateTimeOffset now)
        => new(incident + ":" + operation, incident, operation, operation == "budget" ? "Keep held; review budget" : "Refuse", now.AddHours(24));
    private static IReadOnlyList<GuardianOwnerItem> AddItem(IReadOnlyList<GuardianOwnerItem> items, GuardianOwnerItem item)
        => items.Any(i => i.Id == item.Id) ? items : [.. items, item];
    private static string Clip(string value, int cap) => value[..Math.Min(value.Length, cap)];
    private string PathFor(string project)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(project, "^PROJ-[0-9]{3,}$")) throw new ArgumentException("Stable project ID required.");
        return Path.Combine(_root, project + ".json");
    }
    private void Save(string project, GuardianProjectState state)
    {
        Directory.CreateDirectory(_root);
        var path = PathFor(project);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, path, true);
    }
}
