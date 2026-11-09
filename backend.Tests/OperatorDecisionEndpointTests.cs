using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Operator;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The operator decision stream and watcher lease on the real networked host
/// (operator agent level OP-2): project scope of tokens, lossless resume by
/// cursor over Server-Sent Events, and the one watcher lease with an audited
/// take-over.
/// </summary>
public sealed class OperatorDecisionEndpointTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "operator-decisions-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_project_scoped_token_receives_only_events_of_its_projects()
    {
        using var factory = BuildFactory();
        var alphaToken = Token(factory, "alpha watcher", StudioRoles.Operator, "alpha");
        var betaToken = Token(factory, "beta reader", StudioRoles.Viewer, "beta");
        factory.Services.GetRequiredService<ProviderLimitRegistry>().Record(new ProviderLimitStatus(
            "claude", Now(factory), Now(factory).AddHours(1), "rate limited", true));
        using var alpha = BearerClient(factory, alphaToken);
        using var beta = BearerClient(factory, betaToken);
        using var owner = Client(factory, Login(factory));
        Assert.Equal(HttpStatusCode.OK, (await alpha.PostAsJsonAsync("/api/operator/watcher/lease",
            new WatcherLeaseRequest("alpha-laptop"))).StatusCode);
        await Sweep(factory);

        var all = await Snapshot(owner, "0");
        var alphaEvents = await Snapshot(alpha, "0");
        var betaEvents = await Snapshot(beta, "0");

        Assert.Contains(all, item => item.Project == "alpha" && item.Type == OperatorDecisionTypes.EscalationOpened);
        Assert.Contains(all, item => item.Project == "beta" && item.Type == OperatorDecisionTypes.EscalationOpened);
        Assert.Contains(all, item => item.Project is null && item.Type == OperatorDecisionTypes.QuotaExhausted);
        Assert.All(alphaEvents, item => Assert.True(item.Project == "alpha" || item.Type == OperatorDecisionTypes.WatcherHeartbeat));
        Assert.All(betaEvents, item => Assert.True(item.Project == "beta" || item.Type == OperatorDecisionTypes.WatcherHeartbeat));
        Assert.Contains(alphaEvents, item => item.CardKey == "alpha-escalated");
        Assert.Contains(betaEvents, item => item.CardKey == "beta-escalated");
        Assert.Contains(betaEvents, item => item.Type == OperatorDecisionTypes.WatcherHeartbeat);

        var streamed = await ReadStream(alpha, "/api/operator/decisions/stream?after=0", alphaEvents.Count);
        Assert.Equal(alphaEvents.Select(item => item.Cursor), streamed.Select(frame => frame.Id));
        Assert.Equal(alphaEvents.Select(item => item.Type), streamed.Select(frame => frame.Event));
    }

    [Fact]
    public async Task The_attention_list_shows_the_current_facts_in_the_readers_scope_and_counts_its_rows()
    {
        using var factory = BuildFactory();
        factory.Services.GetRequiredService<ProviderLimitRegistry>().Record(new ProviderLimitStatus(
            "claude", Now(factory), Now(factory).AddHours(1), "rate limited", true));
        using var alpha = BearerClient(factory, Token(factory, "alpha reader", StudioRoles.Viewer, "alpha"));
        using var owner = Client(factory, Login(factory));
        // The hosted loop's startup read can predate the provider limit and
        // stays fresh for one sweep interval; a sweep reads the sources again.
        await Sweep(factory);

        var all = await Attention(owner);
        var scoped = await Attention(alpha);

        Assert.Equal(all.Items.Count, all.Count);
        Assert.Equal(scoped.Items.Count, scoped.Count);
        Assert.Contains(all.Items, item => item.CardKey == "alpha-escalated" && item.Type == OperatorDecisionTypes.EscalationOpened);
        Assert.Contains(all.Items, item => item.CardKey == "beta-escalated");
        Assert.Contains(all.Items, item => item.Project is null && item.Type == OperatorDecisionTypes.QuotaExhausted);
        Assert.All(scoped.Items, item => Assert.Equal("alpha", item.Project));
        Assert.Contains(scoped.Items, item => item.CardKey == "alpha-escalated"
            && item.SuggestedOperations.Contains(OperatorOperations.CardInspect));

        await factory.Services.GetRequiredService<TaskTransitionService>().MoveAsync("alpha-backlog",
            TaskStates.HumanReview, ProjectPath("alpha"), cause: "test", reason: "ready for review");
        await WaitUntilAttention(factory, owner, list => list.Items.Any(item =>
            item.CardKey == "alpha-backlog" && item.Type == OperatorDecisionTypes.HumanReviewReady));
    }

    [Fact]
    public async Task A_client_resuming_with_its_last_cursor_receives_every_event_in_between_once()
    {
        using var factory = BuildFactory();
        using var owner = Client(factory, Login(factory));
        await Sweep(factory);
        var before = await Snapshot(owner, "0");
        Assert.True(before.Count >= 2);
        var received = await ReadStream(owner, "/api/operator/decisions/stream?after=0", before.Count);
        var last = received[^1].Id!;

        // While the client is away, cards move and the publisher records the facts.
        var transitions = factory.Services.GetRequiredService<TaskTransitionService>();
        await transitions.MoveAsync("alpha-backlog", TaskStates.HumanReview, ProjectPath("alpha"),
            cause: "test", reason: "ready for review");
        await transitions.MoveAsync("beta-backlog", TaskStates.Escalated, ProjectPath("beta"),
            cause: "test", reason: "needs a decision");
        await WaitForEvents(factory, owner, last, item => item.CardKey is "alpha-backlog" or "beta-backlog", 2);
        var missed = await Snapshot(owner, last);

        var resumed = await ReadStream(owner, "/api/operator/decisions/stream", missed.Count, lastEventId: last);

        Assert.Equal(missed.Select(item => item.Cursor), resumed.Select(frame => frame.Id));
        Assert.Empty(resumed.Select(frame => frame.Id).Intersect(received.Select(frame => frame.Id)));
        Assert.Equal(resumed.Count, resumed.Select(frame => frame.Id).Distinct().Count());
        Assert.Contains(resumed, frame => frame.Event == OperatorDecisionTypes.HumanReviewReady
            && frame.Data.GetProperty("cardKey").GetString() == "alpha-backlog");
        Assert.Contains(resumed, frame => frame.Event == OperatorDecisionTypes.EscalationOpened
            && frame.Data.GetProperty("cardKey").GetString() == "beta-backlog");
        Assert.All(resumed, frame => Assert.NotEmpty(frame.Data.GetProperty("suggestedOperations").EnumerateArray()));
    }

    [Fact]
    public async Task A_second_watcher_gets_409_naming_the_holder_and_a_take_over_is_audited()
    {
        using var factory = BuildFactory();
        using var alpha = BearerClient(factory, Token(factory, "alpha watcher", StudioRoles.Operator, "alpha"));
        using var beta = BearerClient(factory, Token(factory, "beta watcher", StudioRoles.Operator, "beta"));
        using var viewer = BearerClient(factory, Token(factory, "viewer", StudioRoles.Viewer, "alpha"));

        var granted = await Json(await alpha.PostAsJsonAsync("/api/operator/watcher/lease", new WatcherLeaseRequest("laptop-1")));
        var holder = granted.GetProperty("lease").GetProperty("holder").GetString()!;
        var leaseId = granted.GetProperty("lease").GetProperty("leaseId").GetString()!;
        Assert.StartsWith("service-account:", holder);

        using var second = await beta.PostAsJsonAsync("/api/operator/watcher/lease", new WatcherLeaseRequest("laptop-2"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var conflict = await Json(second);
        Assert.Equal("watcher-active", conflict.GetProperty("error").GetString());
        Assert.Equal(holder, conflict.GetProperty("holder").GetString());
        Assert.Equal("laptop-1", conflict.GetProperty("session").GetString());
        Assert.Equal(JsonValueKind.String, conflict.GetProperty("lastHeartbeatUtc").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/operator/watcher/lease",
            new WatcherLeaseRequest("viewer", TakeOver: true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alpha.PostAsync($"/api/operator/watcher/lease/{leaseId}/heartbeat", null)).StatusCode);

        var takeOver = await Json(await beta.PostAsJsonAsync("/api/operator/watcher/lease",
            new WatcherLeaseRequest("laptop-2", TakeOver: true, Reason: "laptop-1 went to sleep")));
        var newHolder = takeOver.GetProperty("lease").GetProperty("holder").GetString()!;
        Assert.Equal("taken-over", takeOver.GetProperty("outcome").GetString());
        Assert.Equal(holder, takeOver.GetProperty("previous").GetProperty("holder").GetString());

        var audit = factory.Services.GetRequiredService<ControlPlaneAuditLog>()
            .ReadNewest(entry => entry.Operation == "watcher.lease.take-over", 10);
        var entry = Assert.Single(audit);
        Assert.Equal(ControlPlaneAuditStreams.Operator, entry.Stream);
        Assert.Equal(newHolder, entry.Actor);
        Assert.Equal(holder, entry.Target);
        Assert.Equal("laptop-1 went to sleep", entry.Details!.Value.GetProperty("reason").GetString());
        Assert.Equal(leaseId, entry.Details!.Value.GetProperty("previousLeaseId").GetString());

        using var lost = await alpha.PostAsync($"/api/operator/watcher/lease/{leaseId}/heartbeat", null);
        Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
        Assert.Equal(newHolder, (await Json(lost)).GetProperty("holder").GetString());

        var newLease = takeOver.GetProperty("lease").GetProperty("leaseId").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await beta.DeleteAsync($"/api/operator/watcher/lease/{newLease}")).StatusCode);
        Assert.False((await Json(await alpha.GetAsync("/api/operator/watcher/lease"))).GetProperty("active").GetBoolean());
        Assert.Contains(await Snapshot(alpha, "0"), item => item.Type == OperatorDecisionTypes.WatcherHeartbeat
            && item.Reason.Contains("taken over", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invalid_cursor_is_refused()
    {
        using var factory = BuildFactory();
        using var owner = Client(factory, Login(factory));
        using var response = await owner.GetAsync("/api/operator/decisions?after=not-a-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Operator_log_redacts_token_shaped_text_before_storage()
    {
        using var factory = BuildFactory();
        using var owner = Client(factory, Login(factory));
        var secretPart = new string('b', 32);
        var secret = "api." + new string('a', 32) + "." + secretPart;
        using var posted = await owner.PostAsJsonAsync("/api/operator/log", new
        {
            project = "alpha", kind = "observed", text = "Found " + secret,
            cardKeys = new[] { "alpha-backlog" }, auditIds = Array.Empty<string>()
        });
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        using var read = await owner.GetAsync("/api/operator/log?project=alpha");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var body = await read.Content.ReadAsStringAsync();
        Assert.Contains("[redacted]", body);
        Assert.DoesNotContain(secret, body);
        Assert.DoesNotContain(secretPart, body);
        using var scoped = BearerClient(factory, Token(factory, "alpha log reader", StudioRoles.Viewer, "alpha"));
        using var forbidden = await scoped.GetAsync("/api/operator/log?project=beta");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Operator_move_requires_attention_and_current_card_revision()
    {
        using var factory = BuildFactory();
        using var agent = BearerClient(factory, Token(factory, "card operator", StudioRoles.Operator, "alpha"));
        var path = "/api/tasks/alpha-backlog/move?project=alpha";
        using var noLease = await agent.PostAsJsonAsync(path,
            new MoveJobRequest { TargetState = TaskStates.Preparation });
        Assert.Equal(HttpStatusCode.Conflict, noLease.StatusCode);
        Assert.Equal("attention-lease-required", (await Json(noLease)).GetProperty("error").GetString());

        var attention = await Json(await agent.PostAsync("/api/tasks/alpha-backlog/attention?project=alpha", null));
        var revision = attention.GetProperty("revision").GetString()!;
        using var stale = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new MoveJobRequest { TargetState = TaskStates.Preparation })
        };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"stale\"");
        using var refused = await agent.SendAsync(stale);
        Assert.Equal(HttpStatusCode.PreconditionFailed, refused.StatusCode);

        using var current = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new MoveJobRequest { TargetState = TaskStates.Preparation })
        };
        current.Headers.TryAddWithoutValidation("If-Match", revision);
        using var moved = await agent.SendAsync(current);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
    }

    [Fact]
    public async Task Operator_batch_move_cannot_bypass_card_attention()
    {
        using var factory = BuildFactory();
        using var agent = BearerClient(factory, Token(factory, "batch operator", StudioRoles.Operator, "alpha"));
        using var response = await agent.PostAsJsonAsync("/api/tasks/batch-move", new BatchMoveRequest
        {
            Items = [new BatchMoveItem { JobId = "alpha-backlog", TargetState = TaskStates.Preparation }]
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("attention-lease-required", (await Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Owner_move_ends_operator_attention_and_records_handoff()
    {
        using var factory = BuildFactory();
        using var agent = BearerClient(factory, Token(factory, "card operator", StudioRoles.Operator, "alpha"));
        using var owner = Client(factory, Login(factory));
        var attention = await Json(await agent.PostAsync("/api/tasks/alpha-backlog/attention?project=alpha", null));
        var holder = attention.GetProperty("lease").GetProperty("holder").GetString();
        using var moved = await owner.PostAsJsonAsync("/api/tasks/alpha-backlog/move?project=alpha",
            new MoveJobRequest { TargetState = TaskStates.Preparation });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        using var read = await owner.GetAsync("/api/operator/log?project=alpha");
        var log = await Json(read);
        Assert.Contains(log.GetProperty("entries").EnumerateArray(), entry =>
            entry.GetProperty("text").GetString()!.Contains(holder!, StringComparison.Ordinal));
        using var detail = await owner.GetAsync("/api/tasks/alpha-backlog?project=alpha");
        var card = await Json(detail);
        Assert.True(!card.GetProperty("info").TryGetProperty("attentionHolder", out var holderAfter)
            || holderAfter.ValueKind == JsonValueKind.Null);
        using var ownerAttention = await owner.PostAsync("/api/tasks/alpha-backlog/attention?project=alpha", null);
        Assert.Equal(HttpStatusCode.OK, ownerAttention.StatusCode);
        using var refused = await agent.PostAsync("/api/tasks/alpha-backlog/attention?project=alpha", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    private static async Task Sweep(WebApplicationFactory<Program> factory)
        => await factory.Services.GetRequiredService<OperatorDecisionPublisher>().SweepOnceAsync();

    private static async Task WaitForEvents(WebApplicationFactory<Program> factory, HttpClient client, string after,
        Func<OperatorDecisionEvent, bool> match, int count)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await Sweep(factory);
            if ((await Snapshot(client, after)).Count(match) >= count) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Expected {count} matching events after cursor {after}.");
    }

    private static async Task<OperatorAttentionResponse> Attention(HttpClient client)
    {
        using var response = await client.GetAsync("/api/operator/attention");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OperatorAttentionResponse>(JsonOptions))!;
    }

    private static async Task WaitUntilAttention(WebApplicationFactory<Program> factory, HttpClient client,
        Func<OperatorAttentionResponse, bool> match)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            // A sweep refreshes the facts the list reads.
            await Sweep(factory);
            if (match(await Attention(client))) return;
            await Task.Delay(100);
        }
        Assert.Fail("The attention list never showed the expected row.");
    }

    private static async Task<List<OperatorDecisionEvent>> Snapshot(HttpClient client, string after)
    {
        using var response = await client.GetAsync($"/api/operator/decisions?after={after}&limit=1000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OperatorDecisionsResponse>(JsonOptions);
        return [.. body!.Events];
    }

    private static async Task<List<(string? Id, string Event, JsonElement Data)>> ReadStream(
        HttpClient client, string url, int count, string? lastEventId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (lastEventId is not null) request.Headers.Add("Last-Event-ID", lastEventId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var frames = new List<(string? Id, string Event, JsonElement Data)>();
        string? id = null, name = null, data = null;
        while (frames.Count < count && await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
            else if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
            else if (line.Length == 0 && name is not null)
            {
                frames.Add((id, name, JsonDocument.Parse(data!).RootElement.Clone()));
                (id, name, data) = (null, null, null);
            }
        }
        Assert.Equal(count, frames.Count);
        return frames;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using (response) return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static DateTime Now(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

    private static string Token(WebApplicationFactory<Program> factory, string name, string role, string project)
    {
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        return store.CreateApiToken(new CreateApiTokenRequest(name, role, [project], Now(factory).AddDays(1)), ownerId).Secret;
    }

    private WebApplicationFactory<Program> BuildFactory()
    {
        foreach (var project in new[] { "alpha", "beta" })
        {
            foreach (var state in TaskStates.All)
                Directory.CreateDirectory(Path.Combine(ProjectPath(project), state));
            WriteCard(project, project + "-backlog", TaskStates.Backlog);
            WriteCard(project, project + "-escalated", TaskStates.Escalated);
        }
        var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = "networked",
                ["AllowedHosts"] = "studio.test",
                ["DeliveryChain:Guarded"] = "false",
                ["Secrets:KeyFile"] = Path.Combine(_workspace + "-keys", "secrets.kek"),
                ["Secrets:KeyEnvironmentVariable"] = "AGENT_STUDIO_TEST_UNSET_KEK_" + Guid.NewGuid().ToString("N"),
                ["WatchPaths:0:Name"] = "alpha",
                ["WatchPaths:0:Path"] = ProjectPath("alpha"),
                ["WatchPaths:0:RootPath"] = ProjectPath("alpha"),
                ["WatchPaths:1:Name"] = "beta",
                ["WatchPaths:1:Path"] = ProjectPath("beta"),
                ["WatchPaths:1:RootPath"] = ProjectPath("beta"),
            }));
        });
        factory.Services.GetRequiredService<AccessSecurityStore>().Bootstrap(new BootstrapRequest("first.owner", Password, null));
        return factory;
    }

    private void WriteCard(string project, string id, string state)
    {
        var card = Path.Combine(ProjectPath(project), state, id);
        Directory.CreateDirectory(card);
        File.WriteAllText(Path.Combine(card, "task.json"), JsonSerializer.Serialize(new
        {
            id, key = id, title = id, state, order = 1, agent = "codex", kind = "task",
        }));
        File.WriteAllText(Path.Combine(card, "prompt.md"), "Fixture card.");
    }

    private string ProjectPath(string project) => Path.Combine(_workspace, "projects", project);

    private static (string Session, string Csrf) Login(WebApplicationFactory<Program> factory)
    {
        var login = factory.Services.GetRequiredService<AccessSecurityStore>().Login("first.owner", Password, "first.owner|127.0.0.1");
        return (login.SessionToken, login.CsrfToken);
    }

    private static HttpClient BearerClient(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, (string Session, string Csrf) user)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={user.Session}");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", user.Csrf);
        client.DefaultRequestHeaders.Add("Origin", "https://studio.test");
        return client;
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
        if (Directory.Exists(_workspace + "-keys")) Directory.Delete(_workspace + "-keys", true);
    }
}
