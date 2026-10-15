using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.Runner;

public sealed record OrchestrationDecisionReceipt(
    string Occurrence, string Trigger, string InputsDigest, string Action,
    string Reason, string Decider, DateTime RecordedAt);

public sealed record OrchestrationSwitchReceipt(
    string Profile, string From, string To, string Actor, string Reason, DateTime RecordedAt);

public sealed record OrchestrationMismatch(
    OrchestrationDecisionReceipt Monolith, OrchestrationDecisionReceipt Engine);

public sealed record OrchestrationProfileStatus(
    string Profile, string State, int Streak, int Threshold, int MismatchCount,
    OrchestrationMismatch? LastMismatch);

public sealed class OrchestrationRolloutStore
{
    public static string LibraryVersion => typeof(TransitionDecisionPolicy).Assembly.GetName()
        .Version?.ToString(3) ?? "unknown";
    public static readonly string[] Profiles = ["CodingCompletion", "ReviewReport", "GateResult"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly object Gate = new();
    private readonly IConfiguration _configuration;
    private readonly string? _path;
    private readonly int _threshold;

    public OrchestrationRolloutStore(IConfiguration configuration)
    {
        _configuration = configuration;
        _path = PathFor(configuration);
        _threshold = Math.Clamp(configuration.GetValue("Orchestration:ShadowMatchThreshold", 50), 1, 10000);
    }

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
                "engine" => OrchestrationExecutionMode.Engine,
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
                return new OrchestrationProfileStatus(profile, item.State, item.Streak,
                    _threshold, item.Mismatches.Count, item.Mismatches.LastOrDefault());
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
        lock (Gate)
        {
            var state = Read(_path);
            var item = state.Profiles.GetValueOrDefault(profile) ?? Initial(profile);
            var next = target.ToLowerInvariant() switch
            {
                "shadow" when item.State is "monolith" or "rolled-back" => "shadow",
                "engine" when item.State == "ready" => "engine",
                "rollback" when item.State is "engine" or "ready" or "shadow" => "rolled-back",
                _ => throw new InvalidOperationException("orchestration-profile-switch-not-allowed"),
            };
            state.Switches.Add(new(profile, item.State, next, actor, reason, DateTime.UtcNow));
            item.State = next;
            if (next == "shadow") item.Streak = 0;
            state.Profiles[profile] = item;
            Save(state);
            return new(profile, item.State, item.Streak, _threshold,
                item.Mismatches.Count, item.Mismatches.LastOrDefault());
        }
    }

    public void Compare(string profile, OrchestrationDecisionReceipt monolith,
        OrchestrationDecisionReceipt engine)
    {
        ValidateProfile(profile);
        lock (Gate)
        {
            var state = Read(_path);
            var item = state.Profiles.GetValueOrDefault(profile) ?? Initial(profile);
            if (!item.ComparedOccurrences.Add(engine.Occurrence)) return;
            if (monolith.InputsDigest == engine.InputsDigest
                && monolith.Action == engine.Action)
            {
                item.Streak++;
                if (item.Streak >= _threshold && item.State == "shadow") item.State = "ready";
            }
            else
            {
                item.Streak = 0;
                if (item.State == "ready") item.State = "shadow";
                item.Mismatches.Add(new(monolith, engine));
            }
            state.Profiles[profile] = item;
            Save(state);
        }
    }

    /// <summary>
    /// Counts an occurrence that cannot be compared as a break in the streak.
    /// Like Compare, it counts each occurrence once.
    /// </summary>
    public void Discount(string profile, string occurrence)
    {
        ValidateProfile(profile);
        lock (Gate)
        {
            var state = Read(_path);
            var item = state.Profiles.GetValueOrDefault(profile) ?? Initial(profile);
            if (!item.ComparedOccurrences.Add(occurrence)) return;
            item.Streak = 0;
            if (item.State == "ready") item.State = "shadow";
            state.Profiles[profile] = item;
            Save(state);
        }
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
            _configuration[$"Orchestration:Transitions:{profile}"]
            ?? _configuration["Orchestration:ExecutionMode"]);
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
        public List<OrchestrationMismatch> Mismatches { get; set; } = [];
    }
}
