using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Host-local handoff record for one fenced remote review. Task authority stays
/// on the Task Server; this record carries only the immutable claim and process
/// proof that a replacement daemon needs to adopt the existing execution.
/// </summary>
public sealed record PersistedReviewSlot(
    ReviewClaimResponse Claim,
    string WorkerDirectory,
    string WorkspacePath,
    int? ProcessId,
    DateTime? ProcessStartedAtUtc,
    string Phase,
    DateTime UpdatedAtUtc,
    string? AdoptionFailure = null,
    DateTime? CreatedAtUtc = null,
    DateTime? ReportPendingSinceUtc = null,
    int ReportSubmissionAttempts = 0,
    DateTime? LastReportSubmissionAtUtc = null,
    int? LastReportStatusCode = null,
    string? LastReportErrorCode = null,
    string? LastReportError = null,
    string? TerminalClassification = null,
    ReviewReClaimRequest? PendingReClaim = null)
{
    public string AttemptId => Claim.Attempt?.AttemptId
                               ?? throw new InvalidDataException("Persisted review claim has no attempt.");
}

/// <summary>Atomic review-slot persistence below RUNNER_STATE_DIR/reviews.</summary>
public sealed class ReviewStateStore
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();

    public ReviewStateStore(string runnerStateRoot)
    {
        Root = Path.Combine(Path.GetFullPath(runnerStateRoot), "reviews");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public PersistedReviewSlot Create(ReviewClaimResponse claim, string workspacePath)
    {
        ValidateClaim(claim);
        var attemptId = claim.Attempt!.AttemptId;
        var workerDirectory = Path.Combine(Root, RemoteReviewWorkspace.SafeSegment(attemptId));
        Directory.CreateDirectory(workerDirectory);
        var now = DateTime.UtcNow;
        var slot = new PersistedReviewSlot(
            claim,
            workerDirectory,
            Path.GetFullPath(workspacePath),
            null,
            null,
            "preparing",
            now,
            CreatedAtUtc: now);
        return Save(slot);
    }

    public PersistedReviewSlot Save(PersistedReviewSlot slot)
    {
        ValidateClaim(slot.Claim);
        ValidatePendingReClaim(slot);
        slot = slot with { UpdatedAtUtc = DateTime.UtcNow };
        var path = StatePath(slot.AttemptId);
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        lock (_gate)
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonSerializer.Serialize(slot, Json));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        return slot;
    }

    public IReadOnlyList<PersistedReviewSlot> LoadAll()
    {
        lock (_gate)
        {
            var slots = new List<PersistedReviewSlot>();
            foreach (var path in Directory.EnumerateFiles(Root, "*.review-slot.json")
                         .OrderBy(item => item, StringComparer.Ordinal))
            {
                try
                {
                    var slot = JsonSerializer.Deserialize<PersistedReviewSlot>(File.ReadAllText(path), Json);
                    if (slot is not null)
                    {
                        ValidateClaim(slot.Claim);
                        ValidatePendingReClaim(slot);
                        slots.Add(slot);
                    }
                }
                catch (Exception exception) when (exception is IOException or JsonException)
                {
                    throw new InvalidDataException($"Review state is unreadable: {path}", exception);
                }
            }
            return slots;
        }
    }

    public ReviewSlotHygieneSnapshot GetHygieneSnapshot(DateTime utcNow)
    {
        var slots = LoadAll();
        var pending = slots.Where(slot =>
                string.Equals(slot.Phase, "report-pending", StringComparison.Ordinal)
                || string.Equals(slot.Phase, "report-submitting", StringComparison.Ordinal))
            .ToList();
        var oldest = pending.Count == 0
            ? (TimeSpan?)null
            : pending.Max(slot => utcNow - (slot.ReportPendingSinceUtc ?? slot.UpdatedAtUtc));
        if (oldest < TimeSpan.Zero) oldest = TimeSpan.Zero;
        var cleanupPending = slots.Count(slot =>
            string.Equals(slot.Phase, "terminal-cleanup-pending", StringComparison.Ordinal));
        return new ReviewSlotHygieneSnapshot(
            slots.Count,
            pending.Count,
            oldest,
            cleanupPending);
    }

    public PersistedReviewSlot? Find(string attemptId)
        => LoadAll().FirstOrDefault(slot => string.Equals(
            slot.AttemptId,
            attemptId,
            StringComparison.Ordinal));

    public void Delete(PersistedReviewSlot slot)
    {
        lock (_gate)
        {
            var path = StatePath(slot.AttemptId);
            if (File.Exists(path)) File.Delete(path);
        }
        try
        {
            if (Directory.Exists(slot.WorkerDirectory))
                Directory.Delete(slot.WorkerDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A just-exited worker may briefly retain a file handle. The state
            // record is already gone, so the bounded directory is safe to reap
            // on the next host maintenance pass.
        }
    }

    public void Flush() { }

    private string StatePath(string attemptId)
        => Path.Combine(Root, $"{RemoteReviewWorkspace.SafeSegment(attemptId)}.review-slot.json");

    private static void ValidateClaim(ReviewClaimResponse claim)
    {
        if (claim.Attempt is null || claim.Subject is null || claim.Lease is null)
            throw new InvalidDataException(
                "A persisted review slot requires an attempt, immutable subject, and fenced lease.");
        if (!string.Equals(claim.Attempt.AttemptId, claim.Lease.AttemptId, StringComparison.Ordinal)
            || !string.Equals(claim.Attempt.SubjectId, claim.Subject.SubjectId, StringComparison.Ordinal)
            || !string.Equals(claim.Subject.SubjectId, claim.Lease.SubjectId, StringComparison.Ordinal))
            throw new InvalidDataException("Persisted review attempt, subject, and lease identities disagree.");
    }

    private static void ValidatePendingReClaim(PersistedReviewSlot slot)
    {
        if (slot.PendingReClaim is not { } pending) return;
        var lease = slot.Claim.Lease!;
        if (!string.Equals(pending.ExecutorId, lease.ExecutorId, StringComparison.Ordinal)
            || !string.Equals(pending.PreviousLeaseId, lease.LeaseId, StringComparison.Ordinal)
            || pending.PreviousFence != lease.Fence
            || string.IsNullOrWhiteSpace(pending.InstanceId)
            || string.IsNullOrWhiteSpace(pending.IdempotencyKey))
        {
            throw new InvalidDataException(
                "Persisted review reclaim request does not match its previous fenced lease.");
        }
    }
}

public sealed record ReviewSlotHygieneSnapshot(
    int Total,
    int ReportPending,
    TimeSpan? OldestReportPendingAge,
    int TerminalCleanupPending);
