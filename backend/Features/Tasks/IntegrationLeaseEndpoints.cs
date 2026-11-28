

namespace AgentStudio.Tasks;

/// <summary>
/// Task Server integration-lease API. Remote runners use this before mutating a
/// project's integration branch; the fencing token returned by acquire must
/// accompany heartbeat/release and future integration actions.
/// Acquire polls preserve FIFO position and the original enqueue time. Waiters
/// expire after ten minutes without an acquire poll; a later poll rejoins at the
/// tail. GET reports Queued when the lane is free but has live waiters, Held
/// when a runner owns it, and Free when neither a holder nor waiters remain.
/// </summary>
public static class IntegrationLeaseEndpoints
{
    public static void MapIntegrationLeaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/runner/integration-lease");

        group.MapPost("/acquire", (IntegrationLeaseAcquireRequest req, IntegrationLeaseService leases) =>
            Results.Ok(leases.TryAcquire(req)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);

        group.MapPost("/heartbeat", (IntegrationLeaseHeartbeatRequest req, IntegrationLeaseService leases) =>
            Results.Ok(leases.Renew(req)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        group.MapPost("/release", (IntegrationLeaseReleaseRequest req, IntegrationLeaseService leases) =>
            Results.Ok(leases.Release(req)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        group.MapGet("/{projectName}/{integrationBranch}", (
            string projectName,
            string integrationBranch,
            IntegrationLeaseService leases) => Results.Ok(leases.Peek(projectName, integrationBranch)));
    }
}
