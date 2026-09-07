using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.Configuration;

/// <summary>
/// Read / write surface for the per-checkout orchestrator + supervisor
/// flags that previously could only be flipped by hand-editing
/// <c>backend/appsettings.Local.json</c>. The catalog is intentionally
/// finite: only flags that gate hosted-service lifecycles or supervisor
/// policy are exposed. Writes persist to disk and reload the in-process
/// configuration immediately; hosted loops read these values at tick
/// boundaries so a backend restart is not required for the exposed flags.
///
/// Stable and dev each have their own <c>appsettings.Local.json</c>; the
/// service writes to whichever copy belongs to the running checkout
/// (resolved via <see cref="IHostEnvironment.ContentRootPath"/>). It
/// does not cross-write.
/// </summary>
public sealed class OrchestratorConfigService
{
    /// <summary>
    /// Configuration values that are concrete model pins. The migration API
    /// may update only this allowlist, never an arbitrary configuration path.
    /// </summary>
    public static readonly IReadOnlySet<string> ModelOverrideKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ClaudeCli:SummaryModel",
            "TitleGeneration:Model",
            "PromptEnhancement:Model",
            "WikiSearch:Model",
            "Supervisor:SoftReasoningModel",
            "ProposalManagement:Model",
            "ReviewDecisionOrchestrator:Model",
            "ReviewDecisionOrchestrator:AspectModel",
            "GlobalOrchestrator:Model",
            "CodexCli:Model",
            "CodexCli:DefaultModel",
            "CodeReviewStep:DefaultModel",
            "TaskSpawnerStep:DefaultModel",
        };

    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _env;
    private readonly ILogger<OrchestratorConfigService> _logger;
    private static readonly object FileLock = new();

    public OrchestratorConfigService(
        IConfiguration configuration,
        IHostEnvironment env,
        ILogger<OrchestratorConfigService> logger)
    {
        _configuration = configuration;
        _env = env;
        _logger = logger;
    }

    public string OverrideFilePath =>
        Path.Combine(_env.ContentRootPath, "appsettings.Local.json");

    public OrchestratorConfigSnapshot GetSnapshot()
    {
        var options = OrchestratorConfigCatalog.Definitions
            .Select(BuildOption)
            .ToList();
        return new OrchestratorConfigSnapshot
        {
            Options = options,
            OverrideFilePath = OverrideFilePath,
            OverrideFileExists = File.Exists(OverrideFilePath)
        };
    }

    /// <summary>
    /// Applies a partial override map to <c>appsettings.Local.json</c>.
    /// Unknown keys are rejected; type mismatches are rejected. Returns
    /// the post-write snapshot after reloading the active configuration.
    /// </summary>
    public OrchestratorConfigSnapshot ApplyOverrides(IDictionary<string, JsonElement> values)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        var defs = OrchestratorConfigCatalog.Definitions
            .ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

        var coerced = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            if (!defs.TryGetValue(pair.Key, out var def))
            {
                throw new ArgumentException($"Unknown config key '{pair.Key}'.", nameof(values));
            }
            coerced[def.Key] = CoerceToNode(def, pair.Value);
        }

        WriteOverrides(coerced, "orchestrator/supervisor");

        return GetSnapshot();
    }

    /// <summary>
    /// Applies one model migration to a known configuration pin. Callers must
    /// still compare the catalog version and source model before invoking this
    /// method; this boundary only enforces the finite writable-key contract.
    /// </summary>
    public bool ApplyModelOverride(string key, string model)
    {
        if (!ModelOverrideKeys.Contains(key))
            throw new ArgumentException($"Unknown model configuration key '{key}'.", nameof(key));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("A concrete target model is required.", nameof(model));

        lock (FileLock)
        {
            var path = OverrideFilePath;
            var existed = File.Exists(path);
            var previous = existed ? File.ReadAllText(path) : null;
            var target = model.Trim();
            WriteOverrides(
                new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    [key] = JsonValue.Create(target),
                },
                "model pin");

            if (string.Equals(
                    ModelMetadataRegistry.NormalizeId(_configuration[key]),
                    ModelMetadataRegistry.NormalizeId(target),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // A command-line or environment provider can outrank the local
            // file. Restore the prior file so a rejected click cannot become
            // an unexpected latent override after that provider disappears.
            if (existed)
                File.WriteAllText(path, previous!);
            else if (File.Exists(path))
                File.Delete(path);
            if (_configuration is IConfigurationRoot rootConfig)
                rootConfig.Reload();
            _logger.LogWarning(
                "Model pin override {Key} was not effective after reload; the previous local file was restored",
                key);
            return false;
        }
    }

    private void WriteOverrides(
        IReadOnlyDictionary<string, JsonNode?> values,
        string category)
    {
        lock (FileLock)
        {
            var path = OverrideFilePath;
            JsonObject root;
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                root = string.IsNullOrWhiteSpace(text)
                    ? new JsonObject()
                    : (JsonNode.Parse(text) as JsonObject ?? new JsonObject());
            }
            else
            {
                root = new JsonObject();
            }

            foreach (var (key, value) in values)
                SetNodeAtPath(root, key, value);

            var serialized = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var temp = path + ".tmp";
            File.WriteAllText(temp, serialized);
            try { File.Replace(temp, path, destinationBackupFileName: null); }
            catch (FileNotFoundException) { File.Move(temp, path); }

            if (_configuration is IConfigurationRoot rootConfig)
                rootConfig.Reload();

            _logger.LogInformation(
                "Wrote {Count} {Category} override(s) to {Path} and reloaded configuration",
                values.Count,
                category,
                path);
        }
    }

    private OrchestratorConfigOption BuildOption(OrchestratorConfigDefinition def)
    {
        var overrideRaw = ReadOverrideValue(def.Key);
        var configRaw = _configuration[def.Key];
        var raw = overrideRaw ?? configRaw;
        var current = ParseValue(def, raw);
        return new OrchestratorConfigOption
        {
            Key = def.Key,
            Group = def.Group,
            Label = def.Label,
            Description = def.Description,
            Type = def.Type,
            EnumOptions = def.EnumOptions,
            DefaultValue = def.DefaultValue,
            CurrentValue = current ?? def.DefaultValue,
            ActiveValue = current ?? def.DefaultValue,
            HasOverride = !string.IsNullOrEmpty(overrideRaw) || !string.IsNullOrEmpty(configRaw),
            AppliesImmediately = true,
            RestartRequired = false,
            RestartRequiredReason = null,
            SourceFile = def.SourceFile
        };
    }

    private string? ReadOverrideValue(string key)
    {
        var path = OverrideFilePath;
        if (!File.Exists(path)) return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root == null) return null;
            JsonNode? cursor = root;
            foreach (var segment in key.Split(':'))
            {
                if (cursor is not JsonObject obj) return null;
                cursor = obj[segment];
                if (cursor == null) return null;
            }
            return cursor switch
            {
                JsonValue v when v.TryGetValue<bool>(out var b) => b.ToString(),
                JsonValue v when v.TryGetValue<int>(out var i) => i.ToString(),
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => cursor.ToJsonString()
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? ParseValue(OrchestratorConfigDefinition def, string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        return def.Type switch
        {
            "bool" => bool.TryParse(raw, out var b) ? b : (object?)null,
            "int" => int.TryParse(raw, out var i) ? i : (object?)null,
            "enum" => raw,
            _ => raw
        };
    }

    private static JsonNode? CoerceToNode(OrchestratorConfigDefinition def, JsonElement value)
    {
        switch (def.Type)
        {
            case "bool":
                if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                    throw new ArgumentException($"Key '{def.Key}' expects a boolean.");
                return JsonValue.Create(value.GetBoolean());
            case "int":
                if (value.ValueKind != JsonValueKind.Number)
                    throw new ArgumentException($"Key '{def.Key}' expects an integer.");
                return JsonValue.Create(value.GetInt32());
            case "enum":
                if (value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException($"Key '{def.Key}' expects a string.");
                var s = value.GetString() ?? "";
                if (def.EnumOptions != null && !def.EnumOptions.Contains(s, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException(
                        $"Key '{def.Key}' must be one of: {string.Join(", ", def.EnumOptions)}.");
                return JsonValue.Create(s);
            default:
                throw new InvalidOperationException($"Unsupported type '{def.Type}'.");
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> at the colon-separated config path
    /// (e.g. <c>Supervisor:MetaCycleEnabled</c>) inside <paramref name="root"/>,
    /// creating intermediate object nodes when needed.
    /// </summary>
    private static void SetNodeAtPath(JsonObject root, string key, JsonNode? value)
    {
        var segments = key.Split(':');
        JsonObject cursor = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (cursor[seg] is JsonObject child)
            {
                cursor = child;
            }
            else
            {
                var fresh = new JsonObject();
                cursor[seg] = fresh;
                cursor = fresh;
            }
        }
        cursor[segments[^1]] = value;
    }
}

public sealed class OrchestratorConfigSnapshot
{
    public List<OrchestratorConfigOption> Options { get; set; } = new();
    public string OverrideFilePath { get; set; } = "";
    public bool OverrideFileExists { get; set; }
}

public sealed class OrchestratorConfigOption
{
    public string Key { get; set; } = "";
    public string Group { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "bool";
    public string[]? EnumOptions { get; set; }
    public object? DefaultValue { get; set; }
    public object? CurrentValue { get; set; }
    public object? ActiveValue { get; set; }
    public bool AppliesImmediately { get; set; } = true;
    public bool HasOverride { get; set; }
    public bool RestartRequired { get; set; }
    public string? RestartRequiredReason { get; set; }
    public string SourceFile { get; set; } = "";
}

internal sealed record OrchestratorConfigDefinition(
    string Key,
    string Group,
    string Label,
    string Description,
    string Type,
    object? DefaultValue,
    string SourceFile,
    string[]? EnumOptions = null);

internal static class OrchestratorConfigCatalog
{
    // Keep these grouped so the UI can render them under three headings
    // without the frontend duplicating any logic. Source-file paths are
    // repo-relative for the "open in editor" link and for review evidence.
    public static readonly OrchestratorConfigDefinition[] Definitions =
    {
        new(
            "ReviewDecisionOrchestrator:Enabled",
            "Orchestrator",
            "Review-decision orchestrator",
            "Auto-review lane gets reissue / accept-as-done / escalate decisions on a tick.",
            "bool",
            DefaultValue: false,
            SourceFile: "backend/Services/Runner/ReviewDecisionOrchestrator.cs"),
        new(
            "ReviewDecisionOrchestrator:IntervalSeconds",
            "Orchestrator",
            "Review-decision tick (seconds)",
            "How often the review-decision orchestrator scans the auto-review lane.",
            "int",
            DefaultValue: 30,
            SourceFile: "backend/Services/Runner/ReviewDecisionOrchestrator.cs"),
        new(
            "Orchestrator:PrepEnabled",
            "Orchestrator",
            "Orchestrator prep lane",
            "Hosted service that processes incoming orchestrator-prep jobs.",
            "bool",
            DefaultValue: false,
            SourceFile: "backend/Services/Supervisor/OrchestratorPrepHostedService.cs"),
        new(
            "Orchestrator:SessionTurns:ActiveLimit",
            "Orchestrator",
            "Session-turn active limit",
            "Maximum orchestrator session turns allowed to run at once before later turns report queued positions.",
            "int",
            DefaultValue: 4,
            SourceFile: "backend/Features/Orchestrator/OrchestratorTurnService.cs"),
        new(
            "Supervisor:MetaCycleEnabled",
            "Supervisor",
            "Layer-2.5 meta-cycle",
            "Quiet-batch meta-cycle: pause, inspect evidence, write report, resume / queue / escalate.",
            "bool",
            DefaultValue: false,
            SourceFile: "backend/Services/Supervisor/MetaCycleHostedService.cs"),
        new(
            "Supervisor:SoftReasoningEnabled",
            "Supervisor",
            "Soft-reasoning pass",
            "Layer-2 soft-reasoning second-opinion pass over runner state.",
            "bool",
            DefaultValue: false,
            SourceFile: "backend/Services/Supervisor/SoftReasoningHostedService.cs"),
        new(
            "Supervisor:HardCheckEnabled",
            "Supervisor",
            "Hard health checks",
            "Periodic deterministic health checks over the runner / workspace.",
            "bool",
            DefaultValue: true,
            SourceFile: "backend/Services/Supervisor/HardHealthCheckHostedService.cs"),
        new(
            "Supervisor:ChatNoteEnabled",
            "Supervisor",
            "Supervisor chat-notes",
            "Periodic [supervisor] chat-notes summarising recent observations.",
            "bool",
            DefaultValue: true,
            SourceFile: "backend/Services/Supervisor/ChatNoteHostedService.cs"),
        new(
            "Supervisor:AutoInterventionEnabled",
            "Auto-Intervention",
            "Auto-intervention",
            "Promote selected advisories to automatic pause / cancel / fail / resume invocations. Gated by ADR-0017.",
            "bool",
            DefaultValue: false,
            SourceFile: "backend/Services/Supervisor/AutoInterventionHostedService.cs"),
        new(
            "Supervisor:AutoInterventionRateLimit",
            "Auto-Intervention",
            "Rate limit (per project / hour)",
            "Maximum auto-intervention invocations per project per rolling hour.",
            "int",
            DefaultValue: 3,
            SourceFile: "backend/Services/Supervisor/AutoInterventionHostedService.cs"),
        new(
            "Supervisor:AutoInterventionSeverityThreshold",
            "Auto-Intervention",
            "Severity threshold",
            "Minimum advisory severity that may trigger an auto-intervention.",
            "enum",
            DefaultValue: "High",
            SourceFile: "backend/Services/Supervisor/AutoInterventionHostedService.cs",
            EnumOptions: new[] { "Info", "Warn", "High" }),
    };
}
