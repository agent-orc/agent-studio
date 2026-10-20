using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Security;

public static class AccessSecurityEndpoints
{
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

        auth.MapPost("/bootstrap", (BootstrapRequest request, HttpContext context, AccessSecurityStore store, IConfiguration configuration) =>
            Execute(() =>
            {
                if (RequiresBootstrapCode(context, configuration) && !MatchesBootstrapCode(request.BootstrapCode, configuration))
                    throw new SecurityOperationException(403, "bootstrap-code-required", "A valid installer owner code is required.");
                var created = store.Bootstrap(request);
                SetSessionCookies(context, created.SessionToken, created.CsrfToken);
                return Results.Ok(new AuthStatusResponse(SecurityProfiles.ActiveProfile(configuration), false, true, ToResponse(created.User), created.CsrfToken));
            }));

        auth.MapPost("/login", (LoginRequest request, HttpContext context, AccessSecurityStore store, IConfiguration configuration) =>
            Execute(() =>
            {
                var key = $"{(request.Username ?? string.Empty).Trim().ToLowerInvariant()}|{context.Connection.RemoteIpAddress}";
                var result = store.Login(request.Username ?? string.Empty, request.Password ?? string.Empty, key);
                SetSessionCookies(context, result.SessionToken, result.CsrfToken);
                return Results.Ok(new AuthStatusResponse(SecurityProfiles.ActiveProfile(configuration), false, true, ToResponse(result.User), result.CsrfToken));
            }));

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
                RequireOwner(context, store);
                var result = store.CreateUser(request);
                return Results.Ok(new { user = ToResponse(result.User), temporaryPassword = result.TemporaryPassword, mustChangePassword = true });
            }));

        auth.MapPost("/invitations", (InviteUserRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                var invitation = store.InviteUser(request);
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
                RequireOwner(context, store);
                store.RevokeApiToken(id);
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
    }

    private static IResult Execute(Func<IResult> operation)
    {
        try { return operation(); }
        catch (SecurityOperationException ex) { return Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: ex.Status); }
    }

    private static ApiTokenResponse ToApiTokenResponse(StudioApiToken token)
        => new(token.Id, token.Name, token.Role, token.Projects, token.CreatedBy,
            token.CreatedAt, token.ExpiresAt, token.LastUsedAt, token.RevokedAt);

    private static HumanPrincipal RequireHuman(HttpContext context, AccessSecurityStore store)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal
           ?? store.AuthenticateSession(
               context.Request.Cookies[AccessSecurityStore.SessionCookieName]
               ?? context.Request.Cookies[AccessSecurityStore.InsecureSessionCookieName])
           ?? throw new SecurityOperationException(401, "authentication-required", "A human session is required.");

    private static HumanPrincipal RequireOwner(HttpContext context, AccessSecurityStore store)
    {
        var principal = RequireHuman(context, store);
        if (principal.User.Role != StudioRoles.Owner)
            throw new SecurityOperationException(403, "owner-required", "Owner role is required.");
        return principal;
    }

    private static HumanPrincipal? FindHuman(HttpContext context, AccessSecurityStore store, bool touch = true)
    {
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal principal)
            return principal;
        var token = context.Request.Cookies[AccessSecurityStore.SessionCookieName]
                    ?? context.Request.Cookies[AccessSecurityStore.InsecureSessionCookieName];
        return store.AuthenticateSession(token, touch);
    }

    private static bool RequiresBootstrapCode(HttpContext context, IConfiguration configuration)
        => SecurityProfiles.IsNetworked(configuration)
           && (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address));

    private static bool MatchesBootstrapCode(string? candidate, IConfiguration configuration)
    {
        var expected = configuration["Security:BootstrapCode"];
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(candidate)) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(candidate)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    private static AuthUserResponse ToResponse(StudioUser user)
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
        // __Host--Cookies erfordern Secure+HTTPS; ueber HTTP (lokaler Dev-Betrieb
        // via ng-serve-Proxy) wuerde der Browser sie stumm verwerfen und der Login
        // bliebe wirkungslos. Namen und Secure-Flag folgen daher dem Schema.
        var https = context.Request.IsHttps;
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
