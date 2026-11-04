using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Security;

/// <summary>
/// The kill switch history, drill results, and drill schedule in
/// <c>.security/operator-kill-switch.json</c>, replaced atomically on every
/// change. One process writes it at a time: the API that holds
/// <see cref="OperatorKillSwitchHostLock"/>, or the out-of-band command while
/// the API is stopped. The audit trail stays the record of each use; this file
/// is the Owner's view of it and the retry queue.
/// </summary>
public sealed class OperatorKillSwitchLog(string securityFolder)
{
    public const string FileName = "operator-kill-switch.json";
    public const int MaxKills = 200;
    public const int MaxDrills = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    public string FilePath => Path.Combine(securityFolder, FileName);

    public OperatorKillSwitchState Read()
    {
        lock (_gate) return ReadLocked();
    }

    public OperatorKillSwitchState Update(Func<OperatorKillSwitchState, OperatorKillSwitchState> change)
    {
        lock (_gate)
        {
            var next = change(ReadLocked());
            next = next with
            {
                Kills = next.Kills.OrderByDescending(kill => kill.At).Take(MaxKills).ToList(),
                Drills = next.Drills.OrderByDescending(drill => drill.StartedAt).Take(MaxDrills).ToList(),
            };
            Directory.CreateDirectory(securityFolder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
            Restrict(temp);
            File.Move(temp, FilePath, true);
            return next;
        }
    }

    public OperatorKillRecord? Find(string id) => Read().Kills.FirstOrDefault(kill => kill.Id == id);

    public void Replace(OperatorKillRecord record) => Update(state => state with
    {
        Kills = [.. state.Kills.Where(kill => kill.Id != record.Id), record],
    });

    public static OperatorKillRequest? ReadRequest(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<OperatorKillRequest>(File.ReadAllText(path), Json) : null; }
        catch (JsonException exception)
        {
            // A torn latch still refuses every session; it is applied as a request without a reason.
            // Its id derives from the file, so the waiting command and the API read the same request.
            SilentCatch.Note(exception, "OperatorKillSwitchLog: unreadable kill request, applied without its reason");
            var writtenAt = File.GetLastWriteTimeUtc(path);
            return new OperatorKillRequest("request_torn_" + writtenAt.Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture),
                writtenAt, null);
        }
    }

    public static void WriteRequest(string path, OperatorKillRequest request)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(request, Json));
        Restrict(temp);
        File.Move(temp, path, true);
    }

    private OperatorKillSwitchState ReadLocked()
    {
        if (!File.Exists(FilePath)) return new OperatorKillSwitchState();
        try { return JsonSerializer.Deserialize<OperatorKillSwitchState>(File.ReadAllText(FilePath), Json) ?? new OperatorKillSwitchState(); }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Kill switch state at '{FilePath}' cannot be read safely.", exception);
        }
    }

    private static void Restrict(string path)
    {
        try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(exception, "OperatorKillSwitchLog: could not restrict file permissions");
        }
    }
}

/// <summary>
/// An exclusive lock file the running API holds for its lifetime. The
/// out-of-band command takes it to learn that the API is stopped and that it
/// may write the access state itself; while the API holds it the command only
/// writes the latch and waits for the API to apply it.
/// </summary>
public static class OperatorKillSwitchHostLock
{
    public const string FileName = "control-plane.lock";

    public static FileStream? TryAcquire(string securityFolder)
    {
        try
        {
            Directory.CreateDirectory(securityFolder);
            return new FileStream(Path.Combine(securityFolder, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
