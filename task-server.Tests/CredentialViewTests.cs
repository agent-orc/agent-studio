using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// I8 (docs/credentials-and-logins/index.html#surface and #renewal warnings, D4 option A).
/// Every fixture is redacted metadata; no credential-shaped value is written.
/// </summary>
public sealed class CredentialViewTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    // ---- Pure due-date policy -------------------------------------------------------------

    [Theory]
    [InlineData(20.0, null, false)]
    [InlineData(14.0, 14, false)]
    [InlineData(10.0, 14, false)]
    [InlineData(7.0, 7, false)]
    [InlineData(3.0, 7, false)]
    [InlineData(1.0, 1, false)]
    [InlineData(0.25, 1, false)]
    [InlineData(-2.0, 1, true)]
    public void Issuer_expiry_maps_to_the_most_urgent_applicable_threshold(
        double daysLeft, int? threshold, bool overdue)
    {
        var now = Start.UtcDateTime;
        var due = CredentialReminderPolicy.Assess(Issued(now.AddDays(daysLeft)), now);
        Assert.Equal("known", due.Knowledge);
        Assert.Equal("expiry", due.Subject);
        Assert.Equal(threshold, due.ThresholdDays);
        Assert.Equal(overdue, due.Overdue);
    }

    [Fact]
    public void Refreshable_access_token_expiry_is_never_a_login_due_date()
    {
        var now = Start.UtcDateTime;
        var record = Record("codex_chatgpt_login") with { AccessTokenExpiresAt = now.AddHours(1) };
        var due = CredentialReminderPolicy.Assess(record, now);
        Assert.Equal("unknown", due.Knowledge);
        Assert.True(due.ExpiryUnknown);
        Assert.Null(due.DueAt);
        Assert.Null(due.ThresholdDays);
        Assert.Equal("issuer-unknown", due.UnknownReason);
    }

    [Fact]
    public void Unknown_expiry_stays_unknown_and_carries_the_next_verification_as_review_date()
    {
        var now = Start.UtcDateTime;
        var record = Record("claude_oauth_token") with { NextProbeAt = now.AddMinutes(30) };
        var due = CredentialReminderPolicy.Assess(record, now);
        Assert.Equal("unknown", due.Knowledge);
        Assert.Equal(now.AddMinutes(30), due.ReviewDueAt);
        Assert.Null(due.ThresholdDays);
    }

    [Fact]
    public void Rotation_due_date_raises_rotation_reminder_while_expiry_remains_unknown()
    {
        var now = Start.UtcDateTime;
        var record = Record("github_deploy_key") with { ExpiryKnowledge = "none", RotationDueAt = now.AddDays(6) };
        var due = CredentialReminderPolicy.Assess(record, now);
        Assert.Equal("rotation", due.Subject);
        Assert.Equal(7, due.ThresholdDays);
        Assert.False(due.ExpiryUnknown);

        var noRotation = CredentialReminderPolicy.Assess(record with { RotationDueAt = null }, now);
        Assert.Equal("none", noRotation.Knowledge);
        Assert.Null(noRotation.ThresholdDays);
    }

    [Fact]
    public void Reminder_urgency_never_falls_for_the_same_due_date()
    {
        var dueAt = Start.UtcDateTime.AddDays(5);
        var existing = new CredentialReminderPolicy.ExistingReminder(dueAt, 7, Closed: false);
        // A clock that steps back by three days computes 14 again; the item keeps 7.
        var decision = CredentialReminderPolicy.Decide(Due(dueAt, 14), existing);
        Assert.Equal(CredentialReminderPolicy.ReminderAction.Keep, decision.Action);
        Assert.Equal(7, decision.ThresholdDays);
        Assert.Equal(CredentialReminderPolicy.ReminderAction.Escalate,
            CredentialReminderPolicy.Decide(Due(dueAt, 1), existing).Action);
        Assert.Equal(CredentialReminderPolicy.ReminderAction.Close,
            CredentialReminderPolicy.Decide(Due(null, null), existing).Action);
        Assert.Equal(CredentialReminderPolicy.ReminderAction.Open,
            CredentialReminderPolicy.Decide(Due(dueAt, 14), null).Action);
    }

    [Theory]
    [InlineData(-3.0, "clock-skew")]
    [InlineData(0.0, "ok")]
    [InlineData(23.0, "ok")]
    [InlineData(25.0, "stale")]
    public void Evidence_quality_flags_future_and_stale_observations(double hoursAgo, string expected)
    {
        var now = Start.UtcDateTime;
        Assert.Equal(expected, CredentialReminderPolicy.EvidenceQuality(
            Record("claude_oauth_token"), now.AddHours(-hoursAgo), now));
    }

    // ---- Pure status and action policy -----------------------------------------------------

    [Fact]
    public void Status_separates_incident_login_quota_network_and_diagnosis()
    {
        var record = Record("claude_native_login");
        CredentialStatusPolicy.Circuit? None = null;
        var incident = new CredentialStatusPolicy.Circuit("provider_incident", Start.UtcDateTime.AddSeconds(60));
        var invalid = new CredentialStatusPolicy.Circuit("credential_invalid", null);
        var indeterminate = new CredentialStatusPolicy.Circuit("indeterminate", Start.UtcDateTime.AddSeconds(60));
        CredentialStatusPolicy.Unit Unit(string outcome) => new(outcome, "unavailable", "generation-a", null);

        Assert.Equal("provider_incident", CredentialStatusPolicy.Classify(record, None, incident, []).Status);
        Assert.Equal(incident.NextRetryAt, CredentialStatusPolicy.Classify(record, None, incident, []).NextRetryAt);
        Assert.Equal("credential_invalid", CredentialStatusPolicy.Classify(record, invalid, incident, []).Status);
        Assert.Equal("quota_exhausted", CredentialStatusPolicy.Classify(record, None, incident, [Unit("quota_exhausted")]).Status);
        Assert.Equal("network_failure", CredentialStatusPolicy.Classify(record, None, None, [Unit("network_failure")]).Status);
        Assert.Equal("diagnosis_pending", CredentialStatusPolicy.Classify(record, indeterminate, None, []).Status);
        Assert.Equal("healthy", CredentialStatusPolicy.Classify(record, None, None, [Unit("healthy")]).Status);
        Assert.Equal("not_verified", CredentialStatusPolicy.Classify(record, None, None, []).Status);
        // A unit still on a previous generation cannot mark the current generation invalid.
        Assert.Equal("not_verified", CredentialStatusPolicy.Classify(record, None, None,
            [new("credential_invalid", "unavailable", "generation-old", null)]).Status);
        Assert.Equal("credential_invalid", CredentialStatusPolicy.Classify(
            record with { Locator = record.Locator with { EffectiveSource = "absent" } }, None, None, []).Status);
    }

    [Theory]
    [InlineData("claude_oauth_token", "R1")]
    [InlineData("claude_native_login", "R2")]
    [InlineData("codex_chatgpt_login", "R3")]
    public void Confirmed_invalid_login_offers_the_host_specific_renewal_to_operators_only(string kind, string method)
    {
        var record = Record(kind);
        var operatorActions = CredentialActionPolicy.For(StudioUserRoles.Operator, record, "credential_invalid", false, "docs/operations/setup/cli-relogin-runbook.md");
        var renew = Assert.Single(operatorActions, action => action.Kind == "renew");
        Assert.True(renew.Primary);
        Assert.Equal(method, renew.Method);
        Assert.Empty(CredentialActionPolicy.For(StudioUserRoles.Viewer, record, "credential_invalid", false, null));
    }

    [Theory]
    [InlineData("provider_incident")]
    [InlineData("quota_exhausted")]
    [InlineData("network_failure")]
    [InlineData("diagnosis_pending")]
    [InlineData("not_verified")]
    public void Non_login_outcomes_never_offer_sign_in(string status)
    {
        var actions = CredentialActionPolicy.For(StudioUserRoles.Owner, Record("codex_chatgpt_login"), status, false,
            "docs/operations/setup/cli-relogin-runbook.md");
        Assert.DoesNotContain(actions, action => action.Kind == "renew");
    }

    [Fact]
    public void Administrator_credentials_need_the_owner_and_runbook_links_must_be_repository_docs()
    {
        var key = Record("github_deploy_key");
        Assert.Equal("requires-administrator", Assert.Single(
            CredentialActionPolicy.For(StudioUserRoles.Operator, key, "credential_invalid", false, null)).Kind);
        Assert.Contains(CredentialActionPolicy.For(StudioUserRoles.Owner, key, "credential_invalid", false,
            "docs/operations/setup/cli-relogin-runbook.md"), action => action.Kind == "runbook");
        Assert.Equal("docs/operations/setup/cli-relogin-runbook.md#3-renew-claude-through-studio",
            CredentialActionPolicy.SafeRunbookHref("docs/operations/setup/cli-relogin-runbook.md#3-renew-claude-through-studio"));
        Assert.Null(CredentialActionPolicy.SafeRunbookHref("https://evil.example/login"));
        Assert.Null(CredentialActionPolicy.SafeRunbookHref("docs/../../etc/passwd.md"));
        Assert.Null(CredentialActionPolicy.SafeRunbookHref("R1"));
    }

    // ---- Fake-clock store behavior -------------------------------------------------------------

    [Fact]
    public async Task One_item_per_generation_escalates_through_fourteen_seven_and_one_days()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        var expiresAt = Start.UtcDateTime.AddDays(15);
        await ObserveAsync(store, clock, Issued(expiresAt));

        Assert.Null(Assert.Single((await store.GetCredentialViewAsync(StudioUserRoles.Operator, default)).Rows).Reminder);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(Assert.Single((await store.GetCredentialViewAsync(StudioUserRoles.Operator, default)).Rows).Reminder);
        await store.ReconcileCredentialRemindersAsync(default);
        var fourteen = Assert.Single((await store.GetCredentialViewAsync(StudioUserRoles.Operator, default)).Rows).Reminder!;
        Assert.Equal(14, fourteen.ThresholdDays);
        clock.Advance(TimeSpan.FromDays(7));
        await store.ReconcileCredentialRemindersAsync(default);
        var seven = Assert.Single((await store.GetCredentialViewAsync(StudioUserRoles.Operator, default)).Rows).Reminder!;
        Assert.Equal(7, seven.ThresholdDays);
        Assert.Equal(fourteen.OpenedAt, seven.OpenedAt);
        clock.Advance(TimeSpan.FromDays(6));
        await store.ReconcileCredentialRemindersAsync(default);
        var one = Assert.Single(await store.ListCredentialRemindersAsync(default));
        Assert.Equal(1, one.ThresholdDays);
        Assert.Equal(fourteen.OpenedAt, one.OpenedAt);
        Assert.Equal(1, await AuditCountAsync(store, "credential.reminder-opened"));
        Assert.Equal(2, await AuditCountAsync(store, "credential.reminder-escalated"));
    }

    [Fact]
    public async Task Late_discovery_inside_the_final_day_emits_only_the_one_day_reminder()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        await ObserveAsync(store, clock, Issued(Start.UtcDateTime.AddHours(12)));

        var item = Assert.Single(await store.ListCredentialRemindersAsync(default));
        Assert.Equal(1, item.ThresholdDays);
        Assert.Equal(1, await AuditCountAsync(store, "credential.reminder-opened"));
        Assert.Equal(0, await AuditCountAsync(store, "credential.reminder-escalated"));
    }

    [Fact]
    public async Task Duplicate_observations_keep_one_unchanged_item()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        var record = Issued(Start.UtcDateTime.AddDays(10));
        await ObserveAsync(store, clock, record);
        var first = Assert.Single(await store.ListCredentialRemindersAsync(default));
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            await ObserveAsync(store, clock, record, expectedGeneration: "generation-a");
            await store.GetCredentialViewAsync(StudioUserRoles.Viewer, default);
        }
        Assert.Equal(first, Assert.Single(await store.ListCredentialRemindersAsync(default)));
        Assert.Equal(1, await AuditCountAsync(store, "credential.reminder-opened"));
    }

    [Fact]
    public async Task Rotation_to_a_new_generation_clears_the_previous_generation_item()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        var first = Issued(Start.UtcDateTime.AddDays(3));
        await ObserveAsync(store, clock, first);
        Assert.Null(Assert.Single(await store.ListCredentialRemindersAsync(default)).ClosedAt);

        clock.Advance(TimeSpan.FromMinutes(1));
        var renewed = Record("claude_oauth_token") with { Generation = "generation-b", Supersedes = "generation-a" };
        await ObserveAsync(store, clock, renewed, expectedGeneration: "generation-a");

        var closed = Assert.Single(await store.ListCredentialRemindersAsync(default));
        Assert.Equal("generation-a", closed.Generation);
        Assert.Equal("renewed", closed.CloseReason);
        var row = Assert.Single((await store.GetCredentialViewAsync(StudioUserRoles.Operator, default)).Rows);
        Assert.Null(row.Reminder);
        Assert.Equal("unknown", row.Due.Knowledge);
    }

    [Fact]
    public async Task Unknown_expiry_and_access_token_expiry_raise_no_item()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        await ObserveAsync(store, clock, Record("codex_chatgpt_login") with
        {
            AccessTokenExpiresAt = Start.UtcDateTime.AddMinutes(40),
            UnknownReasons = Reasons(except: "accessTokenExpiresAt"),
        });
        clock.Advance(TimeSpan.FromDays(30));
        await store.ReconcileCredentialRemindersAsync(default);

        Assert.Empty(await store.ListCredentialRemindersAsync(default));
        var row = Assert.Single((await store.GetCredentialViewAsync(StudioUserRoles.Operator, default)).Rows);
        Assert.True(row.Due.ExpiryUnknown);
        Assert.NotEqual("healthy", row.Status);
    }

    [Fact]
    public async Task Host_clock_skew_is_flagged_and_the_server_clock_alone_sets_urgency()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        // An observation stamped by a host clock three hours ahead is refused at the boundary.
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCredentialRegistryAsync(new(
            Issued(Start.UtcDateTime.AddDays(8)), "instance-a", null, Start.UtcDateTime.AddHours(3)), "test", default));
        // Verification times from that clock are kept but flagged; they never set urgency.
        await ObserveAsync(store, clock, Issued(Start.UtcDateTime.AddDays(8)) with
        {
            LastVerifiedAt = Start.UtcDateTime.AddHours(3),
            UnknownReasons = Reasons(except: "expiresAt").Where(pair => pair.Key != "lastVerifiedAt")
                .ToDictionary(pair => pair.Key, pair => pair.Value),
        });

        var row = Assert.Single((await store.GetCredentialViewAsync(StudioUserRoles.Operator, default)).Rows);
        Assert.Equal("clock-skew", row.EvidenceQuality);
        Assert.Equal(14, row.Reminder!.ThresholdDays);

        // A server clock step backwards after escalation does not lower urgency or reopen.
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(7, Assert.Single(await ReconciledAsync(store)).ThresholdDays);
        clock.Advance(TimeSpan.FromDays(-2));
        Assert.Equal(7, Assert.Single(await ReconciledAsync(store)).ThresholdDays);
        Assert.Equal(1, await AuditCountAsync(store, "credential.reminder-opened"));
    }

    [Fact]
    public async Task One_credential_shared_by_coding_and_review_has_one_row_and_one_item()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        await ObserveAsync(store, clock, Issued(Start.UtcDateTime.AddDays(2)) with
        {
            Bindings = [new("coding", "execution", "source:provider-auth"), new("review", "review", "source:provider-auth")],
        });

        var row = Assert.Single((await store.GetCredentialViewAsync(StudioUserRoles.Operator, default)).Rows);
        Assert.Equal(["coding", "review"], row.Services);
        Assert.Single(await store.ListCredentialRemindersAsync(default));
    }

    [Fact]
    public async Task Provider_incident_shows_retry_without_an_action_and_invalid_login_offers_its_renewal()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        await ObserveAsync(store, clock, Record("codex_chatgpt_login") with { Provider = "codex" });
        await ObserveAsync(store, clock, Record("claude_native_login") with { Provider = "claude" });
        await RegisterRunnerAsync(store, clock, "coding", "coding-instance",
            CapabilityProtocol.ProviderAuthentication("codex"), CapabilityProtocol.ProviderAuthentication("claude"));

        clock.Advance(TimeSpan.FromSeconds(1));
        await AdvertiseAsync(store, clock, "codex", "provider_incident", 2);
        await AdvertiseAsync(store, clock, "claude", "credential_invalid", 3);

        var rows = (await store.GetCredentialViewAsync(StudioUserRoles.Operator, default)).Rows;
        var codex = Assert.Single(rows, row => row.Provider == "codex");
        Assert.Equal("provider_incident", codex.Status);
        Assert.NotNull(codex.NextRetryAt);
        Assert.Empty(codex.Actions);
        Assert.Null(codex.HealthItem);

        var claude = Assert.Single(rows, row => row.Provider == "claude");
        Assert.Equal("credential_invalid", claude.Status);
        Assert.Equal("renewal", claude.HealthItem!.Kind);
        var renew = Assert.Single(claude.Actions, action => action.Kind == "renew");
        Assert.Equal("R2", renew.Method);
        Assert.True(renew.Primary);
    }

    [Fact]
    public async Task Viewer_projection_omits_locator_and_evidence_detail()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        await ObserveAsync(store, clock, Issued(Start.UtcDateTime.AddDays(2)) with { EvidenceRefs = ["evidence:probe-1"] });

        var viewer = Assert.Single((await store.GetCredentialViewAsync(StudioUserRoles.Viewer, default)).Rows);
        Assert.Null(viewer.LocalRef);
        Assert.Empty(viewer.EvidenceRefs);
        Assert.Empty(viewer.Actions);
        var operatorRow = Assert.Single((await store.GetCredentialViewAsync(StudioUserRoles.Operator, default)).Rows);
        Assert.Equal("host-local-reference", operatorRow.LocalRef);
        Assert.Equal(["evidence:probe-1"], operatorRow.EvidenceRefs);
    }

    // ---- Server-side role enforcement over HTTP --------------------------------------------------

    [Fact]
    public async Task Roles_are_enforced_by_the_task_server_for_view_raw_metadata_and_administration()
    {
        using var temp = new TempDirectory();
        var studioToken = "studio-" + Guid.NewGuid().ToString("N");
        var engineToken = "engine-" + Guid.NewGuid().ToString("N");
        var ownerCode = "owner-code-" + Guid.NewGuid().ToString("N");
        var clock = new ManualTimeProvider(Start);
        await using var factory = new RoleFactory(temp.Path, studioToken, engineToken, ownerCode, clock);
        using var edge = Client(factory, studioToken);
        var bootstrap = await edge.PostAsJsonAsync("/api/v1/studio/auth/bootstrap",
            new StudioBootstrapRequest("owner", "owner password 1234", BootstrapCode: ownerCode));
        Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
        using var owner = Client(factory, studioToken,
            (await bootstrap.Content.ReadFromJsonAsync<StudioOwnerBootstrapSessionDto>())!.SessionToken);
        var operatorSession = await UserSessionAsync(edge, owner, "alex", StudioUserRoles.Operator);
        var viewerSession = await UserSessionAsync(edge, owner, "vera", StudioUserRoles.Viewer);
        using var operatorClient = Client(factory, studioToken, operatorSession);
        using var viewerClient = Client(factory, studioToken, viewerSession);

        var put = await owner.PutAsJsonAsync("/api/v1/management/credentials",
            new CredentialRegistryObservationRequest(Issued(clock.GetUtcNow().UtcDateTime.AddDays(3)), "instance-a", null, clock.GetUtcNow().UtcDateTime));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        Assert.Equal("owner-role-required", await ErrorCodeAsync(await operatorClient.PutAsJsonAsync(
            "/api/v1/management/credentials",
            new CredentialRegistryObservationRequest(Issued(clock.GetUtcNow().UtcDateTime.AddDays(3)), "instance-a", null, clock.GetUtcNow().UtcDateTime)),
            HttpStatusCode.Forbidden));
        Assert.Equal("owner-role-required", await ErrorCodeAsync(await operatorClient.PostAsJsonAsync(
            "/api/v1/management/principals/any/rotate", new { }), HttpStatusCode.Forbidden));
        Assert.Equal("operator-role-required", await ErrorCodeAsync(
            await viewerClient.GetAsync("/api/v1/management/credentials"), HttpStatusCode.Forbidden));

        var viewerView = await viewerClient.GetFromJsonAsync<CredentialViewDto>("/api/v1/management/credential-view");
        Assert.Equal(StudioUserRoles.Viewer, viewerView!.Role);
        Assert.Null(Assert.Single(viewerView.Rows).LocalRef);
        var operatorView = await operatorClient.GetFromJsonAsync<CredentialViewDto>("/api/v1/management/credential-view");
        Assert.Equal(StudioUserRoles.Operator, operatorView!.Role);
        Assert.NotNull(Assert.Single(operatorView.Rows).LocalRef);

        // A session always outranks an acting-role claim.
        viewerClient.DefaultRequestHeaders.Add(CredentialViewProtocol.ActingRoleHeader, StudioUserRoles.Owner);
        Assert.Equal(StudioUserRoles.Viewer,
            (await viewerClient.GetFromJsonAsync<CredentialViewDto>("/api/v1/management/credential-view"))!.Role);

        // The Studio edge may act for a role it authenticated; no other principal may.
        using var actingEdge = Client(factory, studioToken);
        actingEdge.DefaultRequestHeaders.Add(CredentialViewProtocol.ActingRoleHeader, StudioUserRoles.Operator);
        Assert.Equal(StudioUserRoles.Operator,
            (await actingEdge.GetFromJsonAsync<CredentialViewDto>("/api/v1/management/credential-view"))!.Role);
        using var actingViewerEdge = Client(factory, studioToken);
        actingViewerEdge.DefaultRequestHeaders.Add(CredentialViewProtocol.ActingRoleHeader, StudioUserRoles.Viewer);
        Assert.Equal("operator-role-required", await ErrorCodeAsync(
            await actingViewerEdge.GetAsync("/api/v1/management/credentials"), HttpStatusCode.Forbidden));
        using var bareEdge = Client(factory, studioToken);
        Assert.Equal(StudioUserRoles.Viewer,
            (await bareEdge.GetFromJsonAsync<CredentialViewDto>("/api/v1/management/credential-view"))!.Role);
    }

    // ---- Fixtures ---------------------------------------------------------------------------

    private static CredentialRegistryRecordDto Record(string kind) => new(
        "installation", "host-a", kind, kind.StartsWith("codex", StringComparison.Ordinal) ? "codex" : "claude",
        "runner-provider-owner", "recovery-owner", "generation-a", CredentialRegistryProtocol.RenewalMethods[kind], 1,
        kind, null, ["host:host-a"], [new("coding", "execution", "source:provider-auth")],
        new(kind switch
        {
            "claude_native_login" or "codex_chatgpt_login" => "native-cli-store",
            "github_deploy_key" => "ssh-private-key",
            _ => "environment-file",
        }, "host-local-reference", "active"), "unknown", Reasons(), null, null, null, null, null, "not_verified", [],
        null, null, null, null, null, null, null, null, null);

    private static CredentialRegistryRecordDto Issued(DateTime expiresAt) => Record("claude_oauth_token") with
    {
        ExpiryKnowledge = "issuer",
        ExpiresAt = expiresAt,
        UnknownReasons = Reasons(except: "expiresAt"),
    };

    private static Dictionary<string, string> Reasons(string? except = null)
    {
        var reasons = new Dictionary<string, string>
        {
            ["createdAt"] = "not-observed", ["discoveredAt"] = "not-surveyed",
            ["lastVerifiedAt"] = "not-verified", ["lastRenewedAt"] = "not-observed",
            ["expiresAt"] = "issuer-unknown", ["rotationDueAt"] = "not-set",
            ["accessTokenExpiresAt"] = "not-observed", ["lastRealSuccessAt"] = "not-verified",
            ["nextProbeAt"] = "not-scheduled",
        };
        if (except is not null) reasons.Remove(except);
        return reasons;
    }

    private static CredentialDueDto Due(DateTime? dueAt, int? threshold) =>
        new(dueAt is null ? "unknown" : "known", dueAt is null ? null : "expiry", dueAt, threshold, false,
            dueAt is null, null, null);

    private static async Task<TaskServerStore> StoreAsync(string path, ManualTimeProvider clock)
    {
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = path }), clock);
        await store.InitializeAsync();
        return store;
    }

    private static Task ObserveAsync(TaskServerStore store, ManualTimeProvider clock,
        CredentialRegistryRecordDto record, string? expectedGeneration = null)
        => store.UpsertCredentialRegistryAsync(new(record, "instance-a", expectedGeneration,
            clock.GetUtcNow().UtcDateTime), "test", default);

    private static async Task<IReadOnlyList<CredentialReminderDto>> ReconciledAsync(TaskServerStore store)
    {
        await store.ReconcileCredentialRemindersAsync(default);
        return await store.ListCredentialRemindersAsync(default);
    }

    private static async Task<int> AuditCountAsync(TaskServerStore store, string action)
        => (await store.ListAuditAsync(0, default)).Count(entry => entry.Action == action);

    private static async Task RegisterRunnerAsync(TaskServerStore store, ManualTimeProvider clock,
        string runner, string instance, params string[] capabilities)
    {
        await store.RegisterRunnerAsync(runner, new RegisterRunnerRequest(runner, "host-a", instance, "1.0",
            TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]), runner, default);
        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(runner, instance,
            CapabilityProtocol.CurrentSchemaVersion, clock.GetUtcNow().UtcDateTime, 300, 1,
            [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor"),
             ..capabilities.Select(key => new AdvertisedCapabilityDto(key, "provider-auth"))]), runner, default);
    }

    private static Task AdvertiseAsync(TaskServerStore store, ManualTimeProvider clock,
        string provider, string outcome, long generation)
        => store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            "coding", "coding-instance", 2, clock.GetUtcNow().UtcDateTime, 180, generation,
            [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor"),
             new AdvertisedCapabilityDto(CapabilityProtocol.ProviderAuthentication(provider), "provider-auth", "ready",
                CredentialGeneration: "generation-a", CredentialObservedAt: clock.GetUtcNow().UtcDateTime,
                EffectiveSource: "native-cli-store", HealthOutcome: outcome,
                ServiceAvailability: "unavailable", CredentialHealth: outcome == "credential_invalid" ? "invalid" : "unknown")],
            CredentialHealthVersion: 2), "coding", default);

    private static async Task<string> UserSessionAsync(HttpClient edge, HttpClient owner, string username, string role)
    {
        var password = username + " password 1234";
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/studio/auth/users",
            new StudioCreateUserRequest(username, password, role))).StatusCode);
        var login = await edge.PostAsJsonAsync("/api/v1/studio/auth/login", new StudioLoginRequest(username, password));
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<StudioAuthSessionDto>())!.SessionToken;
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiError>())!.Code;
    }

    private static HttpClient Client(RoleFactory factory, string credential, string? session = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (session is not null) client.DefaultRequestHeaders.Add("X-Studio-Session-Token", session);
        return client;
    }

    private sealed class RoleFactory(string dataDirectory, string studioToken, string engineToken, string ownerCode,
        TimeProvider clock)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services => services.AddSingleton(clock));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TaskServer:RetentionSchedulerEnabled"] = "false",
                    ["AUTH"] = "bearer",
                    ["STUDIO_AUTH_TOKEN"] = studioToken,
                    ["ENGINE_AUTH_TOKEN"] = engineToken,
                    ["OWNER_BOOTSTRAP_CODE"] = ownerCode,
                }));
        }
    }
}
