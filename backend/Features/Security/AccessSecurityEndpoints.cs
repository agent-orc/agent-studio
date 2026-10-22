using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Security;

public static class AccessSecurityEndpoints
{
    private const int MaxLoginPayloadBytes = 4 * 1024;

    public static void MapAccessSecurityEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapGet("/status", (HttpContext context, AccessSecurityStore store, IConfiguration configuration) =>
        {
            var principal = FindHuman(context, store, touch: false);
            return Results.Ok(new AuthStatusResponse(
                SecurityProfiles.ActiveProfile(configuration),
                store.BootstrapRequired,
                principal is not null,
                principal is null ? null : ToResponse(principal.User),
                BootstrapCodeRequired: store.BootstrapRequired && RequiresBootstrapCode(context, configuration)));
        });

        auth.MapPost("/bootstrap", (BootstrapRequest request, HttpContext context, AccessSecurityStore store, IConfiguration configuration, ILoggerFactory loggers) =>
            Execute(() =>
            {
                // The address is the client the edge resolved (Cloudflare or
                // direct), forwarded by the trusted frontend hop (AGT-3230).
                var audit = loggers.CreateLogger("AgentStudio.Security.Bootstrap");
                if (RequiresBootstrapCode(context, configuration) && !MatchesBootstrapCode(request.BootstrapCode, configuration))
                {
                    audit.LogWarning("Owner bootstrap without a valid installer code from client {ClientAddress}", context.Connection.RemoteIpAddress);
                    throw new SecurityOperationException(403, "bootstrap-code-required", "A valid installer owner code is required.");
                }
                var created = store.Bootstrap(request);
                audit.LogInformation("Owner {Username} bootstrapped from client {ClientAddress}", created.User.Username, context.Connection.RemoteIpAddress);
                SetSessionCookies(context, created.SessionToken, created.CsrfToken);
                return Results.Ok(new AuthStatusResponse(SecurityProfiles.ActiveProfile(configuration), false, true, ToResponse(created.User), created.CsrfToken));
            }));

        auth.MapPost("/login", async (HttpContext context, AccessSecurityStore store, IConfiguration configuration) =>
        {
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            if (context.Request.ContentLength > MaxLoginPayloadBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            // Bound the body before JSON binding, including requests without Content-Length.
            var body = new byte[MaxLoginPayloadBytes + 1];
            var length = 0;
            while (length < body.Length)
            {
                var read = await context.Request.Body.ReadAsync(body.AsMemory(length), context.RequestAborted);
                if (read == 0) break;
                length += read;
            }
            if (length > MaxLoginPayloadBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            LoginRequest? request;
            try { request = JsonSerializer.Deserialize<LoginRequest>(body.AsSpan(0, length), JsonSerializerOptions.Web); }
            catch (JsonException) { return Results.BadRequest(); }
            if (request is null) return Results.BadRequest();

            return Execute(() =>
            {
                var key = ClientThrottleKey(context.Connection.RemoteIpAddress);
                var result = store.Login(request.Username ?? string.Empty, request.Password ?? string.Empty, key);
                SetSessionCookies(context, result.SessionToken, result.CsrfToken);
                return Results.Ok(new AuthStatusResponse(SecurityProfiles.ActiveProfile(configuration), false, true, ToResponse(result.User), result.CsrfToken));
            });
        });

        auth.MapPost("/invite/accept", (AcceptInvitationRequest request, HttpContext context, AccessSecurityStore store, IConfiguration configuration) =>
            Execute(() =>
            {
                var accepted = store.AcceptInvitation(request);
                SetSessionCookies(context, accepted.SessionToken, accepted.CsrfToken);
                return Results.Ok(new AuthStatusResponse(SecurityProfiles.ActiveProfile(configuration), false, true,
                    ToResponse(accepted.User), accepted.CsrfToken));
            }));

        // The local profile treats X-Client-Id as attribution only, so it must not
        // become an alternate credential for any auth administration endpoint.
        // A missing human session yields a graceful 401 (never a 500) so the
        // local profile can present the sign-in affordance instead of an error.
        auth.MapGet("/session", (HttpContext context, AccessSecurityStore store, IConfiguration configuration) =>
        {
            var principal = FindHuman(context, store);
            return principal is null
                ? Results.Json(
                    new
                    {
                        error = "authentication-required",
                        message = "Sign in to establish a human Studio session.",
                        loginUrl = "/api/auth/login"
                    },
                    statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(new AuthStatusResponse(
                    SecurityProfiles.ActiveProfile(configuration), false, true, ToResponse(principal.User)));
        });

        auth.MapPost("/logout", (HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                store.Logout(RequireHuman(context, store).Session);
                context.Response.Cookies.Delete(AccessSecurityStore.SessionCookieName, SessionCookieOptions(https: true));
                context.Response.Cookies.Delete("__Host-agentstudio-csrf", CsrfCookieOptions(https: true));
                context.Response.Cookies.Delete(AccessSecurityStore.InsecureSessionCookieName, SessionCookieOptions(https: false));
                context.Response.Cookies.Delete("agentstudio-csrf", CsrfCookieOptions(https: false));
                return Results.NoContent();
            }));

        auth.MapPost("/change-password", (ChangePasswordRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() => Results.Ok(ToResponse(store.ChangePassword(RequireHuman(context, store), request)))));

        auth.MapGet("/users", (HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                return Results.Ok(store.ListUsers().Select(ToResponse));
            }));

        auth.MapPost("/users", (CreateUserRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                var owner = RequireOwner(context, store);
                var result = store.CreateUser(request, owner.User.Id);
                return Results.Ok(new { user = ToResponse(result.User), temporaryPassword = result.TemporaryPassword, mustChangePassword = true });
            }));

        auth.MapPost("/invitations", (InviteUserRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                var owner = RequireOwner(context, store);
                var invitation = store.InviteUser(request, owner.User.Id);
                return Results.Ok(new
                {
                    user = ToResponse(invitation.User),
                    signInLink = "/?invite=" + Uri.EscapeDataString(invitation.Token),
                    expiresAt = invitation.ExpiresAt
                });
            }));

        auth.MapPut("/users/{id}", (string id, UpdateUserRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                var owner = RequireOwner(context, store);
                return Results.Ok(ToResponse(store.UpdateUser(id, request, owner.User.Id)));
            }));

        auth.MapPost("/users/{id}/reset-password", (string id, PasswordResetRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                var reset = store.ResetPassword(id, request);
                return Results.Ok(new PasswordResetResponse(reset.User.Id, reset.TemporaryPassword, true));
            }));

        auth.MapPost("/runner-enrollments", (RunnerEnrollmentRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                var created = store.CreateEnrollment(request);
                return Results.Ok(new OneTimeEnrollmentResponse(created.Code, created.Enrollment.Name, created.Enrollment.Scopes, created.Enrollment.ExpiresAt));
            }));

        auth.MapGet("/runner-enrollments", (HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                return Results.Ok(store.ListEnrollments().Select(x =>
                    new RunnerEnrollmentResponse(x.Id, x.Name, x.Scopes, x.CreatedAt, x.ExpiresAt, x.UsedAt)));
            }));

        auth.MapDelete("/runner-enrollments/{id}", (string id, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                store.RevokeEnrollment(id);
                return Results.NoContent();
            }));

        auth.MapGet("/api-tokens", (HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                return Results.Ok(store.ListApiTokens().Select(ToApiTokenResponse));
            }));

        auth.MapPost("/api-tokens", (CreateApiTokenRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                var owner = RequireOwner(context, store);
                var created = store.CreateApiToken(request, owner.User.Id);
                return Results.Ok(new CreatedApiTokenResponse(ToApiTokenResponse(created.Token), created.Secret));
            }));

        auth.MapDelete("/api-tokens/{id}", (string id, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                var owner = RequireOwner(context, store);
                store.RevokeApiToken(id, owner.User.Id);
                return Results.NoContent();
            }));

        auth.MapPost("/runner-enroll", (RunnerEnrollRequest request, AccessSecurityStore store,
            AgentStudio.Clients.ClientIdentityStore clients) =>
            Execute(() =>
            {
                var enrolled = store.EnrollRunner(request.Code);
                clients.EnsureEnrolledRunner(enrolled.Runner.Id, enrolled.Runner.Name);
                return Results.Ok(new OneTimeSecretResponse(enrolled.Runner.Id, enrolled.Runner.Name, enrolled.Credential.Id,
                    enrolled.Secret, enrolled.Credential.Scopes, enrolled.Credential.ExpiresAt));
            }));

        auth.MapGet("/runners", (HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                return Results.Ok(store.ListRunners().Select(ToRunnerResponse));
            }));

        // Provisioning can prove the authenticated service identity without
        // reopening the legacy client registry. No secret material is echoed.
        auth.MapGet("/runner", (HttpContext context) =>
        {
            if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal runner)
                return Results.Json(new { error = "runner-authentication-required" }, statusCode: StatusCodes.Status401Unauthorized);
            return Results.Ok(new
            {
                id = runner.RunnerId,
                name = runner.RunnerName,
                credentialId = runner.CredentialId,
                scopes = runner.Scopes.Order(StringComparer.Ordinal)
            });
        });

        auth.MapPost("/runners/{id}/credentials", (string id, RunnerRotateRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                var rotated = store.RotateRunner(id, request);
                return Results.Ok(new OneTimeSecretResponse(rotated.Runner.Id, rotated.Runner.Name, rotated.Credential.Id,
                    rotated.Secret, rotated.Credential.Scopes, rotated.Credential.ExpiresAt));
            }));

        auth.MapDelete("/runners/{id}/credentials/{credentialId}", (string id, string credentialId, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                store.RevokeCredential(id, credentialId);
                return Results.NoContent();
            }));

        auth.MapDelete("/runners/{id}", (string id, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                store.RevokeRunner(id);
                return Results.NoContent();
            }));

        app.MapIamEndpoints();
    }

    internal static IResult Execute(Func<IResult> operation)
    {
        try { return operation(); }
        catch (SecurityOperationException ex) { return Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: ex.Status); }
    }

    private static ApiTokenResponse ToApiTokenResponse(StudioApiToken token)
        => new(token.Id, token.Name, token.Role, token.Projects, token.CreatedBy,
            token.CreatedAt, token.ExpiresAt, token.LastUsedAt, token.RevokedAt);

    internal static HumanPrincipal RequireHuman(HttpContext context, AccessSecurityStore store)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal
           ?? store.AuthenticateSession(ReadSessionToken(context))
           ?? throw new SecurityOperationException(401, "authentication-required", "A human session is required.");

    private static HumanPrincipal RequireOwner(HttpContext context, AccessSecurityStore store)
    {
        var principal = RequireHuman(context, store);
        if (principal.User.Role != StudioRoles.Owner)
            throw new SecurityOperationException(403, "owner-required", "Owner role is required.");
        return principal;
    }

    internal static HumanPrincipal? FindHuman(HttpContext context, AccessSecurityStore store, bool touch = true)
    {
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal principal)
            return principal;
        var token = ReadSessionToken(context);
        return store.AuthenticateSession(token, touch);
    }

    private static string? ReadSessionToken(HttpContext context)
    {
        var secureToken = context.Request.Cookies[AccessSecurityStore.SessionCookieName];
        if (SecurityProfiles.IsNetworked(context.RequestServices.GetRequiredService<IConfiguration>()))
            return secureToken;
        return secureToken ?? context.Request.Cookies[AccessSecurityStore.InsecureSessionCookieName];
    }

    internal static string ClientThrottleKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }

    // Only a direct loopback connection may skip the installer code; a
    // forwarded 127.0.0.1 comes from whoever controls the proxy header.
    private static bool RequiresBootstrapCode(HttpContext context, IConfiguration configuration)
        => SecurityProfiles.IsNetworked(configuration) && !ConnectionPeer.IsDirectLoopback(context);

    private static bool MatchesBootstrapCode(string? candidate, IConfiguration configuration)
    {
        var expected = configuration["Security:BootstrapCode"];
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(candidate)) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(candidate)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    internal static AuthUserResponse ToResponse(StudioUser user)
        => new(user.Id, user.Username, user.DisplayName, user.Role, user.Projects, user.Disabled, user.MustChangePassword,
            user.ProductionApprovalProjects);

    private static object ToRunnerResponse(RunnerServiceIdentity runner) => new
    {
        runner.Id, runner.Name, runner.CreatedAt, runner.RevokedAt,
        runner.DrainRequestedAt, runner.RetireRequestedAt, runner.RetiredAt,
        runner.LastSeenAt, runner.LastClaimAt, runner.ActiveSlots, runner.AvailableSlots,
        credentials = runner.Credentials.Select(x => new { x.Id, x.Scopes, x.CreatedAt, x.ExpiresAt, x.LastUsedAt, x.RevokedAt })
    };

    private static void SetSessionCookies(HttpContext context, string token, string csrfToken)
    {
        // Local HTTP needs a fallback cookie name. Networked sessions always
        // use the Secure __Host- name, even if a proxy failed to mark HTTPS.
        var https = context.Request.IsHttps
            || SecurityProfiles.IsNetworked(context.RequestServices.GetRequiredService<IConfiguration>());
        context.Response.Cookies.Append(
            https ? AccessSecurityStore.SessionCookieName : AccessSecurityStore.InsecureSessionCookieName,
            token, SessionCookieOptions(https));
        context.Response.Cookies.Append(
            https ? "__Host-agentstudio-csrf" : "agentstudio-csrf",
            csrfToken, CsrfCookieOptions(https));
        context.Response.Headers.CacheControl = "no-store";
    }

    private static CookieOptions SessionCookieOptions(bool https) => new()
    {
        HttpOnly = true,
        Secure = https,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = "/"
    };

    private static CookieOptions CsrfCookieOptions(bool https) => new()
    {
        HttpOnly = false,
        Secure = https,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = "/"
    };
}
