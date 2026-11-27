using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;

namespace AgentStudio.Dispatch;

public sealed record DispatchLeaseV1(string Project, string Persona, long Fence, DateTimeOffset ExpiresAt)
{
    public DispatchLeaseState? Rollout { get; init; }
    public string? Actor { get; init; }
    public string? Reason { get; init; }
    public string? Window { get; init; }
}
public sealed record DispatchShadowRecordV1(string EvaluationId, string DecisionId, string FactsDigest,
    DateTimeOffset At, string Path, DispatchPlan Plan, bool Shadow = true)
{
    public string? Window { get; init; }
}

/// <summary>Dispatcher-specific PersonaLogEntryV1 fields for later PT-18 ingestion. No item bodies or secrets.</summary>
public sealed record DispatcherLogEntryV1(string Id, string Project, string PersonaId, string Kind,
    string OccurrenceId, DateTimeOffset At, string DecisionId, string StrategyId, string StrategyRevision,
    string Rule, string FactsDigest, IReadOnlyList<DispatchPassOver> PassedOver,
    IReadOnlyList<string> ExpectedOverlap, string Reason, bool Shadow, string? AuditId,
    string? PreviousDigest, string Digest)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpectedEffect { get; init; }
}

public sealed partial class DispatchStore
{
    private static readonly ConcurrentDictionary<string, object> PersonaLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (long Length, string? Digest)> _dispatcherHeads = new(StringComparer.Ordinal);
    private object PersonaGate(string project) => PersonaLocks.GetOrAdd(Project(project), _ => new object());
    public DispatchLeaseV1? ReadLease(string project)
    {
        lock (PersonaGate(project)) return Read<DispatchLeaseV1>(Path.Combine(Project(project), "lease.json"));
    }

    /// <summary>Fenced lease for DS-6/DS-7. A descriptor's live mode never acquires authority.</summary>
    public DispatchLeaseV1? AcquireLease(string project, string persona, long expectedFence,
        DateTimeOffset now, TimeSpan duration, bool active, bool liveGrants, bool shadowGatePassed)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(duration));
        lock (PersonaGate(project))
        {
            var old = ReadLease(project);
            if (old?.Rollout is not null) return null;
            if (!MonolithTransitionAdapter.DispatcherLease(persona, active, liveGrants, shadowGatePassed,
                old is null || old.ExpiresAt <= now, expectedFence, old?.Fence ?? 0)) return null;
            var lease = new DispatchLeaseV1(project, persona, (old?.Fence ?? 0) + 1, now + duration);
            Write(Path.Combine(Project(project), "lease.json"), lease);
            return lease;
        }
    }

    public bool ReleaseLease(string project, string persona, long fence, DateTimeOffset now)
    {
        lock (PersonaGate(project))
        {
            var lease = ReadLease(project);
            if (lease is null || lease.Persona != persona || lease.Fence != fence) return false;
            Write(Path.Combine(Project(project), "lease.json"), lease with { ExpiresAt = now });
            return true;
        }
    }

    public IReadOnlyList<DispatchShadowRecordV1> ReadShadow(string project)
    {
        lock (PersonaGate(project))
        {
            var folder = Path.Combine(Project(project), "shadow");
            return !Directory.Exists(folder) ? [] : Directory.EnumerateFiles(folder, "*.json")
                .Select(Read<DispatchShadowRecordV1>).OfType<DispatchShadowRecordV1>()
                .OrderBy(row => row.At).ThenBy(row => row.DecisionId, StringComparer.Ordinal).ToArray();
        }
    }

    public IReadOnlyList<DispatcherLogEntryV1> ReadDispatcherLog(string project)
    {
        lock (PersonaGate(project))
        {
            var path = Path.Combine(Project(project), "dispatcher.jsonl");
            return !File.Exists(path) ? [] : File.ReadLines(path).Where(line => line.Length > 0)
                .Select(line => JsonSerializer.Deserialize<DispatcherLogEntryV1>(line, Json)!).ToArray();
        }
    }

    public void AppendShadow(DispatchShadowRecordV1 record)
    {
        lock (PersonaGate(record.Plan.Project))
        {
            var recordPath = Path.Combine(Project(record.Plan.Project), "shadow", Key(record.DecisionId) + ".json");
            if (File.Exists(recordPath)) return;
            var path = Path.Combine(Project(record.Plan.Project), "dispatcher.jsonl");
            var length = File.Exists(path) ? new FileInfo(path).Length : 0;
            // Reuse the append head in this process. Another writer or a restart
            // reloads it once; pickup never rereads the entire history per persona.
            var previous = _dispatcherHeads.TryGetValue(path, out var head) && head.Length == length
                ? head.Digest : ReadDispatcherLog(record.Plan.Project).LastOrDefault()?.Digest;
            var plan = record.Plan;
            var passed = plan.Slots.SelectMany(slot => slot.PassedOver).ToArray();
            var rule = plan.Slots.Count == 0 ? "no-free-slots" : string.Join(',', plan.Slots.Select(slot => slot.ReasonCode).Distinct());
            var passedCards = passed.Select(p => p.CardKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var reason = $"{plan.Persona}: {plan.Admitted.Count} planned admissions, {passedCards} passed over; {rule}";
            if (reason.Length > 280) reason = reason[..280];
            var entry = new DispatcherLogEntryV1(record.DecisionId, plan.Project, plan.Persona, "dispatch-plan",
                record.EvaluationId, record.At, record.DecisionId, plan.StrategyId, plan.StrategyRevision,
                rule, record.FactsDigest, passed, passed.Select(p => p.Detail).OfType<string>().Distinct().ToArray(),
                reason, record.Shadow, null, previous, "")
            { ExpectedEffect = $"{plan.Admitted.Count} candidates are planned to start; {passedCards} distinct candidates were passed over during slot evaluation under {rule}." };
            entry = entry with { Digest = LogDigest(entry) };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Write(recordPath, record);
            File.AppendAllText(path, JsonSerializer.Serialize(entry, Json) + "\n");
            _dispatcherHeads[path] = (new FileInfo(path).Length, entry.Digest);
        }
    }

    public bool VerifyDispatcherLog(string project)
    {
        string? previous = null;
        foreach (var row in ReadDispatcherLog(project))
        {
            if (row.PreviousDigest != previous || row.Digest != LogDigest(row with { Digest = "" })) return false;
            previous = row.Digest;
        }
        return true;
    }
    private static string LogDigest(DispatcherLogEntryV1 row)
        => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row, Json))));
}
