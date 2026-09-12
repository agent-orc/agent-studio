using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// Persists the return value of a completed post-step. The pipeline attempt and
/// terminal step row remain authoritative; this payload is read only when both
/// still match the current attempt, so it is evidence rather than a second
/// lifecycle store.
/// </summary>
internal static class PostStepCheckpointStore
{
    private const string DirectoryName = "post-step-checkpoints";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static void Write<T>(
        PipelineExecutionLog? log,
        string jobFolderPath,
        string stepId,
        T value)
    {
        if (log is null
            || !log.TryGetTerminalStep(jobFolderPath, stepId, out _, out var attempt))
            return;

        try
        {
            var directory = Path.Combine(jobFolderPath, DirectoryName);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, stepId + ".json");
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(new Envelope<T>(attempt, value), Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SilentCatch.Note(ex, $"Could not persist restart checkpoint for pipeline step {stepId}");
        }
    }

    internal static bool TryRead<T>(
        PipelineExecutionLog? log,
        string jobFolderPath,
        string stepId,
        out T? value)
    {
        value = default;
        if (log is null
            || !log.TryGetTerminalStep(jobFolderPath, stepId, out _, out var attempt))
            return false;

        var path = Path.Combine(jobFolderPath, DirectoryName, stepId + ".json");
        if (!File.Exists(path)) return false;
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(File.ReadAllText(path), Json);
            if (envelope is null || envelope.Attempt != attempt) return false;
            value = envelope.Value;
            return value is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private sealed record Envelope<T>(int Attempt, T Value);
}
