using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The persona routes under <c>/api/workspaces/{id}/personas</c> in the
/// networked profile (persona team PT-1): activation without a step-up answers
/// <c>403 step-up-required</c>; after <c>POST /api/auth/step-up</c> it records
/// the descriptor revision, digest and shadow state; pause and deactivation
/// need no step-up; every change is in the IAM audit.
/// </summary>
public sealed class PersonaEndpointsTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "persona-endpoints-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));
    private string _ownerTotpSecret = string.Empty;

    [Fact]
    public async Task Activation_needs_a_step_up_and_pause_and_deactivation_do_not()
    {
        using var factory = BuildFactory();
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var workspaceId = factory.Services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock).Id;
        using var owner = await OwnerClientAsync(factory);
        var auditStart = security.ControlPlaneAudit.HeadSequence;
        var personas = $"/api/workspaces/{workspaceId}/personas";

        using (var refused = await owner.PostAsJsonAsync($"{personas}/operations-warden/activate", new { }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("step-up-required", (await Body(refused)).GetProperty("error").GetString());
        }

        await ReadOk(owner.PostAsJsonAsync("/api/auth/step-up", new { code = CurrentCode() }));
        var activation = await ReadOk(owner.PostAsJsonAsync($"{personas}/operations-warden/activate",
            new { allowance = new { tokensPerWeek = 120_000 }, reason = "Start in shadow" }));
        Assert.Equal(("shadow", "2026.11.1", 120_000), (activation.GetProperty("state").GetString(),
            activation.GetProperty("descriptorRevision").GetString(), activation.GetProperty("allowance").GetProperty("tokensPerWeek").GetInt32()));
        Assert.StartsWith("sha256:", activation.GetProperty("descriptorDigest").GetString());

        Assert.Equal("paused", (await ReadOk(owner.PostAsJsonAsync($"{personas}/operations-warden/pause", new { })))
            .GetProperty("state").GetString());
        Assert.Equal("inactive", (await ReadOk(owner.PostAsJsonAsync($"{personas}/operations-warden/deactivate", new { reason = "Retune" })))
            .GetProperty("state").GetString());

        var list = await ReadOk(owner.GetAsync(personas));
        Assert.Equal(workspaceId, list.GetProperty("workspaceId").GetString());
        var entries = list.GetProperty("personas").EnumerateArray().ToList();
        Assert.Equal(11, entries.Count);
        var warden = entries.Single(entry => entry.GetProperty("id").GetString() == "operations-warden");
        Assert.Equal("inactive", warden.GetProperty("activation").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null,
            entries.Single(entry => entry.GetProperty("id").GetString() == "security-officer").GetProperty("activation").ValueKind);

        using (var unknown = await owner.PostAsJsonAsync($"/api/workspaces/elsewhere/personas/operations-warden/pause", new { }))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(["persona.activate", "persona.pause", "persona.deactivate"],
            security.ControlPlaneAudit.Read(auditStart + 1, 100)
                .Where(entry => entry.Target == $"persona:{workspaceId}/operations-warden").Select(entry => entry.Operation));
    }

    [Fact]
    public async Task Persona_sessions_use_their_own_grants_instruction_sources_and_team_stop()
    {
        using var factory = BuildFactory();
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var workspaceId = factory.Services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock).Id;
        using var owner = await OwnerClientAsync(factory);
        var basePath = $"/api/workspaces/{workspaceId}/personas";
        await ReadOk(owner.PostAsJsonAsync("/api/auth/step-up", new { code = CurrentCode() }));
        await ReadOk(owner.PostAsJsonAsync($"{basePath}/operations-warden/activate", new { }));
        var warden = security.FindPersonaPrincipal(workspaceId, "operations-warden")!;
        Assert.StartsWith("opa_persona_", warden.Id);
        Assert.Equal(("persona", "operations-warden", "operations-warden"),
            (warden.Origin, warden.PersonaId, warden.PersonaRoleId));
        var grant = Assert.Single(security.ListStandingGrants(warden.Id));
        Assert.Equal(OperatorTier.T1, grant.Tier);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddDays(7), grant.ExpiresAt);
        Assert.DoesNotContain(OperatorOperationIds.CardRequeue, grant.Operations);
        var session = security.MintPersonaOccurrenceSession(workspaceId, "operations-warden", "occ-warden");
        Assert.Equal("occ-warden", session.Session.OccurrenceId);
        Assert.Equal(TimeSpan.FromMinutes(30), session.Session.ExpiresAt - session.Session.CreatedAt);
        Assert.Equal($"persona:operations-warden/session:{session.Session.Id}",
            security.AuthenticateOperatorSession(session.Secret)!.ActorId);
        Assert.Equal("invalid-occurrence", Assert.Throws<SecurityOperationException>(() =>
            security.MintPersonaOccurrenceSession(workspaceId, "operations-warden", "too-long", 31)).Code);
        using var persona = BearerClient(factory, session.Secret, "occ-warden");
        using (var wrongOccurrence = BearerClient(factory, session.Secret, "another-occurrence"))
        using (var refused = await wrongOccurrence.GetAsync(basePath))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("occurrence-required", (await Body(refused)).GetProperty("error").GetString());
        }

        using (var requeue = await persona.PostAsJsonAsync("/api/tasks/alpha-card/move", new { targetState = TaskStates.Preparation }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, requeue.StatusCode);
            Assert.Equal("grant-required", (await Body(requeue)).GetProperty("error").GetString());
        }
        using (var publish = await PostWithSource(persona,
            $"{basePath}/operations-warden/operations/persona.item.publish", "repository-file"))
            Assert.Equal(HttpStatusCode.NotImplemented, publish.StatusCode);
        var publication = Assert.Single(security.ControlPlaneAudit.ReadNewest(entry => entry.Actor ==
            $"persona:operations-warden/session:{session.Session.Id}" && entry.Operation.EndsWith("/persona.item.publish", StringComparison.Ordinal), 5));
        Assert.Equal(grant.Id, publication.Details!.Value.GetProperty("grantId").GetString());

        await ReadOk(owner.PostAsJsonAsync("/api/auth/step-up", new { code = CurrentCode() }));
        await ReadOk(owner.PostAsJsonAsync($"{basePath}/chief-of-staff/activate", new { }));
        var managerSession = security.MintPersonaOccurrenceSession(workspaceId, "chief-of-staff", "occ-manager");
        Assert.Contains(OperatorOperationIds.PersonaProposalAccept,
            Assert.Single(security.ListStandingGrants(managerSession.Principal.Id)).Operations);
        using var manager = BearerClient(factory, managerSession.Secret, "occ-manager");
        using (var allowed = await PostWithSource(manager,
            $"{basePath}/chief-of-staff/operations/persona.proposal.accept", "owner-chat"))
        {
            Assert.Equal(HttpStatusCode.Conflict, allowed.StatusCode);
            Assert.Equal("manager-not-active", (await Body(allowed)).GetProperty("refusal").GetString());
        }
        using (var refused = await PostWithSource(manager,
            $"{basePath}/chief-of-staff/operations/persona.proposal.accept", "repository-file"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("instruction-source-denied", (await Body(refused)).GetProperty("error").GetString());
        }


        using (var renewal = await owner.PostAsJsonAsync($"{basePath}/renew-grants", new { }))
            Assert.Equal("step-up-required", (await Body(renewal)).GetProperty("error").GetString());
        await ReadOk(owner.PostAsJsonAsync("/api/auth/step-up", new { code = CurrentCode() }));
        var renewed = await ReadOk(owner.PostAsJsonAsync($"{basePath}/renew-grants", new { }));
        Assert.Equal(2, renewed.GetProperty("grants").GetArrayLength());

        var stopped = await ReadOk(owner.PostAsJsonAsync($"{basePath}/stop-all", new { }));
        Assert.Equal(2, stopped.GetProperty("principalIds").GetArrayLength());
        using (var after = await persona.GetAsync(basePath)) Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        using (var after = await manager.GetAsync(basePath)) Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.All(security.ListStandingGrants(warden.Id), item => Assert.NotNull(item.RevokedAt));
        var team = await ReadOk(owner.GetAsync(basePath));
        Assert.All(team.GetProperty("personas").EnumerateArray().Where(entry => entry.GetProperty("activation").ValueKind != JsonValueKind.Null),
            entry => Assert.Equal("inactive", entry.GetProperty("activation").GetProperty("state").GetString()));
        Assert.Contains(security.ControlPlaneAudit.ReadNewest(entry => entry.Operation == "persona.stop-all", 10),
            entry => entry.Target == $"persona:{workspaceId}");
        Assert.True(security.ControlPlaneAudit.VerifyChain().Ok);
    }

    [Fact]
    public async Task Active_manager_refuses_importance_bounds_with_receipts_and_owner_can_undo()
    {
        using var factory = BuildFactory();
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var workspaceId = factory.Services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock).Id;
        var basePath = $"/api/workspaces/{workspaceId}/personas";
        using (var setupOwner = await OwnerClientAsync(factory))
        {
            await ReadOk(setupOwner.PostAsJsonAsync("/api/auth/step-up", new { code = CurrentCode() }));
            await ReadOk(setupOwner.PostAsJsonAsync($"{basePath}/chief-of-staff/activate", new { }));
        }
        _clock.Advance(TimeSpan.FromDays(2));
        var session = security.MintPersonaOccurrenceSession(workspaceId, "chief-of-staff", "occ-manager-active");
        using var manager = BearerClient(factory, session.Secret, "occ-manager-active");
        var items = factory.Services.GetRequiredService<AgentStudio.Personas.PersonaItemStore>();
        var item = items.Upsert(workspaceId, "security-a", "security", "occ-item",
            new AgentStudio.Personas.PersonaItemDraftV1(
                AgentStudio.Personas.PersonaItemKinds.Finding, "Floor finding", "Untrusted item body",
                null, [new AgentStudio.Personas.PersonaEvidenceV1("fact:one")],
                1, 1, 0.5, "fingerprint"), 95, _clock.GetUtcNow().UtcDateTime);
        var path = $"{basePath}/chief-of-staff/operations/persona.importance.adjust";
        using (var large = await PostWithSource(manager, path, "owner-chat",
            new { itemId = item.Id, adjustment = -16, reason = "Review" }))
            Assert.Equal("adjustment-out-of-bounds", (await Body(large)).GetProperty("refusal").GetString());
        using (var floor = await PostWithSource(manager, path, "owner-chat",
            new { itemId = item.Id, adjustment = -1, reason = "Review" }))
            Assert.Equal("importance-floor-or-ceiling", (await Body(floor)).GetProperty("refusal").GetString());
        var adjusted = await ReadOk(PostWithSource(manager, path, "owner-chat",
            new { itemId = item.Id, adjustment = 5, reason = "Review" }));
        Assert.Equal(100, items.Find(workspaceId, item.Id)!.Importance);
        using var owner = await OwnerClientAsync(factory);
        var undo = await ReadOk(owner.PostAsJsonAsync(
            $"{basePath}/manager-actions/{adjusted.GetProperty("id").GetString()}/undo", new { }));
        Assert.True(undo.GetProperty("calibrationReversal").GetBoolean());
        Assert.Equal(95, items.Find(workspaceId, item.Id)!.Importance);
    }

    [Fact]
    public async Task Manager_lane_write_needs_attention_and_revision_and_owner_undo_restores_lane()
    {
        using var factory = BuildFactory("persona-proposal:pit_one");
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var workspaceId = factory.Services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock).Id;
        var basePath = $"/api/workspaces/{workspaceId}/personas";
        using (var setupOwner = await OwnerClientAsync(factory))
        {
            await ReadOk(setupOwner.PostAsJsonAsync("/api/auth/step-up", new { code = CurrentCode() }));
            await ReadOk(setupOwner.PostAsJsonAsync($"{basePath}/chief-of-staff/activate", new { }));
        }
        _clock.Advance(TimeSpan.FromDays(2));
        var session = security.MintPersonaOccurrenceSession(workspaceId, "chief-of-staff", "occ-lane");
        using var manager = BearerClient(factory, session.Secret, "occ-lane");
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var card = scanner.FindJob("alpha-card")!;
        var attention = factory.Services.GetRequiredService<AgentStudio.Operator.OperatorCoordinationStore>();
        var revision = AgentStudio.Operator.OperatorCoordinationStore.Revision(card.FolderPath);
        var path = $"{basePath}/chief-of-staff/operations/card.requeue";
        Assert.NotNull(attention.Acquire(card.TaskKey, "human:owner", true));
        using (var held = await PostWithSource(manager, path, "owner-chat",
            new { cardId = card.Id, targetState = TaskStates.Ready, revision, reason = "Queue proposal" }))
            Assert.Equal("attention-held", (await Body(held)).GetProperty("refusal").GetString());
        await attention.OwnerActionAsync(card.TaskKey, card.ProjectName, "human:owner-takeover");
        using (var stale = await PostWithSource(manager, path, "owner-chat",
            new { cardId = card.Id, targetState = TaskStates.Ready, revision = "\"stale\"", reason = "Queue proposal" }))
            Assert.Equal("stale-card-revision", (await Body(stale)).GetProperty("refusal").GetString());
        var moved = await ReadOk(PostWithSource(manager, path, "owner-chat",
            new { cardId = card.Id, targetState = TaskStates.Ready, revision, reason = "Queue proposal" }));
        Assert.Equal(TaskStates.Ready, scanner.FindJob(card.Id)!.State);
        using var owner = await OwnerClientAsync(factory);
        var undo = await ReadOk(owner.PostAsJsonAsync(
            $"{basePath}/manager-actions/{moved.GetProperty("id").GetString()}/undo", new { }));
        var restored = scanner.FindJob(card.Id)!;
        Assert.Equal(TaskStates.Backlog, restored.State);
        Assert.Equal(AgentStudio.Operator.OperatorCoordinationStore.Revision(restored.FolderPath),
            undo.GetProperty("nextRevision").GetString());
        Assert.True(undo.GetProperty("calibrationReversal").GetBoolean());
    }

    [Fact]
    public async Task Budget_steward_refuses_forbidden_targets_and_open_warden_floor_with_receipts()
    {
        using var factory = BuildFactory();
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var workspaceId = factory.Services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock).Id;
        var basePath = $"/api/workspaces/{workspaceId}/personas";
        using (var setupOwner = await OwnerClientAsync(factory))
        {
            foreach (var persona in new[] { "budget-steward", "chief-of-staff", "operations-warden", "security-officer" })
            {
                await ReadOk(setupOwner.PostAsJsonAsync("/api/auth/step-up", new { code = CurrentCode() }));
                await ReadOk(setupOwner.PostAsJsonAsync($"{basePath}/{persona}/activate", new { }));
            }
        }
        // The steward reports weekly, so its shadow period ends after seven days; renew the grants then.
        _clock.Advance(TimeSpan.FromDays(8));
        using (var renewOwner = await OwnerClientAsync(factory))
        {
            await ReadOk(renewOwner.PostAsJsonAsync("/api/auth/step-up", new { code = CurrentCode() }));
            await ReadOk(renewOwner.PostAsJsonAsync($"{basePath}/renew-grants", new { }));
        }
        var session = security.MintPersonaOccurrenceSession(workspaceId, "budget-steward", "occ-steward");
        using var steward = BearerClient(factory, session.Secret, "occ-steward");
        var rebalance = $"{basePath}/budget-steward/operations/persona.budget.rebalance";
        var pause = $"{basePath}/budget-steward/operations/persona.pause";

        async Task<string> Refusal(string path, object body)
        {
            using var response = await PostWithSource(steward, path, "owner-chat", body);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var receipt = await Body(response);
            Assert.Equal("refused", receipt.GetProperty("outcome").GetString());
            return receipt.GetProperty("refusal").GetString()!;
        }

        // The steward never raises its own allowance or the chief of staff's.
        Assert.Equal("budget-target-forbidden", await Refusal(rebalance, new
        {
            fromPersonaId = "security-officer", toPersonaId = "budget-steward", tokens = 1000, reason = "Raise own",
        }));
        Assert.Equal("budget-target-forbidden", await Refusal(rebalance, new
        {
            fromPersonaId = "security-officer", toPersonaId = "chief-of-staff", tokens = 1000, reason = "Raise chief",
        }));
        // It never pauses itself or the chief of staff.
        Assert.Equal("pause-target-forbidden", await Refusal(pause,
            new { toPersonaId = "budget-steward", reason = "Pause self" }));
        Assert.Equal("pause-target-forbidden", await Refusal(pause,
            new { toPersonaId = "chief-of-staff", reason = "Pause chief" }));
        // The warden keeps running while a floor item of 90 or more is open.
        factory.Services.GetRequiredService<AgentStudio.Personas.PersonaItemStore>().Upsert(workspaceId,
            "security-officer", "security-officer", "occ-floor",
            new AgentStudio.Personas.PersonaItemDraftV1(AgentStudio.Personas.PersonaItemKinds.Finding,
                "Floor finding", "Body", null, [new AgentStudio.Personas.PersonaEvidenceV1("fact:one")],
                1, 1, 0.5, "floor-fingerprint"), 90, _clock.GetUtcNow().UtcDateTime);
        Assert.Equal("warden-floor-open", await Refusal(pause,
            new { toPersonaId = "operations-warden", reason = "Pause warden" }));

        var receipts = factory.Services.GetRequiredService<AgentStudio.Personas.PersonaManagerService>()
            .List(workspaceId);
        Assert.Equal(["budget-target-forbidden", "budget-target-forbidden"], receipts
            .Where(r => r.Operation == "persona.budget.rebalance").Select(r => r.Refusal));
        Assert.Equal(["pause-target-forbidden", "pause-target-forbidden", "warden-floor-open"], receipts
            .Where(r => r.Operation == "persona.pause").Select(r => r.Refusal));
        Assert.All(receipts, r => Assert.Equal("refused", r.Outcome));
    }

    [Fact]
    public async Task End_all_operator_access_also_ends_persona_sessions()
    {
        using var factory = BuildFactory();
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var workspaceId = factory.Services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock).Id;
        using var owner = await OwnerClientAsync(factory);
        var basePath = $"/api/workspaces/{workspaceId}/personas";
        await ReadOk(owner.PostAsJsonAsync("/api/auth/step-up", new { code = CurrentCode() }));
        await ReadOk(owner.PostAsJsonAsync($"{basePath}/operations-warden/activate", new { }));
        var session = security.MintPersonaOccurrenceSession(workspaceId, "operations-warden", "occ-kill");
        using var persona = BearerClient(factory, session.Secret, "occ-kill");
        using (var before = await PostWithSource(persona,
            $"{basePath}/operations-warden/operations/persona.item.publish", "card"))
            Assert.Equal(HttpStatusCode.NotImplemented, before.StatusCode);
        var killed = await ReadOk(owner.PostAsJsonAsync("/api/auth/iam/operator-kill-switch/end-all", new { }));
        Assert.Contains(killed.GetProperty("operatorAgentIds").EnumerateArray(), id => id.GetString() == session.Principal.Id);
        using (var after = await persona.GetAsync(basePath)) Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.All(security.ListStandingGrants(session.Principal.Id), grant => Assert.NotNull(grant.RevokedAt));
    }

    [Fact]
    public async Task Run_now_queues_a_manual_run_trigger_and_decisions_use_the_proposal_lifecycle()
    {
        using var factory = BuildFactory();
        var workspaceId = factory.Services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock).Id;
        var sweeps = factory.Services.GetRequiredService<AgentStudio.Personas.PersonaSweepCoordinator>();
        using var owner = await OwnerClientAsync(factory);
        var basePath = $"/api/workspaces/{workspaceId}/personas";

        using (var inactive = await owner.PostAsJsonAsync($"{basePath}/operations-warden/run-now", new { }))
        {
            Assert.Equal(HttpStatusCode.Conflict, inactive.StatusCode);
            Assert.Equal("persona-not-running", (await Body(inactive)).GetProperty("error").GetString());
        }
        Assert.Null(sweeps.PendingManualRun(workspaceId, "operations-warden"));
        await ReadOk(owner.PostAsJsonAsync("/api/auth/step-up", new { code = CurrentCode() }));
        await ReadOk(owner.PostAsJsonAsync($"{basePath}/operations-warden/activate", new { }));
        using (var accepted = await owner.PostAsJsonAsync($"{basePath}/operations-warden/run-now", new { }))
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var trigger = sweeps.PendingManualRun(workspaceId, "operations-warden")!;
        Assert.Equal(AgentStudio.Personas.PersonaSchedulingService.ManualRunTrigger, trigger.Type);
        Assert.StartsWith("run-now:", trigger.Ref);

        var items = factory.Services.GetRequiredService<AgentStudio.Personas.PersonaItemStore>();
        var finding = items.Upsert(workspaceId, "operations-warden", "operations-warden", "occ-1",
            new AgentStudio.Personas.PersonaItemDraftV1(AgentStudio.Personas.PersonaItemKinds.Finding, "Backup gap",
                "No off-host backup.", null, [new AgentStudio.Personas.PersonaEvidenceV1("case:one")], 5, 5, 0.9, "backup"),
            95, _clock.GetUtcNow().UtcDateTime);
        using (var invalid = await owner.PostAsJsonAsync($"{basePath}/items/{finding.Id}/decision", new { outcome = "accepted" }))
            Assert.Equal("invalid-persona-item-decision", (await Body(invalid)).GetProperty("error").GetString());
        using (var missing = await owner.PostAsJsonAsync($"{basePath}/items/pit_missing/decision", new { outcome = "resolved" }))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("resolved", (await ReadOk(owner.PostAsJsonAsync($"{basePath}/items/{finding.Id}/decision",
            new { outcome = "resolved" }))).GetProperty("state").GetString());
        using (var again = await owner.PostAsJsonAsync($"{basePath}/items/{finding.Id}/decision", new { outcome = "resolved" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("persona-item-already-decided", (await Body(again)).GetProperty("error").GetString());
        }
    }

    private static HttpClient BearerClient(WebApplicationFactory<Program> factory, string token, string occurrenceId)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Persona-Occurrence-Id", occurrenceId);
        return client;
    }

    private static Task<HttpResponseMessage> PostWithSource(HttpClient client, string path, string source,
        object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        request.Headers.Add("X-Operator-Instruction-Source", source);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadOk(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private string CurrentCode()
    {
        _clock.Advance(TimeSpan.FromSeconds(30));
        return SecondFactorCodes.CurrentTotp(_ownerTotpSecret, _clock.GetUtcNow().UtcDateTime);
    }

    private async Task<HttpClient> OwnerClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = true });
        client.DefaultRequestHeaders.Add("Origin", "https://studio.test");
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "first.owner", password = Password, secondFactorCode = CurrentCode(),
        });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("X-CSRF-Token", (await response.Content.ReadFromJsonAsync<AuthStatusResponse>())!.CsrfToken!);
        return client;
    }

    private WebApplicationFactory<Program> BuildFactory(string creationSource = "human")
    {
        Directory.CreateDirectory(_workspace);
        var projectPath = Path.Combine(_workspace, "projects", "alpha");
        foreach (var lane in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(projectPath, lane));
        var cardPath = Path.Combine(projectPath, TaskStates.Backlog, "alpha-card");
        Directory.CreateDirectory(cardPath);
        File.WriteAllText(Path.Combine(cardPath, "task.json"), JsonSerializer.Serialize(new
        {
            id = "alpha-card", title = "Alpha card", state = TaskStates.Backlog, order = 1,
            agent = "codex", kind = "task", creationSource,
        }));
        File.WriteAllText(Path.Combine(cardPath, "prompt.md"), "Fixture card.");
        var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = "networked",
                ["AllowedHosts"] = "studio.test",
                ["DeliveryChain:Guarded"] = "false",
                ["WatchPaths:0:Name"] = "alpha",
                ["WatchPaths:0:Path"] = projectPath,
                ["WatchPaths:0:RootPath"] = projectPath,
                ["Secrets:KeyFile"] = Path.Combine(_workspace + "-keys", "secrets.kek"),
                ["Secrets:KeyEnvironmentVariable"] = "AGENT_STUDIO_TEST_UNSET_KEK_" + Guid.NewGuid().ToString("N"),
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
        });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var bootstrap = store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        var owner = store.AuthenticateSession(bootstrap.SessionToken)!;
        _ownerTotpSecret = store.BeginTotpEnrollment(owner);
        store.ConfirmTotpEnrollment(owner, SecondFactorCodes.CurrentTotp(_ownerTotpSecret, _clock.GetUtcNow().UtcDateTime));
        return factory;
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
        if (Directory.Exists(_workspace + "-keys")) Directory.Delete(_workspace + "-keys", true);
    }
}
