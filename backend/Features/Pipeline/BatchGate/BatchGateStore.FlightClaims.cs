using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3569: the durable claim of one batch flight on its scope, the project's
/// flight slot. <see cref="Fence"/> grows with every grant on the scope, so a
/// holder whose claim was expired and granted again can never pass a claim
/// check. <see cref="End"/> is set once the claim was released or expired.
/// </summary>
public sealed record BatchFlightClaim(
    string ClaimId, string Project, string BatchId, string Owner, long Fence,
    BatchFlightStage Stage, DateTimeOffset ClaimedAtUtc, DateTimeOffset StageStartedAtUtc,
    DateTimeOffset StageDeadlineUtc, int StageDeadlineMinutes, DateTimeOffset HeartbeatExpiresAtUtc,
    BatchFlightClaimEnd? End = null)
{
    public BatchFlightClaimFacts Facts(DateTimeOffset nowUtc)
        => new(Stage, StageDeadlineUtc, HeartbeatExpiresAtUtc, End is not null, End?.Settled ?? false, nowUtc);

    public bool SameGrant(BatchFlightClaim other)
        => ClaimId == other.ClaimId && Fence == other.Fence
           && string.Equals(Project, other.Project, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// How a claim ended. A released claim is settled at once. An expired claim is
/// settled once its <see cref="Outcome"/> reached the batch; until then the
/// scope stays closed to new claims.
/// </summary>
public sealed record BatchFlightClaimEnd(
    DateTimeOffset AtUtc, string Reason, BatchFlightClaimExpiry? Expiry = null,
    BatchFlightExpiryOutcome? Outcome = null, bool Settled = true, DateTimeOffset? SettledAtUtc = null);

/// <summary>A claim request's result: the granted claim, or the refusal and the claim that holds the scope.</summary>
public sealed record BatchFlightClaimGrant(BatchFlightClaim? Claim, string? Refusal, BatchFlightClaim? Current);

public sealed partial class BatchGateStore
{
    private const string FlightClaimName = "claim.json";

    public BatchFlightClaim? ReadFlightClaim(string project)
    {
        var path = Path.Combine(FlightClaimFolder(project), FlightClaimName);
        return File.Exists(path) ? Read<BatchFlightClaim>(path) : null;
    }

    public IReadOnlyList<BatchFlightClaim> ListFlightClaims()
    {
        var root = Path.Combine(_root, "flight-claims");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .Select(folder => Path.Combine(folder, FlightClaimName))
            .Where(File.Exists).Select(Read<BatchFlightClaim>)
            .OrderBy(claim => claim.Project, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>The live claim of <paramref name="project"/>, or null.</summary>
    public BatchFlightClaim? LiveFlightClaim(string project, DateTimeOffset nowUtc)
        => ReadFlightClaim(project) is { } claim
           && MonolithTransitionAdapter.BatchFlightClaimLive(claim.Facts(nowUtc)) ? claim : null;

    /// <summary>
    /// Claims the flight slot of the batch's project for <paramref name="batchId"/>
    /// in the <see cref="BatchFlightStage.Reserving"/> step. One live claim per
    /// project across processes: the decision runs under a cross-process lock.
    /// </summary>
    public BatchFlightClaimGrant TryClaimFlight(string project, string batchId, string owner,
        int reservationDeadlineMinutes, DateTimeOffset nowUtc)
    {
        var folder = FlightClaimFolder(project);
        using var guard = ExclusiveGuard(folder, "claim.lock");
        var current = ReadFlightClaim(project);
        var decision = MonolithTransitionAdapter.BatchFlightClaim(current?.Facts(nowUtc));
        if (!decision.Granted) return new(null, decision.Refusal, current);
        var claim = new BatchFlightClaim(Guid.NewGuid().ToString("N"), project, batchId, owner,
            (current?.Fence ?? 0) + 1, BatchFlightStage.Reserving, nowUtc, nowUtc,
            nowUtc + TimeSpan.FromMinutes(reservationDeadlineMinutes), reservationDeadlineMinutes,
            nowUtc + BatchFlightClaimPolicy.HeartbeatTtl);
        WriteAtomic(Path.Combine(folder, FlightClaimName), claim);
        return new(claim, null, current);
    }

    /// <summary>
    /// Moves a live claim to its next step with a fresh deadline. Null when the
    /// claim is no longer the live grant of its scope: the caller must stop.
    /// </summary>
    public BatchFlightClaim? AdvanceFlightClaim(BatchFlightClaim held, BatchFlightStage stage,
        int deadlineMinutes, DateTimeOffset nowUtc)
        => UpdateLive(held, nowUtc, current => current with
        {
            Stage = stage, StageStartedAtUtc = nowUtc,
            StageDeadlineUtc = nowUtc + TimeSpan.FromMinutes(deadlineMinutes),
            StageDeadlineMinutes = deadlineMinutes,
            HeartbeatExpiresAtUtc = nowUtc + BatchFlightClaimPolicy.HeartbeatTtl,
        });

    /// <summary>Extends the heartbeat of a live claim; a step past its deadline is not extended.</summary>
    public BatchFlightClaim? RenewFlightClaim(BatchFlightClaim held, DateTimeOffset nowUtc)
        => UpdateLive(held, nowUtc, current => MonolithTransitionAdapter.BatchFlightClaimRenews(
                current.Facts(nowUtc))
            ? current with { HeartbeatExpiresAtUtc = nowUtc + BatchFlightClaimPolicy.HeartbeatTtl }
            : null);

    /// <summary>True while <paramref name="held"/> is the live grant of its scope.</summary>
    public bool IsFlightClaimLive(BatchFlightClaim held, DateTimeOffset nowUtc)
        => ReadFlightClaim(held.Project) is { } current && current.SameGrant(held)
           && MonolithTransitionAdapter.BatchFlightClaimLive(current.Facts(nowUtc));

    /// <summary>
    /// The holder ends its own claim. A claim another process already expired
    /// stays expired, so its outcome still reaches the batch.
    /// </summary>
    public bool ReleaseFlightClaim(BatchFlightClaim held, string reason, DateTimeOffset nowUtc)
    {
        var folder = FlightClaimFolder(held.Project);
        using var guard = ExclusiveGuard(folder, "claim.lock");
        if (ReadFlightClaim(held.Project) is not { End: null } current || !current.SameGrant(held))
            return false;
        WriteAtomic(Path.Combine(folder, FlightClaimName),
            current with { End = new BatchFlightClaimEnd(nowUtc, reason, SettledAtUtc: nowUtc) });
        return true;
    }

    /// <summary>
    /// Ends a claim that is no longer live. Refused while the claim is live or
    /// when another grant replaced it. <paramref name="settled"/> false keeps
    /// the scope closed until <see cref="SettleFlightClaim"/> records that the
    /// outcome reached the batch.
    /// </summary>
    public BatchFlightClaim? ExpireFlightClaim(BatchFlightClaim observed, BatchFlightExpiryOutcome outcome,
        string reason, bool settled, DateTimeOffset nowUtc, bool ownerGone = false)
    {
        var folder = FlightClaimFolder(observed.Project);
        using var guard = ExclusiveGuard(folder, "claim.lock");
        if (ReadFlightClaim(observed.Project) is not { End: null } current || !current.SameGrant(observed)
            || MonolithTransitionAdapter.BatchFlightClaimExpiryOf(
                current.Facts(nowUtc) with { OwnerGone = ownerGone }) is not { } expiry)
            return null;
        var expired = current with
        {
            End = new BatchFlightClaimEnd(nowUtc, reason, expiry, outcome, settled,
                settled ? nowUtc : null),
        };
        WriteAtomic(Path.Combine(folder, FlightClaimName), expired);
        return expired;
    }

    /// <summary>Records that the outcome of an expired claim reached its batch; the scope is claimable again.</summary>
    public bool SettleFlightClaim(BatchFlightClaim expired, DateTimeOffset nowUtc)
    {
        var folder = FlightClaimFolder(expired.Project);
        using var guard = ExclusiveGuard(folder, "claim.lock");
        if (ReadFlightClaim(expired.Project) is not { End: { Settled: false } end } current
            || !current.SameGrant(expired))
            return false;
        WriteAtomic(Path.Combine(folder, FlightClaimName),
            current with { End = end with { Settled = true, SettledAtUtc = nowUtc } });
        return true;
    }

    private BatchFlightClaim? UpdateLive(BatchFlightClaim held, DateTimeOffset nowUtc,
        Func<BatchFlightClaim, BatchFlightClaim?> change)
    {
        var folder = FlightClaimFolder(held.Project);
        using var guard = ExclusiveGuard(folder, "claim.lock");
        if (ReadFlightClaim(held.Project) is not { } current || !current.SameGrant(held)
            || !MonolithTransitionAdapter.BatchFlightClaimLive(current.Facts(nowUtc)))
            return null;
        if (change(current) is not { } next) return null;
        WriteAtomic(Path.Combine(folder, FlightClaimName), next);
        return next;
    }

    private string FlightClaimFolder(string project)
        => Path.Combine(_root, "flight-claims", SafeName(project.ToLowerInvariant()));

    private static void WriteAtomic<T>(string path, T value)
    {
        BatchGateDurableWrites.Starting(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        BatchGateDurableWrites.Completed(path);
    }
}
