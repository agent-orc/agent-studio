using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>Task-folder evidence for fenced coding generations and a single recovery delta.</summary>
public static class SessionContinuationLedgerStore
{
    public const string LedgerFileName = "session-continuation-ledger.jsonl";
    public const string DeltaFileName = "mechanical-round-delta.json";
    public const string FreshReasonFileName = "session-continuation-fresh-reason.txt";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SessionContinuationLedgerEntry? Latest(string folder)
    {
        var path = Path.Combine(folder, LedgerFileName);
        if (!File.Exists(path)) return null;
        SessionContinuationLedgerEntry? latest = null;
        foreach (var line in File.ReadLines(path))
        {
            try { latest = JsonSerializer.Deserialize<SessionContinuationLedgerEntry>(line, Json) ?? latest; }
            catch (JsonException exception)
            {
                SilentCatch.Note(exception, "SessionContinuationLedgerStore: torn row cannot authorize resume");
                return null;
            }
        }
        return latest;
    }

    public static void Append(string folder, SessionContinuationLedgerEntry entry)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, LedgerFileName);
        if (File.Exists(path) && File.ReadLines(path).Any(line =>
            line.Contains($"\"attemptId\":\"{entry.AttemptId}\"", StringComparison.Ordinal)))
            return;
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, Encoding.UTF8, 4096, leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(entry, Json));
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    public static void SaveDelta(string folder, MechanicalRoundDelta delta)
    {
        var path = Path.Combine(folder, DeltaFileName);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(delta, Json));
        File.Move(temp, path, overwrite: true);
    }

    public static MechanicalRoundDelta? ConsumeDelta(string folder)
    {
        var path = Path.Combine(folder, DeltaFileName);
        if (!File.Exists(path)) return null;
        var content = File.ReadAllText(path);
        MechanicalRoundDelta? delta;
        try { delta = JsonSerializer.Deserialize<MechanicalRoundDelta>(content, Json); }
        catch (JsonException exception)
        {
            SilentCatch.Note(exception, "SessionContinuationLedgerStore: invalid delta cannot authorize resume");
            delta = null;
        }
        File.Delete(path);
        return delta;
    }

    public static MechanicalRoundDelta? PeekDelta(string folder)
    {
        var path = Path.Combine(folder, DeltaFileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MechanicalRoundDelta>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    public static void SaveFreshReason(string folder, string reason)
    {
        var path = Path.Combine(folder, FreshReasonFileName);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, reason);
        File.Move(temp, path, overwrite: true);
    }

    public static string? ConsumeFreshReason(string folder)
    {
        var path = Path.Combine(folder, FreshReasonFileName);
        if (!File.Exists(path)) return null;
        var reason = File.ReadAllText(path).Trim();
        File.Delete(path);
        return reason;
    }

    public static string? PeekFreshReason(string folder)
    {
        var path = Path.Combine(folder, FreshReasonFileName);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }
}
