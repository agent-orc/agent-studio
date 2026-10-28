using System.Text.Json;

namespace AgentRunner;

/// <summary>Persistent claim barrier used by a host agent for one role adapter.</summary>
public static class HostRoleDrainControl
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public sealed record Request(string Id, DateTime RequestedAtUtc);
    public sealed record Acknowledgement(string Id, int ProcessId, int ActiveSlots, DateTime ObservedAtUtc);

    public static string RequestPath(string stateDir, string role)
        => Path.Combine(stateDir, $"host-{role}-drain-requested.json");
    public static string AcknowledgementPath(string stateDir, string role)
        => Path.Combine(stateDir, $"host-{role}-drain-acknowledged.json");

    public static Request RequestDrain(string stateDir, string role)
    {
        Directory.CreateDirectory(stateDir);
        if (ReadRequest(stateDir, role) is { } existing) return existing;
        var request = new Request(Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        Write(RequestPath(stateDir, role), request);
        return request;
    }

    public static Request? ReadRequest(string stateDir, string role)
        => Read<Request>(RequestPath(stateDir, role));

    public static Acknowledgement? ReadAcknowledgement(string stateDir, string role)
        => Read<Acknowledgement>(AcknowledgementPath(stateDir, role));

    public static void Acknowledge(string stateDir, string role, Request request, int activeSlots)
    {
        if (ReadRequest(stateDir, role)?.Id != request.Id) return;
        Write(AcknowledgementPath(stateDir, role),
            new Acknowledgement(request.Id, Environment.ProcessId, activeSlots, DateTime.UtcNow));
    }

    public static void Clear(string stateDir, string role)
    {
        File.Delete(RequestPath(stateDir, role));
        File.Delete(AcknowledgementPath(stateDir, role));
    }

    private static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
                     ?? throw new InvalidDataException($"Drain control is empty: {path}"); }
        catch (JsonException exception) { throw new InvalidDataException($"Drain control is unreadable: {path}", exception); }
    }

    private static void Write<T>(string path, T value)
    {
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                   FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
