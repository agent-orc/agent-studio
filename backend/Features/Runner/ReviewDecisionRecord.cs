using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Runner;

/// <summary>
/// Append-only record describing one decision the
/// <c>ReviewDecisionOrchestrator</c> made about a 4-review job that ended
/// in <c>[[TASK_NEEDS_INPUT]]</c>. Persisted to
/// <c>{workspace}/logs/decisions/{project}.jsonl</c>; consumed by the
/// Layer 3 review and the planned executive-summary surface.
///
/// Schema lives at <c>docs/app/schemas/orchestrator-decision.schema.json</c>;
/// keep the two in sync.
/// </summary>
public sealed record ReviewDecisionRecord(
    DateTime CreatedAt,
    string JobId,
    string Project,
    ReviewDecisionKind Kind,
    string Reason,
    string Prompt,
    string Response,
    string FollowUp)
{
    /// <summary>Explicit run/review attempt chain. Never reconstructed from reason text.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AttemptChainId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GateId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubjectSha { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureFingerprint { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? FailureFingerprints { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureKind { get; init; }
    /// <summary>Structured council reaction when this decision was driven by a quality-grade review.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentStudio.OrchestrationDecisions.CouncilReviewReaction? CouncilReaction { get; init; }
    /// <summary>
    /// Review-attempt epoch active when this record was written. Legacy rows
    /// without the field belong to epoch 0.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AttemptEpoch { get; init; }
}

/// <summary>
/// Writes <see cref="ReviewDecisionRecord"/>s into the per-project decision
/// journal. Static + path-based so tests do not need to instantiate a service.
/// </summary>
public static class ReviewDecisionLog
{
    private const int FingerprintSampleCount = 8;
    private const int FingerprintSampleSize = 512;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // Journals are independent per workspace/project. A process-wide lock made
    // an unrelated 27 MB read block every append and board read in every other
    // project. Each canonical file path now owns its own gate and latest-by-job
    // index instead.
    private static readonly ConcurrentDictionary<string, LatestIndexEntry> LatestIndexes = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static string DecisionsDir(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, "logs", "decisions");
    }

    public static string DecisionsFile(string workspaceRoot, string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        return Path.Combine(DecisionsDir(workspaceRoot), $"{project}.jsonl");
    }

    public static void Append(string workspaceRoot, ReviewDecisionRecord record)
    {
        if (record.Kind == ReviewDecisionKind.Escalate)
        {
            record = record with
            {
                Reason = HumanReviewEscalation.EnsureFormattedReason(record.Reason),
            };
        }

        var dir = DecisionsDir(workspaceRoot);
        Directory.CreateDirectory(dir);
        var path = DecisionsFile(workspaceRoot, record.Project);
        var line = JsonSerializer.Serialize(record, Json);
        var payload = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        var entry = EntryFor(path);
        lock (entry.Gate)
        {
            FileStream? beforeStream = null;
            var cachedBefore = entry.Initialized && TryOpenJournal(path, out beforeStream);
            JournalSnapshot before = default;
            if (cachedBefore)
            {
                using (beforeStream)
                {
                    before = CaptureSnapshot(beforeStream!, path, beforeStream!.Length);
                }
                cachedBefore = entry.Exists && entry.Snapshot.Equals(before);
            }
            else if (entry.Initialized && !entry.Exists)
            {
                cachedBefore = !File.Exists(path);
            }

            using (var stream = new FileStream(
                       path,
                       FileMode.Append,
                       FileAccess.Write,
                       FileShare.Read,
                       bufferSize: 4096,
                       FileOptions.SequentialScan))
            {
                stream.Write(payload);
            }

            // Appends made through this process can update the immutable index
            // in O(1). If the file changed between our cached snapshot and the
            // write, discard the cache instead of risking a missed external
            // record; the next read performs a safe rebuild.
            if (cachedBefore && TryOpenJournal(path, out var afterStream))
            {
                using (afterStream)
                {
                    var after = CaptureSnapshot(afterStream!, path, afterStream!.Length);
                    var expectedLength = before.Length + payload.Length;
                    var previousContentMatches = !entry.Exists
                        || string.Equals(
                            ComputeFingerprint(afterStream!, before.Length),
                            before.Fingerprint,
                            StringComparison.Ordinal);
                    // File.GetCreationTimeUtc maps to inode change time on some
                    // Unix file systems, so a normal append can change it. The
                    // previous-content fingerprint plus the exact expected
                    // length is the stable identity check we need here.
                    if (previousContentMatches && after.Length == expectedLength)
                    {
                        if (!string.IsNullOrEmpty(record.JobId))
                            entry.Latest = entry.Latest.SetItem(record.JobId, record);
                        entry.Snapshot = after;
                        entry.Exists = true;
                        entry.Initialized = true;
                        return;
                    }
                }
            }

            entry.Initialized = false;
        }
    }

    public static IReadOnlyList<ReviewDecisionRecord> ReadAll(string workspaceRoot, string project)
    {
        var path = DecisionsFile(workspaceRoot, project);
        if (!File.Exists(path)) return Array.Empty<ReviewDecisionRecord>();
        var result = new List<ReviewDecisionRecord>();
        var entry = EntryFor(path);
        lock (entry.Gate)
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var rec = JsonSerializer.Deserialize<ReviewDecisionRecord>(line, Json);
                    if (rec != null) result.Add(rec);
                }
                catch (JsonException __ex) { SilentCatch.Note(__ex, "ReviewDecisionRecord: skip malformed lines; the file is append-only"); /* skip malformed lines; the file is append-only */ }
            }
        }
        return result;
    }

    /// <summary>
    /// Returns an immutable latest-record-by-job snapshot for one journal.
    /// The first call builds the index once. Later calls validate a small,
    /// distributed content fingerprint and either return the cached snapshot,
    /// parse only appended bytes, or rebuild after truncation/replacement.
    /// </summary>
    internal static IReadOnlyDictionary<string, ReviewDecisionRecord> ReadLatestByJob(
        string workspaceRoot,
        string project)
    {
        var path = DecisionsFile(workspaceRoot, project);
        var entry = EntryFor(path);
        lock (entry.Gate)
        {
            if (!TryOpenJournal(path, out var stream))
            {
                if (entry.Initialized && !entry.Exists)
                {
                    entry.CacheHits++;
                    return entry.Latest;
                }

                entry.Latest = ImmutableDictionary.Create<string, ReviewDecisionRecord>(StringComparer.Ordinal);
                entry.Snapshot = default;
                entry.Exists = false;
                entry.Initialized = true;
                entry.FullLoads++;
                return entry.Latest;
            }

            using (stream)
            {
                var length = stream!.Length;
                var current = CaptureSnapshot(stream, path, length);
                if (entry.Initialized && entry.Exists)
                {
                    if (entry.Snapshot.Equals(current))
                    {
                        entry.CacheHits++;
                        return entry.Latest;
                    }

                    if (CanApplyAsAppend(entry.Snapshot, current, stream))
                    {
                        entry.Latest = ParseRange(stream, entry.Snapshot.Length, entry.Latest);
                        entry.Snapshot = current;
                        entry.IncrementalLoads++;
                        return entry.Latest;
                    }
                }

                entry.Latest = ParseRange(
                    stream,
                    startOffset: 0,
                    ImmutableDictionary.Create<string, ReviewDecisionRecord>(StringComparer.Ordinal));
                entry.Snapshot = current;
                entry.Exists = true;
                entry.Initialized = true;
                entry.FullLoads++;
                return entry.Latest;
            }
        }
    }

    internal static ReviewDecisionIndexDiagnostics GetLatestIndexDiagnostics(
        string workspaceRoot,
        string project)
    {
        var entry = EntryFor(DecisionsFile(workspaceRoot, project));
        lock (entry.Gate)
        {
            return new ReviewDecisionIndexDiagnostics(
                entry.FullLoads,
                entry.IncrementalLoads,
                entry.CacheHits);
        }
    }

    private static LatestIndexEntry EntryFor(string path)
        => LatestIndexes.GetOrAdd(Path.GetFullPath(path), static _ => new LatestIndexEntry());

    private static bool TryOpenJournal(string path, out FileStream? stream)
    {
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            return true;
        }
        catch (FileNotFoundException)
        {
            stream = null;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            stream = null;
            return false;
        }
    }

    private static bool CanApplyAsAppend(
        JournalSnapshot cached,
        JournalSnapshot current,
        FileStream stream)
    {
        if (current.Length <= cached.Length) return false;

        // LastWriteTime necessarily changes for an append. Validate the bytes
        // that formed the previous snapshot instead. A rotation, truncation,
        // or external rewrite changes this distributed fingerprint and forces
        // a full rebuild.
        var previousContentFingerprint = ComputeFingerprint(stream, cached.Length);
        return string.Equals(
            previousContentFingerprint,
            cached.Fingerprint,
            StringComparison.Ordinal);
    }

    private static ImmutableDictionary<string, ReviewDecisionRecord> ParseRange(
        FileStream stream,
        long startOffset,
        ImmutableDictionary<string, ReviewDecisionRecord> seed)
    {
        stream.Position = startOffset;
        var latest = seed.ToBuilder();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: startOffset == 0,
            bufferSize: 64 * 1024,
            leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<ReviewDecisionRecord>(line, Json);
                if (record != null && !string.IsNullOrEmpty(record.JobId))
                    latest[record.JobId] = record;
            }
            catch (JsonException __ex)
            {
                SilentCatch.Note(
                    __ex,
                    "ReviewDecisionRecord latest index: skip malformed lines; the file is append-only");
            }
        }
        return latest.ToImmutable();
    }

    private static JournalSnapshot CaptureSnapshot(FileStream stream, string path, long length)
        => new(
            Length: length,
            CreationTimeUtc: File.GetCreationTimeUtc(path),
            LastWriteTimeUtc: File.GetLastWriteTimeUtc(path),
            Fingerprint: ComputeFingerprint(stream, length));

    private static string ComputeFingerprint(FileStream stream, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        BitConverter.TryWriteBytes(lengthBytes, length);
        hash.AppendData(lengthBytes);

        if (length == 0) return Convert.ToHexString(hash.GetHashAndReset());

        var sample = new byte[FingerprintSampleSize];
        var sampleCount = length <= FingerprintSampleSize
            ? 1
            : FingerprintSampleCount;
        for (var i = 0; i < sampleCount; i++)
        {
            var maxOffset = Math.Max(0, length - FingerprintSampleSize);
            var offset = sampleCount == 1
                ? 0
                : maxOffset * i / (sampleCount - 1);
            stream.Position = offset;
            var wanted = (int)Math.Min(FingerprintSampleSize, length - offset);
            var read = 0;
            while (read < wanted)
            {
                var count = stream.Read(sample, read, wanted - read);
                if (count == 0) break;
                read += count;
            }
            hash.AppendData(sample.AsSpan(0, read));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class LatestIndexEntry
    {
        internal readonly object Gate = new();
        internal ImmutableDictionary<string, ReviewDecisionRecord> Latest =
            ImmutableDictionary.Create<string, ReviewDecisionRecord>(StringComparer.Ordinal);
        internal JournalSnapshot Snapshot;
        internal bool Exists;
        internal bool Initialized;
        internal long FullLoads;
        internal long IncrementalLoads;
        internal long CacheHits;
    }

    private readonly record struct JournalSnapshot(
        long Length,
        DateTime CreationTimeUtc,
        DateTime LastWriteTimeUtc,
        string Fingerprint);
}

internal readonly record struct ReviewDecisionIndexDiagnostics(
    long FullLoads,
    long IncrementalLoads,
    long CacheHits);
