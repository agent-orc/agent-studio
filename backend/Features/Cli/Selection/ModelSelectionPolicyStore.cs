using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Projects;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Cli;

/// <summary>A consumer either follows catalogue equivalence or names ordered routes.</summary>
public sealed record ConsumerPolicyRoute(string Cli, string? Model = null, string? Thinking = null)
{
    public SelectionRoute ToSelectionRoute() => new(Cli, Model, Thinking);
}

[JsonConverter(typeof(ConsumerFallbacksConverter))]
public sealed record ConsumerFallbacks(string? Mode, IReadOnlyList<ConsumerPolicyRoute> Routes)
{
    public static ConsumerFallbacks Catalogue { get; } = new("catalogue", []);
}

public sealed class ConsumerFallbacksConverter : JsonConverter<ConsumerFallbacks>
{
    public override ConsumerFallbacks Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var mode = reader.GetString();
            if (mode != "catalogue") throw new JsonException("fallbacks must be catalogue or a route array.");
            return ConsumerFallbacks.Catalogue;
        }
        if (reader.TokenType == JsonTokenType.StartArray)
            return new ConsumerFallbacks(null, JsonSerializer.Deserialize<List<ConsumerPolicyRoute>>(ref reader, options) ?? []);
        throw new JsonException("fallbacks must be catalogue or a route array.");
    }

    public override void Write(Utf8JsonWriter writer, ConsumerFallbacks value, JsonSerializerOptions options)
    {
        if (value.Mode == "catalogue") writer.WriteStringValue("catalogue");
        else JsonSerializer.Serialize(writer, value.Routes, options);
    }
}

public sealed record ConsumerModelPolicy(
    ConsumerPolicyRoute? Primary = null,
    string? Route = null,
    ConsumerFallbacks? Fallbacks = null,
    bool SameClassOnly = false,
    bool StickyWithinCard = false,
    double? CostGuardFactor = null);

public sealed record ModelSelectionHysteresis(int ReturnBelowCapPoints = 10, int MinDwellMinutes = 30);
public sealed record ModelSelectionMigration(int InheritedSteps, int ExplicitSteps, int Projects, bool Applied);

public sealed record ModelSelectionPolicy
{
    public int Version { get; init; } = 1;
    public Dictionary<string, ConsumerModelPolicy> Consumers { get; init; } = Defaults();
    public Dictionary<string, Dictionary<string, int>> Caps { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = new(StringComparer.OrdinalIgnoreCase) { ["weekly"] = 95, ["5h"] = 95 },
        ["codex"] = new(StringComparer.OrdinalIgnoreCase) { ["weekly"] = 95 },
    };
    public double CostGuardFactor { get; init; } = 2.0;
    public ModelSelectionHysteresis Hysteresis { get; init; } = new();
    public Dictionary<string, SelectionRoute> ModelFallbacks { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-6-sol:xhigh"] = new SelectionRoute("claude", "claude-opus-5", "xhigh"),
    };
    // Compatibility projection for the old model-routes endpoint. The old file
    // is read once and never written again.
    public Dictionary<string, CliModelRouteProfile> LegacyProfiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public ModelSelectionMigration Migration { get; init; } = new(0, 0, 0, false);

    private static Dictionary<string, ConsumerModelPolicy> Defaults()
    {
        var review = new ConsumerModelPolicy(
            new ConsumerPolicyRoute("claude", "claude-opus-5", "high"),
            Fallbacks: new ConsumerFallbacks(null, [new ConsumerPolicyRoute("codex", "gpt-6-sol", "high")]),
            SameClassOnly: true, StickyWithinCard: true);
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["coding"] = new(Route: "qualification", Fallbacks: ConsumerFallbacks.Catalogue),
            ["review-aspect"] = review,
            ["review-deep"] = review,
            ["review-grade"] = review,
            ["concept-fit"] = review,
            ["chat"] = new(new ConsumerPolicyRoute("codex"), Fallbacks: ConsumerFallbacks.Catalogue),
            ["one-shot"] = new(new ConsumerPolicyRoute("claude", "claude-haiku-4-5"), Fallbacks: ConsumerFallbacks.Catalogue),
            ["merge-resolver"] = new(new ConsumerPolicyRoute("claude", "claude-opus-5", "high"), Fallbacks: ConsumerFallbacks.Catalogue),
            ["card-creation"] = new(Route: "qualification", Fallbacks: ConsumerFallbacks.Catalogue),
        };
    }
}

/// <summary>One workspace policy, with a one-time migration of legacy routes and step pins.</summary>
public sealed class ModelSelectionPolicyStore(
    IConfiguration config, ProjectSettingsService projects, IModelSelectionLedger ledger)
{
    private readonly object _lock = new();
    private ModelSelectionPolicy? _cached;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ModelSelectionPolicy Get()
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;
            var path = Path.Combine(Root(), "model-selection-policy.json");
            if (File.Exists(path))
            {
                var stored = JsonSerializer.Deserialize<ModelSelectionPolicy>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException($"Invalid model selection policy: {path}");
                return _cached = stored with
                {
                    Consumers = new(stored.Consumers, StringComparer.OrdinalIgnoreCase),
                    Caps = stored.Caps.ToDictionary(pair => pair.Key,
                        pair => new Dictionary<string, int>(pair.Value, StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase),
                    ModelFallbacks = new(stored.ModelFallbacks, StringComparer.OrdinalIgnoreCase),
                    LegacyProfiles = new(stored.LegacyProfiles, StringComparer.OrdinalIgnoreCase),
                };
            }

            var policy = new ModelSelectionPolicy();
            var legacyPath = Path.Combine(Root(), "cli-model-routing.json");
            if (File.Exists(legacyPath))
            {
                var profiles = JsonSerializer.Deserialize<List<CliModelRouteProfile>>(File.ReadAllText(legacyPath), Json) ?? [];
                policy = policy with { LegacyProfiles = profiles.Where(p => !string.IsNullOrWhiteSpace(p.CliType))
                    .ToDictionary(p => p.CliType, StringComparer.OrdinalIgnoreCase) };
            }
            var capsPath = Path.Combine(Root(), "cli-quota-caps.json");
            if (File.Exists(capsPath))
            {
                var oldCaps = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(
                    File.ReadAllText(capsPath), Json);
                if (oldCaps is not null) policy = policy with { Caps = oldCaps };
            }
            var migration = projects.MigrateModelSelectionSteps(policy.LegacyProfiles);
            policy = policy with { Migration = migration };
            Persist(policy);
            _cached = policy;
            ledger.Record(new SelectionLedgerEntry(Guid.CreateVersion7().ToString("N"), DateTime.UtcNow,
                "policy-migrated", "workspace", null, null, null, null, null, null, null,
                "policy-migration", new SelectionRoute("", null, null), new SelectionRoute("", null, null),
                "consumer-default", $"Migrated {migration.InheritedSteps} inherited and {migration.ExplicitSteps} explicit step pins across {migration.Projects} projects.",
                null, null, null, null));
            return policy;
        }
    }

    public ModelSelectionPolicy SetConsumer(string consumer, ConsumerModelPolicy setting)
    {
        lock (_lock)
        {
            var current = Get();
            if (!current.Consumers.ContainsKey(consumer)) throw new ArgumentException("Unknown consumer class.");
            var consumers = new Dictionary<string, ConsumerModelPolicy>(current.Consumers, StringComparer.OrdinalIgnoreCase)
            {
                [consumer] = setting,
            };
            _cached = current with { Consumers = consumers };
            Persist(_cached);
            return _cached;
        }
    }

    public ModelSelectionPolicy SetLegacyProfile(CliModelRouteProfile profile)
    {
        lock (_lock)
        {
            var current = Get();
            var profiles = new Dictionary<string, CliModelRouteProfile>(current.LegacyProfiles, StringComparer.OrdinalIgnoreCase)
            {
                [profile.CliType] = profile,
            };
            _cached = current with { LegacyProfiles = profiles };
            Persist(_cached);
            return _cached;
        }
    }

    public ModelSelectionPolicy SetCap(string cli, string window, int value)
    {
        lock (_lock)
        {
            var current = Get();
            var caps = current.Caps.ToDictionary(pair => pair.Key,
                pair => new Dictionary<string, int>(pair.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            if (!caps.TryGetValue(cli, out var windows)) caps[cli] = windows = new(StringComparer.OrdinalIgnoreCase);
            windows[window] = value;
            _cached = current with { Caps = caps };
            Persist(_cached);
            return _cached;
        }
    }

    private string Root() => string.IsNullOrWhiteSpace(config["TaskRepository"])
        ? LocalStateRoot.Combine("agent-taskboard") : config["TaskRepository"]!;

    private void Persist(ModelSelectionPolicy policy)
    {
        var path = Path.Combine(Root(), "model-selection-policy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(policy, Json));
        File.Move(temporary, path, true);
    }
}
