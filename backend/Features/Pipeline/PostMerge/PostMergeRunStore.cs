using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Runner;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Project-scoped T3 authority. Writes are atomic and run IDs fence late completions.</summary>
public sealed class PostMergeRunStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly object _gate = new();

    private readonly Dictionary<string, (DateTime WriteTime, long Length, PostMergeAcceptanceEvidence Evidence)> _acceptance = [];

    public PostMergeRunStore(string? root = null)
    {
        _root = root ?? LocalStateRoot.Combine("agentstudio", "post-merge");
    }

    internal string Root => _root;

    public PostMergeProjectState SetDevelopDisposition(string projectId, string developState, string? lineStop,
        string? inconclusiveRunId = null)
    {
        lock (_gate)
        {
            var state = ReadLocked(projectId) with { DevelopState = developState, LineStop = lineStop,
                InconclusiveRunId = inconclusiveRunId };
            WriteLocked(projectId, state);
            return state;
        }
    }

    public PostMergeProjectState Read(string projectId)
    {
        lock (_gate) return ReadLocked(projectId);
    }

    internal PostMergeAcceptanceEvidence? ReadAcceptanceEvidence(string projectId)
    {
        lock (_gate)
        {
            var file = new FileInfo(PathFor(projectId));
            if (!file.Exists) return null;
            if (_acceptance.TryGetValue(projectId, out var cached)
                && cached.WriteTime == file.LastWriteTimeUtc && cached.Length == file.Length)
                return cached.Evidence;
            var json = File.ReadAllText(file.FullName);
            var state = ParseState(json, projectId);
            var receipts = state.Runs.Where(run => run.Status == "green" && run.FinishedAtUtc.HasValue)
                .Reverse().Select(run => new PostMergeLastReport(run.TipSha, run.FinishedAtUtc!.Value)).ToArray();
            var window = state.PendingWindow;
            var waitingSince = window?.AdmissionReason is { Length: > 0 }
                ? JsonSerializer.Deserialize<StoredMetadata>(json, Json)?.AdmissionWaitingSinceUtc ?? window.ObservedAtUtc
                : (DateTimeOffset?)null;
            var version = CanonicalFields.Sha256Hex([state.LastGreenSha, window?.TipSha,
                window?.OpenedAtUtc?.ToString("O"), window?.AdmissionReason, waitingSince?.ToString("O"),
                .. receipts.Select(receipt => receipt.TipSha)]);
            var evidence = new PostMergeAcceptanceEvidence(receipts, state.LastGreenSha, window, waitingSince, version);
            _acceptance[projectId] = (file.LastWriteTimeUtc, file.Length, evidence);
            return evidence;
        }
    }

    public void ObserveWindow(string projectId, PostMergeWindowObservation observation)
    {
        lock (_gate)
        {
            var state = ReadLocked(projectId);
            if (state.PendingWindow?.ObservedAtUtc > observation.ObservedAtUtc
                || state.ActiveRun?.TipSha == observation.TipSha) return;
            WriteLocked(projectId, state with { PendingWindow = observation });
        }
    }

    public void EndGateRun(string projectId, string id, DateTimeOffset now, string reason)
    {
        lock (_gate)
        {
            var state = ReadLocked(projectId);
            if (state.ActiveRun is not { } active || active.Id != id) return;
            WriteLocked(projectId, state with { ActiveRun = null,
                PendingWindow = state.PendingWindow ?? new(active.TipSha,
                    active.WindowOpenedAtUtc ?? active.StartedAtUtc, now, state.WindowMinutes ?? 30, reason),
                DevelopState = state.LineStop is null ? "inconclusive" : "red",
                Runs = [.. state.Runs.TakeLast(511), active with
                    { Status = "inconclusive", FinishedAtUtc = now, Reason = reason }] });
        }
    }

    public void SetGateSubject(string projectId, string? subjectId)
    {
        lock (_gate) WriteLocked(projectId, ReadLocked(projectId) with { GateSubjectId = subjectId });
    }

    public void CloseWindow(string projectId, string reason, DateTimeOffset now, string? expectedTip = null)
    {
        lock (_gate)
        {
            var state = ReadLocked(projectId);
            if (state.PendingWindow is not { } pending || expectedTip is not null && pending.TipSha != expectedTip) return;
            WriteLocked(projectId, state with { PendingWindow = null,
                ClosedWindow = new(pending.TipSha, now, reason), GateSubjectId = null });
        }
    }

    public PostMergeProjectState UpdateIncidents(string projectId,
        Func<IReadOnlyList<PostMergeIncident>, IReadOnlyList<PostMergeIncident>> update)
    {
        lock (_gate)
        {
            var state = ReadLocked(projectId);
            state = state with { Incidents = update(state.Incidents ?? []) };
            WriteLocked(projectId, state);
            return state;
        }
    }

    public PostMergeProjectState? Record(string projectId, PostMergeRunRecord run,
        string? owner = null, long fence = 0, DateTimeOffset? observedAt = null)
    {
        lock (_gate)
        {
            var state = ReadLocked(projectId);
            if (state.Runs.FirstOrDefault(item => item.Id == run.Id) is { } recorded)
                return JsonSerializer.Serialize(recorded, Json) == JsonSerializer.Serialize(run, Json) ? state : null;
            var active = state.ActiveRun;
            var custody = ReadCustodyLocked(projectId);
            var now = observedAt ?? run.StartedAtUtc;
            if (active?.Id == run.Id && custody?.RunId == run.Id
                && !MonolithTransitionAdapter.PostMergeCustodyMayWrite(owner, fence, custody.Owner,
                    custody.Fence, custody.ExpiresAtUtc, now, run.Status == "running", run.Status != "running")) return null;
            if (active?.Id == run.Id && custody is null && owner is not null) return null;
            if (run.Status == "running")
            {
                if (active?.Id == run.Id)
                    return active.TipSha == run.TipSha && active.Kind == run.Kind ? state : null;
                if (active is not null && !MonolithTransitionAdapter.PostMergeCustodyMayReplace(
                        active.StartedAtUtc, active.Kind, custody?.ExpiresAtUtc, now))
                    return null;
                if (active is not null)
                    state = state with { Runs = [.. state.Runs.TakeLast(510), active with
                    {
                        Status = "inconclusive", FinishedAtUtc = now,
                        Reason = "runner claim expired before a terminal receipt",
                    }] };
                custody = owner is null ? null : new(run.Id, owner, 1, now + PostMergeCustodyPolicy.LeaseDuration);
                state = state with { DevelopState = state.LineStop is null ? "running" : "red", ActiveRun = run, PendingWindow = state.PendingWindow?.TipSha == run.TipSha ? null : state.PendingWindow };
            }
            else if (run.Status == "skipped")
            {
                if (active is not null
                    || !string.Equals(run.TipSha, state.LastGreenSha, StringComparison.OrdinalIgnoreCase))
                    return null;
                state = state with { DevelopState = state.LineStop is null ? "green" : "red",
                    Runs = [.. state.Runs.TakeLast(511), run] };
            }
            else if (run.Status is "green" or "red" or "inconclusive")
            {
                if (active is null || active.Id != run.Id || active.TipSha != run.TipSha
                    || active.Kind != run.Kind || run.FinishedAtUtc is null
                    || run.FinishedAtUtc < run.StartedAtUtc
                    || run.Verdicts.Count == 0 && MonolithTransitionAdapter.PostMergeCustodyNeedsVerdict(run.Status)
                    || run.Status == "green" && (run.Verdicts.Count < run.Commands.Count
                        || run.Verdicts.Any(verdict => verdict.ExitCode != 0)
                        && run.Failure?.AllFailuresQuarantined != true))
                    return null;
                state = state with
                {
                    DevelopState = run.Status,
                    AttributionEscalatedTaskId = run.Status == "green"
                        ? null : state.AttributionEscalatedTaskId,
                    LastGreenSha = run.Status == "green" ? run.TipSha : state.LastGreenSha,
                    LastWindowTipSha = MonolithTransitionAdapter.PostMergeCustodyAdvancesWindow(run.Status, run.Kind)
                        ? run.TipSha : state.LastWindowTipSha,
                    ActiveRun = null,
                    FlakyHistory = [.. (state.FlakyHistory ?? [])
                        .Where(item => item.AtUtc >= run.FinishedAtUtc.Value.AddDays(-14)),
                        .. run.FlakyTests.Select(test => new PostMergeFlakyOccurrence(test, run.Id,
                            run.FinishedAtUtc.Value))],
                    Runs = [.. state.Runs.TakeLast(511), run],
                };
            }
            else return null;
            WriteLocked(projectId, state, custody, replaceCustody: true);
            return state;
        }
    }

    public PostMergeCustody? RenewCustody(string projectId, string runId, string owner, long fence,
        DateTimeOffset now, bool recover, string? recoveryId = null)
    {
        lock (_gate)
        {
            var state = ReadLocked(projectId);
            var current = ReadCustodyLocked(projectId);
            if (recover && recoveryId is not null && current?.RecoveryId == recoveryId
                && state.ActiveRun?.Id == runId && current.Owner == owner
                && (fence == current.Fence || fence == current.Fence - 1)) return current;
            if (state.ActiveRun?.Id != runId || current?.RunId != runId
                || !MonolithTransitionAdapter.PostMergeCustodyMayRecover(owner, fence, current.Owner, current.Fence)
                || !recover && !MonolithTransitionAdapter.PostMergeCustodyMayWrite(owner, fence, current.Owner,
                    current.Fence, current.ExpiresAtUtc, now, false)) return null;
            var renewed = current with { Fence = recover ? checked(current.Fence + 1) : current.Fence,
                ExpiresAtUtc = now + PostMergeCustodyPolicy.LeaseDuration,
                RecoveryId = recover ? recoveryId : current.RecoveryId };
            WriteLocked(projectId, state, renewed, replaceCustody: true);
            return renewed;
        }
    }

    public PostMergeCustody? ReadCustody(string projectId)
    {
        lock (_gate) return ReadCustodyLocked(projectId);
    }

    private sealed record StoredMetadata(PostMergeCustody? Custody, PostMergeWindowObservation? PendingWindow,
        DateTimeOffset? AdmissionWaitingSinceUtc);

    private static StoredMetadata? ReadMetadata(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<StoredMetadata>(File.ReadAllText(path), Json) : null;

    private PostMergeCustody? ReadCustodyLocked(string projectId) => ReadMetadata(PathFor(projectId))?.Custody;

    /// <summary>Records the repair card after its own escalation move succeeds; evidence only, never a placement input.</summary>
    public void RecordAttributionEscalation(string projectId, string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("An escalated repair card ID is required.", nameof(taskId));
        lock (_gate)
        {
            var state = ReadLocked(projectId);
            WriteLocked(projectId, state with { AttributionEscalatedTaskId = taskId });
        }
    }

    private PostMergeProjectState ReadLocked(string projectId)
    {
        var path = PathFor(projectId);
        if (!File.Exists(path)) return PostMergeProjectState.Empty(projectId);
        return ParseState(File.ReadAllText(path), projectId);
    }

    private static PostMergeProjectState ParseState(string json, string projectId)
        => JsonSerializer.Deserialize<PostMergeProjectState>(json, Json)
            ?? throw new IOException($"Post-merge state for {projectId} is empty.");

    private void WriteLocked(string projectId, PostMergeProjectState state,
        PostMergeCustody? custody = null, bool replaceCustody = false)
    {
        var path = PathFor(projectId);
        Directory.CreateDirectory(_root);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var document = JsonSerializer.SerializeToNode(state, Json)!;
        var previous = ReadMetadata(path);
        document["custody"] = JsonSerializer.SerializeToNode(replaceCustody ? custody : previous?.Custody, Json);
        if (state.PendingWindow is { AdmissionReason.Length: > 0 } pending)
        {
            var since = previous?.PendingWindow is { AdmissionReason.Length: > 0 } prior && prior.TipSha == pending.TipSha
                ? previous.AdmissionWaitingSinceUtc ?? prior.ObservedAtUtc : pending.ObservedAtUtc;
            document["admissionWaitingSinceUtc"] = since;
        }
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, document, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
        _acceptance.Remove(projectId);
    }

    private string PathFor(string projectId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(projectId, "^PROJ-[0-9]{3,}$"))
            throw new ArgumentException("A stable project ID is required.", nameof(projectId));
        return Path.Combine(_root, projectId + ".json");
    }
}

internal sealed record PostMergeAcceptanceEvidence(IReadOnlyList<PostMergeLastReport> GreenReceipts,
    string? LastGreenSha, PostMergeWindowObservation? PendingWindow, DateTimeOffset? AdmissionWaitingSinceUtc, string Version);

public sealed record PostMergeCustody(string RunId, string Owner, long Fence, DateTimeOffset ExpiresAtUtc, string? RecoveryId = null);
