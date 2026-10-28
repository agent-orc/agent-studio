using System.Text.Json;

namespace AgentRunner;

/// <summary>Host-local admission barrier for migrating a coding role.</summary>
public static class CodingDrainGuard
{
    public const string RequestFileName = "coding-drain-requested.json";
    public const string AcknowledgementFileName = "coding-drain-acknowledged.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Request(string Id, DateTime RequestedAtUtc);
    public sealed record Acknowledgement(string Id, int ProcessId, int ActiveSlots, DateTime ObservedAtUtc);

    public static Request RequestDrain(string stateDir)
    {
        Directory.CreateDirectory(stateDir);
        var existing = ReadRequest(stateDir);
        if (existing is not null) return existing;
        var request = new Request(Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        Write(Path.Combine(stateDir, RequestFileName), request);
        return request;
    }

    public static Request? ReadRequest(string stateDir)
        => Read<Request>(Path.Combine(stateDir, RequestFileName));

    public static Acknowledgement? ReadAcknowledgement(string stateDir)
        => Read<Acknowledgement>(Path.Combine(stateDir, AcknowledgementFileName));

    public static void Acknowledge(string stateDir, Request request, int activeSlots)
    {
        if (ReadRequest(stateDir)?.Id != request.Id) return;
        Write(Path.Combine(stateDir, AcknowledgementFileName),
            new Acknowledgement(request.Id, Environment.ProcessId, activeSlots, DateTime.UtcNow));
    }

    public static IReadOnlyList<string> BusyAttempts(string stateDir)
        => new RunnerStateStore(stateDir).LoadAll().Select(slot => slot.AttemptId).ToArray();

    public static void Clear(string stateDir)
    {
        File.Delete(Path.Combine(stateDir, RequestFileName));
        File.Delete(Path.Combine(stateDir, AcknowledgementFileName));
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
