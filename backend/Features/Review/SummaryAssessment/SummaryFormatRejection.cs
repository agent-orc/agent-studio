using System.Text.Json;
using AgentStudio.Persistence;

namespace AgentStudio.Review;

/// <summary>Remembers a malformed response so automatic replays do not resend the same input.</summary>
internal sealed record SummaryFormatRejection(string Digest, string Error)
{
    private static string PathFor(string folder) => Path.Combine(TaskPaths.LogsDir(folder), "summary-format-rejection.json");

    internal static string? Read(string folder, string digest)
    {
        try
        {
            var rejection = JsonSerializer.Deserialize<SummaryFormatRejection>(File.ReadAllText(PathFor(folder)));
            return rejection?.Digest == digest ? rejection.Error : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static void Write(string folder, string digest, string error)
        => new AtomicJsonFileWriter().ReplaceExisting(PathFor(folder),
            JsonSerializer.Serialize(new SummaryFormatRejection(digest, error)));
}
