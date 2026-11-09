using AgentStudio.Runner;
using AgentStudio.Secrets;
using AgentStudio.Security;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3575: each fact source of the installation security view, the ranking
/// by score, and the action-soon highlight. Pure projection over
/// <see cref="SecurityPostureFacts"/>.
/// </summary>
public sealed class SecurityPostureProjectionTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private static SecurityPostureFacts Facts(string profile = SecurityProfiles.Networked) => new()
    {
        Now = Now,
        Profile = profile,
        TrustedProxyCount = 2,
        Server = new SecurityPostureServerVersion("1.4.0", "v1.4.0", "abcdef1234567", null, true),
        Stable = new StableReleaseIdentity("1.4.0", "abcdef1234567", null),
        Integrity = new ControlPlaneAuditIntegrity(ControlPlaneAuditIntegrityStates.Verified, Now, 10, null, false),
        Forwarding = new ControlPlaneAuditForwarding("https", true, 10, 10, 0, Now, null, null, false),
    };

    private static StudioApiToken Token(string id, DateTime expiresAt, DateTime? lastUsedAt = null, DateTime? revokedAt = null) => new()
    {
        Id = id, Name = id, Role = StudioRoles.Operator, Projects = ["*"], SecretHash = "hash-" + id, CreatedBy = "owner",
        CreatedAt = Now.AddDays(-10), ExpiresAt = expiresAt, LastUsedAt = lastUsedAt ?? Now.AddHours(-1), RevokedAt = revokedAt,
    };

    private static StudioUser User(string id, string role, bool totp = false, int passkeys = 0, int recovery = 0, bool disabled = false) => new()
    {
        Id = id, Username = id, DisplayName = id, Role = role, PasswordHash = "pw-" + id, Disabled = disabled,
        CreatedAt = Now.AddDays(-30), PasswordChangedAt = Now.AddDays(-30),
        TotpSecret = totp ? "totp-" + id : null,
        RecoveryCodeHashes = Enumerable.Range(0, recovery).Select(index => $"rc-{id}-{index}").ToList(),
        Passkeys = Enumerable.Range(0, passkeys).Select(index => new StudioPasskey($"pk{index}", "key", "pub", 0, Now)).ToList(),
    };

    private static CapabilityHealthDto Capability(string key, string category, string? version = null) => new(
        key, category, "ready", "healthy", null, Now, Now.AddMinutes(5), true, null, null, null, null, 0, version, null, null,
        [], []);

    private static RunnerCapabilitySnapshotDto Host(string id, string? release, IReadOnlyList<InstalledCliDto>? clis = null) => new(
        id, "host " + id, "host-" + id, "instance", "runner " + (release ?? "?"), 1, "active", Now.AddDays(-5), Now,
        new RemoteHostAdmissionDto("host-" + id, "open", null, null, null, null),
        [Capability("platform:linux", "platform", "Ubuntu 24.04.1 LTS")], null,
        InstalledClis: clis,
        Release: release is null ? null : new RunnerReleaseIdentityDto("rel-" + release, release, release == "1.4.0" ? "abcdef1234567" : "1234567abcdef", Now.AddDays(-30)));

    private static SecurityPostureFinding Finding(SecurityPostureResponse view, string id)
        => Assert.Single(view.Findings, finding => finding.Id == id);

    // --- Credentials -------------------------------------------------------

    [Fact]
    public void A_token_expiring_within_fourteen_days_needs_action_soon_and_ranks_by_days_left()
    {
        var view = SecurityPostureProjection.Build(Facts() with
        {
            ApiTokens = [Token("soon", Now.AddDays(3)), Token("later", Now.AddDays(12)), Token("notice", Now.AddDays(24)),
                Token("far", Now.AddDays(200))],
        });

        var soon = Finding(view, "credentials:token:soon:expiring");
        var later = Finding(view, "credentials:token:later:expiring");
        var notice = Finding(view, "credentials:token:notice:expiring");
        Assert.True(soon.ActionSoon);
        Assert.True(later.ActionSoon);
        Assert.False(notice.ActionSoon);
        Assert.Equal((81, SecurityPostureSeverities.High), (soon.Score, soon.Severity));
        Assert.Equal((72, SecurityPostureSeverities.High), (later.Score, later.Severity));
        Assert.Equal((40, SecurityPostureSeverities.Medium), (notice.Score, notice.Severity));
        Assert.Equal("API token notice expires in 24 days", notice.Title);
        Assert.DoesNotContain(view.Findings, finding => finding.Id.StartsWith("credentials:token:far", StringComparison.Ordinal));
        Assert.Equal(["expiring", "expiring", "active", "active"], view.Credentials.Tokens.Select(token => token.State));
    }

    [Fact]
    public void A_token_expiring_within_a_day_is_critical()
    {
        var view = SecurityPostureProjection.Build(Facts() with { ApiTokens = [Token("today", Now.AddHours(6))] });

        var finding = Finding(view, "credentials:token:today:expiring");
        Assert.Equal((90, SecurityPostureSeverities.Critical, true), (finding.Score, finding.Severity, finding.ActionSoon));
        Assert.Equal("API token today expires within a day", finding.Title);
        Assert.Equal(Now.AddHours(6), finding.DueAt);
    }

    [Fact]
    public void Revoked_and_expired_tokens_stay_quiet_and_tokens_without_expiry_or_use_are_flagged()
    {
        var idle = Token("idle", Now.AddDays(300)) with { CreatedAt = Now.AddDays(-200), LastUsedAt = null };
        var view = SecurityPostureProjection.Build(Facts() with
        {
            ApiTokens = [Token("revoked", Now.AddDays(2), revokedAt: Now.AddDays(-1)), Token("gone", Now.AddDays(-2)),
                Token("forever", DateTime.MaxValue), idle],
        });

        Assert.DoesNotContain(view.Findings, finding => finding.Id.Contains(":revoked", StringComparison.Ordinal));
        Assert.DoesNotContain(view.Findings, finding => finding.Id.Contains(":gone", StringComparison.Ordinal));
        Assert.Equal(["revoked", "expired", "no-expiry", "active"], view.Credentials.Tokens.Select(token => token.State));
        Assert.Null(view.Credentials.Tokens.Single(token => token.Id == "forever").ExpiresAt);
        Assert.Equal(50, Finding(view, "credentials:token:forever:no-expiry").Score);
        var unused = Finding(view, "credentials:token:idle:idle");
        Assert.Equal((25, SecurityPostureSeverities.Low, false), (unused.Score, unused.Severity, unused.ActionSoon));
        Assert.Equal("API token idle unused for 200 days", unused.Title);
    }

    [Fact]
    public void Service_account_tokens_carry_their_account_and_disabled_accounts_raise_nothing()
    {
        var token = new ServiceAccountToken
        {
            Id = "sat1", SecretHash = "hash", Name = "deploy", CreatedAt = Now.AddDays(-5), ExpiresAt = Now.AddDays(5),
        };
        var view = SecurityPostureProjection.Build(Facts() with
        {
            ServiceAccounts =
            [
                new ServiceAccountFact("sa_live", "deploy bot", "Admin", ["mine"], false, [token]),
                new ServiceAccountFact("sa_off", "old bot", "Viewer", ["*"], true, [token with { Id = "sat2" }]),
            ],
        });

        var row = Assert.Single(view.Credentials.Tokens, item => item.Id == "sat1");
        Assert.Equal(("deploy bot", "service-account", "Admin", "expiring"), (row.Holder, row.Kind, row.Role, row.State));
        Assert.Equal("disabled", view.Credentials.Tokens.Single(item => item.Id == "sat2").State);
        Assert.Equal("Service account deploy bot token deploy expires in 5 days", Finding(view, "credentials:token:sat1:expiring").Title);
        Assert.DoesNotContain(view.Findings, finding => finding.Id.Contains("sat2", StringComparison.Ordinal));
    }

    [Fact]
    public void Operator_agents_report_live_sessions_and_their_next_expiry()
    {
        OperatorAgentSession Session(string id, DateTime expires, DateTime? revoked = null) => new()
        {
            Id = id, SecretHash = "hash", CreatedAt = Now.AddHours(-1), CreatedBy = "owner", ExpiresAt = expires, RevokedAt = revoked,
        };
        var view = SecurityPostureProjection.Build(Facts() with
        {
            OperatorAgents =
            [
                new OperatorAgentFact("opa_1", "laptop agent", false,
                    [Session("a", Now.AddHours(5)), Session("b", Now.AddHours(2)), Session("c", Now.AddHours(1), Now), Session("d", Now.AddHours(-1))]),
            ],
        });

        var agent = Assert.Single(view.Credentials.OperatorAgents);
        Assert.Equal((2, Now.AddHours(2)), (agent.LiveSessions, agent.NextSessionExpiry));
    }

    [Fact]
    public void Inventory_states_map_to_findings_and_provider_sign_ins_turn_expiring()
    {
        CredentialInventoryItemDto Item(string id, string category, string state, bool required = false, DateTime? expires = null) => new(
            id, category, "host-1", category == "provider" ? "claude_login" : "github_app", "label " + id, [], null, null,
            expires, null, state, required, "detail " + id, []);
        var view = SecurityPostureProjection.Build(Facts() with
        {
            Inventory =
            [
                Item("app", "service", "missing", required: true),
                Item("write", "repository", "unexpected"),
                Item("login", "provider", "ok", expires: Now.AddDays(6)),
                Item("signed-out", "provider", "missing"),
                Item("fine", "provider", "ok", expires: Now.AddDays(90)),
                Item("spare", "service", "unused"),
            ],
        });

        Assert.Equal((75, true), (Finding(view, "credentials:inventory:app").Score, Finding(view, "credentials:inventory:app").ActionSoon));
        Assert.Equal(85, Finding(view, "credentials:inventory:write").Score);
        Assert.Equal("label signed-out is not working", Finding(view, "credentials:inventory:signed-out").Title);
        var login = Finding(view, "credentials:inventory:login:expiring");
        Assert.Equal((78, true, "host-1"), (login.Score, login.ActionSoon, login.Subject));
        Assert.Equal("expiring", view.Credentials.Inventory.Single(item => item.Id == "login").State);
        Assert.DoesNotContain(view.Findings, finding => finding.Id.Contains("fine", StringComparison.Ordinal) || finding.Id.Contains("spare", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unavailable_inventory_is_named_instead_of_read_as_empty()
    {
        var view = SecurityPostureProjection.Build(Facts() with
        {
            InventorySource = SecurityPostureSources.Unavailable, InventoryProblem = "The Task Server did not answer.",
        });

        Assert.Equal("The Task Server did not answer.", Finding(view, "credentials:inventory-unavailable").Detail);
        Assert.Equal(SecurityPostureSources.Unavailable, view.Credentials.InventorySource);
    }

    // --- Second factor -----------------------------------------------------

    [Fact]
    public void Users_without_a_second_factor_need_action_soon_and_the_owner_is_critical()
    {
        var view = SecurityPostureProjection.Build(Facts() with
        {
            Users =
            [
                User("owner", StudioRoles.Owner), User("ops", StudioRoles.Operator), User("product", StudioRoles.Contributor),
                User("keyed", StudioRoles.Viewer, passkeys: 1), User("totp", StudioRoles.Operator, totp: true, recovery: 8),
                User("spent", StudioRoles.Operator, totp: true), User("gone", StudioRoles.Viewer, disabled: true),
            ],
        });

        Assert.Equal((95, SecurityPostureSeverities.Critical, true),
            (Finding(view, "second-factor:owner").Score, Finding(view, "second-factor:owner").Severity, Finding(view, "second-factor:owner").ActionSoon));
        Assert.Equal(85, Finding(view, "second-factor:ops").Score);
        Assert.Equal((60, true), (Finding(view, "second-factor:product").Score, Finding(view, "second-factor:product").ActionSoon));
        Assert.Equal(25, Finding(view, "second-factor:spent:recovery").Score);
        Assert.DoesNotContain(view.Findings, finding => finding.Id is "second-factor:keyed" or "second-factor:totp" or "second-factor:gone");
        Assert.Equal((6, 3), (view.SecondFactor.ActiveUsers, view.SecondFactor.Covered));
        Assert.Equal(["missing", "missing", "missing", "covered", "covered", "no-recovery-codes", "disabled"],
            view.SecondFactor.Users.Select(user => user.State));
        Assert.True(view.SecondFactor.Users.Single(user => user.Id == "ops").Required);
        Assert.False(view.SecondFactor.Users.Single(user => user.Id == "product").Required);
    }

    [Fact]
    public void The_local_profile_lists_users_without_second_factor_findings()
    {
        var view = SecurityPostureProjection.Build(Facts(SecurityProfiles.Local) with { Users = [User("owner", StudioRoles.Owner)] });

        Assert.Single(view.SecondFactor.Users);
        Assert.DoesNotContain(view.Findings, finding => finding.Area == SecurityPostureAreas.SecondFactor);
    }

    // --- Versions ----------------------------------------------------------

    [Fact]
    public void Hosts_behind_the_server_and_clis_below_the_required_version_are_ranked()
    {
        var view = SecurityPostureProjection.Build(Facts() with
        {
            Hosts =
            [
                Host("current", "1.4.0", [new InstalledCliDto("claude", "2.1.200", "/usr/bin/claude", Now, "2.1.305", true),
                    new InstalledCliDto("codex", "0.166.0", "/usr/bin/codex", Now, "0.166.0", false)]),
                Host("old", "1.3.0"),
                Host("bare", null),
                Host("retired", "1.0.0") with { Status = "retired" },
            ],
        });

        var cli = Finding(view, "versions:cli:current:claude");
        Assert.Equal((74, true), (cli.Score, cli.ActionSoon));
        Assert.Equal("claude 2.1.200 on host current is below the required 2.1.305", cli.Title);
        Assert.DoesNotContain(view.Findings, finding => finding.Id == "versions:cli:current:codex");
        Assert.InRange(Finding(view, "versions:host:old").Score, 55, 70);
        Assert.Equal(20, Finding(view, "versions:host:bare").Score);
        Assert.DoesNotContain(view.Findings, finding => finding.Id.Contains("retired", StringComparison.Ordinal));
        Assert.DoesNotContain(view.Findings, finding => finding.Id == "versions:host:current");
        var host = view.Versions.Hosts.Single(item => item.RunnerId == "current");
        Assert.Equal(("1.4.0", HostReleaseDriftStates.Current, "Ubuntu 24.04.1 LTS"), (host.ReleaseVersion, host.ReleaseState, host.OperatingSystem));
        Assert.Equal(3, view.Versions.Hosts.Count);
    }

    [Fact]
    public void An_unknown_server_build_and_an_unreachable_host_registry_are_named()
    {
        var view = SecurityPostureProjection.Build(Facts() with
        {
            Server = new SecurityPostureServerVersion("unknown", "unknown", "unknown", null, false),
            HostSource = SecurityPostureSources.Unavailable,
            HostProblem = "The Task Server did not answer /api/v1/management/remote-hosts.",
        });

        Assert.Equal(30, Finding(view, "versions:server-unknown").Score);
        Assert.Equal("The Task Server did not answer /api/v1/management/remote-hosts.", Finding(view, "versions:hosts-unavailable").Detail);
    }

    // --- Audit -------------------------------------------------------------

    private static ControlPlaneAuditEntry Entry(long sequence, string stream, string actor, string operation, string outcome, DateTime at) => new()
    {
        Sequence = sequence, At = at, Stream = stream, Actor = actor, Operation = operation, Outcome = outcome,
        PreviousHash = "p", Hash = "h",
    };

    [Fact]
    public void Refused_token_requests_and_secret_writes_become_grouped_anomalies()
    {
        var entries = Enumerable.Range(0, 12)
            .Select(index => Entry(index, ControlPlaneAuditStreams.ApiToken, "api-token:tok1", "GET /api/auth/iam", "403", Now.AddHours(-index)))
            .Append(Entry(20, ControlPlaneAuditStreams.OperatorAgent, "operator-agent:opa/session:s1", "POST /api/tasks/X/move", "403", Now.AddHours(-2)))
            .Append(Entry(21, ControlPlaneAuditStreams.ApiToken, "api-token:tok1", "GET /api/tasks", "200", Now.AddHours(-1)))
            .Append(Entry(22, ControlPlaneAuditStreams.ApiToken, "api-token:tok2", "GET /api/tasks", "401", Now.AddDays(-9)))
            .ToArray();
        var view = SecurityPostureProjection.Build(Facts() with
        {
            RecentAudit = entries,
            RecentSecretAudit =
            [
                new SecretAuditEntry { At = Now.AddHours(-3), Action = "set", Actor = "user:dev", Scope = "project:mine", Reference = "DEPLOY_KEY",
                    Outcome = "denied:permission", Repeated = 3 },
                new SecretAuditEntry { At = Now.AddHours(-3), Action = "set", Actor = "user:owner", Scope = "project:mine", Reference = "DEPLOY_KEY",
                    Outcome = "ok" },
            ],
        });

        var token = Finding(view, "audit:refused-request:api-token:tok1");
        Assert.Equal((65, "12 refused requests by api-token:tok1"), (token.Score, token.Title));
        Assert.Equal(45, Finding(view, "audit:refused-request:operator-agent:opa/session:s1").Score);
        Assert.Equal("3 refused secret writes by user:dev", Finding(view, "audit:refused-secret-write:user:dev").Title);
        Assert.DoesNotContain(view.Findings, finding => finding.Id.Contains("tok2", StringComparison.Ordinal));
        var grouped = view.Audit.Anomalies.Single(item => item.Actor == "api-token:tok1");
        Assert.Equal((12, Now), (grouped.Count, grouped.At));
        Assert.Equal(3, view.Audit.Anomalies.Count);
        Assert.True(SecurityPostureProjection.IsAnomaly(entries[0]));
        Assert.False(SecurityPostureProjection.IsAnomaly(entries[13]));
    }

    [Fact]
    public void A_failed_chain_open_alerts_and_a_missing_sink_are_ranked_first()
    {
        var view = SecurityPostureProjection.Build(Facts() with
        {
            Integrity = new ControlPlaneAuditIntegrity(ControlPlaneAuditIntegrityStates.Failed, Now, 4, "hash mismatch at 5", true),
            Forwarding = new ControlPlaneAuditForwarding("none", false, 10, 0, 0, null, null, null, false),
            OpenAuditAlerts = [ControlPlaneAuditAlertCodes.ChainBroken, ControlPlaneAuditAlertCodes.SinkUnreachable],
        });

        Assert.Equal(("audit:integrity", 1, 95), (view.Findings[0].Id, view.Findings[0].Rank, view.Findings[0].Score));
        Assert.Equal("hash mismatch at 5", view.Findings[0].Detail);
        Assert.Equal(80, Finding(view, "audit:alert:audit-sink-unreachable").Score);
        Assert.DoesNotContain(view.Findings, finding => finding.Id == "audit:alert:audit-chain-broken");
        Assert.Equal(50, Finding(view, "audit:sink-unconfigured").Score);
    }

    [Fact]
    public void Kill_switch_use_drills_and_break_glass_windows_are_reported()
    {
        var view = SecurityPostureProjection.Build(Facts() with
        {
            OperatorAgents = [new OperatorAgentFact("opa_1", "agent", false, [])],
            DrillIntervalHours = 720,
            Kills = [new OperatorKillRecord { Id = "k1", Kind = "all", Trigger = "studio", Actor = "user:owner", At = Now.AddDays(-2) }],
            Drills = [new OperatorKillDrillResult("d1", "scheduled", Now.AddDays(-1), 900, 400, "ok", null, true, false, "session kept answering", null)],
            BreakGlassWindows =
            [
                new BreakGlassWindow { Id = "bg1", OwnerId = "owner", Project = "*", SessionIds = ["s1"], OpenedAt = Now.AddMinutes(-30), ExpiresAt = Now.AddMinutes(90) },
                new BreakGlassWindow { Id = "bg0", OwnerId = "owner", Project = "*", SessionIds = ["s1"], OpenedAt = Now.AddDays(-3),
                    ExpiresAt = Now.AddDays(-3).AddHours(1), ClosedAt = Now.AddDays(-3).AddHours(1), ReviewCardId = "SEC-9" },
            ],
        });

        Assert.Equal(35, Finding(view, "audit:kill-switch").Score);
        Assert.Equal((75, "session kept answering"), (Finding(view, "audit:kill-drill").Score, Finding(view, "audit:kill-drill").Detail));
        var open = Finding(view, "audit:break-glass:bg1");
        Assert.Equal((80, true, Now.AddMinutes(90)), (open.Score, open.ActionSoon, open.DueAt));
        Assert.Equal("Review card SEC-9 lists its entries.", Finding(view, "audit:break-glass:bg0").Detail);
        Assert.Equal((1, false, Now.AddDays(-2)), (view.Audit.ActiveBreakGlassWindows, view.Audit.LastDrillPassed, view.Audit.LastKillAt));
    }

    [Fact]
    public void An_overdue_drill_is_flagged_only_while_operator_agents_exist()
    {
        var stale = Facts() with { DrillIntervalHours = 720, OperatorAgents = [new OperatorAgentFact("opa_1", "agent", false, [])] };

        Assert.Equal("No drill has run yet.", Finding(SecurityPostureProjection.Build(stale), "audit:kill-drill-stale").Detail);
        Assert.DoesNotContain(SecurityPostureProjection.Build(stale with { OperatorAgents = [] }).Findings,
            finding => finding.Id == "audit:kill-drill-stale");
    }

    // --- Exposure and cards ------------------------------------------------

    [Fact]
    public void Networked_exposure_names_what_the_api_cannot_see_and_flags_proxy_problems()
    {
        var view = SecurityPostureProjection.Build(Facts() with
        {
            TrustedProxyCount = 0, LoginAddressHealthy = false, CloudflareEdge = true,
            AnonymousRoutes = ["GET /healthz", "POST /api/auth/login"],
        });

        Assert.True(view.Exposure.HttpsRequired);
        Assert.Equal(["GET /healthz", "POST /api/auth/login"], view.Exposure.AnonymousRoutes);
        Assert.Equal(["not-reported", "configured", "not-reported"], view.Exposure.External.Select(fact => fact.State));
        Assert.Equal((75, true), (Finding(view, "exposure:login-address").Score, Finding(view, "exposure:login-address").ActionSoon));
        Assert.Equal(45, Finding(view, "exposure:trusted-proxies").Score);
        var local = SecurityPostureProjection.Build(Facts(SecurityProfiles.Local) with { TrustedProxyCount = 0, LoginAddressHealthy = false });
        Assert.Empty(local.Exposure.External);
        Assert.DoesNotContain(local.Findings, finding => finding.Area == SecurityPostureAreas.Exposure);
    }

    [Fact]
    public void Open_security_cards_are_listed_escalated_first_and_finished_ones_are_left_out()
    {
        TaskInfo Card(string key, string state, int ageDays) => new()
        {
            Id = key.ToLowerInvariant(), Key = key, Title = "title " + key, State = state, ProjectName = "Studio",
            CreatedAt = Now.AddDays(-ageDays), Tags = ["security"],
        };
        var view = SecurityPostureProjection.Build(Facts() with
        {
            SecurityCards = [Card("SEC-1", TaskStates.Ready, 9), Card("SEC-2", TaskStates.Escalated, 1), Card("SEC-3", TaskStates.Completed, 20)],
        });

        Assert.Equal(["SEC-2", "SEC-1"], view.Cards.Select(card => card.Key));
        Assert.Equal(60, Finding(view, "cards:SEC-2").Score);
        Assert.Equal((35, "Open security card SEC-1: title SEC-1"), (Finding(view, "cards:SEC-1").Score, Finding(view, "cards:SEC-1").Title));
    }

    // --- Ranking -----------------------------------------------------------

    [Fact]
    public void Findings_are_numbered_by_score_then_due_date_and_the_summary_adds_up()
    {
        var view = SecurityPostureProjection.Build(Facts() with
        {
            Users = [User("owner", StudioRoles.Owner)],
            ApiTokens = [Token("a", Now.AddDays(10)), Token("b", Now.AddDays(10).AddHours(-3)), Token("forever", DateTime.MaxValue)],
            SecurityCards = [new TaskInfo { Id = "c", Key = "SEC-1", Title = "t", State = TaskStates.Ready, ProjectName = "p", Tags = ["security"] }],
        });

        Assert.Equal(Enumerable.Range(1, view.Findings.Count), view.Findings.Select(finding => finding.Rank));
        Assert.Equal(view.Findings.OrderByDescending(finding => finding.Score).Select(finding => finding.Score), view.Findings.Select(finding => finding.Score));
        Assert.Equal(["second-factor:owner", "credentials:token:b:expiring", "credentials:token:a:expiring", "credentials:token:forever:no-expiry", "cards:SEC-1"],
            view.Findings.Select(finding => finding.Id));
        var summary = view.Summary;
        Assert.Equal(view.Findings.Count, summary.Total);
        Assert.Equal(summary.Total, summary.Critical + summary.High + summary.Medium + summary.Low + summary.Info);
        Assert.Equal((1, 2, 1, 1, 3), (summary.Critical, summary.High, summary.Medium, summary.Low, summary.ActionSoon));
    }

    [Theory]
    [InlineData(100, SecurityPostureSeverities.Critical)]
    [InlineData(90, SecurityPostureSeverities.Critical)]
    [InlineData(89, SecurityPostureSeverities.High)]
    [InlineData(70, SecurityPostureSeverities.High)]
    [InlineData(69, SecurityPostureSeverities.Medium)]
    [InlineData(40, SecurityPostureSeverities.Medium)]
    [InlineData(39, SecurityPostureSeverities.Low)]
    [InlineData(20, SecurityPostureSeverities.Low)]
    [InlineData(19, SecurityPostureSeverities.Info)]
    public void Severity_bands_follow_the_score(int score, string severity)
        => Assert.Equal(severity, SecurityPostureSeverities.ForScore(score));
}
