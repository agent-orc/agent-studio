using System.Globalization;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Security;

/// <summary>
/// Turns <see cref="SecurityPostureFacts"/> into the ranked security view. Pure:
/// every fact source adds its rows and findings, then <see cref="Rank"/> orders
/// the findings by score. Scores: 90 and above critical, 70 high, 40 medium,
/// 20 low, below that info (<see cref="SecurityPostureSeverities.ForScore"/>).
/// A finding needs action soon when it turns acute within
/// <see cref="ActionWindow"/>, such as a token expiring in 14 days, a host CLI
/// below the required version, or a user without a second factor.
/// </summary>
public static class SecurityPostureProjection
{
    public static readonly TimeSpan ActionWindow = TimeSpan.FromDays(14);
    public static readonly TimeSpan NoticeWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan IdleWindow = TimeSpan.FromDays(90);
    public const int AuditWindowDays = 7;
    public const int MaxAnomalies = 50;

    private sealed record Draft(
        string Id, int Score, string Area, string Title, string Detail, bool ActionSoon,
        DateTime? DueAt = null, string? Subject = null);

    public static SecurityPostureResponse Build(SecurityPostureFacts facts)
    {
        var drafts = new List<Draft>();
        var exposure = Exposure(facts, drafts);
        var credentials = Credentials(facts, drafts);
        var secondFactor = SecondFactor(facts, drafts);
        var versions = Versions(facts, drafts);
        var audit = Audit(facts, drafts);
        var cards = Cards(facts, drafts);
        var findings = Rank(drafts);
        return new SecurityPostureResponse(facts.Now, Summarize(findings), findings,
            exposure, credentials, secondFactor, versions, audit, cards);
    }

    private static List<SecurityPostureFinding> Rank(IEnumerable<Draft> drafts)
        => drafts
            .OrderByDescending(draft => draft.Score)
            .ThenBy(draft => draft.DueAt ?? DateTime.MaxValue)
            .ThenBy(draft => draft.Id, StringComparer.Ordinal)
            .Select((draft, index) => new SecurityPostureFinding(
                draft.Id, index + 1, Math.Clamp(draft.Score, 0, 100), SecurityPostureSeverities.ForScore(draft.Score),
                draft.Area, draft.Title, draft.Detail, draft.ActionSoon, draft.DueAt, draft.Subject))
            .ToList();

    private static SecurityPostureSummary Summarize(IReadOnlyList<SecurityPostureFinding> findings)
    {
        int Count(string severity) => findings.Count(finding => finding.Severity == severity);
        return new SecurityPostureSummary(findings.Count,
            Count(SecurityPostureSeverities.Critical), Count(SecurityPostureSeverities.High),
            Count(SecurityPostureSeverities.Medium), Count(SecurityPostureSeverities.Low),
            Count(SecurityPostureSeverities.Info), findings.Count(finding => finding.ActionSoon));
    }

    // --- Exposure ----------------------------------------------------------

    private static SecurityPostureExposure Exposure(SecurityPostureFacts facts, List<Draft> drafts)
    {
        var networked = facts.Profile == SecurityProfiles.Networked;
        if (networked && !facts.LoginAddressHealthy)
            drafts.Add(new Draft("exposure:login-address", 75, SecurityPostureAreas.Exposure,
                "Sign-in sees an internal client address",
                "A recent sign-in resolved to a proxy or private address, so throttling cannot tell clients apart. Check the trusted proxy addresses and the edge forwarding.",
                true));
        if (networked && facts.TrustedProxyCount == 0)
            drafts.Add(new Draft("exposure:trusted-proxies", 45, SecurityPostureAreas.Exposure,
                "No trusted proxy is configured",
                "Security:TrustedProxyAddresses is empty, so forwarded client addresses are ignored and every request appears to come from the edge.",
                false));
        var external = networked
            ? new SecurityPostureExternalFact[]
            {
                new("access-layer", "Edge access layer", "not-reported",
                    "The identity-aware access layer runs at the edge. The API cannot see it; check-edge-config.sh verifies it on the host."),
                new("origin-protection", "Origin protection", facts.CloudflareEdge ? "configured" : "not-reported",
                    facts.CloudflareEdge
                        ? "Security:PublicCloudflareEdge is on: sign-in refuses requests whose client address is not a forwarded public one."
                        : "Origin-pull authentication is set on the edge container and is not visible to the API."),
                new("firewall", "Host firewall", "not-reported",
                    "The firewall ahead of the container runtime is applied by the host scripts and is not visible to the API."),
            }
            : [];
        return new SecurityPostureExposure(facts.Profile, networked, facts.CloudflareEdge, facts.TrustedProxyCount,
            facts.LoginAddressHealthy, facts.AnonymousRoutes, external);
    }

    // --- Credentials -------------------------------------------------------

    private static SecurityPostureCredentials Credentials(SecurityPostureFacts facts, List<Draft> drafts)
    {
        var tokens = new List<SecurityPostureToken>();
        foreach (var token in facts.ApiTokens)
        {
            var expiresAt = token.ExpiresAt == DateTime.MaxValue ? (DateTime?)null : token.ExpiresAt;
            var row = new SecurityPostureToken(token.Id, token.Name, "api-token", token.Role, token.Projects,
                token.CreatedAt, expiresAt, token.LastUsedAt, TokenState(token.RevokedAt, false, expiresAt, facts.Now));
            tokens.Add(row);
            TokenFindings(row, $"API token {token.Name}", facts.Now, drafts);
        }
        foreach (var account in facts.ServiceAccounts)
        foreach (var token in account.Tokens)
        {
            var row = new SecurityPostureToken(token.Id, account.Name, "service-account", account.Role, account.Projects,
                token.CreatedAt, token.ExpiresAt, token.LastUsedAt,
                TokenState(token.RevokedAt, account.Disabled, token.ExpiresAt, facts.Now));
            tokens.Add(row);
            TokenFindings(row, $"Service account {account.Name} token {token.Name ?? token.Id}", facts.Now, drafts);
        }

        var agents = facts.OperatorAgents.Select(agent =>
        {
            var live = agent.Sessions.Where(session => session.RevokedAt is null && session.ExpiresAt > facts.Now).ToArray();
            return new SecurityPostureOperatorAgent(agent.Id, agent.Name, agent.Disabled,
                agent.Disabled ? 0 : live.Length, agent.Disabled ? null : live.Select(s => (DateTime?)s.ExpiresAt).Min());
        }).ToList();

        var inventory = new List<SecurityPostureInventoryItem>();
        foreach (var item in facts.Inventory)
        {
            var state = item.State;
            if (state == "ok" && item.ExpiresAt is { } expiry && expiry - facts.Now <= ActionWindow)
                state = expiry <= facts.Now ? "missing" : "expiring";
            inventory.Add(new SecurityPostureInventoryItem(item.Id, item.Category, item.Kind, item.Label, item.HostId,
                item.ExpiresAt, item.LastUseAt, state, item.Detail));
            InventoryFinding(item, state, facts.Now, drafts);
        }
        if (facts.InventorySource == SecurityPostureSources.Unavailable)
            drafts.Add(new Draft("credentials:inventory-unavailable", 30, SecurityPostureAreas.Credentials,
                "Host credentials could not be read",
                facts.InventoryProblem ?? "The credential inventory did not answer, so host sign-ins and the GitHub App are not shown.",
                false));
        return new SecurityPostureCredentials(tokens, agents, inventory, facts.InventorySource, facts.InventoryProblem);
    }

    private static string TokenState(DateTime? revokedAt, bool disabled, DateTime? expiresAt, DateTime now)
    {
        if (revokedAt is not null) return "revoked";
        if (disabled) return "disabled";
        if (expiresAt is not { } expiry) return "no-expiry";
        if (expiry <= now) return "expired";
        return expiry - now <= ActionWindow ? "expiring" : "active";
    }

    private static void TokenFindings(SecurityPostureToken token, string subject, DateTime now, List<Draft> drafts)
    {
        if (token.State is "revoked" or "disabled" or "expired") return;
        var id = $"credentials:token:{token.Id}";
        if (token.ExpiresAt is { } expiry)
        {
            if (ExpiryFinding(id, subject, expiry, now, SecurityPostureAreas.Credentials,
                    "Renew or replace it before it stops working.") is { } finding)
                drafts.Add(finding);
        }
        else
        {
            drafts.Add(new Draft(id + ":no-expiry", 50, SecurityPostureAreas.Credentials, $"{subject} never expires",
                "A token without an expiry stays valid until someone revokes it. Replace it with one that expires.", false,
                Subject: token.Holder));
        }
        var lastActivity = token.LastUsedAt ?? token.CreatedAt;
        if (now - lastActivity > IdleWindow)
            drafts.Add(new Draft(id + ":idle", 25, SecurityPostureAreas.Credentials,
                $"{subject} unused for {(int)(now - lastActivity).TotalDays} days",
                token.LastUsedAt is null ? "It was never used. Revoke it if nobody needs it." : "Revoke it if nobody needs it any more.",
                false, Subject: token.Holder));
    }

    /// <summary>
    /// An expiry within <see cref="ActionWindow"/> needs action soon and scores
    /// 70 to 84 by days left (critical 90 within a day); one within
    /// <see cref="NoticeWindow"/> is a medium notice.
    /// </summary>
    private static Draft? ExpiryFinding(string id, string subject, DateTime expiry, DateTime now, string area, string remedy)
    {
        var left = expiry - now;
        if (left <= TimeSpan.Zero)
            return new Draft(id + ":expired", 75, area, $"{subject} has expired", remedy, true, expiry, subject);
        if (left > NoticeWindow) return null;
        var days = (int)Math.Ceiling(left.TotalDays);
        var when = days <= 1 ? "within a day" : $"in {days} days";
        if (left <= ActionWindow)
        {
            var score = left <= TimeSpan.FromDays(1) ? 90 : 70 + (14 - days);
            return new Draft(id + ":expiring", score, area, $"{subject} expires {when}", remedy, true, expiry, subject);
        }
        return new Draft(id + ":expiring", 40, area, $"{subject} expires {when}", remedy, false, expiry, subject);
    }

    private static void InventoryFinding(CredentialInventoryItemDto item, string state, DateTime now, List<Draft> drafts)
    {
        var id = $"credentials:inventory:{item.Id}";
        var providerLogin = item.Category == "provider";
        switch (state)
        {
            case "unexpected":
                drafts.Add(new Draft(id, 85, SecurityPostureAreas.Credentials, $"{item.Label}: unexpected access",
                    item.Detail, true, Subject: item.HostId));
                break;
            case "missing" when item.Required || providerLogin:
                drafts.Add(new Draft(id, providerLogin ? 72 : 75, SecurityPostureAreas.Credentials,
                    providerLogin ? $"{item.Label} is not working" : $"{item.Label} is missing", item.Detail, true,
                    item.ExpiresAt, item.HostId));
                break;
            case "missing":
                drafts.Add(new Draft(id, 30, SecurityPostureAreas.Credentials, $"{item.Label} is missing", item.Detail, false,
                    Subject: item.HostId));
                break;
            case "expiring" when item.ExpiresAt is { } expiry:
                if (ExpiryFinding(id, item.Label, expiry, now, SecurityPostureAreas.Credentials, item.Detail) is { } finding)
                    drafts.Add(finding with { Subject = item.HostId });
                break;
            case "expiring":
                drafts.Add(new Draft(id, 70, SecurityPostureAreas.Credentials, $"{item.Label} expires soon", item.Detail, true,
                    Subject: item.HostId));
                break;
            default:
                if (item.ExpiresAt is { } notice && state == "ok"
                    && ExpiryFinding(id, item.Label, notice, now, SecurityPostureAreas.Credentials, item.Detail) is { } later)
                    drafts.Add(later with { Subject = item.HostId });
                break;
        }
    }

    // --- Second factor -----------------------------------------------------

    private static SecurityPostureSecondFactor SecondFactor(SecurityPostureFacts facts, List<Draft> drafts)
    {
        var signIn = facts.Profile != SecurityProfiles.Local;
        var users = new List<SecurityPostureUser>();
        foreach (var user in facts.Users)
        {
            var totp = !string.IsNullOrEmpty(user.TotpSecret);
            var passkeys = user.Passkeys.Count;
            var required = user.Role is StudioRoles.Owner or StudioRoles.Operator;
            var state = user.Disabled ? "disabled"
                : !totp && passkeys == 0 ? "missing"
                : totp && passkeys == 0 && user.RecoveryCodeHashes.Count == 0 ? "no-recovery-codes"
                : "covered";
            users.Add(new SecurityPostureUser(user.Id, user.Username, user.DisplayName, user.Role, user.Disabled,
                totp, passkeys, user.RecoveryCodeHashes.Count, required, state));
            if (!signIn) continue;
            var name = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
            if (state == "missing")
                drafts.Add(new Draft($"second-factor:{user.Id}",
                    user.Role == StudioRoles.Owner ? 95 : required ? 85 : 60, SecurityPostureAreas.SecondFactor,
                    $"{name} has no second factor",
                    required
                        ? "This role must use TOTP or a passkey; until one is enrolled the account can only open the enrollment step."
                        : "The account signs in with a password alone. Ask the user to enroll TOTP or a passkey.",
                    true, Subject: user.Username));
            else if (state == "no-recovery-codes")
                drafts.Add(new Draft($"second-factor:{user.Id}:recovery", 25, SecurityPostureAreas.SecondFactor,
                    $"{name} has no recovery codes left",
                    "Losing the authenticator would lock the account. Generate new recovery codes or add a passkey.",
                    false, Subject: user.Username));
        }
        var active = users.Where(user => !user.Disabled).ToArray();
        return new SecurityPostureSecondFactor(active.Length, active.Count(user => user.State != "missing"), users);
    }

    // --- Versions ----------------------------------------------------------

    private static SecurityPostureVersions Versions(SecurityPostureFacts facts, List<Draft> drafts)
    {
        if (!facts.Server.Known)
            drafts.Add(new Draft("versions:server-unknown", 30, SecurityPostureAreas.Versions,
                "Server build identity is unknown",
                "The API runs without a build manifest, so hosts cannot be compared with its release.", false));
        if (facts.HostSource == SecurityPostureSources.Unavailable)
            drafts.Add(new Draft("versions:hosts-unavailable", 30, SecurityPostureAreas.Versions,
                "Runner host versions could not be read",
                facts.HostProblem ?? "The host registry did not answer.", false));
        var hosts = new List<SecurityPostureHost>();
        foreach (var host in facts.Hosts.Where(host => host.Status is not ("retired" or "revoked")))
        {
            var verdict = HostReleaseDriftPolicy.Evaluate(host.Release, facts.Stable, facts.Now);
            var clis = (host.InstalledClis ?? []).Select(cli =>
                new SecurityPostureCli(cli.Name, cli.Version, cli.TargetVersion, cli.IsBelowTarget)).ToList();
            var os = host.Capabilities.FirstOrDefault(capability => capability.Category == "platform"
                && !string.IsNullOrWhiteSpace(capability.Version))?.Version;
            hosts.Add(new SecurityPostureHost(host.RunnerId, host.Name, host.HostId, host.Status, host.RunnerVersion,
                host.Release?.Version, verdict.State, os, host.LastSeenAt, clis,
                (host.Containers ?? []).Select(container =>
                    new SecurityPostureImage(container.Name, container.ImageDigest, container.State)).ToList()));
            if (verdict.State == HostReleaseDriftStates.Behind)
                drafts.Add(new Draft($"versions:host:{host.RunnerId}", verdict.AlarmDue ? 70 : 55, SecurityPostureAreas.Versions,
                    $"{host.Name} runs agent host {host.Release?.Version ?? host.RunnerVersion}, behind the server",
                    $"The server runs {facts.Stable?.Version ?? facts.Server.Version}. {verdict.Reason}", verdict.AlarmDue,
                    Subject: host.HostId));
            else if (verdict.State == HostReleaseDriftStates.Unknown)
                drafts.Add(new Draft($"versions:host:{host.RunnerId}", 20, SecurityPostureAreas.Versions,
                    $"{host.Name} reports no comparable release", verdict.Reason, false, Subject: host.HostId));
            foreach (var cli in clis.Where(cli => cli.BelowTarget))
                drafts.Add(new Draft($"versions:cli:{host.RunnerId}:{cli.Name}", 74, SecurityPostureAreas.Versions,
                    $"{cli.Name} {cli.Version} on {host.Name} is below the required {cli.TargetVersion}",
                    "Update the CLI on this host; older CLIs miss provider fixes and model support.", true,
                    Subject: host.HostId));
        }
        return new SecurityPostureVersions(facts.Server, facts.HostSource, facts.HostProblem, hosts);
    }

    // --- Audit -------------------------------------------------------------

    private static SecurityPostureAudit Audit(SecurityPostureFacts facts, List<Draft> drafts)
    {
        var since = facts.Now.AddDays(-AuditWindowDays);
        var failed = facts.Integrity.State == ControlPlaneAuditIntegrityStates.Failed;
        if (failed)
            drafts.Add(new Draft("audit:integrity", 95, SecurityPostureAreas.Audit, "The audit chain failed verification",
                facts.Integrity.Message ?? "The control plane audit no longer matches its hash chain or the off-host sink.", true));
        foreach (var code in facts.OpenAuditAlerts.Distinct(StringComparer.Ordinal)
                     .Where(code => !(failed && code == ControlPlaneAuditAlertCodes.ChainBroken)))
            drafts.Add(new Draft($"audit:alert:{code}", 80, SecurityPostureAreas.Audit, AlertTitle(code),
                "The audit monitor raised this alert and it is still open.", true));
        if (facts.Profile == SecurityProfiles.Networked && !facts.Forwarding.Configured)
            drafts.Add(new Draft("audit:sink-unconfigured", 50, SecurityPostureAreas.Audit,
                "The audit trail is not copied off the host",
                "Without an off-host sink, whoever controls this host can rewrite the audit history. Configure ControlPlaneAudit:Sink.",
                false));

        var anomalies = new List<SecurityPostureAuditEvent>();
        foreach (var entry in facts.RecentAudit.Where(entry => entry.At >= since))
        {
            var kind = AnomalyKind(entry);
            if (kind is not null)
                anomalies.Add(new SecurityPostureAuditEvent(entry.At, kind, entry.Stream, entry.Actor, entry.Operation, entry.Outcome, 1));
        }
        foreach (var entry in facts.RecentSecretAudit.Where(entry => entry.At >= since
                     && entry.Outcome.StartsWith("denied", StringComparison.Ordinal)))
            anomalies.Add(new SecurityPostureAuditEvent(entry.At, "refused-secret-write", ControlPlaneAuditStreams.Secret,
                entry.Actor, $"{entry.Action} {entry.Reference}", entry.Outcome, entry.Repeated ?? 1));
        var grouped = anomalies
            .GroupBy(item => (item.Kind, item.Stream, item.Actor, item.Operation, item.Outcome))
            .Select(group => group.OrderByDescending(item => item.At).First() with { Count = group.Sum(item => item.Count) })
            .OrderByDescending(item => item.At)
            .ToList();
        foreach (var actor in grouped.Where(item => item.Kind is "refused-request" or "refused-secret-write")
                     .GroupBy(item => (item.Kind, item.Actor)))
        {
            var count = actor.Sum(item => item.Count);
            var secret = actor.Key.Kind == "refused-secret-write";
            drafts.Add(new Draft($"audit:{actor.Key.Kind}:{actor.Key.Actor}", count >= 10 ? 65 : 45, SecurityPostureAreas.Audit,
                secret
                    ? $"{count} refused secret writes by {actor.Key.Actor}"
                    : $"{count} refused requests by {actor.Key.Actor}",
                $"In the last {AuditWindowDays} days. Latest: {actor.First().Operation} answered {actor.First().Outcome}.",
                false, Subject: actor.Key.Actor));
        }

        var lastKill = facts.Kills.Select(kill => (DateTime?)kill.At).Max();
        if (lastKill is { } killedAt && killedAt >= since)
            drafts.Add(new Draft("audit:kill-switch", 35, SecurityPostureAreas.Audit, "The operator kill switch was used",
                $"Last use {killedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC. Review the operator activity of that window.",
                false, Subject: "kill-switch"));
        var lastDrill = facts.Drills.OrderByDescending(drill => drill.StartedAt).FirstOrDefault();
        if (lastDrill is { Passed: false })
            drafts.Add(new Draft("audit:kill-drill", 75, SecurityPostureAreas.Audit, "The last kill switch drill failed",
                lastDrill.Failure ?? "The drill did not refuse the test session in time.", true));
        else if (facts.OperatorAgents.Any(agent => !agent.Disabled) && facts.DrillIntervalHours > 0
                 && (lastDrill is null || facts.Now - lastDrill.StartedAt > TimeSpan.FromHours(facts.DrillIntervalHours * 2)))
            drafts.Add(new Draft("audit:kill-drill-stale", 40, SecurityPostureAreas.Audit, "The kill switch has no recent drill",
                lastDrill is null ? "No drill has run yet." : "The scheduled drill is overdue.", false));

        var activeWindows = facts.BreakGlassWindows.Where(window => window.ClosedAt is null && window.ExpiresAt > facts.Now).ToArray();
        foreach (var window in activeWindows)
            drafts.Add(new Draft($"audit:break-glass:{window.Id}", 80, SecurityPostureAreas.Audit, "A break-glass window is open",
                $"Opened {window.OpenedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC for {window.SessionIds.Count} session(s); gates are bypassed until it closes.",
                true, window.ExpiresAt));
        foreach (var window in facts.BreakGlassWindows.Where(window => window.ClosedAt is not null && window.OpenedAt >= since))
            drafts.Add(new Draft($"audit:break-glass:{window.Id}", 35, SecurityPostureAreas.Audit, "A break-glass window was used",
                window.ReviewCardId is null ? "Reconcile its entries with the host journal." : $"Review card {window.ReviewCardId} lists its entries.",
                false));

        return new SecurityPostureAudit(facts.Integrity.State, facts.Integrity.CheckedAt, facts.Forwarding.Configured,
            facts.Forwarding.Pending, facts.Forwarding.Alert, facts.OpenAuditAlerts, lastKill,
            lastDrill?.StartedAt, lastDrill?.Passed, activeWindows.Length, AuditWindowDays,
            grouped.Take(MaxAnomalies).ToList());
    }

    private static readonly HashSet<string> BearerStreams = new(StringComparer.Ordinal)
    {
        ControlPlaneAuditStreams.ApiToken, ControlPlaneAuditStreams.ServiceAccount, ControlPlaneAuditStreams.OperatorAgent,
    };

    /// <summary>Whether an audit entry belongs to the anomaly list: a refused bearer request, a kill switch or break-glass event.</summary>
    internal static bool IsAnomaly(ControlPlaneAuditEntry entry) => AnomalyKind(entry) is not null;

    private static string? AnomalyKind(ControlPlaneAuditEntry entry)
    {
        if (entry.Stream == ControlPlaneAuditStreams.KillSwitch) return "kill-switch";
        if (entry.Stream == ControlPlaneAuditStreams.BreakGlass) return "break-glass";
        if (BearerStreams.Contains(entry.Stream)
            && int.TryParse(entry.Outcome, NumberStyles.Integer, CultureInfo.InvariantCulture, out var status)
            && status is 401 or 403 or 429)
            return "refused-request";
        return null;
    }

    private static string AlertTitle(string code) => code switch
    {
        ControlPlaneAuditAlertCodes.ChainBroken => "The audit chain is broken",
        ControlPlaneAuditAlertCodes.SinkGap => "The audit sink is missing forwarded entries",
        ControlPlaneAuditAlertCodes.SinkMismatch => "The audit sink differs from the local chain",
        ControlPlaneAuditAlertCodes.SinkUnreachable => "The audit sink is unreachable",
        _ => $"Audit alert {code}",
    };

    // --- Security cards ----------------------------------------------------

    private static IReadOnlyList<SecurityPostureCard> Cards(SecurityPostureFacts facts, List<Draft> drafts)
    {
        var cards = facts.SecurityCards
            .Where(card => card.State is not (TaskStates.Completed or TaskStates.Archive))
            .OrderByDescending(card => card.State == TaskStates.Escalated)
            .ThenBy(card => card.CreatedAt)
            .Select(card => new SecurityPostureCard(string.IsNullOrEmpty(card.Key) ? card.Id : card.Key, card.Title, card.ProjectName, card.State, card.CreatedAt))
            .ToList();
        foreach (var card in cards)
            drafts.Add(new Draft($"cards:{card.Key}", card.State == TaskStates.Escalated ? 60 : 35, SecurityPostureAreas.Cards,
                $"Open security card {card.Key}: {card.Title}",
                card.State == TaskStates.Escalated ? "The card is escalated and waits for an operator." : $"In {card.Project}.",
                false, Subject: card.Key));
        return cards;
    }
}
