using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>
/// Discovers the live Codex CLI model catalog by asking the local CLI for its
/// current model list inside a pseudo-terminal. Results are cached on disk so
/// the UI does not spawn Codex on every model dropdown open.
/// </summary>
public sealed class CodexModelDiscovery
{
    /// <summary>Label used in the "not offered by the installed ..." note.</summary>
    private const string CliLabel = "codex-cli";

    private readonly ILogger<CodexModelDiscovery> _logger;
    private readonly IConfiguration _config;
    private readonly CliVersionTracker? _versionTracker;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CliModelCatalog? _memCache;
    private DateTime _memCacheAt = DateTime.MinValue;

    public CodexModelDiscovery(
        ILogger<CodexModelDiscovery> logger,
        IConfiguration config,
        CliVersionTracker? versionTracker = null)
    {
        _logger = logger;
        _config = config;
        _versionTracker = versionTracker;
    }

    private string CachePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "agent-taskboard");
            Directory.CreateDirectory(dir);
            // v2: entries written by this version carry the CLI-reported
            // reasoning ladder. A v1 file cannot be told apart from one whose
            // ladder came from the static table, so it is simply not read.
            return Path.Combine(dir, "codex-model-catalog.v2.json");
        }
    }

    private string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

    private TimeSpan Ttl =>
        TimeSpan.FromMinutes(_config.GetValue<int?>("CodexModelsCacheMinutes") ?? 60);

    public async Task<CliModelCatalog> GetAsync(string cliPath, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh)
        {
            if (_memCache != null && DateTime.UtcNow - _memCacheAt < Ttl)
                return Publish(WithActiveModelApplied(_memCache));

            var fromDisk = TryLoadDisk();
            if (fromDisk != null && DateTime.UtcNow - fromDisk.FetchedAt < Ttl)
            {
                _memCache = fromDisk;
                _memCacheAt = fromDisk.FetchedAt;
                return Publish(WithActiveModelApplied(fromDisk));
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _memCache != null && DateTime.UtcNow - _memCacheAt < Ttl)
                return Publish(WithActiveModelApplied(_memCache));

            try
            {
                var fresh = await DiscoverViaPtyAsync(cliPath, ct);
                _memCache = fresh;
                _memCacheAt = fresh.FetchedAt;
                TrySaveDisk(fresh);
                return Publish(fresh);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Codex PTY model discovery failed; falling back to cached catalog");
                if (_memCache != null) return Publish(WithSource(WithActiveModelApplied(_memCache), "pty-failed-mem-cache"));
                var fromDisk = TryLoadDisk();
                if (fromDisk != null)
                {
                    _memCache = fromDisk;
                    _memCacheAt = fromDisk.FetchedAt;
                    return Publish(WithSource(WithActiveModelApplied(fromDisk), "pty-failed-disk-cache"));
                }
                // Task item 1: when the CLI cannot be queried and there is no
                // cache, fall back to today's static registry list rather than
                // failing the model surface (mirrors ClaudeModelDiscovery).
                return Publish(FallbackCatalog("pty-failed-registry-fallback"));
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<CliModelCatalog> DiscoverViaPtyAsync(string cliPath, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "agent-taskboard-pty-scratch", "codex");
        Directory.CreateDirectory(scratch);
        var outputPath = Path.Combine(scratch, $"codex-models-{Guid.NewGuid():N}.json");

        _logger.LogInformation("Spawning Codex CLI in PTY for model discovery");
        var (app, args, verbatimCommandLine) = BuildModelsCommand(cliPath, outputPath);
        try
        {
            await using var pty = await PtySession.SpawnAsync(
                app: app,
                args: args,
                cwd: scratch,
                extraEnv: CliEnvironment.ProbeEnvironment(),
                cols: 220,
                rows: 80,
                verbatimCommandLine: verbatimCommandLine,
                ct: ct);

            await pty.WaitForIdleAsync(idleMs: 1000, timeoutMs: 10000, ct);

            var output = await WaitForOutputFileAsync(outputPath, pty, ct);
            var models = ParseDebugModelsJson(output, ReadActiveModel());
            if (models.Count == 0)
            {
                var snapshot = pty.SnapshotStripped();
                _logger.LogWarning("Codex model discovery captured 0 models. Snapshot tail:\n{Tail}",
                    snapshot.Length > 1200 ? snapshot[^1200..] : snapshot);
                throw new InvalidOperationException("No models parsed from Codex CLI catalog");
            }

            return new CliModelCatalog
            {
                Models = models,
                Source = "cli-pty",
                FetchedAt = DateTime.UtcNow
            };
        }
        finally
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch (Exception __ex) { SilentCatch.Note(__ex, "CodexModelDiscovery:132"); }
        }
    }

    public static List<CliModelInfo> ParseDebugModelsJson(string output, string? activeModel = null)
    {
        var json = ExtractJsonObject(output);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("models", out var modelArray)
            || modelArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsed = new List<(CliModelInfo Model, int Priority, int Index)>();
        var index = 0;

        foreach (var item in modelArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var visibility = GetString(item, "visibility");
            if (!string.Equals(visibility, "list", StringComparison.OrdinalIgnoreCase)) continue;

            var id = GetString(item, "slug");
            if (string.IsNullOrWhiteSpace(id)) continue;
            id = id.Trim();
            if (!seen.Add(id)) continue;

            var label = GetString(item, "display_name");
            if (string.IsNullOrWhiteSpace(label)) label = id;

            var priority = GetInt(item, "priority") ?? int.MaxValue;
            // Only a model onboarded for live-discovered ladders (gpt-6-astra)
            // takes its ladder/default from this CLI response; every other
            // model - the gpt-5.6 family included - keeps the static table's
            // answer byte-for-byte (AGT-2707 review: the CLI's own
            // default_reasoning_level must not silently override an
            // already-shipped model's product default).
            var (levels, defaultLevel) = ModelMetadataRegistry.UsesLiveDiscoveredThinkingLadder(id)
                ? ReadReasoningLadder(item, id)
                : StaticLadderFor(id);
            parsed.Add((new CliModelInfo
            {
                Id = id,
                Label = label.Trim(),
                Vendor = GuessVendor(id),
                IsDefault = string.Equals(id, activeModel, StringComparison.OrdinalIgnoreCase),
                AvailabilityNote = ModelMetadataRegistry.Find(id) == null
                    ? "Discovered from CLI; missing registry metadata."
                    : null,
                ThinkingLevels = levels,
                DefaultThinkingLevel = defaultLevel
            }, priority, index++));
        }

        var models = parsed
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Index)
            .Select(x => x.Model)
            .ToList();

        if (models.Count > 0 && models.All(m => !m.IsDefault))
        {
            models[0] = models[0] with { IsDefault = true };
        }

        return models;
    }

    /// <summary>
    /// The reasoning ladder for one `codex debug models` entry. The CLI is the
    /// source of truth: it reports <c>supported_reasoning_levels</c> (an array
    /// of <c>{ effort, description }</c>) and <c>default_reasoning_level</c>,
    /// and both drift across releases - 0.153.4 dropped <c>minimal</c> from the
    /// 5.6 family and added <c>max</c>. The static <c>CliThinkingLevels</c>
    /// table answers only when the CLI reports nothing (AGT-2707).
    /// </summary>
    private static (List<string> Levels, string? Default) ReadReasoningLadder(JsonElement item, string id)
    {
        var levels = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("supported_reasoning_levels", out var reported)
            && reported.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in reported.EnumerateArray())
            {
                var effort = entry.ValueKind switch
                {
                    JsonValueKind.String => entry.GetString(),
                    JsonValueKind.Object => GetString(entry, "effort"),
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(effort)) continue;
                var level = effort.Trim().ToLowerInvariant();
                if (seen.Add(level)) levels.Add(level);
            }
        }

        if (levels.Count == 0) return StaticLadderFor(id);

        var reportedDefault = GetString(item, "default_reasoning_level")?.Trim().ToLowerInvariant();
        // A default the CLI does not list is not a rung we can send it; fall
        // back to the AGT-2025 rule (the biggest value the ladder offers).
        var defaultLevel = !string.IsNullOrWhiteSpace(reportedDefault) && seen.Contains(reportedDefault)
            ? reportedDefault
            : levels[^1];
        return (levels, defaultLevel);
    }

    /// <summary>
    /// The answer for a model the CLI reported no ladder for: curated registry
    /// metadata, else the static capability table, with the AGT-2025 codex rule
    /// (the biggest rung on offer) as the default. Deliberately does not read
    /// the published live ladders, so a model the current CLI stopped
    /// describing cannot be answered with a previous release's ladder.
    /// </summary>
    private static (List<string> Levels, string? Default) StaticLadderFor(string id)
    {
        var levels = ModelMetadataRegistry.StaticThinkingLevelsFor(CliTypes.Codex, id);
        return ([.. levels], levels.Count > 0 ? levels[^1] : null);
    }

    /// <summary>
    /// Fill in reasoning ladders a cached catalog is missing. A ladder that is
    /// already present came from the CLI itself and must survive: recomputing
    /// it from the static table is exactly the drift this replaced (AGT-2707).
    /// </summary>
    internal static CliModelCatalog WithCurrentCodexCapabilities(CliModelCatalog cat)
    {
        var models = cat.Models.Select(m =>
        {
            if (m.ThinkingLevels is { Count: > 0 }) return m;
            var (levels, defaultLevel) = StaticLadderFor(m.Id);
            return m with { ThinkingLevels = levels, DefaultThinkingLevel = defaultLevel };
        }).ToList();

        return cat with { Models = models };
    }

    /// <summary>
    /// Union of the live catalog and the registry: a model this Studio knows
    /// for OpenAI but the installed codex-cli does not list is appended as
    /// unavailable-with-a-reason so it renders disabled instead of vanishing
    /// (AGT-2707). Recomputed on every read, so the merge follows both the
    /// current registry and the currently installed CLI.
    /// </summary>
    internal static CliModelCatalog WithKnownButUnavailableModels(CliModelCatalog cat, string? cliVersion)
    {
        var models = ModelMetadataRegistry.AppendUnavailableRegistryEntries(
                cat.Models,
                vendor: "openai",
                cliType: CliTypes.Codex,
                availabilityNote: ModelMetadataRegistry.UnavailableOnInstalledCliNote(CliLabel, cliVersion));
        return cat with
        {
            Models = models.Select(model => model.Available ? model : model with
            {
                AvailabilityNote = ModelMetadataRegistry.UnavailableOnInstalledCliNote(
                    CliLabel,
                    cliVersion,
                    model.Id)
            }).ToList()
        };
    }

    /// <summary>
    /// Registry-backed static catalog used when the codex CLI cannot be queried
    /// and no cache exists (task item 1's "fall back to today's static list").
    /// Mirrors <c>ClaudeModelDiscovery.FallbackCatalog</c>. Contains no gpt-5.6:
    /// <c>gpt-5.6-sol</c> has no registry entry at all, and the onboarded
    /// <c>gpt-5.6-terra</c> / <c>gpt-5.6-luna</c> entries carry an
    /// <c>Available:false</c> baseline (AGT-2707 round 2), so this static list
    /// never assumes a gpt-5.6 model is offered without a live CLI answer and a
    /// Publish of this catalog keeps the default on the account-valid gpt-5.5
    /// baseline.
    /// </summary>
    public static CliModelCatalog FallbackCatalog(string source = "registry-fallback")
    {
        var models = ModelMetadataRegistry.ForVendor("openai")
            .Select(m => ModelMetadataRegistry.ToCliModelInfo(m, CliTypes.Codex))
            .Where(m => m.Available)
            .ToList();
        if (models.Count > 0 && models.All(m => !m.IsDefault))
            models[0] = models[0] with { IsDefault = true };
        return new CliModelCatalog
        {
            Models = models,
            Source = source,
            FetchedAt = DateTime.UtcNow
        };
    }

    private static bool IsGpt56(string? id)
        => id != null && id.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Derive the Codex product default from a live/cached catalog: as soon as
    /// the installed CLI lists a gpt-5.6-* model, that becomes the default
    /// (following the CLI's own active model when it is already a gpt-5.6, else
    /// the highest-priority gpt-5.6 in the list). Returns null when no gpt-5.6
    /// is present so the caller keeps the static gpt-5.5 baseline (AGT-2025).
    /// </summary>
    internal static string? PickDetectedDefault(CliModelCatalog cat)
    {
        var models = cat.Models;
        if (models == null || models.Count == 0) return null;

        // Follow the CLI's own default when it already points at a gpt-5.6 model.
        // Merged-in registry entries are unavailable and can never be the default.
        var flagged = models.FirstOrDefault(m => m.Available && m.IsDefault && IsGpt56(m.Id));
        if (flagged != null) return flagged.Id;

        // Otherwise: the models are priority-ordered, so the first gpt-5.6 is the
        // highest-priority one the CLI advertises.
        return models.FirstOrDefault(m => m.Available && IsGpt56(m.Id))?.Id;
    }

    /// <summary>
    /// Publish the detected Codex default into the shared registry so task
    /// creation, cli-type switches, and client-default materialization all
    /// follow the CLI, then return the catalog unchanged. Called on every path
    /// that yields a catalog (fresh, mem-cache, disk-cache) so a null result
    /// (no gpt-5.6) correctly clears back to the gpt-5.5 baseline.
    /// </summary>
    private CliModelCatalog Publish(CliModelCatalog cat)
    {
        // Ladders first: every later default resolution reads them.
        ModelMetadataRegistry.SetDetectedCodexLadders(cat.Models);
        var detected = PickDetectedDefault(cat);
        ModelMetadataRegistry.SetDetectedCodexDefault(detected);
        // AGT-2716: also publish the plain available-id set so
        // ModelFamilyResolver can resolve non-flagship Codex families
        // (gpt-mini today) against what the CLI actually reports.
        ModelMetadataRegistry.SetDetectedVendorAvailability(
            "openai", cat.Models.Where(m => m.Available).Select(m => m.Id));
        _logger.LogDebug("Codex detected default published: {Detected} (source={Source})",
            detected ?? "<none>", cat.Source);
        return WithKnownButUnavailableModels(cat, _versionTracker?.CurrentVersion(CliTypes.Codex));
    }

    private CliModelCatalog WithActiveModelApplied(CliModelCatalog cat)
    {
        cat = WithCurrentCodexCapabilities(cat);
        var active = ReadActiveModel();
        if (string.IsNullOrWhiteSpace(active)) return cat;

        var models = cat.Models.Select(m => m with
        {
            IsDefault = string.Equals(m.Id, active, StringComparison.OrdinalIgnoreCase)
        }).ToList();
        return cat with { Models = models };
    }

    private string? ReadActiveModel()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            foreach (var rawLine in File.ReadLines(ConfigPath))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal)) break;
                var match = Regex.Match(line, "^model\\s*=\\s*\"(?<model>[^\"]+)\"");
                if (match.Success) return match.Groups["model"].Value.Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read Codex config.toml");
        }

        return null;
    }

    private static string ExtractJsonObject(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("Codex model catalog output did not contain JSON");
        return output[start..(end + 1)].Replace("\r", "");
    }

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)
            ? i
            : null;

    private static string? GuessVendor(string id)
    {
        if (id.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)) return "openai";
        if (id.StartsWith("o1", StringComparison.OrdinalIgnoreCase)) return "openai";
        if (id.StartsWith("o3", StringComparison.OrdinalIgnoreCase)) return "openai";
        if (id.StartsWith("o4", StringComparison.OrdinalIgnoreCase)) return "openai";
        if (id.StartsWith("codex", StringComparison.OrdinalIgnoreCase)) return "openai";
        return null;
    }

    private async Task<string> WaitForOutputFileAsync(string outputPath, PtySession pty, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                return await File.ReadAllTextAsync(outputPath, ct);
            await Task.Delay(100, ct);
        }

        var snapshot = pty.SnapshotStripped();
        _logger.LogWarning("Codex model discovery did not write an output file. Snapshot tail:\n{Tail}",
            snapshot.Length > 1200 ? snapshot[^1200..] : snapshot);
        throw new InvalidOperationException("Codex model catalog output file was not written");
    }

    private static (string App, string[] Args, bool VerbatimCommandLine) BuildModelsCommand(string cliPath, string outputPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // Codex is commonly installed through npm on Windows. That leaves a
            // shell shim named "codex" plus a "codex.cmd" launcher; ConPTY cannot
            // execute the shell shim directly, so let cmd.exe resolve PATHEXT.
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(comspec))
                comspec = Path.Combine(Environment.SystemDirectory, "cmd.exe");

            return (comspec, ["/d", "/c", $"{QuoteForCmd(cliPath)} debug models > {QuoteForCmd(outputPath)}"], true);
        }

        return ("/bin/sh", ["-lc", $"{QuoteForSh(cliPath)} debug models > {QuoteForSh(outputPath)}"], false);
    }

    private static string QuoteForCmd(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (!value.Any(ch => char.IsWhiteSpace(ch) || ch is '&' or '(' or ')' or '^' or '%' or '!' or '"' or '<' or '>' or '|'))
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string QuoteForSh(string value)
        => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static CliModelCatalog WithSource(CliModelCatalog cat, string source)
        => cat with { Source = source };

    private CliModelCatalog? TryLoadDisk()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var json = File.ReadAllText(CachePath);
            return JsonSerializer.Deserialize<CliModelCatalog>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load Codex model catalog cache");
            return null;
        }
    }

    private void TrySaveDisk(CliModelCatalog cat)
    {
        try { File.WriteAllText(CachePath, JsonSerializer.Serialize(cat, JsonOpts)); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to persist Codex model catalog cache"); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
