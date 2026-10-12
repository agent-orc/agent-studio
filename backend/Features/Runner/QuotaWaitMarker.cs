using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Runner;

/// <summary>
/// Durable Run-Liveness-style marker for an intentional quota-reset wait.
/// The card may remain in Ready (admission wait) or Progress (library
/// mid-run wait); this sidecar makes both cases visible across polling and
/// backend restarts without inventing another filesystem lane.
/// </summary>
public sealed record QuotaWaitRecord
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("cliType")] public string CliType { get; init; } = "";
    [JsonPropertyName("startedAt")] public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    [JsonPropertyName("resetAt")] public DateTime ResetAt { get; init; }
    [JsonPropertyName("thresholdMinutes")] public int ThresholdMinutes { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    [JsonPropertyName("scope")] public string Scope { get; init; } = "quota";
}

public static class QuotaWaitMarker
{
    public const string FileName = "quota-wait.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static void Write(string jobFolder, QuotaWaitRecord marker, ILogger? logger = null)
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
            logger?.LogWarning(ex, "Failed to persist quota wait marker in {Folder}", jobFolder);
        }
    }

    /// <summary>Counts <see cref="TryRead"/> calls per observing flow (tests).</summary>
    public static FlowCallCounter Reads { get; } = new();

    public static QuotaWaitRecord? TryRead(string jobFolder, ILogger? logger = null)
    {
        Reads.Hit();
        if (string.IsNullOrWhiteSpace(jobFolder)) return null;
        try
        {
            var path = Path.Combine(jobFolder, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<QuotaWaitRecord>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read quota wait marker in {Folder}", jobFolder);
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
            logger?.LogWarning(ex, "Failed to clear quota wait marker in {Folder}", jobFolder);
            return false;
        }
    }

    public static QuotaWaitStatus? ToStatus(QuotaWaitRecord? marker)
        => marker is null ? null : new QuotaWaitStatus(
            marker.CliType,
            marker.StartedAt,
            marker.ResetAt,
            marker.ThresholdMinutes,
            marker.Reason,
            marker.Scope);
}
