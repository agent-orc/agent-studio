using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Shared;

namespace AgentStudio.Runner;

/// <summary>
/// Durable attribution for the effective route of the latest quota-switched
/// run. It keeps claim replay, the task header, and token accounting aligned
/// with the launch spec without rewriting the card's configured model.
/// </summary>
public sealed record QuotaFallbackRecord
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("attemptId")] public string? AttemptId { get; init; }
    [JsonPropertyName("primaryCliType")] public string PrimaryCliType { get; init; } = "";
    [JsonPropertyName("primaryModel")] public string? PrimaryModel { get; init; }
    [JsonPropertyName("primaryThinkingLevel")] public string? PrimaryThinkingLevel { get; init; }
    [JsonPropertyName("effectiveCliType")] public string EffectiveCliType { get; init; } = "";
    [JsonPropertyName("effectiveModel")] public string? EffectiveModel { get; init; }
    [JsonPropertyName("effectiveThinkingLevel")] public string? EffectiveThinkingLevel { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    [JsonPropertyName("startedAt")] public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    [JsonPropertyName("resetAt")] public DateTime? ResetAt { get; init; }
    [JsonPropertyName("executionPath")] public string? ExecutionPath { get; init; }
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
        if (string.IsNullOrWhiteSpace(jobFolder) || !Directory.Exists(jobFolder)) return;
        try
        {
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
            return File.Exists(path)
                ? JsonSerializer.Deserialize<QuotaFallbackRecord>(File.ReadAllText(path), Options)
                : null;
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
        => marker is null ? null : new QuotaFallbackStatus(
            marker.EffectiveCliType,
            marker.EffectiveModel,
            marker.Reason,
            marker.EffectiveThinkingLevel,
            marker.StartedAt,
            marker.PrimaryCliType,
            marker.PrimaryModel,
            marker.PrimaryThinkingLevel,
            marker.ResetAt,
            marker.ExecutionPath,
            marker.AttemptId);

    public static QuotaFallbackRecord FromPlan(
        QuotaAdmissionPlan plan,
        string? attemptId,
        DateTime startedAt)
        => new()
        {
            AttemptId = attemptId,
            PrimaryCliType = plan.RequestedCliType ?? CliTypes.Claude,
            PrimaryModel = plan.RequestedModel,
            PrimaryThinkingLevel = plan.RequestedThinkingLevel,
            EffectiveCliType = plan.CliType,
            EffectiveModel = plan.Model,
            EffectiveThinkingLevel = plan.ThinkingLevel,
            Reason = plan.Reason,
            StartedAt = startedAt,
            ResetAt = plan.NextResetAt,
            ExecutionPath = plan.ExecutionPath,
        };
}
