using System.Text.Json;

namespace AgentRunner.RunTargets;

/// <summary>One started revision of an app: a compose project with one container.</summary>
public sealed record RunTargetSlot(string Revision, string Artifact, string ComposeProject, string ComposeFile,
    int ContainerPort, string HealthPath)
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
    public string Status { get; set; } = "stopped";
    public int CrashAttempts { get; set; }
    public int HostRestarts { get; set; }
    public DateTime? LastRestartAt { get; set; }
    public DateTime? NextRestartAt { get; set; }
    public int DockerRestarts { get; set; }
    public List<RunTargetEvent> Events { get; set; } = [];
}

public sealed class RunTargetHostState
{
    public Dictionary<string, RunTargetAppState> Apps { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Durable host state so a restarted agent knows current and previous revisions.</summary>
public sealed class RunTargetStateStore(string stateDirectory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Directory { get; } = stateDirectory;
    public string AppsDirectory => Path.Combine(Directory, "apps");
    public string EdgeDirectory => Path.Combine(Directory, "edge");
    public string CaddyDirectory => Path.Combine(EdgeDirectory, "caddy");
    public string CaddyfilePath => Path.Combine(CaddyDirectory, "Caddyfile");
    public string EdgeComposePath => Path.Combine(EdgeDirectory, "compose.yaml");
    private string StatePath => Path.Combine(Directory, "state.json");

    public RunTargetHostState Load()
        => File.Exists(StatePath)
            ? JsonSerializer.Deserialize<RunTargetHostState>(File.ReadAllText(StatePath), Json) ?? new()
            : new();

    public void Save(RunTargetHostState state) => WriteAtomically(StatePath, JsonSerializer.Serialize(state, Json));

    /// <summary>Writes when the content differs; returns whether it changed.</summary>
    public bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return false;
        WriteAtomically(path, content);
        return true;
    }

    public string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public static void WriteAtomically(string path, string content)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }
}
