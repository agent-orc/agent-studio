using AgentStudio.Shared;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Security;

public static class HostCertificateEndpoints
{
    public static void MapHostCertificateEndpoints(this WebApplication app)
    {
        app.MapPost("/api/hosts/{id}/certificates", async (string id, HostCertificateRequest request,
            HttpContext context, HostCertificateAuthority authority, AccessSecurityStore store,
            ControlPlaneAuditLog audit, CancellationToken ct) =>
        {
            if (!AdmittedOperator(context)) return Results.Json(new { error = "operator-policy-required" }, statusCode: 403);
            if (request.PublicKey is null) return Results.BadRequest(new { error = "invalid-host-certificate-request" });
            try
            {
                audit.Append(new ControlPlaneAuditRecord(ControlPlaneAuditStreams.Operator,
                    OperatorActorScope.Current!.Actor, "host.certificate.issue", "issuance-requested", id,
                    ParameterDigest: OperationsProtocol.Digest(request.PublicKey)));
                var emergencyWindow = store.ActiveBreakGlassWindow(OperatorActorScope.Current!.SessionId);
                var receipt = await authority.IssueAsync(id, OperatorActorScope.Current.Actor, request.PublicKey, ct,
                    emergencyWindow?.ExpiresAt);
                return Results.Ok(receipt);
            }
            catch (InvalidDataException) { return Results.BadRequest(new { error = "invalid-host-certificate-request" }); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            { return Results.Json(new { error = "host-certificate-issuer-unavailable" }, statusCode: 503); }
        }).RequirePermission(IamPermissions.WorkspaceOperate);

        app.MapPost("/api/hosts/{id}/certificates/{certificateId}/revoke", async (string id, string certificateId,
            HttpContext context, HostCertificateAuthority authority, ControlPlaneAuditLog audit, CancellationToken ct) =>
        {
            if (!AdmittedOperator(context)) return Results.Json(new { error = "operator-policy-required" }, statusCode: 403);
            try
            {
                audit.Append(new ControlPlaneAuditRecord(ControlPlaneAuditStreams.Operator,
                    OperatorActorScope.Current!.Actor, "host.certificate.revoke", "revocation-requested", id,
                    ParameterDigest: OperationsProtocol.Digest(certificateId)));
                await authority.RevokeAsync(id, certificateId, ct);
                return Results.NoContent();
            }
            catch (FileNotFoundException) { return Results.NotFound(new { error = "host-certificate-not-found" }); }
            catch (InvalidDataException) { return Results.BadRequest(new { error = "invalid-host-certificate-request" }); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            { return Results.Json(new { error = "host-certificate-revocation-unavailable" }, statusCode: 503); }
        }).RequirePermission(IamPermissions.WorkspaceOperate);
    }

    private static bool AdmittedOperator(HttpContext context)
        => OperatorActorScope.Current is not null
            && context.Items[AccessSecurityMiddleware.OperatorPolicyItem] is OperatorPolicyDecision { Admitted: true };
}
