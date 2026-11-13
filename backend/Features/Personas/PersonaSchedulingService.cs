using System.Text.Json;
using AgentStudio.Quota;

namespace AgentStudio.Personas;

public sealed record PersonaTrigger(DateTime At, string Type, string Ref, int Floor = 0);

public static class PersonaOccurrenceStates
{
    public const string Admitted = "admitted";
    public const string Claimed = "claimed";
    public const string Waiting = "waiting";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>One durable admission or no-call receipt. PT-4 fills the usage when the session ends.</summary>
public sealed record PersonaOccurrenceReceipt(
    string Key, string WorkspaceId, string PersonaId, string RoleId, string DescriptorRevision,
    DateTime WindowEndUtc, DateTime CreatedAtUtc, string State, string? Reason,
    IReadOnlyList<string> TriggerRefs, string? HostId, long InputTokens = 0,
    long OutputTokens = 0, long CacheReadTokens = 0, long CacheWriteTokens = 0,
    string? Project = null)
{
    public long Tokens => WorkspaceQuotaPolicy.ModelTokens(InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens);
    public IReadOnlyList<PersonaNoCallAttempt> NoCalls { get; init; } = [];
    public string? ClaimToken { get; init; }
    public DateTime? ClaimedAtUtc { get; init; }
    public string? GroundsDigest { get; init; }
    public string? DescriptorDigest { get; init; }
    public string? Model { get; init; }
    public string? ActualModel { get; init; }
    public string? SessionId { get; init; }
    public DateTime? FinishedAtUtc { get; init; }
    public double? CostUsd { get; init; }
    public string PriceStatus { get; init; } = "unknown";
    public IReadOnlyDictionary<string, int> FactFloors { get; init; } = new Dictionary<string, int>();
}

public sealed record PersonaNoCallAttempt(DateTime AtUtc, string Reason);

public sealed record PersonaBudgetWeek(DateTime StartUtc, DateTime EndUtc, long PoolTokens,
    long UsedTokens, int UsedSessions, IReadOnlyDictionary<string, PersonaBudgetPersona> Personas);
public sealed record PersonaBudgetPersona(long AllowanceTokens, int AllowanceSessions,
    long UsedTokens, int UsedSessions);

/// <summary>Typed facts assembled by the Watcher. The policy never asks a model.</summary>
public sealed record PersonaAdmissionFacts(string QuotaStatus, bool MeterComplete,
    IReadOnlyList<string> IdleHostIds, bool ReadyCardWaiting, bool GrantLive, bool ModelReady = true);

/// <summary>
/// PT-D3 and PT-D4 option B. A single application store owns occurrence keys and
/// weekly receipts. All decisions happen under one lock and persist atomically,
/// so repeated sweeps and restarts cannot admit a second copy of a key.
/// </summary>
public sealed class PersonaSchedulingService
{
    private sealed record State(long? PoolOverride, int MaxOpenPerWorkspace,
        IReadOnlyList<PersonaOccurrenceReceipt> Receipts);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string? _root;
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    public PersonaSchedulingService(IConfiguration configuration)
        : this(string.IsNullOrWhiteSpace(configuration["TaskRepository"]) ? null
            : Path.Combine(configuration["TaskRepository"]!, ".metadata", "persona-scheduling")) { }

    public PersonaSchedulingService(string? root) => _root = root;

    public IReadOnlyList<PersonaOccurrenceReceipt> Receipts(string workspaceId)
    {
        lock (_gate) return [.. Load(workspaceId).Receipts];
    }

    /// <summary>The project token ledger reads completed receipt usage as consumer persona:&lt;id&gt;.</summary>
    public IReadOnlyList<PersonaOccurrenceReceipt> TokenReceipts(string project)
    {
        lock (_gate)
        {
            var ids = _root is null || !Directory.Exists(_root) ? []
                : Directory.GetFiles(_root, "*.json").Select(Path.GetFileNameWithoutExtension).ToArray();
            return ids.OfType<string>().Concat(_states.Keys).Distinct(StringComparer.Ordinal).SelectMany(id => Load(id).Receipts)
                .Where(r => r.State == PersonaOccurrenceStates.Completed && r.Tokens > 0
                    && string.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }

    public PersonaBudgetWeek Budget(string workspaceId, DateTime now, long? workspaceLimit,
        IReadOnlyList<PersonaActivationV1> activations)
    {
        lock (_gate) return BudgetLocked(Load(workspaceId), now, workspaceLimit, activations);
    }

    public void SetBudget(string workspaceId, long? poolTokens, int maxOpenPerWorkspace,
        long activeAllowances = 0)
    {
        if (poolTokens is < 0 || maxOpenPerWorkspace is < 1 or > 4)
            throw new ArgumentException("Persona pool must be nonnegative and open occurrences 1 to 4.");
        if (poolTokens is { } size && activeAllowances > size)
            throw new ArgumentException("Active persona allowances exceed the pool.");
        lock (_gate)
        {
            var state = Load(workspaceId) with { PoolOverride = poolTokens, MaxOpenPerWorkspace = maxOpenPerWorkspace };
            Save(workspaceId, state);
        }
    }

    public IReadOnlyList<PersonaOccurrenceReceipt> Sweep(string workspaceId, DateTime now,
        long? workspaceLimit, IReadOnlyList<PersonaActivationV1> activations,
        IReadOnlyDictionary<string, IReadOnlyList<PersonaTrigger>> triggers,
        Func<PersonaActivationV1, PersonaAdmissionFacts> facts,
        TimeZoneInfo? timeZone = null)
    {
        lock (_gate)
        {
            var state = Load(workspaceId);
            var receipts = state.Receipts.ToList();
            var changed = new List<PersonaOccurrenceReceipt>();
            for (var n = 0; n < receipts.Count; n++)
            {
                var claimed = receipts[n];
                if (claimed.State == PersonaOccurrenceStates.Admitted &&
                    claimed.CreatedAtUtc.AddMinutes(30) <= now)
                {
                    receipts[n] = claimed with { State = PersonaOccurrenceStates.Failed,
                        Reason = "runner-lease-unclaimed",
                        NoCalls = [.. claimed.NoCalls,
                            new PersonaNoCallAttempt(now, "runner-lease-unclaimed")] };
                    changed.Add(receipts[n]);
                    continue;
                }
                if (claimed.State != PersonaOccurrenceStates.Claimed || claimed.ClaimedAtUtc is null ||
                    claimed.ClaimedAtUtc.Value.AddMinutes(
                        claimed.TriggerRefs.Contains("investigation", StringComparer.Ordinal) ? 15 : 5) > now)
                    continue;
                receipts[n] = claimed with { State = PersonaOccurrenceStates.Failed,
                    Reason = "persona-lease-expired" };
                changed.Add(receipts[n]);
            }
            foreach (var activation in activations.Where(a => a.State is PersonaStates.Active or PersonaStates.Shadow))
            {
                var due = Due(activation, now, triggers.GetValueOrDefault(activation.PersonaId) ?? [],
                    timeZone ?? TimeZoneInfo.Utc)
                    .Concat(receipts.Where(r => r.PersonaId == activation.PersonaId
                        && r.DescriptorRevision == activation.DescriptorRevision
                        && r.State == PersonaOccurrenceStates.Waiting && r.WindowEndUtc.AddDays(7) > now)
                        .Select(r => (WindowEnd: r.WindowEndUtc, Refs: r.TriggerRefs)))
                    .DistinctBy(item => item.WindowEnd)
                    .OrderBy(item => item.WindowEnd).ToArray();
                foreach (var (windowEnd, refs) in due)
                {
                    var key = Key(workspaceId, activation.PersonaId, windowEnd, activation.DescriptorRevision);
                    var floors = (triggers.GetValueOrDefault(activation.PersonaId) ?? [])
                        .Where(t => refs.Contains(t.Ref) && t.Floor >= 70)
                        .GroupBy(t => t.Ref, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Max(t => t.Floor), StringComparer.Ordinal);
                    var existing = receipts.FindIndex(r => r.Key == key);
                    if (existing >= 0)
                    {
                        var joined = receipts[existing].TriggerRefs.Concat(refs)
                            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                        var current = receipts[existing];
                        var mergedFloors = current.FactFloors.Concat(floors)
                            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                            .ToDictionary(g => g.Key, g => g.Max(pair => pair.Value), StringComparer.Ordinal);
                        var updated = joined.SequenceEqual(current.TriggerRefs) && mergedFloors.Count == current.FactFloors.Count
                            && mergedFloors.All(pair => current.FactFloors.GetValueOrDefault(pair.Key) == pair.Value)
                            ? current : current with { TriggerRefs = joined, FactFloors = mergedFloors };
                        if (current.State == PersonaOccurrenceStates.Waiting)
                        {
                            var retryBudget = BudgetLocked(state with { Receipts = receipts }, now, workspaceLimit, activations);
                            var retryAdmission = Available(facts(activation), receipts, now);
                            var retryReason = Refusal(activation, retryBudget.Personas.GetValueOrDefault(activation.PersonaId),
                                retryBudget, state.MaxOpenPerWorkspace, receipts, now, retryAdmission);
                            if (retryReason is null)
                                updated = updated with { State = PersonaOccurrenceStates.Admitted, Reason = null,
                                    HostId = retryAdmission.IdleHostIds.First(), CreatedAtUtc = now };
                            else if (retryReason != current.Reason)
                                updated = updated with { Reason = retryReason,
                                    NoCalls = [.. current.NoCalls, new PersonaNoCallAttempt(now, retryReason)] };
                        }
                        if (updated != current)
                        {
                            receipts[existing] = updated;
                            changed.Add(updated);
                        }
                        continue;
                    }
                    var budget = BudgetLocked(state with { Receipts = receipts }, now, workspaceLimit, activations);
                    var allowance = budget.Personas.GetValueOrDefault(activation.PersonaId);
                    var admission = Available(facts(activation), receipts, now);
                    var reason = Refusal(activation, allowance, budget, state.MaxOpenPerWorkspace,
                        receipts, now, admission);
                    var host = reason is null ? admission.IdleHostIds.FirstOrDefault() : null;
                    var receipt = new PersonaOccurrenceReceipt(key, workspaceId, activation.PersonaId,
                        activation.RoleId ?? activation.Descriptor.Id, activation.DescriptorRevision,
                        windowEnd, now, reason is null ? PersonaOccurrenceStates.Admitted : PersonaOccurrenceStates.Waiting,
                        reason, refs, host)
                    {
                        NoCalls = reason is null ? [] : [new PersonaNoCallAttempt(now, reason)],
                        FactFloors = floors,
                        DescriptorDigest = activation.DescriptorDigest,
                    };
                    receipts.Add(receipt);
                    changed.Add(receipt);
                }
            }
            if (changed.Count > 0) Save(workspaceId, state with { Receipts = receipts });
            return changed;
        }
    }

    private static PersonaAdmissionFacts Available(PersonaAdmissionFacts raw,
        IReadOnlyList<PersonaOccurrenceReceipt> receipts, DateTime now)
    {
        var occupied = receipts.Where(r => r.State is PersonaOccurrenceStates.Admitted or PersonaOccurrenceStates.Claimed
            && r.CreatedAtUtc.AddMinutes(30) > now).Select(r => r.HostId).ToHashSet();
        return raw with { IdleHostIds = raw.IdleHostIds.Where(id => !occupied.Contains(id)).ToArray() };
    }

    /// <summary>PT-4 records a completed call; this is idempotent for a replayed receipt.</summary>
    public PersonaOccurrenceReceipt Complete(string workspaceId, string key, long input, long output,
        long cacheRead, long cacheWrite, string? project)
    {
        if (input < 0 || output < 0 || cacheRead < 0 || cacheWrite < 0)
            throw new ArgumentException("Token counts cannot be negative.");
        lock (_gate)
        {
            var state = Load(workspaceId);
            var current = state.Receipts.Single(r => r.Key == key);
            if (current.State == PersonaOccurrenceStates.Completed) return current;
            if (current.State is not (PersonaOccurrenceStates.Admitted or PersonaOccurrenceStates.Claimed))
                throw new InvalidOperationException("Only an admitted occurrence can consume tokens.");
            var completed = current with { State = PersonaOccurrenceStates.Completed,
                InputTokens = input, OutputTokens = output, CacheReadTokens = cacheRead,
                CacheWriteTokens = cacheWrite, Project = project };
            Save(workspaceId, state with { Receipts = state.Receipts.Select(r => r.Key == key ? completed : r).ToList() });
            return completed;
        }
    }

    public PersonaOccurrenceReceipt? Claim(string workspaceId, string key, string hostId, DateTime now,
        string groundsDigest, string model)
    {
        lock (_gate)
        {
            var state = Load(workspaceId);
            var current = state.Receipts.FirstOrDefault(r => r.Key == key);
            if (current is null || current.State != PersonaOccurrenceStates.Admitted || current.HostId != hostId
                || current.CreatedAtUtc.AddMinutes(30) <= now) return null;
            var claimed = current with { State = PersonaOccurrenceStates.Claimed,
                ClaimToken = Guid.NewGuid().ToString("N"), ClaimedAtUtc = now,
                GroundsDigest = groundsDigest, Model = model };
            Save(workspaceId, state with { Receipts = state.Receipts.Select(r => r.Key == key ? claimed : r).ToArray() });
            return claimed;
        }
    }

    public PersonaOccurrenceReceipt? Finish(string workspaceId, string key, string hostId,
        string claimToken, bool success, string? reason, long input, long output, string? project,
        string? actualModel = null, long cacheRead = 0, long cacheWrite = 0)
    {
        lock (_gate)
        {
            var state = Load(workspaceId);
            var current = state.Receipts.FirstOrDefault(r => r.Key == key);
            if (current is null || current.State != PersonaOccurrenceStates.Claimed
                || current.HostId != hostId || current.ClaimToken != claimToken) return null;
            var finished = current with { State = success ? PersonaOccurrenceStates.Completed : PersonaOccurrenceStates.Failed,
                Reason = reason, InputTokens = Math.Max(0, input), OutputTokens = Math.Max(0, output),
                CacheReadTokens = Math.Max(0, cacheRead), CacheWriteTokens = Math.Max(0, cacheWrite),
                Project = project, ActualModel = actualModel, FinishedAtUtc = DateTime.UtcNow };
            Save(workspaceId, state with { Receipts = state.Receipts.Select(r => r.Key == key ? finished : r).ToArray() });
            return finished;
        }
    }

    public void BindSession(string workspaceId, string key, string claimToken, string sessionId)
    {
        lock (_gate)
        {
            var state = Load(workspaceId);
            var current = state.Receipts.Single(r => r.Key == key);
            if (current.State != PersonaOccurrenceStates.Claimed || current.ClaimToken != claimToken)
                throw new InvalidOperationException("Persona lease changed before session binding.");
            Save(workspaceId, state with { Receipts = state.Receipts.Select(r => r.Key == key
                ? current with { SessionId = sessionId } : r).ToArray() });
        }
    }

    private static string? Refusal(PersonaActivationV1 activation, PersonaBudgetPersona? allowance,
        PersonaBudgetWeek budget, int maxOpen, IReadOnlyList<PersonaOccurrenceReceipt> receipts,
        DateTime now, PersonaAdmissionFacts facts)
    {
        if (!facts.GrantLive) return "grant-not-live";
        if (!facts.MeterComplete) return "workspace-meter-unavailable";
        if (facts.QuotaStatus != WorkspaceQuotaPolicy.Ok) return "workspace-quota-warning";
        if (budget.Personas.Values.Sum(x => x.AllowanceTokens) > budget.PoolTokens)
            return "allowances-exceed-pool";
        if (allowance is null || allowance.UsedTokens >= allowance.AllowanceTokens
            || allowance.UsedSessions >= allowance.AllowanceSessions) return "persona-allowance-exhausted";
        if (budget.UsedTokens >= budget.PoolTokens) return "persona-pool-exhausted";
        if (budget.UsedSessions >= budget.Personas.Values.Sum(x => x.AllowanceSessions))
            return "persona-pool-sessions-exhausted";
        var open = receipts.Where(r => r.State is PersonaOccurrenceStates.Admitted or PersonaOccurrenceStates.Claimed
            && r.CreatedAtUtc.AddMinutes(activation.Allowance.MinutesPerSession) > now).ToList();
        if (open.Any(r => r.PersonaId == activation.PersonaId)) return "persona-occurrence-open";
        if (open.Count >= maxOpen) return "workspace-occurrences-full";
        if (facts.ReadyCardWaiting) return "ready-card-waiting";
        if (facts.IdleHostIds.Count == 0) return "runner-slot-unavailable";
        if (!facts.ModelReady) return "model-route-wait";
        return null;
    }

    private static PersonaBudgetWeek BudgetLocked(State state, DateTime now, long? workspaceLimit,
        IReadOnlyList<PersonaActivationV1> activations)
    {
        var start = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var end = start.AddDays(7);
        var week = state.Receipts.Where(r => r.CreatedAtUtc >= start && r.CreatedAtUtc < end).ToList();
        var pool = state.PoolOverride ?? (workspaceLimit is { } limit
            ? Math.Max(0, limit / 10) : 1_200_000);
        var byPersona = activations.ToDictionary(a => a.PersonaId, a =>
        {
            var rows = week.Where(r => r.PersonaId == a.PersonaId).ToList();
            return new PersonaBudgetPersona(a.State == PersonaStates.Inactive ? 0 : a.Allowance.TokensPerWeek,
                a.State == PersonaStates.Inactive ? 0 : a.Allowance.SessionsPerWeek + (a.Allowance.InvestigationsPerWeek ?? 0),
                rows.Sum(r => r.Tokens), rows.Count(r => r.State != PersonaOccurrenceStates.Waiting));
        }, StringComparer.Ordinal);
        return new(start, end, pool, week.Sum(r => r.Tokens),
            week.Count(r => r.State != PersonaOccurrenceStates.Waiting), byPersona);
    }

    private static IReadOnlyList<(DateTime WindowEnd, IReadOnlyList<string> Refs)> Due(
        PersonaActivationV1 activation, DateTime now, IReadOnlyList<PersonaTrigger> triggers,
        TimeZoneInfo timeZone)
    {
        var windows = new Dictionary<DateTime, HashSet<string>>();
        var cadence = activation.Descriptor.Cadence;
        var period = PersonaDescriptorValidator.ReportPeriod(cadence);
        var time = TimeSpan.TryParse(cadence.DueAtLocal, out var parsed) ? parsed : TimeSpan.Zero;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, timeZone);
        var localActivation = TimeZoneInfo.ConvertTimeFromUtc(activation.ActivatedAt, timeZone);
        var scheduledLocal = localNow.Date.Add(time);
        if (scheduledLocal > localNow) scheduledLocal = scheduledLocal.AddDays(-1);
        var elapsed = (scheduledLocal.Date - localActivation.Date).TotalDays;
        if (elapsed >= 0 && elapsed % period.TotalDays == 0
            && (cadence.Days is null || cadence.Days.Contains(scheduledLocal.DayOfWeek.ToString().ToLowerInvariant())))
        {
            var scheduledUtc = TimeZoneInfo.ConvertTimeToUtc(scheduledLocal, timeZone);
            if (scheduledUtc >= activation.ActivatedAt)
                windows[scheduledUtc] = new HashSet<string>(["cadence"], StringComparer.Ordinal);
        }
        if (activation.Attributes.Thoroughness >= 4 &&
            activation.Allowance.InvestigationsPerWeek is > 0 &&
            cadence.Investigation == "P7D" && elapsed >= 0 && elapsed % 7 == 0)
        {
            var investigationUtc = TimeZoneInfo.ConvertTimeToUtc(scheduledLocal, timeZone).AddSeconds(1);
            if (investigationUtc <= now && investigationUtc >= activation.ActivatedAt)
                windows[investigationUtc] = new HashSet<string>(["investigation"], StringComparer.Ordinal);
        }
        foreach (var trigger in triggers.Where(t => t.At >= activation.ActivatedAt && t.At <= now
            && (t.Type == "manual.run" || activation.Descriptor.Triggers.Contains(t.Type, StringComparer.Ordinal))))
        {
            var hours = trigger.Floor >= 90 ? 1 : 6;
            var end = trigger.Type == "manual.run" ? trigger.At :
                trigger.At.Date.AddHours((Math.Floor(trigger.At.TimeOfDay.TotalHours / hours) + 1) * hours);
            if (!windows.TryGetValue(end, out var refs)) windows[end] = refs = new(StringComparer.Ordinal);
            refs.Add(trigger.Ref);
        }
        return windows.OrderBy(w => w.Key).Select(w => (w.Key, (IReadOnlyList<string>)w.Value.Order(StringComparer.Ordinal).ToArray())).ToArray();
    }

    public static string Key(string workspaceId, string personaId, DateTime windowEnd, string revision)
        => string.Join("|", workspaceId, personaId, windowEnd.ToString("O"), revision);

    private State Load(string workspaceId)
    {
        if (_states.TryGetValue(workspaceId, out var state)) return state;
        var path = PathFor(workspaceId);
        state = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? new(null, 2, [])
            : new(null, 2, []);
        _states[workspaceId] = state;
        return state;
    }

    private void Save(string workspaceId, State state)
    {
        if (PathFor(workspaceId) is { } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
            File.Move(temp, path, true);
        }
        _states[workspaceId] = state;
    }

    private string? PathFor(string workspaceId)
    {
        if (_root is null) return null;
        if (!PersonaDescriptorValidator.Slug().IsMatch(workspaceId))
            throw new ArgumentException("Invalid workspace id.");
        return Path.Combine(_root, workspaceId + ".json");
    }
}
