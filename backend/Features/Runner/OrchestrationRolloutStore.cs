using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.Runner;

public sealed record OrchestrationDecisionReceipt(
    string Occurrence, string Trigger, string InputsDigest, string Action,
    string Reason, string Decider, DateTime RecordedAt);

/// <param name="Override">
/// AGT-3218: an operator forced the engine without a passed applier canary.
/// </param>
public sealed record OrchestrationSwitchReceipt(
    string Profile, string From, string To, string Actor, string Reason, DateTime RecordedAt,
    bool Override = false);

public sealed record OrchestrationMismatch(
    OrchestrationDecisionReceipt Monolith, OrchestrationDecisionReceipt Engine);

public sealed record OrchestrationProfileStatus(
    string Profile, string State, int Streak, int Threshold, int MismatchCount,
    OrchestrationMismatch? LastMismatch,
    OrchestrationApplierContractStatus? ApplierContract = null,
    OrchestrationCanaryStatus? Canary = null,
    EngineEffectCheck? LastEffectCheck = null);

/// <summary>Whether every action the profile can complete has an applier effect row.</summary>
public sealed record OrchestrationApplierContractStatus(string Status, IReadOnlyList<string> Actions,
    IReadOnlyList<string> Uncovered);

/// <summary>One engine decision the applier canary watches.</summary>
public sealed record OrchestrationCanaryObservation(string Occurrence, string Action, DateTime ObservedAt,
    string Status, EngineEffectCheck? LastCheck = null);

/// <summary>
/// The applier canary of a profile: <c>watching</c> the first engine decisions
/// after a switch, <c>passed</c>, <c>failed</c> (rolled back unless
/// <paramref name="Override"/>), or <c>stopped</c> by an operator rollback.
/// An observation is <c>pending</c>, <c>verified</c>, <c>not-counted</c> (an
/// ask, a checkpoint, a handoff, a refusal by design, a failed probe, or a
/// wait that reached the apply deadline), or <c>missing</c>.
/// </summary>
/// <param name="Override">An operator forced the engine; a failed canary alerts but does not roll back.</param>
/// <param name="AwaitingRecoveryPass">
/// The canary watches but judges nothing until the recovery service finished
/// one pass over the applying occurrences after this process started.
/// </param>
public sealed record OrchestrationCanaryStatus(string Status, int Required, int Verified, string InstanceId,
    DateTime StartedAt, DateTime? EndedAt, IReadOnlyList<OrchestrationCanaryObservation> Observations,
    bool PassedOnThisInstance, bool Override = false, bool AwaitingRecoveryPass = false);

public sealed class OrchestrationRolloutStore
{
    public static string LibraryVersion => typeof(TransitionDecisionPolicy).Assembly.GetName()
        .Version?.ToString(3) ?? "unknown";
    public static readonly string[] Profiles = ["CodingCompletion", "ReviewReport", "GateResult", "BatchPlan"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly object Gate = new();
    public const string CanaryDecisionsKey = "Orchestration:ApplierCanary:Decisions";
    public const string CanaryWindowKey = "Orchestration:ApplierCanary:WindowSeconds";
    public const string ApplyDeadlineKey = "Orchestration:ApplierCanary:ApplyDeadlineSeconds";
    public const string CanaryActor = "applier-canary";
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly string? _path;
    private readonly int _threshold;

    public OrchestrationRolloutStore(IConfiguration configuration) : this(configuration, TimeProvider.System) { }

    public OrchestrationRolloutStore(IConfiguration configuration, TimeProvider time)
    {
        _configuration = configuration;
        _time = time;
        _path = PathFor(configuration);
        _threshold = Math.Clamp(configuration.GetValue("Orchestration:ShadowMatchThreshold", 50), 1, 10000);
        InstanceId = configuration["Orchestration:InstanceId"] is { Length: > 0 } configured ? configured
            : $"{Environment.MachineName}/{BuildCommit(configuration)}";
    }

    /// <summary>
    /// This host and build. An applier canary passes for the applier code that
    /// ran it, so a passed canary counts only on the same instance.
    /// </summary>
    public string InstanceId { get; }

    /// <summary>
    /// Set by the applier canary while it waits for the first recovery pass
    /// after this process started; the canary status shows it.
    /// </summary>
    public bool AwaitingRecoveryPass
    {
        get => Volatile.Read(ref _awaitingRecoveryPass);
        set => Volatile.Write(ref _awaitingRecoveryPass, value);
    }

    private bool _awaitingRecoveryPass;

    /// <summary>Engine decisions the canary watches after a switch (default 3).</summary>
    public int CanaryDecisions => Math.Clamp(_configuration.GetValue(CanaryDecisionsKey, 3), 1, 50);

    /// <summary>
    /// How long a completed decision may take to show the effects its applier
    /// writes, counted from CompletedAt (default two minutes).
    /// </summary>
    public TimeSpan CanaryWindow
        => TimeSpan.FromSeconds(Math.Clamp(_configuration.GetValue(CanaryWindowKey, 120), 10, 3600));

    /// <summary>
    /// How long the applier may take to complete a watched decision, counted
    /// from the observation, and how long a later pass may take to write a
    /// downstream effect, counted from CompletedAt (default three hours). An
    /// AcceptAndIntegrate decision awaits the whole integration, queue and
    /// delivery gate included, before it completes.
    /// </summary>
    public TimeSpan ApplyDeadline
        => TimeSpan.FromSeconds(Math.Clamp(_configuration.GetValue(ApplyDeadlineKey, 10800), 300, 86400));

    public static OrchestrationExecutionMode? ReadMode(IConfiguration configuration, string profile)
    {
        if (!Profiles.Contains(profile, StringComparer.Ordinal)) return null;
        var path = PathFor(configuration);
        if (path is null) return null;
        lock (Gate)
        {
            var item = Read(path).Profiles.GetValueOrDefault(profile);
            return item?.State switch
            {
                "shadow" or "ready" => OrchestrationExecutionMode.Shadow,
                "canary" or "engine" => OrchestrationExecutionMode.Engine,
                "rolled-back" => OrchestrationExecutionMode.Monolith,
                _ => null,
            };
        }
    }

    public IReadOnlyList<OrchestrationProfileStatus> GetProfiles()
    {
        lock (Gate)
        {
            var state = Read(_path);
            return Profiles.Select(profile =>
            {
                var item = state.Profiles.GetValueOrDefault(profile) ?? Initial(profile);
                return Status(profile, item);
            }).ToArray();
        }
    }

    public bool IsRolledBack(string? profile)
    {
        if (profile is null) return false;
        lock (Gate) return Read(_path).Profiles.GetValueOrDefault(profile)?.State == "rolled-back";
    }

    public IReadOnlyList<OrchestrationMismatch> Mismatches(string profile)
    {
        ValidateProfile(profile);
        lock (Gate) return Read(_path).Profiles.GetValueOrDefault(profile)?.Mismatches.ToArray() ?? [];
    }

    public IReadOnlyList<OrchestrationSwitchReceipt> SwitchReceipts()
    {
        lock (Gate) return Read(_path).Switches.ToArray();
    }

    public OrchestrationProfileStatus Switch(string profile, string target, string actor, string reason)
    {
        ValidateProfile(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        // AGT-3055: behind the Task Server proxy the engine cannot claim batch-plan occurrences.
        if (profile == AgentStudio.Pipeline.BatchGatePilotService.BatchPlanProfile
            && target.ToLowerInvariant() is "shadow" or "canary" or "engine" or "force-engine"
            && AgentStudio.Host.TaskServerPlaneProxy.IsConfigured(_configuration))
            throw new InvalidOperationException("orchestration-profile-batch-plan-task-server-proxy: "
                + AgentStudio.Pipeline.BatchGatePilotService.BatchPlanProxyRefusal(
                    target.ToLowerInvariant() == "shadow" ? OrchestrationExecutionMode.Shadow
                        : OrchestrationExecutionMode.Engine));
        lock (Gate)
        {
            var state = Read(_path);
            var item = state.Profiles.GetValueOrDefault(profile) ?? Initial(profile);
            // AGT-3218: shadow compares receipts and never runs the applier, so
            // Ready proves no effect. Engine needs a canary that passed here,
            // unless an operator forces it in an emergency; that override is
            // audited on the switch receipt and its canary only alerts.
            var lowered = target.ToLowerInvariant();
            var next = lowered switch
            {
                "shadow" when item.State is "monolith" or "rolled-back" => "shadow",
                "canary" when item.State == "ready" => "canary",
                "engine" when item.State == "ready" && PassedHere(item) => "engine",
                "engine" when item.State == "ready"
                    => throw new InvalidOperationException("orchestration-profile-applier-canary-required"),
                "force-engine" when item.State != "engine" => "engine",
                "rollback" when item.State is "engine" or "canary" or "ready" or "shadow" => "rolled-back",
                _ => throw new InvalidOperationException("orchestration-profile-switch-not-allowed"),
            };
            var forced = lowered == "force-engine";
            var now = _time.GetUtcNow().UtcDateTime;
            state.Switches.Add(new(profile, item.State, next, actor, reason, now, forced));
            item.State = next;
            if (next == "shadow") item.Streak = 0;
            // Every switch to the engine watches its first decisions again.
            if (next is "canary" or "engine")
                item.Canary = new CanaryRecord
                {
                    Required = CanaryDecisions, InstanceId = InstanceId, StartedAt = now, Override = forced,
                };
            else if (item.Canary is { Status: "watching" } open)
            {
                open.Status = "stopped";
                open.EndedAt = now;
            }
            state.Profiles[profile] = item;
            Save(state);
            return Status(profile, item);
        }
    }

    /// <summary>
    /// Adds an engine decision of <paramref name="profile"/> to its watching
    /// canary until the canary holds its required count; a not-counted decision
    /// makes room for the next one. Called when the engine completes a
    /// non-shadow occurrence, before the applier runs, so a decision that never
    /// applies is watched too.
    /// </summary>
    public void ObserveEngineDecision(string profile, string occurrence, string action)
    {
        if (!Profiles.Contains(profile, StringComparer.Ordinal)) return;
        lock (Gate)
        {
            var state = Read(_path);
            if (state.Profiles.GetValueOrDefault(profile) is not { State: "canary" or "engine" } item
                || item.Canary is not { Status: "watching" } canary
                || canary.Observations.Count(value => value.Status is "pending" or "verified") >= canary.Required
                || canary.Observations.Any(value => value.Occurrence == occurrence)) return;
            canary.Observations.Add(new(occurrence, action, _time.GetUtcNow().UtcDateTime, "pending"));
            Save(state);
        }
    }

    /// <summary>Watched decisions whose effects are not verified yet.</summary>
    public IReadOnlyList<(string Profile, OrchestrationCanaryObservation Observation)> PendingCanaryObservations()
    {
        lock (Gate)
            return Read(_path).Profiles
                .Where(pair => pair.Value.Canary is { Status: "watching" })
                .SelectMany(pair => pair.Value.Canary!.Observations
                    .Where(value => value.Status == "pending")
                    .Select(value => (pair.Key, value)))
                .ToArray();
    }

    /// <summary>
    /// Records an effect check of a watched decision with its verdict (by
    /// default: verified for a verified row check, otherwise waiting). A
    /// verified check counts; a not-counted one leaves its place to the next
    /// engine decision. When the required decisions are verified the canary
    /// passes on this instance and a canary profile becomes engine.
    /// </summary>
    public OrchestrationProfileStatus RecordEffectCheck(string profile, EngineEffectCheck check,
        ApplierCanaryVerdict? verdict = null)
    {
        ValidateProfile(profile);
        lock (Gate)
        {
            var state = Read(_path);
            var item = state.Profiles.GetValueOrDefault(profile) ?? Initial(profile);
            var canary = item.Canary is { Status: "watching" } watching ? watching : null;
            var index = canary?.Observations.FindIndex(value => value.Occurrence == check.Occurrence) ?? -1;
            if (canary is null || index < 0 || canary.Observations[index].Status != "pending")
                return Status(profile, item);
            var observation = canary.Observations[index];
            var status = (verdict ?? (ApplierCanaryPolicy.Counts(check)
                ? ApplierCanaryVerdict.Verified : ApplierCanaryVerdict.Waiting)) switch
            {
                // A check that does not count never promotes the canary.
                ApplierCanaryVerdict.Verified => ApplierCanaryPolicy.Counts(check) ? "verified" : "pending",
                ApplierCanaryVerdict.NotCounted => "not-counted",
                ApplierCanaryVerdict.Missing => throw new ArgumentException(
                    "A missing effect fails the canary; call FailCanary.", nameof(verdict)),
                _ => "pending",
            };
            // An unchanged pending check is not written again.
            if (status == "pending" && observation.LastCheck is { } last
                && last.Missing.SequenceEqual(check.Missing) && last.Evidence == check.Evidence)
                return Status(profile, item);
            canary.Observations[index] = observation with { Status = status, LastCheck = check };
            item.LastEffectCheck = check;
            if (canary.Observations.Count(value => value.Status == "verified") >= canary.Required)
            {
                canary.Status = "passed";
                canary.EndedAt = check.CheckedAt;
                item.CanaryPasses.RemoveAll(pass => pass.InstanceId == canary.InstanceId);
                item.CanaryPasses.Add(new(canary.InstanceId, check.CheckedAt, canary.Required));
                if (item.State == "canary")
                {
                    state.Switches.Add(new(profile, "canary", "engine", CanaryActor,
                        $"The first {canary.Required} engine decisions applied their effects.", check.CheckedAt));
                    item.State = "engine";
                }
            }
            state.Profiles[profile] = item;
            Save(state);
            return Status(profile, item);
        }
    }

    /// <summary>
    /// A watched decision lacks an effect after its deadline: the canary fails,
    /// this instance loses an earlier pass (a direct switch back to engine
    /// needs a new canary), and the profile rolls back to the monolith unless
    /// an operator forced the engine. Returns null when the canary was already
    /// resolved, so a caller alerts once.
    /// </summary>
    public OrchestrationProfileStatus? FailCanary(string profile, EngineEffectCheck check)
    {
        ValidateProfile(profile);
        lock (Gate)
        {
            var state = Read(_path);
            var item = state.Profiles.GetValueOrDefault(profile) ?? Initial(profile);
            var canary = item.Canary is { Status: "watching" } watching ? watching : null;
            var index = canary?.Observations.FindIndex(value => value.Occurrence == check.Occurrence) ?? -1;
            if (canary is null || index < 0) return null;
            canary.Observations[index] = canary.Observations[index] with { Status = "missing", LastCheck = check };
            canary.Status = "failed";
            canary.EndedAt = check.CheckedAt;
            item.LastEffectCheck = check;
            item.CanaryPasses.RemoveAll(pass => pass.InstanceId == InstanceId || pass.InstanceId == canary.InstanceId);
            if (item.State is "canary" or "engine" && !canary.Override)
            {
                state.Switches.Add(new(profile, item.State, "rolled-back", CanaryActor,
                    $"Engine decision {check.Occurrence} ({check.Action}) is missing "
                    + $"{string.Join(", ", check.Missing)}: {check.Evidence}", check.CheckedAt));
                item.State = "rolled-back";
            }
            state.Profiles[profile] = item;
            Save(state);
            return Status(profile, item);
        }
    }

    /// <summary>Every action the profile can complete has an applier effect row.</summary>
    public static OrchestrationApplierContractStatus ApplierContract(string profile)
    {
        var actions = EngineApplierContract.ProfileActions.GetValueOrDefault(profile) ?? [];
        var uncovered = actions.Where(action => EngineApplierContract.Find(action) is null).ToArray();
        return new(actions.Count > 0 && uncovered.Length == 0 ? "covered" : "uncovered", actions, uncovered);
    }

    private bool PassedHere(ProfileRecord item)
        => item.CanaryPasses.Any(pass => pass.InstanceId == InstanceId);

    private OrchestrationProfileStatus Status(string profile, ProfileRecord item)
        => new(profile, item.State, item.Streak, _threshold, item.Mismatches.Count,
            item.Mismatches.LastOrDefault(), ApplierContract(profile),
            item.Canary is { } canary
                ? new OrchestrationCanaryStatus(canary.Status, canary.Required,
                    canary.Observations.Count(value => value.Status == "verified"), canary.InstanceId,
                    canary.StartedAt, canary.EndedAt, canary.Observations.ToArray(), PassedHere(item),
                    canary.Override, canary.Status == "watching" && AwaitingRecoveryPass)
                : null,
            item.LastEffectCheck);

    private static string BuildCommit(IConfiguration configuration)
    {
        try { return AgentStudio.Runtime.BuildIdentity.Load(configuration).Commit; }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
        {
            // An unreadable manifest still names this build by its assembly version.
            return typeof(OrchestrationRolloutStore).Assembly.GetName().Version?.ToString() ?? "unknown";
        }
    }

    public void Compare(string profile, OrchestrationDecisionReceipt monolith,
        OrchestrationDecisionReceipt engine, DateTime? createdAt = null, long creationSequence = 0)
    {
        ValidateProfile(profile);
        lock (Gate)
        {
            var state = Read(_path);
            var item = state.Profiles.GetValueOrDefault(profile) ?? Initial(profile);
            var matched = monolith.InputsDigest == engine.InputsDigest
                && monolith.Action == engine.Action;
            // Older journals have ComparedOccurrences but no ordered outcomes.
            // Recovery supplies their creation time and backfills the order.
            if (!item.Outcomes.TryAdd(engine.Occurrence,
                    new(createdAt ?? engine.RecordedAt, creationSequence, matched))) return;
            if (item.ComparedOccurrences.Add(engine.Occurrence) && !matched)
            {
                item.Mismatches.Add(new(monolith, engine));
            }
            RecomputeStreak(item);
            state.Profiles[profile] = item;
            Save(state);
        }
    }

    /// <summary>
    /// Counts an occurrence that cannot be compared as a break in the streak.
    /// Like Compare, it counts each occurrence once.
    /// </summary>
    public void Discount(string profile, string occurrence, DateTime? createdAt = null,
        long creationSequence = 0)
    {
        ValidateProfile(profile);
        lock (Gate)
        {
            var state = Read(_path);
            var item = state.Profiles.GetValueOrDefault(profile) ?? Initial(profile);
            if (!item.Outcomes.TryAdd(occurrence, new(createdAt ?? DateTime.MaxValue,
                    creationSequence, false))) return;
            item.ComparedOccurrences.Add(occurrence);
            RecomputeStreak(item);
            state.Profiles[profile] = item;
            Save(state);
        }
    }

    private void RecomputeStreak(ProfileRecord item)
    {
        item.Streak = 0;
        foreach (var outcome in item.Outcomes.OrderBy(pair => pair.Value.CreationSequence)
                     .ThenBy(pair => pair.Value.CreatedAt)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
            item.Streak = outcome.Matched ? item.Streak + 1 : 0;
        if (item.State == "ready" && item.Streak < _threshold) item.State = "shadow";
        else if (item.State == "shadow" && item.Streak >= _threshold) item.State = "ready";
    }

    public static OrchestrationDecisionReceipt Receipt(string occurrence, string trigger,
        string payload, string action, string reason, string decider)
        => new(occurrence, trigger,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
            action, reason, decider, DateTime.UtcNow);

    private static void ValidateProfile(string profile)
    {
        if (!Profiles.Contains(profile, StringComparer.Ordinal))
            throw new ArgumentException("Unknown orchestration transition profile.");
    }

    private ProfileRecord Initial(string profile)
    {
        var mode = OrchestrationExecutionModeParser.Parse(
            _configuration[$"Orchestration:Transitions:{profile}"]);
        return new ProfileRecord { State = mode switch
        {
            OrchestrationExecutionMode.Engine => "engine",
            OrchestrationExecutionMode.Shadow => "shadow",
            _ => "monolith",
        } };
    }

    private static string? PathFor(IConfiguration configuration)
        => string.IsNullOrWhiteSpace(configuration["TaskRepository"]) ? null
            : Path.Combine(configuration["TaskRepository"]!, ".orchestration-rollout.json");

    private static State Read(string? path)
        => path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Orchestration rollout state is empty.")
            : new State();

    private void Save(State state)
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public sealed class State
    {
        public Dictionary<string, ProfileRecord> Profiles { get; set; } = new(StringComparer.Ordinal);
        public List<OrchestrationSwitchReceipt> Switches { get; set; } = [];
    }

    public sealed class ProfileRecord
    {
        public string State { get; set; } = "monolith";
        public int Streak { get; set; }
        public HashSet<string> ComparedOccurrences { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, ShadowOutcome> Outcomes { get; set; } = new(StringComparer.Ordinal);
        public List<OrchestrationMismatch> Mismatches { get; set; } = [];
        public CanaryRecord? Canary { get; set; }
        public List<CanaryPass> CanaryPasses { get; set; } = [];
        public EngineEffectCheck? LastEffectCheck { get; set; }
    }

    public sealed class CanaryRecord
    {
        public string Status { get; set; } = "watching";
        public int Required { get; set; }
        public string InstanceId { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public bool Override { get; set; }
        public List<OrchestrationCanaryObservation> Observations { get; set; } = [];
    }

    public sealed record CanaryPass(string InstanceId, DateTime PassedAt, int Decisions);

    public sealed record ShadowOutcome(DateTime CreatedAt, long CreationSequence, bool Matched);
}
