using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Builds the fenced authority inventory sent with runner registration. A
/// coding slot is reported when the host can still prove its exact process
/// generation or has a durable terminal result waiting to be delivered. A
/// review slot is reported only while it can still prove its exact process
/// generation is live: once verification has produced a durable terminal
/// result, delivering the report is pure idempotency-key replay against the
/// attempt authority and needs no re-adoption (AGT-2762 - reporting a
/// report-pending slot as active made the server reject its next
/// re-registration as "claim authority lost" once the attempt had already
/// settled).
/// </summary>
public static class RunnerActiveAttemptReporter
{
    public static IReadOnlyList<Contract.RunnerActiveAttempt> Coding(
        IEnumerable<PersistedRunnerSlot> slots)
    {
        var active = new List<Contract.RunnerActiveAttempt>();
        foreach (var slot in slots)
        {
            var observed = slot;
            if ((slot.ProcessId is null || slot.ProcessStartedAtUtc is null)
                && DurableAgentProcess.TryRecoverIdentity(slot, out var recovered, out _))
            {
                observed = recovered;
            }
            var observation = DurableAgentProcess.InspectForReattach(observed);
            if (!observation.IsLive && observation.Result is null) continue;
            var attemptId = observed.RunId ?? observed.Lease.AttemptId ?? observed.AttemptId;
            if (string.IsNullOrWhiteSpace(attemptId)) continue;
            active.Add(new Contract.RunnerActiveAttempt(
                Contract.RunnerAttemptKinds.Coding,
                attemptId,
                observed.TaskKey,
                observed.Lease.LeaseId,
                observed.Lease.FencingToken,
                observed.Lease.AuthorityEpoch,
                observed.LeaseInstanceId));
        }
        return active;
    }

    public static IReadOnlyList<Contract.RunnerActiveAttempt> Review(
        IEnumerable<PersistedReviewSlot> slots)
    {
        var active = new List<Contract.RunnerActiveAttempt>();
        foreach (var slot in slots)
        {
            var observed = slot;
            if ((slot.ProcessId is null || slot.ProcessStartedAtUtc is null)
                && DurableReviewProcess.TryRecoverIdentity(slot, out var recovered, out _))
            {
                observed = recovered;
            }
            // A durable terminal result means verification already finished;
            // only report delivery remains, and that is a fenced, idempotent
            // replay the authority does not need to re-adopt.
            if (DurableReviewProcess.HasCompleted(observed))
                continue;
            if (!DurableReviewProcess.VerifyLive(observed, out _))
                continue;
            var lease = observed.Claim.Lease;
            if (lease is null) continue;
            active.Add(new Contract.RunnerActiveAttempt(
                Contract.RunnerAttemptKinds.Review,
                observed.AttemptId,
                observed.Claim.Attempt?.TaskId ?? string.Empty,
                lease.LeaseId,
                lease.Fence,
                lease.AuthorityEpoch,
                lease.InstanceId));
        }
        return active;
    }
}
