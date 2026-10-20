using System.Text;
using System.Text.Json;

namespace AgentRunner.RunTargets;

/// <summary>
/// One started revision of an app: a compose project with one container. It
/// keeps the secret reference names (never values) so the host can restart or
/// restore it without a spec from Studio.
/// </summary>
public sealed record RunTargetSlot(string Revision, string Artifact, string ComposeProject, string ComposeFile,
    int ContainerPort, string HealthPath, IReadOnlyList<string>? SecretRefs = null)
{
    public string Upstream => $"{ComposeProject}:{ContainerPort}";
}

/// <summary>Host-local state of one project environment. Holds no secret values.</summary>
public sealed class RunTargetAppState
{
    public string Project { get; set; } = "";
    public string EnvironmentId { get; set; } = "";
    public RunTargetSlot? Current { get; set; }
    public RunTargetSlot? Previous { get; set; }

    /// <summary>Routing of the last valid spec; it stays when Studio sends none.</summary>
    public List<string> Domains { get; set; } = [];
    public string Edge { get; set; } = "acme";
    public string Status { get; set; } = "stopped";
    public int CrashAttempts { get; set; }
    public int HostRestarts { get; set; }
    public DateTime? LastRestartAt { get; set; }
    public DateTime? NextRestartAt { get; set; }
    public int DockerRestarts { get; set; }

    /// <summary>Restarts of the current revision inside the crash-rollback window.</summary>
    public List<DateTime> RecentRestarts { get; set; } = [];

    /// <summary>Revision the host rolled back from after a crash loop; only a deploy job brings it back.</summary>
    public string? CrashRollbackRevision { get; set; }

    /// <summary>True while Studio sends no spec for the app and the host keeps it as it is.</summary>
    public bool Held { get; set; }
    public List<RunTargetEvent> Events { get; set; } = [];
}

public sealed class RunTargetHostState
{
    public Dictionary<string, RunTargetAppState> Apps { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Domain to app key. The first app that routed a domain keeps it (<see cref="RunTargetCaddyfile.ClaimDomains"/>).</summary>
    public Dictionary<string, string> DomainOwners { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Durable host state so a restarted agent knows current and previous
/// revisions. Every write is flushed to disk before it replaces the old file,
/// and the state is kept twice: <c>state.json</c> and <c>state.last-good.json</c>.
/// A torn or unparsable <c>state.json</c> falls back to the last-good copy, so
/// the host keeps supervising its apps.
/// </summary>
public sealed class RunTargetStateStore(string stateDirectory, Action<string>? log = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Directory { get; } = stateDirectory;
    public string AppsDirectory => Path.Combine(Directory, "apps");
    public string EdgeDirectory => Path.Combine(Directory, "edge");
    public string CaddyDirectory => Path.Combine(EdgeDirectory, "caddy");
    public string CaddyfilePath => Path.Combine(CaddyDirectory, "Caddyfile");
    public string EdgeComposePath => Path.Combine(EdgeDirectory, "compose.yaml");
    public string StatePath => Path.Combine(Directory, "state.json");
    public string LastGoodPath => Path.Combine(Directory, "state.last-good.json");

    /// <exception cref="InvalidDataException">Neither copy of an existing state can be read.</exception>
    public RunTargetHostState Load()
    {
        if (!File.Exists(StatePath) && !File.Exists(LastGoodPath)) return new();
        if (TryParse(StatePath, out var state)) return state;
        if (TryParse(LastGoodPath, out var lastGood))
        {
            log?.Invoke($"run-target: {StatePath} is unreadable; continuing from {LastGoodPath}");
            return lastGood;
        }
        throw new InvalidDataException($"Neither {StatePath} nor {LastGoodPath} holds a readable run-target state.");
    }

    public void Save(RunTargetHostState state)
    {
        var text = JsonSerializer.Serialize(state, Json);
        if (!WriteIfChanged(StatePath, text)) return;
        WriteAtomically(LastGoodPath, text);
    }

    /// <summary>Writes when the content differs; returns whether it changed.</summary>
    public bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return false;
        WriteAtomically(path, content);
        return true;
    }

    public string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>Writes a temporary file, flushes it to disk, then renames it over <paramref name="path"/>.</summary>
    public static void WriteAtomically(string path, string content)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write(new UTF8Encoding(false).GetBytes(content));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }

    private static bool TryParse(string path, out RunTargetHostState state)
    {
        state = new();
        if (!File.Exists(path)) return false;
        try
        {
            if (JsonSerializer.Deserialize<RunTargetHostState>(File.ReadAllText(path), Json) is not { } parsed) return false;
            state = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
