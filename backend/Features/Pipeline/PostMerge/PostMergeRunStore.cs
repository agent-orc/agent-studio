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
            if (run.Commands is null || run.Verdicts is null
                || run.Commands.Any(string.IsNullOrWhiteSpace))
                return null;
            var state = ReadLocked(projectId);
            if (state.Runs.FirstOrDefault(item => item.Id == run.Id) is { } recorded)
                return JsonSerializer.Serialize(recorded, Json) == JsonSerializer.Serialize(run, Json) ? state : null;
            // Older receipts may contain duplicate plan entries. Only their exact
            // persisted replay is valid; new claims and receipts require uniqueness.
            if (run.Commands.Distinct(StringComparer.Ordinal).Count() != run.Commands.Count)
                return null;
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
                    return active.TipSha == run.TipSha && active.Kind == run.Kind
                        && active.Project == run.Project && active.StartedAtUtc == run.StartedAtUtc
                        && active.Commands.SequenceEqual(run.Commands, StringComparer.Ordinal) ? state : null;
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
                    || active.Kind != run.Kind || active.Project != run.Project || active.StartedAtUtc != run.StartedAtUtc
                    || !ValidCommandCoverage(active, run) || run.FinishedAtUtc is null
                    || run.FinishedAtUtc < run.StartedAtUtc
                    || run.Verdicts.Count == 0 && MonolithTransitionAdapter.PostMergeCustodyNeedsVerdict(run.Status)
                    || run.Status == "green" && (run.Verdicts.Any(verdict => verdict.TimedOut)
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

    // Receipt integrity belongs at the persistence boundary. Custody and state
    // transitions remain decisions of the orchestration adapter above.
    private static bool ValidCommandCoverage(PostMergeRunRecord active, PostMergeRunRecord run)
    {
        if (!active.Commands.SequenceEqual(run.Commands, StringComparer.Ordinal)) return false;
        var coverage = active.Commands.ToDictionary(command => command,
            _ => new List<PostMergeCommandVerdict>(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var shardIds = new HashSet<(string Command, int Index)>();
        foreach (var verdict in run.Verdicts)
        {
            if (verdict is null || string.IsNullOrWhiteSpace(verdict.Command) || !seen.Add(verdict.Command))
                return false;
            if (coverage.TryGetValue(verdict.Command, out var exact))
            {
                exact.Add(verdict);
                continue;
            }
            // The host represents shards with a suite ID, an explicit terminal
            // shard count, and generated settings commands. Only a suite that
            // opted into sharding at start may replace its original command.
            var suite = verdict.SuiteId;
            if (suite is null || !ShardCount(active.Envelope?.ShardPlan, suite, out var planned)
                || !ShardCount(run.Envelope?.ShardPlan, suite, out var completed)
                || planned != "auto" && planned != completed
                || !int.TryParse(completed, out var count) || count < 1 || count > ProjectTestSuite.MaxShards
                || run.SuiteSelection is not { } selection
                || !selection.SuiteCommands.TryGetValue(suite, out var original)
                || original is null || !coverage.ContainsKey(original)
                || active.SuiteSelection?.SuiteCommands is { Count: > 0 } frozen
                    && (!frozen.TryGetValue(suite, out var frozenCommand) || frozenCommand != original)
                || !original.Contains("dotnet test ", StringComparison.Ordinal)
                || !original.Contains("--no-build", StringComparison.Ordinal)) return false;
            var bare = System.Text.RegularExpressions.Regex.Replace(original,
                "\\s--filter(?:\\s+|=)(?:\"[^\"]*\"|'[^']*'|\\S+)", "").Trim();
            var prefix = bare + " --settings '";
            if (verdict.Command.Length <= prefix.Length
                || !verdict.Command.StartsWith(prefix, StringComparison.Ordinal)
                || !verdict.Command.EndsWith("'", StringComparison.Ordinal)) return false;
            var settingsArgument = verdict.Command[prefix.Length..^1];
            // The runner shell-quotes one path, escaping apostrophes as '\''.
            // An unescaped quote ends that argument and permits extra arguments.
            const string escapedQuote = "'\\''";
            if (settingsArgument.Replace(escapedQuote, "", StringComparison.Ordinal).Contains('\''))
                return false;
            var settings = settingsArgument.Replace(escapedQuote, "'", StringComparison.Ordinal).Replace('\\', '/');
            var marker = "/post-merge-" + active.Id + "/shard-";
            var markerIndex = settings.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0 || !settings.EndsWith(".runsettings", StringComparison.Ordinal)
                || !int.TryParse(settings[(markerIndex + marker.Length)..^12], out var index)
                || index < 0 || index >= count
                || settings[(markerIndex + marker.Length)..^12]
                    != index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || !shardIds.Add((original, index))) return false;
            coverage[original].Add(verdict);
        }
        foreach (var (command, verdicts) in coverage)
        {
            if (verdicts.Count == 0)
            {
                if (run.Status == "green") return false;
                continue; // Red and interrupted runs may stop before the next command.
            }
            if (verdicts.Any(verdict => verdict.Command == command))
            {
                if (verdicts.Count != 1) return false;
                continue;
            }
            var suite = verdicts[0].SuiteId!;
            if (verdicts.Any(verdict => verdict.SuiteId != suite)
                || !ShardCount(run.Envelope?.ShardPlan, suite, out var completed)
                || !int.TryParse(completed, out var count)
                || (run.Status == "green" ? verdicts.Count != count : verdicts.Count > count)) return false;
            // Paths are shard identities, even if a command uses different separators.
            var paths = verdicts.Select(verdict => verdict.Command.Replace('\\', '/')).ToArray();
            if (paths.Select(path => path[..path.LastIndexOf('/')]).Distinct(StringComparer.Ordinal).Count() != 1)
                return false;
        }
        return run.Status != "green" || active.Commands.Count > 0;
    }

    private static bool ShardCount(string? plan, string suite, out string count)
    {
        var entries = (plan ?? "").Split(',').Where(entry => entry.StartsWith(suite + "=", StringComparison.Ordinal)).ToArray();
        count = entries.Length == 1 ? entries[0][(suite.Length + 1)..] : "";
        return entries.Length == 1;
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

    private PostMergeCustody? ReadCustodyLocked(string projectId)
        => File.Exists(PathFor(projectId))
            ? JsonNode.Parse(File.ReadAllText(PathFor(projectId)))?["custody"]?.Deserialize<PostMergeCustody>(Json) : null;

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
        return JsonSerializer.Deserialize<PostMergeProjectState>(File.ReadAllText(path), Json)
            ?? throw new IOException($"Post-merge state for {projectId} is empty.");
    }

    private void WriteLocked(string projectId, PostMergeProjectState state,
        PostMergeCustody? custody = null, bool replaceCustody = false)
    {
        var path = PathFor(projectId);
        Directory.CreateDirectory(_root);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var document = JsonSerializer.SerializeToNode(state, Json)!;
        document["custody"] = JsonSerializer.SerializeToNode(replaceCustody ? custody : ReadCustodyLocked(projectId), Json);
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, document, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    private string PathFor(string projectId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(projectId, "^PROJ-[0-9]{3,}$"))
            throw new ArgumentException("A stable project ID is required.", nameof(projectId));
        return Path.Combine(_root, projectId + ".json");
    }
}

public sealed record PostMergeCustody(string RunId, string Owner, long Fence, DateTimeOffset ExpiresAtUtc, string? RecoveryId = null);
