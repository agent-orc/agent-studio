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
                principal is null ? null : SessionUser(principal.User, store, context),
                BootstrapCodeRequired: store.BootstrapRequired && RequiresBootstrapCode(context, configuration),
                SecondFactorEnrollmentRequired: principal?.Session.SecondFactorEnrollmentOnly == true,
                RecoveryCodesRemaining: principal?.Session.RecoveryCodesRemaining,
                RecoveryEnrollmentAllowed: store.IsRecoveryEnrollmentAllowed(principal?.Session),
                RestoreNotice: principal?.User.Role == StudioRoles.Owner ? RestoreSecurityNotice.Read(store.SecurityFolder) : null));
        });

        auth.MapPost("/bootstrap", (BootstrapRequest request, HttpContext context, AccessSecurityStore store, IConfiguration configuration, ILoggerFactory loggers) =>
            Execute(() =>
            {
                // The address is the client the edge resolved (Cloudflare or
                // direct), forwarded by the trusted frontend hop (AGT-3230).
                var audit = loggers.CreateLogger("AgentStudio.Security.Bootstrap");
                var errors = AccessSecurityStore.BootstrapFieldErrors(request);
                if (RequiresBootstrapCode(context, configuration) && !MatchesBootstrapCode(request.BootstrapCode, configuration))
                {
                    audit.LogWarning("Owner bootstrap without a valid installer code from client {ClientAddress}", context.Connection.RemoteIpAddress);
                    errors["bootstrapCode"] = ["A valid installer owner code is required."];
                }
                if (errors.Count > 0)
                    return Results.Json(new { error = "bootstrap-validation", message = "Check the owner details and try again.", errors },
                        statusCode: StatusCodes.Status400BadRequest);
                var created = store.Bootstrap(request, publicEntry: true,
                    device: SessionDevice(context), address: SessionAddress(context));
                audit.LogInformation("Owner {Username} bootstrapped from client {ClientAddress}", created.User.Username, context.Connection.RemoteIpAddress);
                SetSessionCookies(context, created.SessionToken, created.CsrfToken);
                return Results.Ok(new AuthStatusResponse(SecurityProfiles.ActiveProfile(configuration), false, true, SessionUser(created.User, store, context), created.CsrfToken,
                    SecondFactorEnrollmentRequired: true));
            }));

        auth.MapPost("/login", async (HttpContext context, AccessSecurityStore store, IConfiguration configuration,
            NetworkedLoginAddressHealth addressHealth, ILogger<NetworkedLoginAddressHealth> logger) =>
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
                if (SecurityProfiles.IsNetworked(configuration)
                    && NetworkedLoginAddressHealth.IsInvalid(context.Connection.RemoteIpAddress, configuration))
                {
                    logger.LogError("login-client-address-invalid resolved={Address}", context.Connection.RemoteIpAddress);
                    addressHealth.ReportInvalid();
                    if (SecurityProfiles.PublicCloudflareEdgeEnabled(configuration))
                        throw new SecurityOperationException(503, "login-client-address-invalid", "Login is temporarily unavailable.");
                }
                var key = ClientThrottleKey(context.Connection.RemoteIpAddress);
                var result = store.Login(request.Username ?? string.Empty, request.Password ?? string.Empty, key,
                    request.SecondFactorCode, requireSecondFactor: true,
                    device: SessionDevice(context),
                    address: SessionAddress(context), passkey: request.Passkey,
                    origin: RequestOrigin(context), rpId: context.Request.Host.Host);
                SetSessionCookies(context, result.SessionToken, result.CsrfToken);
                var session = store.AuthenticateSession(result.SessionToken, touch: false)?.Session;
                return Results.Ok(new AuthStatusResponse(SecurityProfiles.ActiveProfile(configuration), false, true, SessionUser(result.User, store, context), result.CsrfToken,
                    SecondFactorEnrollmentRequired: session?.SecondFactorEnrollmentOnly == true,
                    RecoveryCodesRemaining: session?.RecoveryCodesRemaining,
                    RecoveryEnrollmentAllowed: store.IsRecoveryEnrollmentAllowed(session),
                    RestoreNotice: result.User.Role == StudioRoles.Owner ? RestoreSecurityNotice.Read(store.SecurityFolder) : null));
            });
        });

        auth.MapPost("/invite/accept", (AcceptInvitationRequest request, HttpContext context, AccessSecurityStore store, IConfiguration configuration) =>
            Execute(() =>
            {
                var accepted = store.AcceptInvitation(request, publicEntry: true,
                    device: SessionDevice(context), address: SessionAddress(context));
                SetSessionCookies(context, accepted.SessionToken, accepted.CsrfToken);
                return Results.Ok(new AuthStatusResponse(SecurityProfiles.ActiveProfile(configuration), false, true,
                    SessionUser(accepted.User, store, context), accepted.CsrfToken,
                    SecondFactorEnrollmentRequired: store.AuthenticateSession(accepted.SessionToken, touch: false)?.Session.SecondFactorEnrollmentOnly == true));
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
                    SecurityProfiles.ActiveProfile(configuration), false, true, SessionUser(principal.User, store, context),
                    SecondFactorEnrollmentRequired: principal.Session.SecondFactorEnrollmentOnly,
                    RecoveryCodesRemaining: principal.Session.RecoveryCodesRemaining,
                    RecoveryEnrollmentAllowed: store.IsRecoveryEnrollmentAllowed(principal.Session),
                    RestoreNotice: principal.User.Role == StudioRoles.Owner ? RestoreSecurityNotice.Read(store.SecurityFolder) : null));
        });

        auth.MapPost("/restore-notice/acknowledge", (RestoreNoticeAcknowledgeRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                RestoreSecurityNotice.Acknowledge(store.SecurityFolder, request.ArchiveId);
                return Results.NoContent();
            }));

        auth.MapPost("/logout", (HttpContext context, AccessSecurityStore store, AgentStudio.QualityStudio.QualityStudioRevokeOutbox revokes) =>
            Execute(() =>
            {
                var session = RequireHuman(context, store).Session;
                store.Logout(session);
                revokes.EnqueueSid(session.Id);
                context.Response.Cookies.Delete(AccessSecurityStore.SessionCookieName, SessionCookieOptions(https: true));
                context.Response.Cookies.Delete("__Host-agentstudio-csrf", CsrfCookieOptions(https: true));
                context.Response.Cookies.Delete(AccessSecurityStore.InsecureSessionCookieName, SessionCookieOptions(https: false));
                context.Response.Cookies.Delete("agentstudio-csrf", CsrfCookieOptions(https: false));
                return Results.NoContent();
            }));

        auth.MapPost("/totp/start", (HttpContext context, AccessSecurityStore store) =>
            Execute(() => Results.Ok(new { secret = store.BeginTotpEnrollment(RequireHuman(context, store)) })));

        auth.MapPost("/totp/confirm", (ConfirmTotpRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() => Results.Ok(new { recoveryCodes = store.ConfirmTotpEnrollment(RequireHuman(context, store), request.Code) })));

        auth.MapPost("/passkeys/register-options", (HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                var principal = RequireHuman(context, store);
                return Results.Ok(new { challenge = store.BeginPasskeyRegistration(principal),
                    rpId = context.Request.Host.Host, userId = principal.User.Id, username = principal.User.Username });
            }));
        auth.MapPost("/passkeys/register", (PasskeyRegistrationProof proof, HttpContext context, AccessSecurityStore store) =>
            Execute(() => Results.Ok(new { recoveryCodes = store.RegisterPasskey(RequireHuman(context, store), proof,
                RequestOrigin(context), context.Request.Host.Host) })));
        auth.MapPost("/passkeys/assertion-options", (PasskeyAssertionOptionsRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() => Results.Ok(new { challenge = store.BeginPasskeyAssertion(request.Username,
                ClientThrottleKey(context.Connection.RemoteIpAddress)), rpId = context.Request.Host.Host })));
        auth.MapPost("/passkeys/step-up-options", (HttpContext context, AccessSecurityStore store) =>
            Execute(() => Results.Ok(new { challenge = store.BeginPasskeyStepUp(RequireHuman(context, store)),
                rpId = context.Request.Host.Host })));

        auth.MapPost("/step-up", (StepUpRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                var principal = RequireHuman(context, store);
                if (request.Passkey is not null)
                    store.VerifyPasskeyStepUp(principal, request.Passkey, RequestOrigin(context), context.Request.Host.Host);
                else store.VerifyStepUp(principal, request.Code);
                return Results.NoContent();
            }));

        auth.MapGet("/sessions", (HttpContext context, AccessSecurityStore store) =>
            Execute(() => Results.Ok(store.ListSessions(RequireHuman(context, store)))));

        auth.MapDelete("/sessions/{id}", (string id, HttpContext context, AccessSecurityStore store) =>
            Execute(() => { store.RevokeSession(RequireHuman(context, store), id); return Results.NoContent(); }));

        auth.MapPost("/sessions/revoke-others", (HttpContext context, AccessSecurityStore store) =>
            Execute(() => { store.RevokeOtherSessions(RequireHuman(context, store)); return Results.NoContent(); }));

        auth.MapGet("/users/{id}/sessions", (string id, HttpContext context, AccessSecurityStore store) =>
            Execute(() => Results.Ok(store.ListSessions(RequireOwner(context, store), id))));

        auth.MapDelete("/users/{id}/sessions/{sessionId}", (string id, string sessionId, HttpContext context, AccessSecurityStore store) =>
            Execute(() => { store.RevokeSession(RequireOwner(context, store), sessionId, id); return Results.NoContent(); }));

        auth.MapPost("/users/{id}/sessions/revoke-others", (string id, HttpContext context, AccessSecurityStore store) =>
            Execute(() => { store.RevokeOtherSessions(RequireOwner(context, store), id); return Results.NoContent(); }));

        auth.MapPost("/change-password", (ChangePasswordRequest request, HttpContext context, AccessSecurityStore store,
            AgentStudio.QualityStudio.QualityStudioRevokeOutbox revokes) =>
            Execute(() =>
            {
                var human = RequireHuman(context, store);
                var changed = store.ChangePassword(human, request);
                revokes.EnqueueSub(human.User.Id);
                return Results.Ok(ToResponse(changed));
            }));

        auth.MapGet("/users", (HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                return Results.Ok(store.ListUsers().Select(ToResponse));
            }));

        auth.MapGet("/directory", (HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireHuman(context, store);
                return Results.Ok(store.ListUsers().Select(user => new { user.Id, user.DisplayName }));
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

        auth.MapPut("/users/{id}", (string id, UpdateUserRequest request, HttpContext context, AccessSecurityStore store,
            AgentStudio.QualityStudio.QualityStudioRevokeOutbox revokes) =>
            Execute(() =>
            {
                var owner = RequireOwner(context, store);
                var updated = store.UpdateUser(id, request, owner.User.Id);
                revokes.EnqueueSub(id);
                return Results.Ok(ToResponse(updated));
            }));

        auth.MapPost("/users/{id}/reset-password", (string id, PasswordResetRequest request, HttpContext context, AccessSecurityStore store,
            AgentStudio.QualityStudio.QualityStudioRevokeOutbox revokes) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                var reset = store.ResetPassword(id, request);
                revokes.EnqueueSub(id);
                return Results.Ok(new PasswordResetResponse(reset.User.Id, reset.TemporaryPassword, true));
            }));

        auth.MapPost("/runner-enrollments", (RunnerEnrollmentRequest request, HttpContext context, AccessSecurityStore store,
            AgentStudio.Registry.ProjectRegistry projects) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                RequireKnownProjects(request.Projects, projects);
                var created = store.CreateEnrollment(request);
                return Results.Ok(new OneTimeEnrollmentResponse(created.Code, created.Enrollment.Name, created.Enrollment.Scopes,
                    created.Enrollment.ExpiresAt, created.Enrollment.Projects));
            }));

        auth.MapGet("/runner-enrollments", (HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                return Results.Ok(store.ListEnrollments().Select(x =>
                    new RunnerEnrollmentResponse(x.Id, x.Name, x.Scopes, x.CreatedAt, x.ExpiresAt, x.UsedAt, x.Projects)));
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

        auth.MapPut("/runners/{id}/projects", (string id, RunnerProjectsRequest request, HttpContext context, AccessSecurityStore store,
            AgentStudio.Registry.ProjectRegistry projects) =>
            Execute(() =>
            {
                RequireOwner(context, store);
                RequireKnownProjects(request.Projects, projects);
                return Results.Ok(ToRunnerResponse(store.UpdateRunnerProjects(id, request.Projects)));
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
        app.MapControlPlaneAuditEndpoints();
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

    private static string RequestOrigin(HttpContext context)
        => context.Request.Scheme + "://" + context.Request.Host;

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

    private static string SessionDevice(HttpContext context)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(userAgent) ? "Browser" : userAgent[..Math.Min(userAgent.Length, 160)];
    }

    private static string SessionAddress(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "Local connection";

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
            user.ProductionApprovalProjects, user.TotpSecret is not null || user.Passkeys.Count > 0,
            user.FactorChangedAt ?? user.Passkeys.OrderByDescending(passkey => passkey.CreatedAt).FirstOrDefault()?.CreatedAt);

    /// <summary>
    /// The signed-in user's own record, with the projects where it reviews
    /// (AGT-3670), named as the board names them: bindings hold project ids.
    /// </summary>
    private static AuthUserResponse SessionUser(StudioUser user, AccessSecurityStore store, HttpContext context)
    {
        var registry = context.RequestServices.GetService<AgentStudio.Registry.ProjectRegistry>();
        var projects = store.ReviewerProjects(user.Id, (bound, requested) => ProjectAccessAuthorization.SameProject(bound, requested, registry))
            .Select(project => project == ProjectAccessAuthorization.AllProjectsGrant
                ? project : registry?.FindByIdOrDisplayName(project)?.DisplayName ?? project)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return ToResponse(user) with { ReviewerProjects = projects };
    }

    /// <summary>
    /// SM-9 notices: a runner enrolled before project binding keeps all projects,
    /// and a credential issued before the default lifetime keeps no expiry. Both
    /// keep working; the Owner sees why and what to do.
    /// </summary>
    internal const string RunnerProjectsNotice = "Enrolled before project binding: holds all projects until you choose its projects.";
    internal const string RunnerExpiryNotice = "No expiry: issued before the 30-day default. Rotate it to adopt the default.";

    private static object ToRunnerResponse(RunnerServiceIdentity runner) => new
    {
        runner.Id, runner.Name, runner.CreatedAt, runner.RevokedAt,
        runner.DrainRequestedAt, runner.RetireRequestedAt, runner.RetiredAt,
        runner.LastSeenAt, runner.LastClaimAt, runner.ActiveSlots, runner.AvailableSlots,
        projects = runner.Projects ?? [RunnerProjectBinding.AllProjects],
        projectsNotice = runner.Projects is null ? RunnerProjectsNotice : null,
        credentials = runner.Credentials.Select(x => new
        {
            x.Id, x.Scopes, x.CreatedAt, x.ExpiresAt, x.LastUsedAt, x.RevokedAt,
            expiryNotice = x.ExpiresAt is null && x.RevokedAt is null ? RunnerExpiryNotice : null,
        })
    };

    private static void RequireKnownProjects(IReadOnlyList<string>? requested, AgentStudio.Registry.ProjectRegistry projects)
    {
        var unknown = (requested ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name) && name.Trim() != RunnerProjectBinding.AllProjects
                           && projects.FindByIdOrDisplayName(name.Trim()) is null)
            .ToArray();
        if (unknown.Length > 0)
            throw new SecurityOperationException(400, "unknown-project", $"Unknown project: {string.Join(", ", unknown)}.");
    }

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
