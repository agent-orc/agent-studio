using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Security;

/// <summary>
/// The one audit writer of the control plane (security model SM-1, decision
/// D4 option A2). Every entry carries actor, session, delegating Owner, route
/// or operation, target, parameter digest, outcome, time, and the hash of the
/// previous entry, and is appended as one JSON line to
/// <c>.security/control-plane-audit.jsonl</c>. The writer never edits or
/// removes a line. The IAM, service-account, API-token, operator, secret, and
/// run audits are routed through it with their full legacy record in
/// <c>details</c>; <see cref="AuditSinkForwarder"/> copies each entry to the
/// off-host sink and <see cref="ControlPlaneAuditVerifier"/> recomputes the chain.
/// </summary>
public sealed class ControlPlaneAuditLog
{
    public const string FileName = "control-plane-audit.jsonl";
    public const string ForwardPositionFileName = "control-plane-audit.forwarded.json";
    public static readonly string GenesisHash = new('0', 64);
    private const string HashMarker = ",\"hash\":\"";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _folder;
    private readonly TimeProvider _clock;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _appended = new(0, 1);
    private (long Sequence, string Hash)? _head;
    // The file length after this process last read or wrote it. A longer file means another process (the
    // out-of-band kill switch while the API was stopped or starting) appended; a shorter one keeps the cached
    // head, so a truncation still shows up as a gap below HighestWritten.
    private long _knownLength;
    private long _highestWritten;
    private bool _tornTail;
    private bool _recoveryAnchorLoaded;
    private ControlPlaneAuditEntry? _recoveryAnchor;

    public ControlPlaneAuditLog(string folder, TimeProvider? clock = null, ILogger? logger = null)
    {
        _folder = folder;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    public string FilePath => Path.Combine(_folder, FileName);
    public string ForwardPositionPath => Path.Combine(_folder, ForwardPositionFileName);

    /// <summary>The highest sequence this writer appended or imported from an existing head.</summary>
    public long HighestWritten { get { lock (_gate) return _highestWritten; } }

    public long HeadSequence { get { lock (_gate) return HeadLocked().Sequence; } }

    /// <summary>
    /// Appends one entry. Inside an operator agent session the entry is
    /// attributed to that session (<see cref="AgentStudio.Shared.OperatorActorScope"/>):
    /// actor, session, and delegating Owner come from the session, and a
    /// different non-human actor the producer named is kept as <c>reportedActor</c>.
    /// </summary>
    public ControlPlaneAuditEntry Append(ControlPlaneAuditRecord record)
    {
        var actor = record.Actor;
        string? reported = null;
        var session = record.Session;
        var delegatedBy = record.DelegatedBy;
        if (AgentStudio.Shared.OperatorActorScope.Current is { } op)
        {
            if (!string.IsNullOrWhiteSpace(actor) && actor != op.Actor
                && !AgentStudio.Shared.TimelineActors.IsHuman(actor) && !AgentStudio.Shared.TimelineActors.IsOperatorAgent(actor)
                && !AgentStudio.Shared.TimelineActors.IsPersona(actor))
                reported = actor;
            actor = op.Actor;
            session = op.SessionId;
            delegatedBy = op.DelegatedBy;
        }
        // The producer's record keeps its own member names: a JSON element as
        // given, any other object with the serializer defaults its legacy file uses.
        var details = record.Details switch
        {
            null => (JsonElement?)null,
            JsonElement element => element,
            var other => JsonSerializer.SerializeToElement(other, other.GetType()),
        };
        lock (_gate)
        {
            var head = HeadLocked();
            var body = JsonSerializer.Serialize(new Body(
                head.Sequence + 1, record.At ?? _clock.GetUtcNow().UtcDateTime, record.Stream,
                string.IsNullOrWhiteSpace(actor) ? "system" : actor, reported, session, delegatedBy,
                record.Operation, record.Target, record.ParameterDigest, record.Outcome, details, head.Hash), Json);
            var hash = Sha256Hex(body);
            var line = body[..^1] + HashMarker + hash + "\"}";
            WriteLineLocked(line);
            _head = (head.Sequence + 1, hash);
            _highestWritten = Math.Max(_highestWritten, head.Sequence + 1);
            // Appends run under the gate, so the count cannot change between the check and the release.
            if (_appended.CurrentCount == 0) _appended.Release();
            var entry = Parse(line)!;
            if (entry.Operation == RecoveryOperation)
            {
                _recoveryAnchor = entry;
                _recoveryAnchorLoaded = true;
            }
            return entry;
        }
    }

    /// <summary>Completes when an entry was appended or <paramref name="timeout"/> passed.</summary>
    public async Task WaitForAppendAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _appended.WaitAsync(timeout, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    }

    /// <summary>Entries with a sequence of at least <paramref name="fromSequence"/>, oldest first.</summary>
    public IReadOnlyList<ControlPlaneAuditEntry> Read(long fromSequence, int limit)
    {
        var result = new List<ControlPlaneAuditEntry>();
        foreach (var line in SnapshotLines())
        {
            var entry = Parse(line);
            if (entry is null || entry.Sequence < fromSequence) continue;
            result.Add(entry);
            if (result.Count >= limit) break;
        }
        return result;
    }

    /// <summary>The newest entries that match <paramref name="filter"/>, newest first.</summary>
    public IReadOnlyList<ControlPlaneAuditEntry> ReadNewest(Func<ControlPlaneAuditEntry, bool> filter, int limit)
    {
        var result = new List<ControlPlaneAuditEntry>();
        var lines = SnapshotLines();
        for (var index = lines.Count - 1; index >= 0 && result.Count < limit; index--)
            if (Parse(lines[index]) is { } entry && filter(entry)) result.Add(entry);
        return result;
    }

    /// <summary>Reads recent entries from the file tail and stops at the time boundary.</summary>
    public IReadOnlyList<ControlPlaneAuditEntry> ReadNewestSince(
        DateTimeOffset since, Func<ControlPlaneAuditEntry, bool> filter, int limit)
    {
        var result = new List<ControlPlaneAuditEntry>();
        if (limit <= 0 || !File.Exists(FilePath)) return result;
        long length;
        lock (_gate) length = new FileInfo(FilePath).Length;
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var chunk = new byte[16 * 1024];
        var reversed = new List<byte>();
        var position = Math.Min(length, stream.Length);
        if (position == 0) return result;
        stream.Position = position - 1;
        var firstLine = stream.ReadByte() != '\n';
        var boundary = since.AddMinutes(-2);
        while (position > 0)
        {
            var size = (int)Math.Min(chunk.Length, position);
            position -= size;
            stream.Position = position;
            stream.ReadExactly(chunk.AsSpan(0, size));
            for (var index = size - 1; index >= 0; index--)
            {
                if (chunk[index] != '\n') { reversed.Add(chunk[index]); continue; }
                if (firstLine && reversed.Count > 0) { reversed.Clear(); firstLine = false; continue; }
                firstLine = false;
                if (reversed.Count == 0) continue;
                reversed.Reverse();
                var entry = Parse(Encoding.UTF8.GetString(reversed.ToArray()));
                reversed.Clear();
                if (entry is null) continue;
                if (entry.At < boundary) return result;
                if (entry.At >= since && filter(entry)) result.Add(entry);
                if (result.Count >= limit) return result;
            }
        }
        if (reversed.Count > 0 && !firstLine)
        {
            reversed.Reverse();
            if (Parse(Encoding.UTF8.GetString(reversed.ToArray())) is { } entry
                && entry.At >= since && filter(entry)) result.Add(entry);
        }
        return result;
    }

    // Runs outside the append gate so tests can deterministically interleave mutations with verification.
    internal Action? VerificationSnapshotCaptured { get; set; }

    /// <summary>
    /// Recomputes the chain from genesis, or from an explicit recovery anchor
    /// after checking the seal of the damaged prefix. Each subsequent line must
    /// hash correctly, continue the sequence, and name its predecessor's hash.
    /// </summary>
    public ChainCheck VerifyChain()
    {
        byte[] bytes;
        long highestWritten;
        ControlPlaneAuditForwardPosition position;
        lock (_gate)
        {
            // The bytes and both watermarks describe the same instant. Hashing and sink I/O
            // happen after releasing the writer, without consulting newer watermarks.
            bytes = SnapshotBytesLocked();
            highestWritten = _highestWritten;
            position = ReadForwardPosition();
        }
        VerificationSnapshotCaptured?.Invoke();
        var lines = Lines(bytes);
        var hashes = new List<string>(lines.Count);
        var previous = GenesisHash;
        long anchorSequence = 0;
        var start = 0;
        long observedThrough = 0;
        var byteOffset = 0;
        using var prefix = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // A recovery anchor seals the exact damaged prefix, rather than rewriting or
        // silently treating it as intact. Its own entry still has to verify below.
        for (var index = 0; index < lines.Count; index++)
        {
            var parsed = Parse(lines[index]);
            observedThrough = Math.Max(observedThrough, parsed?.Sequence ?? 0);
            if (parsed is { Operation: RecoveryOperation } entry
                && entry.Details is { ValueKind: JsonValueKind.Object } details
                && details.TryGetProperty("prefixHash", out var digest)
                && details.TryGetProperty("previousSequence", out var prior)
                && digest.ValueKind == JsonValueKind.String && prior.ValueKind == JsonValueKind.Number
                && prior.TryGetInt64(out var sequence) && sequence >= 0 && entry.Sequence == sequence + 1
                && digest.GetString() is { } expectedDigest
                && expectedDigest == Convert.ToHexStringLower(prefix.GetCurrentHash())
                && entry.PreviousHash == expectedDigest
                && TrySplit(lines[index], out var body, out var hash) && Sha256Hex(body) == hash)
            {
                anchorSequence = sequence;
                previous = expectedDigest;
                start = index;
            }
            var newline = Array.IndexOf(bytes, (byte)'\n', byteOffset);
            var end = newline < 0 ? bytes.Length : newline + 1;
            prefix.AppendData(bytes.AsSpan(byteOffset, end - byteOffset));
            byteOffset = end;
        }
        ChainCheck Result(bool ok, string? problem = null, long? sequence = null, string? message = null)
            => new(ok, hashes, problem, sequence, message, anchorSequence, highestWritten, position,
                bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)), observedThrough);
        for (var index = start; index < lines.Count; index++)
        {
            var expected = anchorSequence + hashes.Count + 1;
            if (!TrySplit(lines[index], out var body, out var hash) || Sha256Hex(body) != hash)
                return Result(false, "modified", expected, $"Entry at line {index + 1} does not match its hash.");
            var entry = Parse(lines[index]);
            if (entry is null)
                return Result(false, "modified", expected, $"Entry at line {index + 1} cannot be read.");
            if (entry.Sequence != expected)
                return Result(false, entry.Sequence > expected ? "gap" : "reordered", expected,
                    $"Expected sequence {expected}, found {entry.Sequence}.");
            if (entry.PreviousHash != previous)
                return Result(false, "reordered", expected, $"Entry {expected} does not follow entry {expected - 1}.");
            hashes.Add(hash);
            previous = hash;
        }
        return Result(true);
    }

    internal const string RecoveryOperation = "audit.chain-anchor";

    internal ControlPlaneAuditEntry? LatestRecoveryAnchor
    {
        get
        {
            lock (_gate)
            {
                if (!_recoveryAnchorLoaded)
                {
                    _recoveryAnchor = ReadNewest(entry => entry.Operation == RecoveryOperation
                        && TrySplit(entry.Raw, out var body, out var hash) && Sha256Hex(body) == hash, 1).FirstOrDefault();
                    _recoveryAnchorLoaded = true;
                }
                return _recoveryAnchor;
            }
        }
    }

    /// <summary>
    /// Seals a detected local break with a new explicit anchor. Existing bytes stay
    /// untouched. Concurrent appends are included; a changed prefix invalidates the proposal.
    /// </summary>
    internal bool TryAnchorBreak(ChainCheck check, string message, string occurrence, DateTime firstObservedAt)
    {
        lock (_gate)
        {
            var bytes = SnapshotBytesLocked();
            if (check.SnapshotLength < 0 || check.SnapshotLength > bytes.LongLength
                || Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, checked((int)check.SnapshotLength))))
                    != check.SnapshotHash) return false;
            var lastSequence = Lines(bytes).Select(Parse).OfType<ControlPlaneAuditEntry>()
                .Select(entry => entry.Sequence).DefaultIfEmpty(0).Max();
            var sequence = Math.Max(Math.Max(check.ProblemSequence ?? 0, check.HighestWritten),
                Math.Max(Math.Max(lastSequence, _highestWritten), ReadForwardPosition().Sequence));
            _tornTail = bytes.Length > 0 && bytes[^1] != (byte)'\n';
            byte[] prefixBytes = _tornTail ? [.. bytes, (byte)'\n'] : bytes;
            var prefixHash = Convert.ToHexStringLower(SHA256.HashData(prefixBytes));
            _head = (sequence, prefixHash);
            _knownLength = bytes.LongLength;
            Append(new(ControlPlaneAuditStreams.Operator, "system", RecoveryOperation, "history-broken",
                Details: new
                {
                    prefixHash, previousSequence = sequence, firstAffected = check.ProblemSequence ?? check.VerifiedThrough + 1,
                    lastAffected = sequence, message, process = ProcessIdentity, occurrence, firstObservedAt,
                }));
            return true;
        }
    }

    internal static string ProcessIdentity => $"{Environment.MachineName}/pid:{Environment.ProcessId}";

    public ControlPlaneAuditForwardPosition ReadForwardPosition()
    {
        lock (_gate)
        {
            if (!File.Exists(ForwardPositionPath)) return new ControlPlaneAuditForwardPosition(0, GenesisHash, null);
            try
            {
                return JsonSerializer.Deserialize<ControlPlaneAuditForwardPosition>(File.ReadAllText(ForwardPositionPath), Json)
                    ?? new ControlPlaneAuditForwardPosition(0, GenesisHash, null);
            }
            catch (JsonException exception)
            {
                SilentCatch.Note(exception, "ControlPlaneAuditLog: unreadable forward position, forwarding restarts at 1");
                return new ControlPlaneAuditForwardPosition(0, GenesisHash, null);
            }
        }
    }

    public void WriteForwardPosition(ControlPlaneAuditForwardPosition position)
    {
        lock (_gate)
        {
            EnsureFolder();
            var temp = ForwardPositionPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(position, Json));
            Restrict(temp);
            File.Move(temp, ForwardPositionPath, true);
        }
    }

    public static ControlPlaneAuditEntry? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<ControlPlaneAuditEntry>(line, Json);
            return entry is null ? null : entry with { Raw = line };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A lowercase hex SHA-256 over UTF-8 text.</summary>
    public static string Sha256Hex(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The hashed body of a stored line and the hash it claims.</summary>
    public static bool TrySplit(string line, out string body, out string hash)
    {
        body = hash = string.Empty;
        var marker = line.LastIndexOf(HashMarker, StringComparison.Ordinal);
        if (marker < 1 || line.Length != marker + HashMarker.Length + 64 + 2 || !line.EndsWith("\"}", StringComparison.Ordinal))
            return false;
        hash = line.Substring(marker + HashMarker.Length, 64);
        body = line[..marker] + "}";
        return true;
    }

    private (long Sequence, string Hash) HeadLocked()
    {
        if (_head is { } head && CurrentLength() <= _knownLength) return head;
        var last = (0L, GenesisHash);
        if (File.Exists(FilePath))
            foreach (var line in File.ReadLines(FilePath))
                if (Parse(line) is { } entry && TrySplit(line, out _, out var hash))
                    last = (entry.Sequence, hash);
        _head = last;
        _knownLength = CurrentLength();
        _highestWritten = Math.Max(_highestWritten, last.Item1);
        _tornTail = EndsWithoutNewline();
        return last;
    }

    private long CurrentLength() => File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;

    private void WriteLineLocked(string line)
    {
        EnsureFolder();
        var created = !File.Exists(FilePath);
        using (var stream = new FileStream(FilePath, new FileStreamOptions
               { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read }))
        {
            // A line torn by a crash stays as it is (verification reports it);
            // the next entry starts on its own line.
            stream.Write(Encoding.UTF8.GetBytes((_tornTail ? "\n" : "") + line + "\n"));
            _tornTail = false;
            stream.Flush(flushToDisk: true);
            _knownLength = stream.Length;
        }
        if (created) Restrict(FilePath);
    }

    private bool EndsWithoutNewline()
    {
        if (!File.Exists(FilePath)) return false;
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0) return false;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    /// <summary>The bytes stored at one instant under the append gate.</summary>
    private byte[] SnapshotBytesLocked()
    {
        if (!File.Exists(FilePath)) return [];
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static List<string> Lines(byte[] bytes)
    {
        var lines = Encoding.UTF8.GetString(bytes).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private List<string> SnapshotLines()
    {
        lock (_gate) return Lines(SnapshotBytesLocked());
    }

    private void EnsureFolder()
    {
        if (Directory.Exists(_folder)) return;
        Directory.CreateDirectory(_folder);
        Restrict(_folder, directory: true);
    }

    private void Restrict(string path, bool directory = false)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, directory
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "Could not restrict audit file permissions for {Path}", path);
        }
    }

    /// <summary>The hashed part of an entry, in its stored member order.</summary>
    private sealed record Body(
        long Sequence, DateTime At, string Stream, string Actor, string? ReportedActor, string? Session, string? DelegatedBy,
        string Operation, string? Target, string? ParameterDigest, string Outcome, JsonElement? Details, string PreviousHash);
}

/// <summary>
/// The result of <see cref="ControlPlaneAuditLog.VerifyChain"/>: the hashes of
/// the verified segment (index = sequence - AnchorSequence - 1), its captured
/// watermarks and byte fingerprint, and the first problem.
/// </summary>
public sealed record ChainCheck(
    bool Ok, IReadOnlyList<string> Hashes, string? Problem, long? ProblemSequence, string? Message,
    long AnchorSequence, long HighestWritten, ControlPlaneAuditForwardPosition ForwardPosition,
    long SnapshotLength, string SnapshotHash, long ObservedThrough)
{
    public long VerifiedThrough => AnchorSequence + Hashes.Count;

    public string HashAt(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sequence, AnchorSequence);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequence, VerifiedThrough);
        // Only the offset within this captured segment becomes an int, after its bounds are checked.
        return Hashes[checked((int)(sequence - AnchorSequence - 1))];
    }
}
