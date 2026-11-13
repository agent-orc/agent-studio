using System.Text.Json;

namespace AgentStudio.Personas;

/// <summary>The last complete, model-free warden fact set for each workspace.</summary>
public sealed class OpsPostureSnapshotStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string? _root;
    private readonly object _gate = new();

    public OpsPostureSnapshotStore(IConfiguration configuration) =>
        _root = string.IsNullOrWhiteSpace(configuration["TaskRepository"]) ? null :
            Path.Combine(configuration["TaskRepository"]!, ".metadata", "ops-posture-snapshots");

    public void Save(string workspaceId, OpsPostureFactsV1 snapshot)
    {
        var path = PathFor(workspaceId);
        if (path is null) return;
        lock (_gate)
        {
            Directory.CreateDirectory(_root!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Json));
            File.Move(temporary, path, true);
        }
    }

    public OpsPostureFactsV1? Get(string workspaceId)
    {
        var path = PathFor(workspaceId);
        lock (_gate) return path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<OpsPostureFactsV1>(File.ReadAllText(path), Json) : null;
    }

    private string? PathFor(string workspaceId)
    {
        if (!PersonaDescriptorValidator.Slug().IsMatch(workspaceId))
            throw new ArgumentException("Invalid workspace id.");
        return _root is null ? null : Path.Combine(_root, workspaceId + ".json");
    }
}
