using System.Collections.Concurrent;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>Short-lived device sign-in requests claimed by the selected runner.</summary>
public sealed class RunnerDeviceLoginStore(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Login> _logins = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public RunnerDeviceLoginDto Start(StartRunnerDeviceLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RunnerId)
            || request.RunnerId.Length > 100
            || request.RunnerId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.'))
            || request.Provider is not ("codex" or "claude"))
            throw new ArgumentException("Select a valid execution host and provider.");
        lock (_gate)
        {
            Expire();
            if (_logins.Values.Any(login => login.RunnerId == request.RunnerId
                && login.State is ("queued" or "claimed" or "pending")))
                throw new InvalidOperationException("A device sign-in is already active for this runner.");
            var login = new Login("login_" + Guid.NewGuid().ToString("N"),
                request.RunnerId, request.Provider, clock.GetUtcNow().AddMinutes(15).UtcDateTime);
            _logins[login.Id] = login;
            return login.Dto();
        }
    }

    public RunnerDeviceLoginDto? Get(string id)
    {
        lock (_gate)
        {
            Expire();
            return _logins.TryGetValue(id, out var login) ? login.Dto() : null;
        }
    }

    public RunnerDeviceLoginWork? Claim(string runnerId)
    {
        lock (_gate)
        {
            Expire();
            var login = _logins.Values.FirstOrDefault(item => item.RunnerId == runnerId && item.State == "queued");
            if (login is null) return null;
            login.State = "claimed";
            return new(login.Id, login.Provider);
        }
    }

    public bool Instructions(string runnerId, string id, ReportRunnerDeviceInstructionsRequest request)
    {
        if (!Uri.TryCreate(request.VerificationUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || url.UserInfo.Length > 0 || url.Fragment.Length > 0 || !SafeQuery(url.Query)
            || url.Host is not ("auth.openai.com" or "login.openai.com" or "claude.ai" or "console.anthropic.com")
            || request.UserCode is { Length: > 32 })
            throw new ArgumentException("Device sign-in instructions are invalid.");
        lock (_gate)
        {
            Expire();
            if (!_logins.TryGetValue(id, out var login) || login.RunnerId != runnerId || login.State != "claimed")
                return false;
            login.VerificationUrl = request.VerificationUrl;
            login.UserCode = request.UserCode;
            login.State = "pending";
            return true;
        }
    }

    public bool Result(string runnerId, string id, bool verified)
    {
        lock (_gate)
        {
            Expire();
            if (!_logins.TryGetValue(id, out var login) || login.RunnerId != runnerId
                || login.State is not ("claimed" or "pending")) return false;
            login.State = verified ? "completed" : "failed";
            login.UserCode = null;
            return true;
        }
    }

    private void Expire()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var login in _logins.Values)
            if (login.ExpiresAt <= now && login.State is ("queued" or "claimed" or "pending"))
            {
                login.State = "expired";
                login.UserCode = null;
            }
        foreach (var login in _logins.Values.Where(item => item.ExpiresAt.AddHours(1) <= now))
            _logins.TryRemove(login.Id, out _);
    }

    private static bool SafeQuery(string query)
    {
        if (query.Length > 2048) return false;
        if (query.Length == 0) return true;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "client_id", "redirect_uri", "response_type", "scope", "state",
            "code_challenge", "code_challenge_method",
        };
        return query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .All(pair => allowed.Contains(Uri.UnescapeDataString(pair.Split('=', 2)[0])));
    }

    private sealed class Login(string id, string runnerId, string provider, DateTime expiresAt)
    {
        public string Id { get; } = id;
        public string RunnerId { get; } = runnerId;
        public string Provider { get; } = provider;
        public DateTime ExpiresAt { get; } = expiresAt;
        public string State { get; set; } = "queued";
        public string? VerificationUrl { get; set; }
        public string? UserCode { get; set; }
        public RunnerDeviceLoginDto Dto() => new(Id, RunnerId, Provider, State,
            VerificationUrl, UserCode, ExpiresAt);
    }
}

public static class RunnerDeviceLoginEndpoints
{
    public static void MapRunnerDeviceLoginEndpoints(this WebApplication app)
    {
        var management = app.MapGroup("/api/v1/management/runner-device-logins")
            .RequireTaskServerScope(TaskServerScopes.Management);
        management.MapPost("", (StartRunnerDeviceLoginRequest request, RunnerDeviceLoginStore store) =>
        {
            try { return Results.Created("/api/v1/management/runner-device-logins", store.Start(request)); }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (InvalidOperationException) { return Results.Conflict(); }
        }).RequireStudioRole(StudioUserRoles.Owner);
        management.MapGet("/{id}", (string id, RunnerDeviceLoginStore store)
            => store.Get(id) is { } login ? Results.Ok(login) : Results.NotFound())
            .RequireStudioRole(StudioUserRoles.Owner);

        var runners = app.MapGroup("/api/v1/runners/{runnerId}/device-logins")
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);
        runners.MapPost("/claim", (string runnerId, HttpContext context, RunnerDeviceLoginStore store)
            => Bound(context, runnerId)
                ? store.Claim(runnerId) is { } work ? Results.Ok(work) : Results.NoContent()
                : Results.Forbid());
        runners.MapPost("/{id}/instructions", (string runnerId, string id, HttpContext context,
            ReportRunnerDeviceInstructionsRequest request, RunnerDeviceLoginStore store) =>
        {
            if (!Bound(context, runnerId)) return Results.Forbid();
            try { return store.Instructions(runnerId, id, request) ? Results.NoContent() : Results.NotFound(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        runners.MapPost("/{id}/result", (string runnerId, string id, HttpContext context,
            ReportRunnerDeviceResultRequest request, RunnerDeviceLoginStore store)
            => Bound(context, runnerId)
                ? store.Result(runnerId, id, request.Verified) ? Results.NoContent() : Results.NotFound()
                : Results.Forbid());
    }

    private static bool Bound(HttpContext context, string runnerId)
        => context.TaskServerPrincipal() is { } principal
            && principal.Kind == TaskServerPrincipalKinds.Runner
            && principal.RunnerId == runnerId;
}
