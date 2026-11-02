namespace AgentStudio.Security;

/// <summary>
/// The Owner's operator activity view (security model SM-1): operator agents
/// with their sessions, the audit entries those sessions produced, the last
/// chain verification, and forwarding to the off-host sink. Under
/// <c>/api/auth</c>, so no service account, API token, or operator session
/// reads or verifies the audit trail. No route writes or changes an entry.
/// </summary>
public static class ControlPlaneAuditEndpoints
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;

    public static void MapControlPlaneAuditEndpoints(this WebApplication app)
    {
        var audit = app.MapGroup("/api/auth/audit");

        audit.MapGet("/operator-activity", (HttpContext context, AccessSecurityStore store, ControlPlaneAuditMonitor monitor,
            string? operatorAgentId, string? sessionId, int? limit) => AccessSecurityEndpoints.Execute(() =>
            {
                IamEndpoints.Require(context, store, IamPermissions.AccessRead);
                var agents = store.ListOperatorAgents()
                    .Where(agent => operatorAgentId is null || agent.Id == operatorAgentId)
                    .Select(store.ToOperatorAgentResponse).ToList();
                var prefix = operatorAgentId is null
                    ? AgentStudio.Shared.TimelineActors.OperatorAgentPrefix
                    : AgentStudio.Shared.TimelineActors.OperatorAgentPrefix + operatorAgentId + "/";
                var entries = store.ControlPlaneAudit.ReadNewest(entry =>
                        entry.Actor.StartsWith(prefix, StringComparison.Ordinal)
                        && (sessionId is null || entry.Session == sessionId),
                    Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit));
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new OperatorActivityResponse(agents, entries.Select(View).ToList(),
                    monitor.Integrity, monitor.Forwarding));
            })).RequirePermission(IamPermissions.AccessRead);

        audit.MapPost("/verify", async (HttpContext context, AccessSecurityStore store, ControlPlaneAuditVerifier verifier) =>
        {
            try { IamEndpoints.Require(context, store, IamPermissions.AccessManage); }
            catch (SecurityOperationException exception)
            {
                return Results.Json(new { error = exception.Code, message = exception.Message }, statusCode: exception.Status);
            }
            return Results.Ok(await verifier.VerifyAsync(context.RequestAborted));
        }).RequirePermission(IamPermissions.AccessManage);
    }

    public static ControlPlaneAuditEntryView View(ControlPlaneAuditEntry entry) => new(
        entry.Sequence, entry.At, entry.Stream, entry.Actor, entry.Session, entry.DelegatedBy, entry.Operation,
        entry.Target, entry.ParameterDigest, entry.Outcome, entry.Hash, entry.PreviousHash);
}
