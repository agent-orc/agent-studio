using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed record CreateHostJoinTokenRequest(string ServerUrl, string RunnerName, string Role);
public sealed record IssuedHostJoinToken(string JoinToken, string RunnerId, DateTime ExpiresAt);

/// <summary>Encodes a single-use enrolment code, never a management credential.</summary>
public static class HostJoinTokenEncoder
{
    public static string Encode(IssuedEnrolment enrolment, string serverUrl, string releaseVersion,
        DateTime issuedAt)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            serverUrl,
            credential = "",
            releaseVersion,
            issuedAtUtc = issuedAt,
            enrolmentCode = enrolment.EnrolmentCode,
            installationId = enrolment.InstallationId,
            runnerId = enrolment.RunnerId,
        });
        const string prefix = "aosj2";
        var body = Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var checksum = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{prefix}.{body}")))[..16].ToLowerInvariant();
        return $"{prefix}.{body}.{checksum}";
    }
}

public static class HostJoinTokenEndpoints
{
    public static void MapHostJoinTokenEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/management/host-join-tokens", async (
            HttpContext context, CreateHostJoinTokenRequest request, TaskServerStore store,
            TimeProvider clock, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Valid(request)) return Results.BadRequest(new { error = "invalid-host-join-request" });
            var runnerId = request.Role == "review" ? request.RunnerName + "-review" : request.RunnerName;
            try
            {
                var enrolment = await store.CreateEnrolmentAsync(
                    new CreateEnrolmentRequest("runner:" + runnerId,
                        TaskServerPrincipalKinds.Runner, runnerId),
                    TaskServerEndpoints.Actor(context), ct);
                return Results.Json(new IssuedHostJoinToken(
                    HostJoinTokenEncoder.Encode(enrolment, request.ServerUrl,
                        TaskServerBuildIdentity.Current.Release, clock.GetUtcNow().UtcDateTime),
                    runnerId, enrolment.ExpiresAt), statusCode: StatusCodes.Status201Created);
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        }).RequireTaskServerScope(TaskServerScopes.Management)
          .RequireStudioRole(StudioUserRoles.Owner);
    }

    private static bool Valid(CreateHostJoinTokenRequest request)
        => request.Role is "coding" or "review"
            && request.RunnerName is { Length: > 0 and <= 80 }
            && request.RunnerName.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            && Uri.TryCreate(request.ServerUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && uri.AbsolutePath is "" or "/";
}
