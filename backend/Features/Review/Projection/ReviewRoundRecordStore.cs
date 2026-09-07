using System.Text;
using System.Text.Json;

namespace AgentStudio.Review;

/// <summary>
/// Reads and writes <see cref="ReviewRoundRecord"/> files in a job folder.
///
/// <para>
/// Writes are idempotent per (plane, attempt): a replayed report overwrites its
/// own file, so a retried delivery cannot inflate the review-round count. Reads
/// never throw on a damaged file; a record that cannot be deserialized is
/// skipped, and the caller falls back to the Markdown backfill for that round.
/// </para>
/// </summary>
public static class ReviewRoundRecordStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Persist one review round. Returns the job-folder-relative file name, or
    /// null when the folder is unavailable. Never throws for an IO fault: the
    /// verdict itself is already durable in its plane, and a missing record only
    /// degrades the projection to its Markdown backfill.
    /// </summary>
    public static string? Write(string? jobFolder, ReviewRoundRecord record, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder) || !Directory.Exists(jobFolder)) return null;
        if (string.IsNullOrWhiteSpace(record.AttemptId)) return null;
        var fileName = ReviewRoundRecordSchema.FileName(record.Plane, record.AttemptId);
        try
        {
            File.WriteAllText(
                Path.Combine(jobFolder, fileName),
                JsonSerializer.Serialize(record, Json),
                new UTF8Encoding(false));
            return fileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Failed to write review-round record {File} in {Folder}", fileName, jobFolder);
            return null;
        }
    }

    /// <summary>
    /// Every canonical record in the folder, oldest first. Empty for a folder
    /// that predates the record; <see cref="ReviewRoundMarkdownBackfill"/> covers
    /// that case.
    /// </summary>
    public static IReadOnlyList<ReviewRoundRecord> ReadAll(string? jobFolder, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder) || !Directory.Exists(jobFolder)) return [];
        var records = new List<ReviewRoundRecord>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         jobFolder,
                         ReviewRoundRecordSchema.FilePattern,
                         SearchOption.TopDirectoryOnly))
            {
                if (Read(path, logger) is { } record) records.Add(record);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Failed to enumerate review-round records in {Folder}", jobFolder);
        }
        return Order(records);
    }

    /// <summary>Chronological order with a stable tie-break on the attempt id.</summary>
    public static IReadOnlyList<ReviewRoundRecord> Order(IEnumerable<ReviewRoundRecord> records) =>
        records
            .OrderBy(record => record.ReceivedAt)
            .ThenBy(record => record.AttemptId, StringComparer.Ordinal)
            .ToList();

    private static ReviewRoundRecord? Read(string path, ILogger? logger)
    {
        try
        {
            var record = JsonSerializer.Deserialize<ReviewRoundRecord>(File.ReadAllText(path), Json);
            return string.IsNullOrWhiteSpace(record?.AttemptId) ? null : record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning(ex, "Skipped unreadable review-round record {Path}", path);
            return null;
        }
    }
}
