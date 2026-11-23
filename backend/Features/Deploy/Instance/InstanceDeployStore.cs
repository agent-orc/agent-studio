using System.Text.Json;

namespace AgentStudio.Deploy;

/// <summary>
/// <c>&lt;TaskRepository&gt;/deployments/instance/state.json</c>: the instance
/// deploy runs and releases. One small file, replaced through a flushed
/// temporary file; <see cref="InstanceDeployService"/> serializes access.
/// </summary>
public sealed class InstanceDeployStore(IConfiguration configuration)
{
    private const int MaxRuns = 500;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public InstanceDeployState Read()
    {
        var path = StatePath();
        return File.Exists(path)
            ? JsonSerializer.Deserialize<InstanceDeployState>(File.ReadAllText(path), _json) ?? new()
            : new();
    }

    public void Save(InstanceDeployState state)
    {
        var path = StatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bounded = state.Runs.Count > MaxRuns
            ? state with { Runs = state.Runs.OrderBy(item => item.StartedAtUtc).TakeLast(MaxRuns).ToList() }
            : state;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, bounded, _json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string StatePath() => Path.Combine(configuration["TaskRepository"]
            ?? throw new InvalidOperationException("TaskRepository is required."),
        "deployments", "instance", "state.json");
}
