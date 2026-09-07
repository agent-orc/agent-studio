using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Shared;

namespace AgentStudio.Cli;

/// <summary>
/// Durable sidecar recording an active quota fallback for a job claimed
/// through a path that has no long-lived <c>ProjectRunner</c> instance to hold
/// it in memory (the remote-claim and review-attempt-claim paths). Mirrors
/// <c>QuotaWaitMarker</c> (AGT-2107/2055): a local run tracks its fallback in
/// <c>ProjectRunner</c>'s in-memory active-run table, but that table does not
/// exist for a card a remote runner is executing, so the card-badge / timeline
/// projection needs a durable source instead (AGT-2751).
/// </summary>
public sealed record QuotaFallbackRecord
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("cliType")] public string CliType { get; init; } = "";
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("activatedAt")] public DateTime ActivatedAt { get; init; } = DateTime.UtcNow;
}

public static class QuotaFallbackMarker
{
    public const string FileName = "quota-fallback.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static void Write(string jobFolder, QuotaFallbackRecord marker, ILogger? logger = null)
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
            logger?.LogWarning(ex, "Failed to persist quota fallback marker in {Folder}", jobFolder);
        }
    }

    public static QuotaFallbackRecord? TryRead(string jobFolder, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return null;
        try
        {
            var path = Path.Combine(jobFolder, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<QuotaFallbackRecord>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read quota fallback marker in {Folder}", jobFolder);
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
            logger?.LogWarning(ex, "Failed to clear quota fallback marker in {Folder}", jobFolder);
            return false;
        }
    }

    public static QuotaFallbackStatus? ToStatus(QuotaFallbackRecord? marker)
        => marker is null ? null : new QuotaFallbackStatus(marker.CliType, marker.Model, marker.Reason, marker.ActivatedAt);
}
