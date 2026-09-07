using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>
/// Discovers Claude Code's model picker through the interactive /model command.
/// The CLI currently has no stable machine-readable list-models endpoint, so
/// this mirrors the existing PTY discovery pattern used for Copilot.
/// </summary>
public sealed class ClaudeModelDiscovery
{
    private static readonly Regex ModelLineRegex = new(
        @"^\s*(?<marker>[\u276F>\?\*\u2713\u2714\u2705])?\s*(?<label>(?:Claude\s+)?(?:Opus|Fable|Sonnet|Haiku)\s+[A-Za-z0-9 .\-_]+?)(?<default>\s+\((?:default|selected)\))?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex NumberedEntryStartRegex = new(
        @"(?:[\u276F>]\s*)?(?<number>\d+)\.(?=(?:Default(?:\(recommended\))?|Opus|Fable|Sonnet|Haiku))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CollapsedDisplayNameRegex = new(
        @"(?<family>Opus|Fable|Sonnet|Haiku)(?<version>\d+(?:\.\d+)*)(?=(?:with\d+[MK]?context)?\u00B7)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EffortSelectorRegex = new(
        @"[\u25CF\u2022]?\s*(?:Low|Medium|High|Xhigh|Max)\s*effort",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TrustPromptRegex = new(
        @"\btrust(?:ed)?\b|do you want to proceed",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger<ClaudeModelDiscovery> _logger;
    private readonly IConfiguration _config;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CliModelCatalog? _memCache;
    private DateTime _memCacheAt = DateTime.MinValue;

    public ClaudeModelDiscovery(ILogger<ClaudeModelDiscovery> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    private string CachePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "agent-taskboard");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "claude-model-catalog.json");
        }
    }

    private TimeSpan Ttl =>
        TimeSpan.FromMinutes(_config.GetValue<int?>("ClaudeModelsCacheMinutes") ?? 60);

    public async Task<CliModelCatalog> GetAsync(string cliPath, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh)
        {
            if (_memCache != null && DateTime.UtcNow - _memCacheAt < Ttl) return _memCache;
            var fromDisk = TryLoadDisk();
            if (fromDisk != null && DateTime.UtcNow - fromDisk.FetchedAt < Ttl)
            {
                _memCache = fromDisk;
                _memCacheAt = fromDisk.FetchedAt;
                return fromDisk;
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _memCache != null && DateTime.UtcNow - _memCacheAt < Ttl)
                return _memCache;

            try
            {
                var fresh = await DiscoverViaPtyAsync(cliPath, ct);
                _memCache = fresh;
                _memCacheAt = fresh.FetchedAt;
                TrySaveDisk(fresh);
                return fresh;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Claude PTY model discovery failed; falling back to registry catalog");
                if (_memCache != null) return WithSource(_memCache, "pty-failed-mem-cache");
                var fromDisk = TryLoadDisk();
                if (fromDisk != null)
                {
                    _memCache = fromDisk;
                    _memCacheAt = fromDisk.FetchedAt;
                    return WithSource(fromDisk, "pty-failed-disk-cache");
                }
                return FallbackCatalog("pty-failed-registry-fallback");
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<CliModelCatalog> DiscoverViaPtyAsync(string cliPath, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "agent-taskboard-pty-scratch", "claude");
        Directory.CreateDirectory(scratch);

        _logger.LogInformation("Spawning Claude CLI in PTY for /model discovery");
        var (app, args, verbatimCommandLine) = BuildInteractiveCommand(cliPath);
        await using var pty = await PtySession.SpawnAsync(
            app: app,
            args: args,
            cwd: scratch,
            cols: 220,
            rows: 80,
            verbatimCommandLine: verbatimCommandLine,
            ct: ct);

        await pty.WaitForIdleAsync(idleMs: 1500, timeoutMs: 8000, ct);
        await DismissTrustPromptIfPresentAsync(pty, ct);
        await pty.SendKeysAsync("/model<Enter>", ct);

        var appeared = await pty.WaitForPatternAsync(
            new Regex(@"(Select\s+Model|model)", RegexOptions.IgnoreCase),
            timeoutMs: 6000,
            ct);
        if (appeared == null)
        {
            _logger.LogWarning("Claude /model picker did not appear in PTY");
            await pty.SendKeysAsync("<Esc>", ct);
            throw new InvalidOperationException("Claude model picker did not appear");
        }

        await pty.WaitForIdleAsync(idleMs: 700, timeoutMs: 3000, ct);
        var snapshot = pty.SnapshotStripped();
        try { await pty.SendKeysAsync("<Esc>", ct); } catch (Exception __ex) { SilentCatch.Note(__ex, "ClaudeModelDiscovery:128"); }

        var discovered = ParsePickerSnapshot(snapshot);
        if (discovered.Count == 0)
        {
            _logger.LogWarning("Claude model discovery captured 0 models. Snapshot tail:\n{Tail}",
                snapshot.Length > 1200 ? snapshot[^1200..] : snapshot);
            throw new InvalidOperationException("No models parsed from Claude picker");
        }

        return new CliModelCatalog
        {
            Models = Reconcile(discovered),
            Source = "cli-pty",
            FetchedAt = DateTime.UtcNow
        };
    }

    public static List<CliModelInfo> ParsePickerSnapshot(string snapshot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CliModelInfo>();

        var pickerText = snapshot ?? "";
        var effort = EffortSelectorRegex.Match(pickerText);
        if (effort.Success) pickerText = pickerText[..effort.Index];

        var entryStarts = NumberedEntryStartRegex.Matches(pickerText);
        for (var index = 0; index < entryStarts.Count; index++)
        {
            var start = entryStarts[index].Index + entryStarts[index].Length;
            var end = index + 1 < entryStarts.Count ? entryStarts[index + 1].Index : pickerText.Length;
            var entry = pickerText[start..end];
            var displayName = CollapsedDisplayNameRegex.Match(entry);
            if (!displayName.Success) continue;

            var label = $"Claude {displayName.Groups["family"].Value} {displayName.Groups["version"].Value}";
            var isCurrent = entry[..displayName.Index].IndexOfAny(['\u2713', '\u2714', '\u2705']) >= 0;
            AddParsedModel(result, seen, label, isCurrent);
        }

        // Older Claude Code versions rendered one model per terminal line.
        // Keep that parser after the collapsed numbered layout so mixed PTY
        // snapshots deduplicate to the first, richer match.
        foreach (Match match in ModelLineRegex.Matches(pickerText))
        {
            var label = Regex.Replace(match.Groups["label"].Value.Trim(), @"\s+", " ");
            var marker = match.Groups["marker"].Value;
            var isCurrent = marker.IndexOfAny(['\u2713', '\u2714', '\u2705']) >= 0
                            || match.Groups["default"].Success;
            AddParsedModel(result, seen, label, isCurrent);
        }
        return result;
    }

    private static void AddParsedModel(
        List<CliModelInfo> result,
        HashSet<string> seen,
        string label,
        bool isCurrent)
    {
        var normalizedLabel = Regex.Replace(label.Trim(), @"\s+", " ");
        if (!normalizedLabel.StartsWith("Claude ", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(normalizedLabel, @"^(?:Opus|Fable|Sonnet|Haiku)\s", RegexOptions.IgnoreCase))
        {
            normalizedLabel = "Claude " + normalizedLabel;
        }

        var metadata = ModelMetadataRegistry.FindByLabelOrAlias(normalizedLabel);
        var id = metadata?.Id ?? LabelToId(normalizedLabel);
        if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) return;

        var model = metadata != null
            ? ModelMetadataRegistry.ToCliModelInfo(metadata, CliTypes.Claude)
            : ModelMetadataRegistry.UnknownCliModel(id, normalizedLabel, "anthropic", CliTypes.Claude);
        result.Add(model with { IsDefault = isCurrent });
    }

    public static List<CliModelInfo> Reconcile(IReadOnlyList<CliModelInfo> discovered)
    {
        var discoveredIds = new HashSet<string>(discovered.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        var currentId = discovered.FirstOrDefault(m => m.Available && m.IsDefault)?.Id;
        var result = discovered
            .Where(m => m.Available)
            .Select(m => m with { IsDefault = false })
            .ToList();

        foreach (var known in ModelMetadataRegistry.ForVendor("anthropic"))
        {
            if (discoveredIds.Contains(known.Id)) continue;
            result.Add(ModelMetadataRegistry.ToCliModelInfo(known, CliTypes.Claude) with
            {
                IsDefault = false,
                Available = false,
                Deprecated = known.Deprecated,
                AvailabilityNote = "Known in registry but not reported by the installed Claude CLI."
            });
        }

        MarkDefault(result, currentId);
        return result;
    }

    public static CliModelCatalog FallbackCatalog(string source = "registry-fallback")
    {
        var models = ModelMetadataRegistry.ForVendor("anthropic")
            .Select(m => ModelMetadataRegistry.ToCliModelInfo(m, CliTypes.Claude))
            .Where(m => m.Available)
            .ToList();
        MarkDefault(models);
        return new CliModelCatalog
        {
            Models = models,
            Source = source,
            FetchedAt = DateTime.UtcNow
        };
    }

    private static void MarkDefault(List<CliModelInfo> models, string? preferredId = null)
    {
        var defaultId = ModelMetadataRegistry.ForVendor("anthropic").FirstOrDefault(m => m.IsDefault)?.Id;
        var defaultIndex = models.FindIndex(m => m.Available && string.Equals(m.Id, preferredId, StringComparison.OrdinalIgnoreCase));
        if (defaultIndex < 0)
            defaultIndex = models.FindIndex(m => m.Available && string.Equals(m.Id, defaultId, StringComparison.OrdinalIgnoreCase));
        if (defaultIndex < 0) defaultIndex = models.FindIndex(m => m.Available);
        for (var i = 0; i < models.Count; i++)
            models[i] = models[i] with { IsDefault = i == defaultIndex };
    }

    private static string LabelToId(string label)
    {
        var normalized = Regex.Replace(label.Trim(), @"\s+", " ");
        if (!normalized.StartsWith("Claude ", StringComparison.OrdinalIgnoreCase)
            && (normalized.StartsWith("Opus ", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Sonnet ", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Haiku ", StringComparison.OrdinalIgnoreCase)))
        {
            normalized = "Claude " + normalized;
        }
        var id = Regex.Replace(normalized.ToLowerInvariant(), @"\s+", "-");
        return Regex.Replace(id, @"(?<=\d)\.(?=\d)", "-");
    }

    private static async Task DismissTrustPromptIfPresentAsync(PtySession pty, CancellationToken ct)
    {
        var snapshot = pty.SnapshotStripped();
        if (!TrustPromptRegex.IsMatch(snapshot)) return;

        await pty.SendKeysAsync("1<Enter>", ct);
        await pty.WaitForIdleAsync(idleMs: 1500, timeoutMs: 8000, ct);
    }

    private static (string App, string[] Args, bool VerbatimCommandLine) BuildInteractiveCommand(string cliPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(comspec))
                comspec = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            return (comspec, ["/d", "/c", QuoteForCmd(cliPath)], true);
        }

        return ("/bin/sh", ["-lc", QuoteForSh(cliPath)], false);
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
            _logger.LogDebug(ex, "Failed to load Claude model catalog cache");
            return null;
        }
    }

    private void TrySaveDisk(CliModelCatalog cat)
    {
        try { File.WriteAllText(CachePath, JsonSerializer.Serialize(cat, JsonOpts)); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to persist Claude model catalog cache"); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
