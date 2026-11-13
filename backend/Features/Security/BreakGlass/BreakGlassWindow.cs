using System.Text.Json;
using System.Text;

namespace AgentStudio.Security;

public sealed record BreakGlassWindow
{
    public required string Id { get; init; }
    public required string OwnerId { get; init; }
    public required string Project { get; init; }
    public required List<string> SessionIds { get; init; }
    public required DateTime OpenedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public DateTime? AlertedAt { get; init; }
    public string? ReviewCardId { get; init; }
}

public sealed record OpenBreakGlassRequest(string? Credential, List<string>? SessionIds, int LifetimeHours, string? Project);

/// <summary>The Owner chooses the exact operator sessions. Time and revocation are checked on every request.</summary>
public sealed partial class AccessSecurityStore
{
    public static readonly TimeSpan MaxBreakGlassLifetime = TimeSpan.FromHours(2);
    public const string BreakGlassJournalFileName = "break-glass-journal.jsonl";

    public string IssueBreakGlassCredential(HumanPrincipal owner)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, owner.User.Id);
            ConsumeStepUpLocked(next, owner, "Verify a second factor before issuing the offline credential.");
            var credential = "bg." + PasswordSecretHasher.RandomToken();
            next.BreakGlassCredentialHash = PasswordSecretHasher.HashSecret(credential);
            SaveLocked(next);
            AppendIamAudit(owner.User.Id, "break-glass.credential.issue", "break-glass", null, null);
            return credential;
        }
    }

    public IReadOnlyList<BreakGlassWindow> ListBreakGlassWindows()
    {
        lock (_gate) return StateLocked().BreakGlassWindows.OrderByDescending(window => window.OpenedAt).ToList();
    }

    public void RequireBreakGlassOwner(string ownerId)
    {
        lock (_gate) RequireActiveOwnerLocked(StateLocked(), ownerId);
    }

    public BreakGlassWindow OpenBreakGlassWindow(HumanPrincipal owner, string? credential,
        IReadOnlyList<string>? sessionIds, int lifetimeHours, string? project)
        => OpenBreakGlassWindowCore(owner.User.Id, owner, null, credential, sessionIds, lifetimeHours, project);

    /// <summary>The host command uses a TOTP code while the Studio API and browser session are unavailable.</summary>
    public BreakGlassWindow OpenBreakGlassWindowOffline(string username, string? secondFactorCode, string? credential,
        IReadOnlyList<string>? sessionIds, int lifetimeHours, string? project)
    {
        string ownerId;
        lock (_gate)
            ownerId = StateLocked().Users.FirstOrDefault(user => user.Username == username && !user.Disabled)?.Id
                ?? throw new SecurityOperationException(403, "owner-required", "An active Owner is required.");
        return OpenBreakGlassWindowCore(ownerId, null, secondFactorCode, credential, sessionIds, lifetimeHours, project);
    }

    private BreakGlassWindow OpenBreakGlassWindowCore(string ownerId, HumanPrincipal? owner, string? offlineFactor,
        string? credential, IReadOnlyList<string>? sessionIds, int lifetimeHours, string? project)
    {
        if (lifetimeHours is < 1 or > 2)
            throw new SecurityOperationException(400, "break-glass-duration-invalid", "The window lasts one or two hours.");
        if (string.IsNullOrWhiteSpace(project) || project.Length > 64 || project.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new SecurityOperationException(400, "break-glass-project-invalid", "Select a review project.");
        var sessions = sessionIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (sessions.Count == 0 || sessions.Count > 20 || sessions.Any(id => id.Length > 64))
            throw new SecurityOperationException(400, "break-glass-sessions-invalid", "Name one to twenty live operator sessions.");
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, ownerId);
            if (next.BreakGlassCredentialHash is not { } hash || credential is null
                || !PasswordSecretHasher.VerifySecret(credential, hash))
                throw new SecurityOperationException(403, "break-glass-credential-invalid", "The sealed credential is required.");
            var now = Now;
            var active = next.ServiceAccounts.Where(agent => IsOperatorAgent(agent) && agent.DisabledAt is null)
                .SelectMany(agent => agent.Sessions ?? []).Where(session => session.RevokedAt is null && session.ExpiresAt > now)
                .Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
            if (sessions.Any(id => !active.Contains(id)))
                throw new SecurityOperationException(400, "break-glass-session-ended", "Every named operator session must be live.");
            if (owner is not null)
                ConsumeStepUpLocked(next, owner, "Verify a second factor before opening a break-glass window.");
            else
            {
                var user = next.Users.First(item => item.Id == ownerId);
                if (OpenTotp(user) is not { } seed || string.IsNullOrWhiteSpace(offlineFactor)
                    || SecondFactorCodes.MatchTotpStep(seed, offlineFactor, now) is not { } acceptedStep
                    || (user.LastAcceptedTotpStep is { } previousStep && acceptedStep <= previousStep))
                    throw new SecurityOperationException(403, "break-glass-second-factor-invalid", "A current authenticator code is required.");
                next.Users[next.Users.IndexOf(user)] = user with { LastAcceptedTotpStep = acceptedStep };
            }
            var window = new BreakGlassWindow
            {
                Id = "bgw_" + Guid.NewGuid().ToString("N"), OwnerId = ownerId, Project = project,
                SessionIds = sessions, OpenedAt = now, ExpiresAt = now + TimeSpan.FromHours(lifetimeHours),
            };
            // Each sealed credential opens exactly one window. A new seal needs a fresh Owner step-up.
            next.BreakGlassCredentialHash = null;
            next.BreakGlassWindows.Add(window);
            SaveLocked(next);
            AppendBreakGlassAudit(window, "break-glass.open", "opened", $"human:{ownerId}", new { window.SessionIds, window.ExpiresAt });
            return window;
        }
    }

    public BreakGlassWindow? ActiveBreakGlassWindow(string sessionId)
    {
        lock (_gate)
        {
            var now = Now;
            return StateLocked().BreakGlassWindows.LastOrDefault(window => window.ClosedAt is null
                && window.OpenedAt <= now && window.ExpiresAt > now && window.SessionIds.Contains(sessionId, StringComparer.Ordinal));
        }
    }

    public BreakGlassWindow CloseBreakGlassWindow(string id, string actor)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var window = next.BreakGlassWindows.FirstOrDefault(item => item.Id == id)
                ?? throw new SecurityOperationException(404, "break-glass-window-not-found", "Window not found.");
            if (window.ClosedAt is not null) return window;
            var closed = window with { ClosedAt = Now > window.ExpiresAt ? window.ExpiresAt : Now };
            next.BreakGlassWindows[next.BreakGlassWindows.IndexOf(window)] = closed;
            SaveLocked(next);
            AppendBreakGlassAudit(closed, "break-glass.close", "closed", actor, new { closed.ClosedAt });
            return closed;
        }
    }

    public void SetBreakGlassReviewCard(string windowId, string cardId)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var window = next.BreakGlassWindows.First(item => item.Id == windowId);
            if (window.ReviewCardId is not null) return;
            next.BreakGlassWindows[next.BreakGlassWindows.IndexOf(window)] = window with { ReviewCardId = cardId };
            SaveLocked(next);
        }
    }

    public void MarkBreakGlassAlerted(string windowId)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var window = next.BreakGlassWindows.First(item => item.Id == windowId);
            if (window.AlertedAt is not null) return;
            next.BreakGlassWindows[next.BreakGlassWindows.IndexOf(window)] = window with { AlertedAt = Now };
            SaveLocked(next);
        }
    }

    public IReadOnlyList<ControlPlaneAuditEntry> BreakGlassEntries(string windowId)
        => ControlPlaneAudit.Read(1, int.MaxValue).Where(entry => entry.Stream == ControlPlaneAuditStreams.BreakGlass
            && entry.Details is { } details && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("windowId", out var id) && id.GetString() == windowId).ToList();

    public void AppendBreakGlassRequestAudit(BreakGlassWindow window, string actor, string operation, string outcome,
        string? target, string? digest, int status)
        => AppendBreakGlassAudit(window, operation, outcome, actor, new { target, digest, status }, target, digest);

    /// <summary>The stopped-API host path records a bounded operation in both the local journal and chain.</summary>
    public void AppendOfflineBreakGlassEntry(string windowId, string username, string? factor,
        string operation, string? target, string outcome)
    {
        if (operation.Length is < 1 or > 120 || target?.Length > 256 || outcome.Length is < 1 or > 80)
            throw new SecurityOperationException(400, "break-glass-entry-invalid", "Use a short operation, target and outcome.");
        lock (_gate)
        {
            var state = StateLocked();
            var window = state.BreakGlassWindows.FirstOrDefault(item => item.Id == windowId
                && item.ClosedAt is null && item.ExpiresAt > Now)
                ?? throw new SecurityOperationException(403, "break-glass-window-ended", "The window is not active.");
            var owner = state.Users.FirstOrDefault(user => user.Id == window.OwnerId && user.Username == username && !user.Disabled);
            if (owner is null || !ActiveOwnerIds(state.Users, state.RoleBindings, state.Groups).Contains(owner.Id)
                || OpenTotp(owner) is not { } seed || string.IsNullOrWhiteSpace(factor)
                || SecondFactorCodes.MatchTotpStep(seed, factor, Now) is not { } acceptedStep
                || (owner.LastAcceptedTotpStep is { } previousStep && acceptedStep <= previousStep))
                throw new SecurityOperationException(403, "break-glass-second-factor-invalid", "An active Owner's current authenticator code is required.");
            state.Users[state.Users.IndexOf(owner)] = owner with { LastAcceptedTotpStep = acceptedStep };
            SaveLocked(state);
            AppendBreakGlassAudit(window, operation, outcome, $"human:{owner.Id}",
                new { window.SessionIds, host = true }, target);
        }
    }

    private void AppendBreakGlassAudit(BreakGlassWindow window, string operation, string outcome, string actor,
        object? details, string? target = null, string? digest = null)
    {
        var entry = ControlPlaneAudit.Append(new ControlPlaneAuditRecord(ControlPlaneAuditStreams.BreakGlass, actor,
            operation, outcome, target ?? window.Id, null, $"human:{window.OwnerId}", digest,
            new { windowId = window.Id, payload = details }));
        // A distinct host journal survives while the API is stopped. The chain's sink forwarder carries the same entry off host.
        var path = Path.Combine(SecurityFolder, BreakGlassJournalFileName);
        Directory.CreateDirectory(SecurityFolder);
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var journal = new FileStream(path, options);
        var bytes = Encoding.UTF8.GetBytes(entry.Raw + "\n");
        journal.Write(bytes);
        journal.Flush(flushToDisk: true);
        RestrictPermissions(path);
    }
}
