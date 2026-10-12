using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Tasks;

/// <summary>
/// Values of the <c>X-Result-History-Legacy</c> response header and of the
/// persisted outcome. <c>pending</c> is never persisted: it means "no answer
/// yet" or "a backfill is running".
/// </summary>
public static class LegacyResultHistoryStates
{
    public const string HeaderName = "X-Result-History-Legacy";
    public const string Pending = "pending";
    public const string Complete = "complete";
    public const string Failed = "failed";
    public const string Missing = "missing";
}

/// <summary>
/// The persisted answer of one legacy Result backfill: the status.md versions a
/// card committed to the workspace repository before
/// <see cref="ResultVersionStore"/> existed (AGT-3086). Entries are stored in
/// git log order (newest first) and deduplicated by content; the read side
/// still excludes the current Result and the local versions by
/// <see cref="LegacyResultHistoryEntry.ContentHash"/>.
/// </summary>
public sealed record LegacyResultHistoryRecord(
    int SchemaVersion,
    DateTimeOffset ComputedAt,
    string Outcome,
    string? Reason,
    long ElapsedMs,
    IReadOnlyList<LegacyResultHistoryEntry> Entries);

/// <summary>
/// One legacy Result version. <see cref="Markdown"/> is null and
/// <see cref="Truncated"/> is true when the version was dropped to keep the
/// file under its size cap; the metadata and hash stay.
/// </summary>
public sealed record LegacyResultHistoryEntry(
    string Sha,
    DateTime Timestamp,
    string Producer,
    string ProducerKind,
    string Lane,
    string Result,
    string Case,
    string ContentHash,
    string? Markdown,
    bool Truncated);

/// <summary>
/// What the task folder says about its legacy Result history. A file that is
/// oversized, corrupt, or of an unknown schema reads as failed, never as
/// missing, so a broken file cannot pass for "not computed yet".
/// </summary>
public sealed record LegacyResultHistorySnapshot(LegacyResultHistoryRecord? Record, string? UnreadableReason)
{
    public static readonly LegacyResultHistorySnapshot Missing = new(null, null);

    public string State => Record?.Outcome
        ?? (UnreadableReason is null ? LegacyResultHistoryStates.Missing : LegacyResultHistoryStates.Failed);

    public string? Reason => Record?.Reason ?? UnreadableReason;
}

/// <summary>
/// Reads and writes <c>results/history/legacy-git.json</c>. It lives under
/// <c>results/</c> because the workspace repository ignores that folder: the
/// backfill reads the repository's history and must not add to it.
/// </summary>
public static class LegacyResultHistoryFile
{
    public const string RelativePath = "results/history/legacy-git.json";
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static string PathFor(string folderPath)
        => Path.Combine(folderPath, RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public static LegacyResultHistorySnapshot Read(string folderPath)
    {
        var path = PathFor(folderPath);
        if (!File.Exists(path)) return LegacyResultHistorySnapshot.Missing;
        try
        {
            if (!BoundedFileRead.TryReadAllText(path, BoundedFileRead.SidecarBytes, out var text))
                return new(null, $"{RelativePath} exceeds {BoundedFileRead.SidecarBytes} bytes.");
            var record = JsonSerializer.Deserialize<LegacyResultHistoryRecord>(text, Json);
            if (record is null
                || record.SchemaVersion != SchemaVersion
                || record.Outcome is not (LegacyResultHistoryStates.Complete or LegacyResultHistoryStates.Failed)
                || record.Entries is null)
                return new(null, $"{RelativePath} is not a schema {SchemaVersion} legacy history record.");
            return new(record, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(null, $"{RelativePath} could not be read: {ex.Message}");
        }
    }

    /// <summary>Writes the record atomically, after fitting it under the cap.</summary>
    public static LegacyResultHistoryRecord Write(string folderPath, LegacyResultHistoryRecord record)
    {
        var fitted = Fit(record, BoundedFileRead.SidecarBytes, out var bytes);
        ResultVersionStore.WriteAtomic(PathFor(folderPath), bytes);
        return fitted;
    }

    /// <summary>
    /// Keeps the serialized record at or below <paramref name="maxBytes"/> by
    /// dropping the markdown of the oldest entries first and marking them
    /// truncated. A record that is still too large with every markdown dropped
    /// becomes a failed record with a reason.
    /// </summary>
    internal static LegacyResultHistoryRecord Fit(LegacyResultHistoryRecord record, int maxBytes, out byte[] bytes)
    {
        bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length <= maxBytes) return record;

        var entries = record.Entries.ToArray();
        var oldestFirst = Enumerable.Range(0, entries.Length)
            .Where(index => entries[index].Markdown is not null)
            .OrderBy(index => entries[index].Timestamp)
            .ThenBy(index => entries[index].Sha, StringComparer.Ordinal)
            .ToList();
        var next = 0;
        while (bytes.Length > maxBytes && next < oldestFirst.Count)
        {
            // Drop enough markdown to cover the estimated excess in one pass,
            // then measure again; at least one entry goes per pass.
            var excess = bytes.Length - maxBytes;
            var saved = 0L;
            do
            {
                var index = oldestFirst[next++];
                saved += JsonSerializer.SerializeToUtf8Bytes(entries[index].Markdown, Json).Length;
                entries[index] = entries[index] with { Markdown = null, Truncated = true };
            }
            while (saved < excess && next < oldestFirst.Count);

            record = record with { Entries = entries.ToArray() };
            bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        }

        if (bytes.Length <= maxBytes) return record;
        record = record with
        {
            Outcome = LegacyResultHistoryStates.Failed,
            Reason = $"{entries.Length} legacy Result versions do not fit in {maxBytes} bytes even without their markdown.",
            Entries = [],
        };
        bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        return record;
    }
}

/// <summary>
/// When a card's legacy Result backfill should run, and what the
/// <c>X-Result-History-Legacy</c> header says meanwhile. Pure: the caller
/// collects the facts and applies the decision.
///
/// <para>The header reports what is known: <c>complete</c> and <c>failed</c>
/// mirror the persisted outcome (a failed card stays <c>failed</c> while its
/// retry runs), and <c>pending</c> means no outcome exists yet and a backfill is
/// running or just scheduled.</para>
/// </summary>
public static class LegacyResultHistoryPolicy
{
    /// <summary>
    /// A failed backfill is retried at most once per this interval, so a
    /// repository that cannot answer is not walked again on every card open.
    /// </summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(6);

    /// <param name="persisted">
    /// <see cref="LegacyResultHistorySnapshot.State"/>: missing, complete, or failed.
    /// </param>
    /// <param name="computedAt">
    /// When the persisted outcome was computed; null when missing or unreadable.
    /// An unreadable file therefore gets one retry at once, which rewrites it.
    /// </param>
    /// <param name="lastAttemptAt">When this process last started a backfill for the card.</param>
    /// <param name="running">A backfill for the card is in flight.</param>
    public static LegacyResultHistoryDecision Decide(
        string persisted,
        DateTimeOffset? computedAt,
        DateTimeOffset? lastAttemptAt,
        bool running,
        DateTimeOffset now)
    {
        if (persisted == LegacyResultHistoryStates.Complete)
            return new(LegacyResultHistoryStates.Complete, Schedule: false);

        var anchor = Latest(persisted == LegacyResultHistoryStates.Failed ? computedAt : null, lastAttemptAt);
        var schedule = !running && (anchor is null || now - anchor.Value >= RetryInterval);
        if (persisted == LegacyResultHistoryStates.Failed)
            return new(LegacyResultHistoryStates.Failed, schedule);

        // Missing: pending while a backfill runs or is about to. A run that
        // ended recently without leaving a file could not persist its answer,
        // which is a failure too.
        return running || schedule
            ? new(LegacyResultHistoryStates.Pending, schedule)
            : new(LegacyResultHistoryStates.Failed, Schedule: false);
    }

    private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right)
        => left is null ? right : right is null ? left : (left > right ? left : right);
}

public sealed record LegacyResultHistoryDecision(string State, bool Schedule);
