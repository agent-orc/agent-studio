using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Runner;

/// <summary>
/// Durable sidecar for a card parked behind an open cause breaker. The card
/// keeps its lane; this marker is what makes the wait visible across polling
/// and restarts, and what keeps the retry scheduler from minting a successor.
/// </summary>
public sealed record CauseWaitRecord
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("causeKey")] public string CauseKey { get; init; } = "";
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; init; } = "";
    [JsonPropertyName("failureClass")] public string FailureClass { get; init; } = "";
    [JsonPropertyName("since")] public DateTime Since { get; init; } = DateTime.UtcNow;
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    /// <summary>Set while this card runs as the breaker's probe; the marker then does not block the attempt.</summary>
    [JsonPropertyName("probe")] public bool Probe { get; init; }
}

public static class CauseWaitMarker
{
    public const string FileName = "cause-wait.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static void Write(string jobFolder, CauseWaitRecord marker, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return;
        try
        {
            Directory.CreateDirectory(jobFolder);
            var path = Path.Combine(jobFolder, FileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(marker, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to persist cause wait marker in {Folder}", jobFolder);
        }
    }

    public static CauseWaitRecord? TryRead(string jobFolder, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return null;
        try
        {
            var path = Path.Combine(jobFolder, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CauseWaitRecord>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read cause wait marker in {Folder}", jobFolder);
            return null;
        }
    }

    public static bool Clear(string jobFolder, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return false;
        try
        {
            var path = Path.Combine(jobFolder, FileName);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to clear cause wait marker in {Folder}", jobFolder);
            return false;
        }
    }

    /// <summary>A probe card is not shown as waiting: its attempt is running.</summary>
    public static CauseWaitStatus? ToStatus(CauseWaitRecord? marker)
        => marker is null || marker.Probe ? null : new CauseWaitStatus(
            marker.CauseKey,
            marker.Fingerprint,
            marker.FailureClass,
            marker.Since,
            marker.Reason);
}
