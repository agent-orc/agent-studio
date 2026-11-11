using System.Security.Cryptography;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// The single device sign-in service for the first-run checklist and the Access and
/// Execution Hosts actions. Device instructions are volatile; the server that maps the
/// device-login routes (the standalone Task Server, or the networked monolith) persists
/// only the code-free audit through <c>audit</c>.
/// </summary>
public sealed class DeviceLoginSessions
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private static readonly HashSet<string> SafeQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "client_id", "redirect_uri", "response_type", "scope", "state",
        "code_challenge", "code_challenge_method",
    };
    private readonly TimeProvider clock;
    private readonly Func<StudioProviderAuthEventRequest, string, CancellationToken, Task> audit;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Queue<(Session Session, string Result)> _pendingAudits = new();

    public DeviceLoginSessions(TimeProvider clock,
        Func<StudioProviderAuthEventRequest, string, CancellationToken, Task> audit)
    {
        this.clock = clock;
        this.audit = audit;
    }

    /// <summary>
    /// Queues one sign-in per host and provider. The endpoint has already checked that a
    /// non-empty <paramref name="targetRunnerId"/> is an active runner of the host.
    /// </summary>
    public DeviceLoginSessionDto Start(string hostId, string provider, string actor,
        string? targetRunnerId = null)
    {
        if (string.IsNullOrWhiteSpace(hostId) || provider is not ("codex" or "claude"))
            throw new ArgumentException("A host and supported provider are required.");
        lock (_gate)
        {
            ExpireLocked();
            if (_sessions.Values.Any(item => item.HostId == hostId && item.Provider == provider
                && item.State is "queued" or "running"))
                throw new InvalidOperationException("A provider sign-in is already active on this host.");
            var session = new Session(
                "dlogin_" + Guid.NewGuid().ToString("N"),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), hostId, provider,
                actor, clock.GetUtcNow().UtcDateTime.Add(Lifetime),
                string.IsNullOrWhiteSpace(targetRunnerId) ? null : targetRunnerId);
            _sessions.Add(session.Handle, session);
            return View(session, includeCode: false) with { SessionKey = session.SessionKey };
        }
    }

    public DeviceLoginSessionDto? Get(string handle, string sessionKey)
    {
        lock (_gate)
        {
            ExpireLocked();
            if (!_sessions.TryGetValue(handle, out var session)
                || !string.Equals(session.SessionKey, sessionKey, StringComparison.Ordinal)) return null;
            return View(session, includeCode: true);
        }
    }

    public DeviceLoginClaimDto? Claim(string hostId, string runnerId)
    {
        lock (_gate)
        {
            ExpireLocked();
            var session = _sessions.Values.FirstOrDefault(item => item.HostId == hostId && item.State == "queued"
                && (item.TargetRunnerId is null || item.TargetRunnerId == runnerId));
            if (session is null) return null;
            session.State = "running";
            session.RunnerId = runnerId;
            return new(session.Handle, session.Provider, session.ExpiresAt);
        }
    }

    /// <summary>
    /// The state the claiming runner polls while its CLI runs, so a cancel or timeout stops
    /// the process. Null when the session is unknown or claimed by another runner.
    /// </summary>
    public string? RunnerState(string handle, string runnerId)
    {
        lock (_gate)
        {
            ExpireLocked();
            return _sessions.TryGetValue(handle, out var session) && session.RunnerId == runnerId
                ? session.State : null;
        }
    }

    /// <summary>
    /// Returns false when the session is not running for this runner. Throws
    /// <see cref="ArgumentException"/> for a URL outside the provider's sign-in origin,
    /// a URL that could carry a credential, or a malformed code.
    /// </summary>
    public bool Instructions(string handle, string runnerId, DeviceLoginInstructionsRequest request)
    {
        if (!Uri.TryCreate(request.VerificationUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || url.UserInfo.Length > 0 || url.Fragment.Length > 0 || !SafeQuery(url.Query)
            || request.UserCode is { } code && (code.Length is < 4 or > 32
                || code.Any(character => !char.IsAsciiLetterUpper(character)
                    && !char.IsAsciiDigit(character) && character != '-')))
            throw new ArgumentException("Device sign-in instructions are invalid.");
        lock (_gate)
        {
            ExpireLocked();
            if (!_sessions.TryGetValue(handle, out var session) || session.RunnerId != runnerId
                || session.State != "running") return false;
            if (!ProviderOrigin(session.Provider, url.Host)
                || session.Provider == "codex" && request.UserCode is null)
                throw new ArgumentException("Device sign-in instructions are invalid.");
            session.VerificationUrl = request.VerificationUrl;
            session.UserCode = request.UserCode;
            // Headless `claude setup-token` waits for the code its callback page shows (AGT-3243).
            if (session.Provider == "claude") session.CodeState ??= "awaiting";
            return true;
        }
    }

    /// <summary>
    /// Takes the authorization code the Owner pasted after the Claude browser sign-in. Only the
    /// initiating browser session may submit, once per session, after the runner published the
    /// sign-in URL. The code lives only in memory until the claiming runner takes it. Returns
    /// false for an unknown session or a wrong key. Throws <see cref="ArgumentException"/> for
    /// a malformed code and <see cref="InvalidOperationException"/> when no code is awaited.
    /// </summary>
    public bool SubmitCode(string handle, string sessionKey, DeviceLoginCodeRequest request)
    {
        if (!IsAuthorizationCode(request.Code))
            throw new ArgumentException("Paste the complete authorization code shown after the Claude sign-in.");
        lock (_gate)
        {
            ExpireLocked();
            if (!_sessions.TryGetValue(handle, out var session)
                || !string.Equals(session.SessionKey, sessionKey, StringComparison.Ordinal)) return false;
            if (session.State != "running" || session.CodeState != "awaiting")
                throw new InvalidOperationException("This sign-in is not waiting for an authorization code.");
            session.AuthorizationCode = request.Code!.Trim();
            session.CodeState = "submitted";
            return true;
        }
    }

    /// <summary>
    /// Hands a submitted authorization code to the runner that claimed the session, exactly
    /// once, and forgets it. False when the session is not running for this runner;
    /// <paramref name="code"/> stays null until the Owner submits one.
    /// </summary>
    public bool TakeCode(string handle, string runnerId, out string? code)
    {
        code = null;
        lock (_gate)
        {
            ExpireLocked();
            if (!_sessions.TryGetValue(handle, out var session) || session.RunnerId != runnerId
                || session.State != "running") return false;
            if (session.AuthorizationCode is null) return true;
            code = session.AuthorizationCode;
            session.AuthorizationCode = null;
            session.CodeState = "delivered";
            return true;
        }
    }

    /// <summary>
    /// The shape Claude prints on its callback page: <c>code#state</c> in URL-safe characters.
    /// Whitespace and control characters are refused, so a code cannot add a second line or a
    /// key press to the CLI terminal.
    /// </summary>
    public static bool IsAuthorizationCode(string? value)
    {
        var code = value?.Trim();
        return code is { Length: >= 8 and <= 1024 }
            && code.Count(character => character == '#') <= 1
            && code.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or '~' or '#');
    }

    public async Task<bool> CompleteAsync(string handle, string runnerId,
        DeviceLoginCompletionRequest request, CancellationToken ct)
    {
        Session session;
        var changed = false;
        lock (_gate)
        {
            ExpireLocked();
            if (!_sessions.TryGetValue(handle, out session!) || session.RunnerId != runnerId
                || request.Result is not ("completed" or "failed")) return false;
            if (session.State == request.Result && session.Detail == request.Detail) return true;
            if (session.State != "running") return false;
            session.State = request.Result;
            session.Detail = request.Detail;
            ForgetInstructions(session);
            _pendingAudits.Enqueue((session, request.Result));
            changed = true;
        }
        if (changed)
        {
            try { await SweepAsync(ct); }
            catch (Exception ex)
            {
                // The background sweep retries queued audit work. Its failure cannot revoke
                // an already accepted completion or make the runner restore that credential.
                System.Diagnostics.Trace.TraceWarning("Device login audit deferred: {0}", ex.GetType().Name);
            }
        }
        return true;
    }

    public async Task<bool> CancelAsync(string handle, string sessionKey, CancellationToken ct)
    {
        Session session;
        lock (_gate)
        {
            ExpireLocked();
            if (!_sessions.TryGetValue(handle, out session!) || session.SessionKey != sessionKey
                || session.State is not ("queued" or "running")) return false;
            session.State = "failed";
            session.Detail = "The sign-in was cancelled.";
            ForgetInstructions(session);
            _pendingAudits.Enqueue((session, "cancelled"));
        }
        await SweepAsync(ct);
        return true;
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        List<(Session Session, string Result)> pending = [];
        lock (_gate)
        {
            ExpireLocked();
            while (_pendingAudits.TryDequeue(out var item)) pending.Add(item);
        }
        for (var index = 0; index < pending.Count; index++)
        {
            try { await AuditAsync(pending[index].Session, pending[index].Result, ct); }
            catch
            {
                lock (_gate)
                    for (var retry = index; retry < pending.Count; retry++)
                        _pendingAudits.Enqueue(pending[retry]);
                throw;
            }
        }
    }

    private void ExpireLocked()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var session in _sessions.Values)
        {
            if (session.ExpiresAt > now || session.State is not ("queued" or "running")) continue;
            session.State = "failed";
            session.Detail = "The sign-in timed out.";
            ForgetInstructions(session);
            _pendingAudits.Enqueue((session, "timeout"));
        }
        foreach (var handle in _sessions.Where(pair => pair.Value.ExpiresAt.AddMinutes(30) <= now)
            .Select(pair => pair.Key).ToArray()) _sessions.Remove(handle);
    }

    private async Task AuditAsync(Session session, string result, CancellationToken ct)
        => await audit(
            new StudioProviderAuthEventRequest(session.HostId, session.Provider, result),
            session.Actor, ct);

    /// <summary>
    /// The sign-in hosts each CLI prints. Claude Code 2.x authorizes on <c>claude.com</c> (subscription)
    /// and <c>platform.claude.com</c> (Console); earlier releases used <c>claude.ai</c> and
    /// <c>console.anthropic.com</c>. The runner applies the same list before it publishes a URL.
    /// </summary>
    public static bool ProviderOrigin(string provider, string host) => provider switch
    {
        "codex" => host is "auth.openai.com" or "login.openai.com",
        "claude" => host is "claude.com" or "platform.claude.com" or "claude.ai" or "console.anthropic.com",
        _ => false,
    };

    private static void ForgetInstructions(Session session)
    {
        session.UserCode = null;
        session.VerificationUrl = null;
        session.AuthorizationCode = null;
        session.CodeState = null;
    }

    private static bool SafeQuery(string query)
    {
        if (query.Length > 2048) return false;
        // Claude's authorize URL carries the flag `code=true`; any other `code` value could be a grant.
        return query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .All(pair => SafeQueryKeys.Contains(Uri.UnescapeDataString(pair.Split('=', 2)[0]))
                || string.Equals(pair, "code=true", StringComparison.Ordinal));
    }

    private static DeviceLoginSessionDto View(Session session, bool includeCode)
        => new(session.Handle, session.State, session.ExpiresAt,
            includeCode ? session.VerificationUrl : null,
            includeCode ? session.UserCode : null, session.Detail,
            HostId: session.HostId, Provider: session.Provider,
            CodeState: includeCode ? session.CodeState : null);

    private sealed class Session(string handle, string sessionKey, string hostId,
        string provider, string actor, DateTime expiresAt, string? targetRunnerId)
    {
        public string Handle { get; } = handle;
        public string SessionKey { get; } = sessionKey;
        public string HostId { get; } = hostId;
        public string Provider { get; } = provider;
        public string Actor { get; } = actor;
        public DateTime ExpiresAt { get; } = expiresAt;
        public string? TargetRunnerId { get; } = targetRunnerId;
        public string State { get; set; } = "queued";
        public string? RunnerId { get; set; }
        public string? VerificationUrl { get; set; }
        public string? UserCode { get; set; }
        /// <summary>The pasted Claude code until the claiming runner takes it; never audited or viewed.</summary>
        public string? AuthorizationCode { get; set; }
        public string? CodeState { get; set; }
        public string? Detail { get; set; }
    }
}
