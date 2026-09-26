using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Server.Features.Access;
using AgentStudio.Operations.Server.Features.Agents;

namespace AgentStudio.Operations.Server.Features.Dispatch;

public sealed class OperationsCoordinator(OperationsStore store, IOperationPermitAuthority permits, TimeProvider clock)
{
    public Task<AgentRegistration> RegisterAsync(OperationsPrincipal principal, AgentCapability capability) =>
        store.TransactionAsync(state =>
        {
            Reject(AgentPolicy.Registration(principal, capability), 403);
            Expire(state);
            if (!state.Agents.ContainsKey(capability.AgentId) && state.Agents.Count >= OperationsStore.MaxAgents)
                throw new OperationRejected("agent-capacity", 507);
            if (state.Agents.TryGetValue(capability.AgentId, out var previous) && previous.Capability.BootId != capability.BootId
                && state.Attempts.Values.Any(a => a.AgentId == capability.AgentId && a.State is "running" or "cancelling"))
                throw new OperationRejected("previous-boot-active");
            var registration = new AgentRegistration(capability, clock.GetUtcNow());
            state.Agents[capability.AgentId] = registration;
            return Task.FromResult(registration);
        });

    public Task<OperationAttempt> SubmitAsync(OperationsPrincipal principal, OperationCommand command, CancellationToken ct) =>
        store.TransactionAsync(async state =>
        {
            Reject(OperationPolicy.Admission(principal, command, clock.GetUtcNow()), 403);
            var existing = state.Attempts.Values.SingleOrDefault(a => a.PrincipalId == principal.Id
                && a.Command.IdempotencyKey == command.IdempotencyKey);
            if (existing is not null)
            {
                if (OperationsProtocol.Digest(existing.Command) != OperationsProtocol.Digest(command))
                    throw new OperationRejected("idempotency-conflict");
                return existing;
            }
            await RequirePermitAsync(principal.Id, command, ct);
            Expire(state);
            if (state.Attempts.Count >= OperationsStore.MaxAttempts) throw new OperationRejected("attempt-capacity", 507);
            if (!state.Agents.TryGetValue(command.AgentId, out var agent) || !OperationPolicy.Eligible(agent, command, clock.GetUtcNow()))
                throw new OperationRejected("agent-unavailable");
            var attempt = new OperationAttempt(Guid.NewGuid().ToString("N"), principal.Id, command,
                command.AgentId, null, checked(++state.NextFence), null, "queued", []);
            attempt = Event(attempt, "admitted", command.InputDigest);
            state.Attempts.Add(attempt.Id, attempt);
            return attempt;
        });

    public Task<OperationAttempt?> PollAsync(OperationsPrincipal principal, string agentId, string bootId, CancellationToken ct) =>
        store.TransactionAsync(async state =>
        {
            AgentAccess(principal, agentId);
            Expire(state);
            if (!state.Agents.TryGetValue(agentId, out var agent) || agent.Capability.BootId != bootId)
                throw new OperationRejected("agent-not-registered");
            state.Agents[agentId] = agent with { LastSeen = clock.GetUtcNow() };
            // Return the same assignment after a lost response. A restarted agent
            // must use a new boot id and cannot adopt an old execution generation.
            var active = state.Attempts.Values.FirstOrDefault(a => a.AgentId == agentId && a.State is "running" or "cancelling");
            if (active is not null) return active;
            var next = state.Attempts.Values.FirstOrDefault(a => a.AgentId == agentId && a.State == "queued"
                && OperationPolicy.Eligible(state.Agents[agentId], a.Command, clock.GetUtcNow())
                && principal.Scopes.Contains(a.Command.OperationId, StringComparer.Ordinal));
            if (next is null) return null;
            var validUntil = await RequirePermitAsync(next.PrincipalId, next.Command, ct);
            next = Event(next with { BootId = bootId, State = "running", LeaseUntil = LeaseEnd(next.Command, validUntil) }, "dispatched", next.Command.InputDigest);
            state.Attempts[next.Id] = next;
            return next;
        });

    public Task<OperationAttempt> RenewAsync(OperationsPrincipal principal, string id, AttemptLeaseRequest request, CancellationToken ct) =>
        store.TransactionAsync(async state =>
        {
            var attempt = Find(state, id);
            AgentAccess(principal, attempt.AgentId);
            Reject(OperationPolicy.LeaseDenial(attempt, attempt.AgentId, request.BootId, request.Fence, clock.GetUtcNow()));
            if (attempt.State == "cancelling") return attempt;
            var validUntil = await RequirePermitAsync(attempt.PrincipalId, attempt.Command, ct);
            attempt = attempt with { LeaseUntil = new[] { attempt.LeaseUntil!.Value, LeaseEnd(attempt.Command, validUntil) }.Min() };
            state.Attempts[id] = attempt;
            return attempt;
        });

    public Task<OperationAttempt> ReportAsync(OperationsPrincipal principal, string id, AttemptReportRequest request, CancellationToken ct) =>
        store.TransactionAsync(async state =>
        {
            Expire(state);
            var attempt = Find(state, id);
            AgentAccess(principal, attempt.AgentId);
            if (attempt.BootId != request.BootId || attempt.Fence != request.Fence) throw new OperationRejected("stale-attempt");
            if (request.Result is null || request.Result.Output.ValueKind == System.Text.Json.JsonValueKind.Undefined) throw new OperationRejected("invalid-result", 400);
            var digest = OperationsProtocol.Digest(request.Result);
            if (attempt.ResultDigest is not null)
            {
                if (attempt.ResultDigest != digest) throw new OperationRejected("result-replay-conflict");
                return attempt;
            }
            if (attempt.LateResultDigest is not null)
            {
                if (attempt.LateResultDigest != digest) throw new OperationRejected("result-replay-conflict");
                return attempt;
            }
            if (attempt.State == "unreachable" && attempt.Command.OperationId == OperationCatalogue.HostInspect.Id)
            {
                // A prior boot may have finished a read-only inspection before
                // crashing or losing its lease. Keep the outcome unreachable;
                // this receipt only preserves evidence and unblocks its spool.
                Reject(OperationPolicy.ResultDenial(attempt, request.Result, clock.GetUtcNow()));
                attempt = Event(attempt with { LateResult = request.Result, LateResultDigest = digest }, "late-result-recorded", digest);
                state.Attempts[id] = attempt;
                return attempt;
            }
            Reject(OperationPolicy.LeaseDenial(attempt, attempt.AgentId, request.BootId, request.Fence, clock.GetUtcNow()));
            Reject(OperationPolicy.ResultDenial(attempt, request.Result, clock.GetUtcNow()));
            await RequirePermitAsync(attempt.PrincipalId, attempt.Command, ct);
            attempt = Event(attempt with { State = request.Result.Outcome, Result = request.Result, ResultDigest = digest }, "result-recorded", digest);
            state.Attempts[id] = attempt;
            return attempt;
        });

    public Task<OperationAttempt> ReadAsync(OperationsPrincipal principal, string id, bool cancel = false) =>
        store.TransactionAsync(state =>
        {
            Reject(OperationsAccessPolicy.Denial(principal, "service", "operations.read"), 403);
            var attempt = Find(state, id);
            if (attempt.PrincipalId != principal.Id) throw new OperationRejected("operation-owner-mismatch", 403);
            Expire(state);
            attempt = Find(state, id);
            if (cancel)
            {
                Reject(OperationsAccessPolicy.Denial(principal, "service", attempt.Command.OperationId, attempt.AgentId), 403);
                if (attempt.State is "queued" or "running")
                {
                    attempt = Event(attempt with { State = attempt.State == "queued" ? "cancelled" : "cancelling" }, "cancel-requested", attempt.Command.InputDigest);
                    state.Attempts[id] = attempt;
                }
            }
            return Task.FromResult(attempt);
        });

    private void Expire(OperationsState state)
    {
        foreach (var attempt in state.Attempts.Values.ToArray())
        {
            var expired = attempt.Command.Deadline <= clock.GetUtcNow()
                          || (attempt.LeaseUntil is not null && attempt.LeaseUntil <= clock.GetUtcNow());
            if (expired && attempt.State is "queued" or "running" or "cancelling")
                state.Attempts[attempt.Id] = Event(attempt with { State = attempt.State == "queued" ? "timed-out" : "unreachable" }, "authority-expired", attempt.Command.InputDigest);
        }
    }

    private async Task<DateTimeOffset> RequirePermitAsync(string principalId, OperationCommand command, CancellationToken ct)
    {
        var validUntil = await permits.ValidateAsync(principalId, command, ct);
        if (validUntil is null || validUntil <= clock.GetUtcNow()) throw new OperationRejected("permit-unavailable", 503);
        return validUntil.Value;
    }

    private DateTimeOffset LeaseEnd(OperationCommand command, DateTimeOffset permitEnd) =>
        new[] { clock.GetUtcNow().AddSeconds(OperationCatalogue.Find(command.OperationId, command.Version)!.TimeoutSeconds), command.Deadline, permitEnd }.Min();
    private OperationAttempt Event(OperationAttempt attempt, string kind, string digest) => attempt with
    {
        Events = [.. attempt.Events, new OperationEvent(attempt.Events.LongLength + 1, clock.GetUtcNow(), kind, digest)],
    };
    private static OperationAttempt Find(OperationsState state, string id) =>
        state.Attempts.GetValueOrDefault(id) ?? throw new OperationRejected("attempt-not-found", 404);
    private static void AgentAccess(OperationsPrincipal principal, string agentId) =>
        Reject(OperationsAccessPolicy.Denial(principal, "agent", "agents.connect", agentId), 403);
    private static void Reject(string? code, int status = 409)
    {
        if (code is not null) throw new OperationRejected(code, status);
    }
}
