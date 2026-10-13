using System.Text.Json;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

/// <summary>Retry authority for one settled review's delivery continuation.</summary>
public sealed record AutoReviewResumeRetryRecord(
    string AttemptId, int Attempts, DateTimeOffset? NextAtUtc, string? Reason,
    string? ProgressKey = null, bool WaitingForIntegration = false);

public static class AutoReviewResumeRetryStore
{
    private const string FileName = "remote-delivery-resume-retry.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string PathFor(string folder) => Path.Combine(TaskPaths.LogsDir(folder), FileName);

    public static AutoReviewResumeRetryRecord? Read(string folder, string attemptId)
    {
        var path = PathFor(folder);
        if (!File.Exists(path)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<AutoReviewResumeRetryRecord>(File.ReadAllText(path), Json);
            return record?.AttemptId == attemptId ? record : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An interrupted write must not turn a healthy delivery into a
            // permanently exhausted one. A corrupt record loses only its
            // backoff; the authority and integration records remain intact.
            return null;
        }
    }

    public static void Write(string folder, AutoReviewResumeRetryRecord record)
    {
        var path = PathFor(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Clear(string folder, string attemptId)
    {
        if (Read(folder, attemptId) is not null) File.Delete(PathFor(folder));
    }
}
