using System.Net;
using System.Security.Cryptography;

namespace AgentStudio.Security;

/// <summary>
/// Terminal sign-in confirmed with step-up (Dossier operator-elevated-session,
/// ES-3, decision ES-D2 option S2). The terminal starts a sign-in without a
/// credential; Studio answers a single-use user code valid ten minutes and a
/// poll handle. Only an active Owner, after a fresh step-up, confirms it: Studio
/// then creates an <c>oas.*</c> session of the chosen operator agent with the
/// device record, and mints its bearer at the terminal's next poll. Five wrong
/// code entries of one Owner within 15 minutes lock code entry for 15 minutes.
/// Revoking the confirming browser session, a password reset or change, and
/// disabling the confirming Owner end the sessions that browser session or
/// person confirmed. The key thumbprint is stored now; ES-4 enforces it.
/// </summary>
public sealed partial class AccessSecurityStore
{
    public const string OperatorSignInPollHandlePrefix = "osp";
    public static readonly TimeSpan OperatorSignInLifetime = TimeSpan.FromMinutes(10);
    public const int OperatorSignInPollIntervalSeconds = 5;
    public const int OperatorSignInMaxWrongCodes = 5;
    public static readonly TimeSpan OperatorSignInCodeWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan OperatorSignInLockout = TimeSpan.FromMinutes(15);
    private const int MaxDeviceLabelLength = 64;
    private const int MaxRequestedAgentLength = 64;
    private const int MaxSlowDownIntervalSeconds = 60;
    private const int MaxTrackedSignInAddresses = 8192;
    private static readonly TimeSpan OperatorSignInRetention = TimeSpan.FromDays(1);

    /// <summary>Wrong code entries per Owner user id; only active Owners reach the code check.</summary>
    private readonly Dictionary<string, LoginAttempt> _signInCodeFailures = new(StringComparer.Ordinal);
    /// <summary>Starts per client address key within the window.</summary>
    private readonly Dictionary<string, LoginAttempt> _signInStarts = new(StringComparer.Ordinal);
    /// <summary>The last poll and the current interval per sign-in; kept in memory, so a poll writes nothing.</summary>
    private readonly Dictionary<string, (DateTime At, int Interval)> _signInPolls = new(StringComparer.Ordinal);

    private int MaxSignInStartsPerAddress =>
        Math.Clamp(_configuration.GetValue("Security:OperatorSignIn:MaxStartsPerAddress", 10), 1, 1000);
    private int MaxOpenSignIns => Math.Clamp(_configuration.GetValue("Security:OperatorSignIn:MaxOpen", 100), 1, 10_000);

    private void ResetOperatorSignInLimitsLocked()
    {
        _signInCodeFailures.Clear();
        _signInStarts.Clear();
        _signInPolls.Clear();
    }

    /// <summary>
    /// Starts a sign-in for an anonymous terminal. Returns the record, the user
    /// code and the poll handle; the store keeps only their hashes.
    /// </summary>
    public (OperatorSignIn SignIn, string UserCode, string PollHandle) StartOperatorSignIn(
        StartOperatorSignInRequest request, string? address, string throttleKey)
    {
        var key = ValidateDevicePublicKey(request.PublicKey);
        var label = ValidateDeviceLabel(request.DeviceLabel);
        var requestedAgent = ValidateRequestedAgent(request.OperatorAgent);
        var classes = ValidateActionClasses(request.Classes);
        RequireNoOperatorKillRequest();
        lock (_gate)
        {
            var now = Now;
            ThrottleSignInStartLocked(string.IsNullOrWhiteSpace(throttleKey) ? "unknown" : throttleKey, now);
            SweepOperatorSignInsLocked();
            var next = Copy(StateLocked());
            if (next.OperatorSignIns.Count(IsOpenSignIn) >= MaxOpenSignIns)
                throw new SecurityOperationException(429, "signin-busy", "Too many terminal sign-ins are waiting. Try again in a few minutes.");
            string code, codeHash;
            do
            {
                code = OperatorSignInCodes.New();
                codeHash = OperatorSignInCodes.Hash(code);
            }
            while (next.OperatorSignIns.Any(candidate => candidate.UserCodeHash == codeHash));
            var id = Guid.NewGuid().ToString("N");
            var pollHandle = Issued($"{OperatorSignInPollHandlePrefix}.{id}.{PasswordSecretHasher.RandomToken()}");
            var signIn = new OperatorSignIn
            {
                Id = id, UserCodeHash = codeHash, PollHandleHash = PasswordSecretHasher.HashSecret(pollHandle),
                PublicKey = key, KeyThumbprint = key.Thumbprint(), DeviceLabel = label, RequestedAgent = requestedAgent,
                RequestedClasses = classes, RequestedAddress = address, CreatedAt = now, ExpiresAt = now + OperatorSignInLifetime,
            };
            next.OperatorSignIns.Add(signIn);
            SaveLocked(next);
            _signInPolls[id] = (DateTime.MinValue, OperatorSignInPollIntervalSeconds);
            AppendIamAudit(null, "operator-signin.start", "operator-signin", id, new
            {
                signIn.DeviceLabel, signIn.KeyThumbprint, signIn.RequestedAgent, classes, address, signIn.ExpiresAt,
            });
            return (signIn, code, pollHandle);
        }
    }

    /// <summary>
    /// The terminal's poll: pending, slow-down, denied, expired, or, once
    /// after confirmation, the session with its bearer. A poll sooner than the
    /// current interval answers slow-down and raises the interval by five
    /// seconds (RFC 8628).
    /// </summary>
    public PollOperatorSignInResponse PollOperatorSignIn(string? pollHandle)
    {
        if (!TryTokenParts(pollHandle, OperatorSignInPollHandlePrefix, out var id))
            throw new SecurityOperationException(404, "signin-not-found", "No terminal sign-in has this poll handle.");
        lock (_gate)
        {
            var now = Now;
            SweepOperatorSignInsLocked();
            var state = StateLocked();
            var signIn = state.OperatorSignIns.FirstOrDefault(candidate => candidate.Id == id);
            if (signIn is null || !PasswordSecretHasher.VerifySecret(pollHandle!, signIn.PollHandleHash))
                throw new SecurityOperationException(404, "signin-not-found", "No terminal sign-in has this poll handle.");
            var interval = OperatorSignInPollIntervalSeconds;
            if (_signInPolls.TryGetValue(id, out var last))
            {
                interval = last.Interval;
                // One second of grace for the terminal's timer.
                if (IsOpenSignIn(signIn) && now - last.At < TimeSpan.FromSeconds(interval - 1))
                {
                    interval = Math.Min(interval + 5, MaxSlowDownIntervalSeconds);
                    _signInPolls[id] = (now, interval);
                    return new PollOperatorSignInResponse(OperatorSignInPollStatuses.SlowDown, interval,
                        $"Poll at most every {interval} seconds.");
                }
            }
            if (IsOpenSignIn(signIn)) _signInPolls[id] = (now, interval);
            else _signInPolls.Remove(id);
            if (OperatorKillRequested)
                return new PollOperatorSignInResponse(OperatorSignInPollStatuses.Denied, interval,
                    "All operator access was ended. No session is issued.");
            return signIn.State switch
            {
                OperatorSignInStates.Pending => new PollOperatorSignInResponse(OperatorSignInPollStatuses.Pending, interval,
                    "Waiting for the Owner to confirm in Studio."),
                OperatorSignInStates.Denied => new PollOperatorSignInResponse(OperatorSignInPollStatuses.Denied, interval,
                    "The Owner denied this sign-in."),
                OperatorSignInStates.Confirmed => IssueOperatorSignInSessionLocked(signIn, interval, now),
                OperatorSignInStates.Issued => new PollOperatorSignInResponse(OperatorSignInPollStatuses.Expired, interval,
                    "This sign-in was already collected. Start a new one."),
                _ => new PollOperatorSignInResponse(OperatorSignInPollStatuses.Expired, interval,
                    "The code expired before the Owner confirmed it. Start a new sign-in."),
            };
        }
    }

    private PollOperatorSignInResponse IssueOperatorSignInSessionLocked(OperatorSignIn signIn, int interval, DateTime now)
    {
        var next = Copy(StateLocked());
        var agent = next.ServiceAccounts.FirstOrDefault(candidate => candidate.Id == signIn.OperatorAgentId && IsOperatorAgent(candidate));
        var session = agent?.Sessions?.FirstOrDefault(candidate => candidate.Id == signIn.SessionId);
        var index = next.OperatorSignIns.FindIndex(candidate => candidate.Id == signIn.Id);
        if (agent is null || session is null || agent.DisabledAt is not null || session.RevokedAt is not null || session.ExpiresAt <= now)
        {
            next.OperatorSignIns[index] = signIn with { State = OperatorSignInStates.Denied };
            SaveLocked(next);
            _signInPolls.Remove(signIn.Id);
            AppendIamAudit(null, "operator-signin.deny", "operator-signin", signIn.Id,
                new { reason = "session ended before collection", signIn.SessionId });
            return new PollOperatorSignInResponse(OperatorSignInPollStatuses.Denied, interval,
                "The session was ended before this terminal collected it.");
        }
        var secret = Issued($"{OperatorSessionTokenPrefix}.{session.Id}.{PasswordSecretHasher.RandomToken()}");
        var issued = session with { SecretHash = PasswordSecretHasher.HashSecret(secret) };
        next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
        {
            Sessions = agent.Sessions!.Select(candidate => candidate.Id == session.Id ? issued : candidate).ToList(),
        };
        next.OperatorSignIns[index] = signIn with { State = OperatorSignInStates.Issued, IssuedAt = now };
        SaveLocked(next);
        _signInPolls.Remove(signIn.Id);
        AppendIamAudit(null, "operator-signin.issue", "operator-signin", signIn.Id, new
        {
            operatorAgentId = agent.Id, sessionId = session.Id, signIn.KeyThumbprint, session.ExpiresAt,
        });
        var actor = new AgentStudio.Shared.OperatorActor(agent.Id, session.Id, session.CreatedBy);
        return new PollOperatorSignInResponse(OperatorSignInPollStatuses.Session, interval, "Signed in.", secret, agent.Id,
            session.Id, actor.Actor, actor.DelegatedBy, session.ExpiresAt, session.Classes ?? [], signIn.KeyThumbprint);
    }

    /// <summary>What the confirmation page shows for an entered code. A wrong code counts toward the lock.</summary>
    public OperatorSignInLookupResponse LookupOperatorSignIn(string? code, HumanPrincipal owner, string? browserAddress)
    {
        lock (_gate)
        {
            var now = Now;
            SweepOperatorSignInsLocked();
            var state = StateLocked();
            RequireSignInOwnerLocked(state, owner);
            var signIn = FindOpenSignInLocked(state, code, owner.User.Id, now);
            var agents = state.ServiceAccounts
                .Where(account => IsOperatorAgent(account) && account.DisabledAt is null && !IsDrillPrincipal(account))
                .OrderBy(account => account.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var requested = signIn.RequestedAgent is null ? null : agents.FirstOrDefault(account =>
                account.Id == signIn.RequestedAgent || string.Equals(account.Name, signIn.RequestedAgent, StringComparison.OrdinalIgnoreCase));
            return new OperatorSignInLookupResponse(
                signIn.Id, OperatorSignInCodes.Display(OperatorSignInCodes.Normalize(code)!), signIn.State, signIn.DeviceLabel,
                signIn.KeyThumbprint,
                agents.Where(account => account.Devices?.Any(device => device.KeyThumbprint == signIn.KeyThumbprint) == true)
                    .Select(account => account.Id).ToList(),
                signIn.RequestedAgent, requested?.Id,
                signIn.RequestedClasses.Select(ElevatedActionClasses.Find).OfType<ElevatedActionClasses.ActionClass>()
                    .Select(actionClass => new OperatorSignInClassResponse(actionClass.Id, actionClass.Title, actionClass.Mode)).ToList(),
                signIn.RequestedAddress, browserAddress, SameNetworkAddress(signIn.RequestedAddress, browserAddress),
                signIn.CreatedAt, signIn.ExpiresAt, [1, 2, 3, 4, 5, 6, 7, 8], (int)MaxOperatorSessionLifetime.TotalHours,
                agents.Select(account => new OperatorSignInAgentOption(account.Id, account.Name)).ToList());
        }
    }

    /// <summary>
    /// Confirms a pending sign-in: consumes the Owner's fresh step-up and
    /// creates the session of the chosen agent with its device record. The
    /// terminal collects the bearer at its next poll.
    /// </summary>
    public ConfirmOperatorSignInResponse ConfirmOperatorSignIn(ConfirmOperatorSignInRequest request, HumanPrincipal owner, string? browserAddress)
    {
        var lifetime = OperatorSessionLifetime(request.LifetimeHours);
        RequireNoOperatorKillRequest();
        lock (_gate)
        {
            var now = Now;
            SweepOperatorSignInsLocked();
            var next = Copy(StateLocked());
            RequireSignInOwnerLocked(next, owner);
            var signIn = FindOpenSignInLocked(next, request.Code, owner.User.Id, now);
            var classes = ConfirmedClasses(request.Classes, signIn.RequestedClasses);
            if (string.IsNullOrWhiteSpace(request.OperatorAgentId))
                throw new SecurityOperationException(400, "operator-agent-required", "Choose the operator agent this terminal acts as.");
            var agent = ActiveOperatorAgentLocked(next, request.OperatorAgentId);
            if (IsDrillPrincipal(agent))
                throw new SecurityOperationException(404, "operator-agent-not-found", "Operator agent not found or disabled.");
            ConsumeStepUpLocked(next, owner, "Verify a second factor before confirming a terminal sign-in.");

            var devices = agent.Devices ?? [];
            var known = devices.FirstOrDefault(device => device.KeyThumbprint == signIn.KeyThumbprint);
            var device = known is null
                ? new OperatorDevice
                {
                    Id = "dev_" + Guid.NewGuid().ToString("N"), KeyThumbprint = signIn.KeyThumbprint, Label = signIn.DeviceLabel,
                    FirstConfirmedAt = now, LastConfirmedAt = now, LastConfirmedBy = owner.User.Id, LastAddress = signIn.RequestedAddress,
                }
                : known with
                {
                    Label = signIn.DeviceLabel, LastConfirmedAt = now, LastConfirmedBy = owner.User.Id, LastAddress = signIn.RequestedAddress,
                };
            var session = new OperatorAgentSession
            {
                Id = Guid.NewGuid().ToString("N"), Name = signIn.DeviceLabel, SecretHash = string.Empty, CreatedAt = now,
                CreatedBy = owner.User.Id, ExpiresAt = now + lifetime, Classes = classes,
                Device = new OperatorSessionDevice
                {
                    DeviceId = device.Id, Label = signIn.DeviceLabel, KeyThumbprint = signIn.KeyThumbprint,
                    RequestedAddress = signIn.RequestedAddress, ConfirmedAddress = browserAddress,
                    ConfirmingSessionId = owner.Session.Id, SignInId = signIn.Id,
                },
            };
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
            {
                Sessions = [.. agent.Sessions ?? [], session],
                Devices = known is null ? [.. devices, device] : devices.Select(candidate => candidate.Id == known.Id ? device : candidate).ToList(),
            };
            next.OperatorSignIns[next.OperatorSignIns.IndexOf(signIn)] = signIn with
            {
                State = OperatorSignInStates.Confirmed, DecidedAt = now, DecidedBy = owner.User.Id, OperatorAgentId = agent.Id,
                SessionId = session.Id,
            };
            SaveLocked(next);
            AppendIamAudit(owner.User.Id, "operator-signin.confirm", "operator-signin", signIn.Id, new
            {
                operatorAgentId = agent.Id, sessionId = session.Id, signIn.DeviceLabel, signIn.KeyThumbprint, knownDevice = known is not null,
                classes, session.ExpiresAt, signIn.RequestedAddress, confirmedAddress = browserAddress,
                addressMatches = SameNetworkAddress(signIn.RequestedAddress, browserAddress),
            }, owner.Session.Id);
            var actor = new AgentStudio.Shared.OperatorActor(agent.Id, session.Id, session.CreatedBy);
            return new ConfirmOperatorSignInResponse(signIn.Id, agent.Id, session.Id, actor.Actor, session.ExpiresAt, classes);
        }
    }

    /// <summary>Denies a pending sign-in. Denying only takes authority away, so it needs no step-up.</summary>
    public void DenyOperatorSignIn(string? code, HumanPrincipal owner)
    {
        lock (_gate)
        {
            var now = Now;
            SweepOperatorSignInsLocked();
            var next = Copy(StateLocked());
            RequireSignInOwnerLocked(next, owner);
            var signIn = FindOpenSignInLocked(next, code, owner.User.Id, now);
            next.OperatorSignIns[next.OperatorSignIns.IndexOf(signIn)] = signIn with
            {
                State = OperatorSignInStates.Denied, DecidedAt = now, DecidedBy = owner.User.Id,
            };
            SaveLocked(next);
            AppendIamAudit(owner.User.Id, "operator-signin.deny", "operator-signin", signIn.Id,
                new { signIn.DeviceLabel, signIn.KeyThumbprint, signIn.RequestedAddress }, owner.Session.Id);
        }
    }

    /// <summary>
    /// Forgets a device of one operator agent: every live session confirmed for
    /// its key ends at once, and a later sign-in with that key is unknown again.
    /// </summary>
    public void ForgetOperatorDevice(string agentId, string deviceId, string ownerId)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, ownerId);
            var agent = next.ServiceAccounts.FirstOrDefault(candidate => candidate.Id == agentId && IsOperatorAgent(candidate))
                ?? throw new SecurityOperationException(404, "operator-agent-not-found", "Operator agent not found.");
            var device = agent.Devices?.FirstOrDefault(candidate => candidate.Id == deviceId)
                ?? throw new SecurityOperationException(404, "operator-device-not-found", "Device not found.");
            var now = Now;
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(agent)] = agent with
            {
                Devices = agent.Devices!.Where(candidate => candidate.Id != deviceId).ToList(),
            };
            var ended = EndConfirmedOperatorSessionsLocked(next,
                (candidate, session) => candidate.Id == agentId && session.Device!.KeyThumbprint == device.KeyThumbprint, now, ownerId);
            var cancelled = CancelEndedSessionApprovalsLocked(next, ended, now, "operator device forgotten");
            SaveLocked(next);
            AppendIamAudit(ownerId, "operator-device.forget", "operator-agent", agentId, new
            {
                deviceId, device.Label, device.KeyThumbprint, sessionIds = ended.Select(entry => entry.SessionId).ToList(),
            });
            AuditEndedSignInSessions(ownerId, ended, "operator device forgotten", cancelled);
        }
    }

    // --- Ending the sessions a browser session or person confirmed ----------

    /// <summary>
    /// Revokes every live session a terminal sign-in created that
    /// <paramref name="match"/> selects. The caller saves, then audits with
    /// <see cref="AuditEndedSignInSessions"/>.
    /// </summary>
    private static List<(string AgentId, string SessionId)> EndConfirmedOperatorSessionsLocked(
        SecurityState state, Func<ServiceAccount, OperatorAgentSession, bool> match, DateTime now, string? actingUserId)
    {
        var ended = new List<(string AgentId, string SessionId)>();
        for (var index = 0; index < state.ServiceAccounts.Count; index++)
        {
            var agent = state.ServiceAccounts[index];
            if (!IsOperatorAgent(agent) || agent.Sessions is null) continue;
            var hits = agent.Sessions.Where(session => session.Device is not null && session.RevokedAt is null
                && session.ExpiresAt > now && match(agent, session)).Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
            if (hits.Count == 0) continue;
            state.ServiceAccounts[index] = agent with
            {
                Sessions = agent.Sessions.Select(session => hits.Contains(session.Id)
                    ? session with { RevokedAt = now, RevokedBy = actingUserId } : session).ToList(),
            };
            ended.AddRange(hits.Select(sessionId => (agent.Id, sessionId)));
        }
        return ended;
    }

    private static List<OperatorApproval> CancelEndedSessionApprovalsLocked(
        SecurityState state, List<(string AgentId, string SessionId)> ended, DateTime now, string reason)
        => ended.Count == 0 ? [] : CancelOpenApprovalsLocked(state,
            approval => ended.Contains((approval.PrincipalId, approval.SessionId)), now, reason);

    /// <summary>Ends the sessions the browser sessions in <paramref name="browserSessionIds"/> confirmed.</summary>
    private (List<(string AgentId, string SessionId)> Ended, List<OperatorApproval> Cancelled) EndSessionsConfirmedByBrowserLocked(
        SecurityState state, IReadOnlyCollection<string> browserSessionIds, string? actingUserId)
    {
        if (browserSessionIds.Count == 0) return ([], []);
        var now = Now;
        var ended = EndConfirmedOperatorSessionsLocked(state,
            (_, session) => browserSessionIds.Contains(session.Device!.ConfirmingSessionId), now, actingUserId);
        return (ended, CancelEndedSessionApprovalsLocked(state, ended, now, "confirming browser session revoked"));
    }

    /// <summary>Ends the sessions <paramref name="userId"/> confirmed in a terminal sign-in.</summary>
    private (List<(string AgentId, string SessionId)> Ended, List<OperatorApproval> Cancelled) EndSessionsConfirmedByUserLocked(
        SecurityState state, string userId, string? actingUserId, string reason)
    {
        var now = Now;
        var ended = EndConfirmedOperatorSessionsLocked(state, (_, session) => session.CreatedBy == userId, now, actingUserId);
        return (ended, CancelEndedSessionApprovalsLocked(state, ended, now, reason));
    }

    private void AuditEndedSignInSessions(string? actingUserId,
        (List<(string AgentId, string SessionId)> Ended, List<OperatorApproval> Cancelled) result, string reason)
        => AuditEndedSignInSessions(actingUserId, result.Ended, reason, result.Cancelled);

    private void AuditEndedSignInSessions(string? actingUserId, IEnumerable<(string AgentId, string SessionId)> ended, string reason,
        IEnumerable<OperatorApproval> cancelled)
    {
        foreach (var (agentId, sessionId) in ended)
            AppendIamAudit(actingUserId, "operator-session.revoke", "operator-agent", agentId, new { sessionId, reason });
        AuditCancelledApprovals(actingUserId, cancelled);
    }

    // --- Lookup, expiry and limits -------------------------------------------

    private static bool IsOpenSignIn(OperatorSignIn signIn)
        => signIn.State is OperatorSignInStates.Pending or OperatorSignInStates.Confirmed;

    /// <summary>
    /// Marks every open sign-in past its ten minutes expired, ends the session
    /// of a confirmed one the terminal never collected, drops settled records
    /// after a day, and audits each expiry. Saves only when something changed.
    /// </summary>
    private void SweepOperatorSignInsLocked()
    {
        var now = Now;
        var state = StateLocked();
        if (!state.OperatorSignIns.Any(signIn => (IsOpenSignIn(signIn) && signIn.ExpiresAt <= now)
                || (!IsOpenSignIn(signIn) && signIn.ExpiresAt + OperatorSignInRetention <= now)))
            return;
        var next = Copy(state);
        var expired = new List<OperatorSignIn>();
        for (var index = 0; index < next.OperatorSignIns.Count; index++)
        {
            var signIn = next.OperatorSignIns[index];
            if (!IsOpenSignIn(signIn) || signIn.ExpiresAt > now) continue;
            next.OperatorSignIns[index] = signIn with { State = OperatorSignInStates.Expired };
            expired.Add(signIn);
            if (signIn.State == OperatorSignInStates.Confirmed)
                EndConfirmedOperatorSessionsLocked(next,
                    (agent, session) => agent.Id == signIn.OperatorAgentId && session.Id == signIn.SessionId, now, null);
        }
        next.OperatorSignIns.RemoveAll(signIn => !IsOpenSignIn(signIn) && signIn.ExpiresAt + OperatorSignInRetention <= now);
        SaveLocked(next);
        foreach (var signIn in expired)
        {
            _signInPolls.Remove(signIn.Id);
            AppendIamAudit(null, "operator-signin.expire", "operator-signin", signIn.Id, new
            {
                previousState = signIn.State, signIn.SessionId, signIn.DeviceLabel, signIn.KeyThumbprint,
            });
        }
    }

    /// <summary>
    /// The pending sign-in with this code, or a refusal: the code entry is
    /// locked, the code is unknown (counted), expired, already used or denied.
    /// </summary>
    private OperatorSignIn FindOpenSignInLocked(SecurityState state, string? code, string ownerId, DateTime now)
    {
        if (_signInCodeFailures.TryGetValue(ownerId, out var attempt) && attempt.LockedUntil is { } until && until > now)
            throw new SecurityOperationException(429, "signin-locked",
                $"Five wrong codes within 15 minutes. Code entry is locked until {until:HH:mm} UTC.");
        var normalized = OperatorSignInCodes.Normalize(code);
        var hash = normalized is null ? null : OperatorSignInCodes.Hash(normalized);
        var signIn = hash is null ? null : state.OperatorSignIns.FirstOrDefault(candidate => candidate.UserCodeHash == hash);
        if (signIn is null)
        {
            RegisterWrongSignInCodeLocked(ownerId, now);
            throw new SecurityOperationException(404, "signin-code-unknown",
                "No terminal sign-in has this code. Compare it with the code your terminal shows.");
        }
        return signIn.State switch
        {
            OperatorSignInStates.Pending when signIn.ExpiresAt > now => signIn,
            OperatorSignInStates.Pending or OperatorSignInStates.Expired => throw new SecurityOperationException(410,
                "signin-code-expired", "This code expired. Run studio-op login again for a new code."),
            OperatorSignInStates.Denied => throw new SecurityOperationException(409, "signin-code-denied",
                "This sign-in was denied. Run studio-op login again for a new code."),
            _ => throw new SecurityOperationException(409, "signin-code-used", "This code was already used. Each code works once."),
        };
    }

    private void RegisterWrongSignInCodeLocked(string ownerId, DateTime now)
    {
        var prior = _signInCodeFailures.GetValueOrDefault(ownerId);
        var fresh = prior is null || now - prior.WindowStartedAt > OperatorSignInCodeWindow
                    || prior.LockedUntil is { } until && until <= now;
        var failures = fresh ? 1 : prior!.Failures + 1;
        _signInCodeFailures[ownerId] = new LoginAttempt(failures, fresh ? now : prior!.WindowStartedAt,
            failures >= OperatorSignInMaxWrongCodes ? now + OperatorSignInLockout : null);
    }

    private void ThrottleSignInStartLocked(string key, DateTime now)
    {
        if (!_signInStarts.ContainsKey(key) && _signInStarts.Count >= MaxTrackedSignInAddresses)
            foreach (var stale in _signInStarts.Where(entry => now - entry.Value.WindowStartedAt > OperatorSignInCodeWindow)
                         .Select(entry => entry.Key).ToArray())
                _signInStarts.Remove(stale);
        if (!_signInStarts.ContainsKey(key) && _signInStarts.Count >= MaxTrackedSignInAddresses)
            throw new SecurityOperationException(429, "signin-throttled", "Too many terminal sign-ins. Try again later.");
        var prior = _signInStarts.GetValueOrDefault(key);
        var fresh = prior is null || now - prior.WindowStartedAt > OperatorSignInCodeWindow;
        if (!fresh && prior!.Failures >= MaxSignInStartsPerAddress)
            throw new SecurityOperationException(429, "signin-throttled", "Too many terminal sign-ins from this address. Try again later.");
        _signInStarts[key] = new LoginAttempt(fresh ? 1 : prior!.Failures + 1, fresh ? now : prior!.WindowStartedAt, null);
    }

    private static void RequireSignInOwnerLocked(SecurityState state, HumanPrincipal owner)
    {
        if (!ActiveOwnerIds(state.Users, state.RoleBindings, state.Groups).Contains(owner.User.Id))
            throw new SecurityOperationException(403, "owner-required", "Only an active Owner confirms a terminal sign-in.");
    }

    // --- Validation ------------------------------------------------------------

    private static OperatorDevicePublicKey ValidateDevicePublicKey(OperatorDevicePublicKey? key)
    {
        if (key is null)
            throw new SecurityOperationException(400, "device-key-required", "The sign-in needs the device's public key as a JWK.");
        if (key.Kty != "EC" || key.Crv != "P-256")
            throw new SecurityOperationException(400, "device-key-invalid", "The device key must be an EC P-256 public key.");
        var x = DecodeCoordinate(key.X);
        var y = DecodeCoordinate(key.Y);
        try
        {
            using var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
        }
        catch (CryptographicException)
        {
            throw new SecurityOperationException(400, "device-key-invalid", "The device key is not a point on P-256.");
        }
        return new OperatorDevicePublicKey("EC", "P-256", OperatorDevicePublicKey.Base64Url(x), OperatorDevicePublicKey.Base64Url(y));
    }

    private static byte[] DecodeCoordinate(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64)
            throw new SecurityOperationException(400, "device-key-invalid", "The device key must be an EC P-256 public key.");
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(padded); }
        catch (FormatException) { throw new SecurityOperationException(400, "device-key-invalid", "The device key coordinates must be base64url."); }
        if (bytes.Length != 32)
            throw new SecurityOperationException(400, "device-key-invalid", "The device key must be an EC P-256 public key.");
        return bytes;
    }

    private static string ValidateDeviceLabel(string? value)
    {
        var label = (value ?? string.Empty).Trim();
        if (label.Length is < 1 or > MaxDeviceLabelLength || label.Any(char.IsControl))
            throw new SecurityOperationException(400, "device-label-invalid",
                $"The device label must be 1 to {MaxDeviceLabelLength} printable characters.");
        return label;
    }

    private static string? ValidateRequestedAgent(string? value)
    {
        var agent = value?.Trim();
        if (string.IsNullOrEmpty(agent)) return null;
        if (agent.Length > MaxRequestedAgentLength || agent.Any(char.IsControl))
            throw new SecurityOperationException(400, "operator-agent-invalid",
                $"The operator agent is named by its id or a name of at most {MaxRequestedAgentLength} characters.");
        return agent;
    }

    private static List<string> ValidateActionClasses(IReadOnlyList<string>? classes)
    {
        var requested = (classes ?? []).Select(value => value?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();
        var unknown = requested.Where(value => ElevatedActionClasses.Find(value) is null).ToList();
        if (unknown.Count > 0)
            throw new SecurityOperationException(400, "action-class-unknown",
                $"Unknown action class {string.Join(", ", unknown)}. Known: {string.Join(", ", ElevatedActionClasses.All.Select(entry => entry.Id))}.");
        return requested;
    }

    /// <summary>The Owner may keep or remove requested classes, never add one.</summary>
    private static List<string> ConfirmedClasses(IReadOnlyList<string>? chosen, IReadOnlyList<string> requested)
    {
        var classes = (chosen ?? requested).Select(value => value?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();
        var added = classes.Where(value => !requested.Contains(value)).ToList();
        if (added.Count > 0)
            throw new SecurityOperationException(400, "action-class-not-requested",
                $"The terminal did not request {string.Join(", ", added)}. Classes can only be removed at confirmation.");
        return classes;
    }

    /// <summary>The same client address, or the same /64 for IPv6, as the sign-in throttle groups them.</summary>
    internal static bool SameNetworkAddress(string? left, string? right)
        => IPAddress.TryParse(left, out var first) && IPAddress.TryParse(right, out var second)
           && AccessSecurityEndpoints.ClientThrottleKey(first) == AccessSecurityEndpoints.ClientThrottleKey(second);
}
