using System.Text.Json;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>
/// Durable continuation of the per-task post-step infrastructure budget. The
/// existing review sweep resumes it after startup; no shutdown token is awaited.
/// </summary>
public sealed record GateCancellationRetry(string? ExpectedSha, string? AttemptChainId,
    int RetryNumber, DateTimeOffset DueAtUtc)
{
    private static string PathFor(string folder) => Path.Combine(folder, "post-steps", "gate-cancellation-retry.json");

    public static GateCancellationRetry? Read(string folder, string? sha, string? attempt)
    {
        var path = PathFor(folder);
        if (!File.Exists(path)) return null;
        var receipt = JsonSerializer.Deserialize<GateCancellationRetry>(File.ReadAllText(path));
        return receipt is not null
            && string.Equals(receipt.ExpectedSha, sha, StringComparison.OrdinalIgnoreCase)
            && receipt.AttemptChainId == attempt ? receipt : null;
    }

    public static bool Schedule(string folder, string? sha, string? attempt,
        int spent, DateTimeOffset now, TimeSpan backoff)
    {
        if (spent >= PostProcessingOutcomeTaxonomy.MaxPostStepVerdictRetries) return false;
        var path = PathFor(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new GateCancellationRetry(sha, attempt, spent + 1, now + backoff)));
        File.Move(temp, path, overwrite: true);
        return true;
    }
}
