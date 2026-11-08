using System.Text.Json;
using AgentStudio.Security;

namespace AgentStudio.Pipeline;

public sealed record GateCapacity(int Slots, int ProcessorsPerGate, int? HostCores,
    bool AllowOversubscription);

public static class GateCapacityPolicy
{
    public static string? Validate(GateCapacity capacity)
    {
        if (capacity.Slots is < 1 or > 64) return "Slots must be between 1 and 64.";
        if (capacity.ProcessorsPerGate is < 1 or > 64)
            return "Processors per gate must be between 1 and 64.";
        if (capacity.HostCores is < 1 or > 4096)
            return "Host cores must be between 1 and 4096 when supplied.";
        if (capacity.HostCores is { } cores && !capacity.AllowOversubscription
            && (long)capacity.Slots * capacity.ProcessorsPerGate > cores)
            return "Slots times processors per gate exceeds host cores. Enable oversubscription explicitly to allow it.";
        return null;
    }
}

/// <summary>Installation-wide override; configuration remains the default until the first edit.</summary>
public sealed class GateCapacitySettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly IConfiguration _configuration;
    private readonly ControlPlaneAuditLog? _audit;
    private readonly string _path;
    private GateCapacity? _stored;

    public GateCapacitySettings(IConfiguration configuration, string path, ControlPlaneAuditLog? audit = null)
    {
        _configuration = configuration;
        _path = path;
        _audit = audit;
        if (File.Exists(path))
        {
            _stored = JsonSerializer.Deserialize<GateCapacity>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Stored gate capacity is empty.");
            if (GateCapacityPolicy.Validate(_stored) is { } error)
                throw new InvalidDataException("Stored gate capacity is invalid: " + error);
        }
    }

    public GateCapacity Current(string? hostId = null)
    {
        lock (_gate)
        {
            if (_stored is { } stored) return stored;
            int Count(string key, int fallback) => _configuration.GetValue<int?>(key) ?? fallback;
            var slots = hostId is null ? Count("RemoteGate:GateSlots", 1)
                : Count($"RemoteGate:HostCapacity:{hostId}:Slots", Count("RemoteGate:GateSlots", 1));
            var processors = hostId is null ? Count("RemoteGate:ProcessorCount", 2)
                : Count($"RemoteGate:HostCapacity:{hostId}:ProcessorCount", Count("RemoteGate:ProcessorCount", 2));
            return new GateCapacity(slots, processors, _configuration.GetValue<int?>("RemoteGate:HostCores"),
                _configuration.GetValue<bool>("RemoteGate:AllowOversubscription"));
        }
    }

    public bool HasOverride { get { lock (_gate) return _stored is not null; } }

    public bool Set(GateCapacity capacity, string actor)
    {
        if (GateCapacityPolicy.Validate(capacity) is { } error) throw new ArgumentException(error, nameof(capacity));
        lock (_gate)
        {
            var previous = Current();
            var oldStored = _stored;
            if (previous == capacity && _stored is not null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(capacity, Json));
            File.Move(temporary, _path, overwrite: true);
            _stored = capacity;
            try
            {
                _audit?.Append(new ControlPlaneAuditRecord(ControlPlaneAuditStreams.Operator, actor,
                    "gate-capacity.change", "success", "installation", Details: new { previous, current = capacity }));
            }
            catch
            {
                // A setting is not accepted without its audit record.
                _stored = oldStored;
                if (oldStored is null) File.Delete(_path);
                else
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(oldStored, Json));
                    File.Move(temporary, _path, overwrite: true);
                }
                throw;
            }
            return true;
        }
    }
}
