using System.Collections.Concurrent;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>One-time Git credential handoff and host-owned verification request.</summary>
public sealed class RunnerGitSetupStore(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Setup> _setups = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public RunnerGitSetupDto Start(StartRunnerGitSetupRequest request)
    {
        if (request.RunnerId is not { Length: > 0 and <= 100 }
            || request.RunnerId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.'))
            || request.Method is not ("token" or "deploy-key")
            || !ValidGitHubOrigin(request.RepositoryUrl)
            || request.Method == "token" && !request.RepositoryUrl.StartsWith("https://github.com/", StringComparison.Ordinal)
            || request.Method == "token" && (request.Token is not { Length: >= 20 and <= 512 }
                || request.Token.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-')))
            || request.Method == "deploy-key" && request.Token is not null)
            throw new ArgumentException("Select a GitHub repository, runner, and valid access method.");
        lock (_gate)
        {
            Expire();
            var setup = new Setup("git_" + Guid.NewGuid().ToString("N"), request,
                clock.GetUtcNow().AddHours(1).UtcDateTime);
            _setups[setup.Id] = setup;
            return setup.Dto();
        }
    }

    public RunnerGitSetupDto? Get(string id)
    {
        lock (_gate)
        {
            Expire();
            return _setups.TryGetValue(id, out var setup) ? setup.Dto() : null;
        }
    }

    public RunnerGitSetupWork? Claim(string runnerId)
    {
        lock (_gate)
        {
            Expire();
            var setup = _setups.Values.FirstOrDefault(item => item.RunnerId == runnerId
                && item.State is "queued" or "verify-queued");
            if (setup is null) return null;
            var action = setup.State == "queued" ? "configure" : "verify";
            setup.State = action == "configure" ? "configuring" : "verifying";
            var token = setup.Token;
            setup.Token = null;
            return new(setup.Id, setup.RepositoryUrl, setup.Method, action, token);
        }
    }

    public bool Key(string runnerId, string id, string publicKey)
    {
        if (publicKey.Length is < 50 or > 1000 || !publicKey.StartsWith("ssh-ed25519 ", StringComparison.Ordinal)
            || publicKey.Contains('\n') || publicKey.Contains('\r'))
            throw new ArgumentException("The generated public key is invalid.");
        lock (_gate)
        {
            Expire();
            if (!_setups.TryGetValue(id, out var setup) || setup.RunnerId != runnerId
                || setup.Method != "deploy-key" || setup.State != "configuring") return false;
            setup.PublicKey = publicKey;
            setup.State = "awaiting-github";
            return true;
        }
    }

    public bool Configured(string runnerId, string id, bool success)
    {
        lock (_gate)
        {
            Expire();
            if (!_setups.TryGetValue(id, out var setup) || setup.RunnerId != runnerId
                || setup.State != "configuring"
                || success && setup.Method != "token") return false;
            setup.State = success ? "awaiting-verify" : "failed";
            return true;
        }
    }

    public bool Verify(string id)
    {
        lock (_gate)
        {
            Expire();
            if (!_setups.TryGetValue(id, out var setup)
                || setup.State is not ("awaiting-github" or "awaiting-verify" or "denied")) return false;
            setup.State = "verify-queued";
            return true;
        }
    }

    public bool Proof(string runnerId, string id, ReportRunnerGitProofRequest proof)
    {
        lock (_gate)
        {
            Expire();
            if (!_setups.TryGetValue(id, out var setup) || setup.RunnerId != runnerId
                || setup.State != "verifying") return false;
            setup.FetchVerified = proof.FetchVerified;
            setup.PushVerified = proof.PushVerified;
            setup.State = proof.FetchVerified && proof.PushVerified ? "verified" : "denied";
            return true;
        }
    }

    public static bool ValidGitHubOrigin(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)) return false;
        var path = value.StartsWith("git@github.com:", StringComparison.Ordinal)
            ? value["git@github.com:".Length..]
            : Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" && uri.IsDefaultPort
                && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
                ? uri.AbsolutePath.TrimStart('/') : "";
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        var parts = path.Split('/');
        return parts.Length == 2 && parts.All(part => part.Length is > 0 and <= 100
            && part.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'));
    }

    private void Expire()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var setup in _setups.Values)
            if (setup.ExpiresAt <= now && setup.State is not ("verified" or "failed" or "expired"))
            {
                setup.Token = null;
                setup.State = "expired";
            }
        foreach (var setup in _setups.Values.Where(item => item.ExpiresAt.AddHours(1) <= now))
            _setups.TryRemove(setup.Id, out _);
    }

    private sealed class Setup(string id, StartRunnerGitSetupRequest request, DateTime expiresAt)
    {
        public string Id { get; } = id;
        public DateTime ExpiresAt { get; } = expiresAt;
        public string RunnerId { get; } = request.RunnerId;
        public string RepositoryUrl { get; } = request.RepositoryUrl;
        public string Method { get; } = request.Method;
        public string? Token { get; set; } = request.Token;
        public string State { get; set; } = "queued";
        public string? PublicKey { get; set; }
        public bool FetchVerified { get; set; }
        public bool PushVerified { get; set; }
        public RunnerGitSetupDto Dto() => new(Id, RunnerId, RepositoryUrl, Method, State,
            PublicKey, FetchVerified, PushVerified);
    }
}

public static class RunnerGitSetupEndpoints
{
    public static void MapRunnerGitSetupEndpoints(this WebApplication app)
    {
        var management = app.MapGroup("/api/v1/management/runner-git-setups")
            .RequireTaskServerScope(TaskServerScopes.Management);
        management.MapPost("", (HttpContext context, StartRunnerGitSetupRequest request,
            RunnerGitSetupStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Json(store.Start(request), statusCode: StatusCodes.Status201Created); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).RequireStudioRole(StudioUserRoles.Owner);
        management.MapGet("/{id}", (string id, RunnerGitSetupStore store)
            => store.Get(id) is { } setup ? Results.Ok(setup) : Results.NotFound())
            .RequireStudioRole(StudioUserRoles.Owner);
        management.MapPost("/{id}/verify", (string id, RunnerGitSetupStore store)
            => store.Verify(id) ? Results.Accepted() : Results.Conflict())
            .RequireStudioRole(StudioUserRoles.Owner);

        var runners = app.MapGroup("/api/v1/runners/{runnerId}/git-setups")
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);
        runners.MapPost("/claim", (string runnerId, HttpContext context, RunnerGitSetupStore store)
            => Bound(context, runnerId)
                ? store.Claim(runnerId) is { } work ? Results.Ok(work) : Results.NoContent()
                : Results.Forbid());
        runners.MapPost("/{id}/key", (string runnerId, string id, HttpContext context,
            ReportRunnerGitKeyRequest request, RunnerGitSetupStore store) =>
        {
            if (!Bound(context, runnerId)) return Results.Forbid();
            try { return store.Key(runnerId, id, request.PublicKey) ? Results.NoContent() : Results.NotFound(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        runners.MapPost("/{id}/configured", (string runnerId, string id, HttpContext context,
            ReportRunnerGitProofRequest request, RunnerGitSetupStore store)
            => Bound(context, runnerId)
                ? store.Configured(runnerId, id, request.FetchVerified) ? Results.NoContent() : Results.NotFound()
                : Results.Forbid());
        runners.MapPost("/{id}/proof", (string runnerId, string id, HttpContext context,
            ReportRunnerGitProofRequest request, RunnerGitSetupStore store)
            => Bound(context, runnerId)
                ? store.Proof(runnerId, id, request) ? Results.NoContent() : Results.NotFound()
                : Results.Forbid());
    }

    private static bool Bound(HttpContext context, string runnerId)
        => context.TaskServerPrincipal() is { } principal
            && principal.Kind == TaskServerPrincipalKinds.Runner
            && principal.RunnerId == runnerId;
}
