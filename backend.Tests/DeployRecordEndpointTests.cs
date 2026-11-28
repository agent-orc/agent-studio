using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.Deploy;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DeployRecordEndpointTests : IDisposable
{
    private const string Route = "/api/operations/deploy-records";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deploy-record-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new();
    private DeployRecordReport Open => new()
    {
        Id = "server-update", StartedAtUtc = _clock.GetUtcNow(), ToCommit = new string('b', 40),
    };

    [Fact]
    public async Task Only_workspace_operator_token_can_report_and_its_other_installation_writes_stay_forbidden()
    {
        await using var host = Factory(networked: true);
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        var security = host.Services.GetRequiredService<AccessSecurityStore>();
        var owner = security.Bootstrap(new BootstrapRequest("deploy-owner", "fixture password for deploys!", null));
        var reserve = RoutePermissionCatalog.Resolve("PUT", "/api/model-selection/policy/reserve")!.Permission;
        Assert.Equal(reserve, RoutePermissionCatalog.Resolve("POST", Route)!.Permission);
        Assert.Equal(reserve, RoutePermissionCatalog.Resolve("POST", Route + "/import")!.Permission);

        async Task Expect(HttpStatusCode status)
        {
            using var post = await client.PostAsJsonAsync(Route, Open);
            Assert.Equal(status, post.StatusCode);
            using var import = await client.PostAsJsonAsync(Route + "/import", new[] { Open });
            Assert.Equal(status, import.StatusCode);
        }
        string UseToken(string role, string project)
        {
            var (token, secret) = security.CreateApiToken(new(role, role, [project], null), owner.User.Id);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            return token.Id;
        }
        await Expect(HttpStatusCode.Unauthorized);
        UseToken(StudioRoles.Viewer, "*");
        await Expect(HttpStatusCode.Forbidden);
        UseToken(StudioRoles.Operator, "demo");
        await Expect(HttpStatusCode.Forbidden);
        using var scopedRead = await client.GetAsync(Route + "?project=demo");
        Assert.Equal(HttpStatusCode.Forbidden, scopedRead.StatusCode);
        var tokenId = UseToken(StudioRoles.Operator, "*");
        await Expect(HttpStatusCode.OK);
        using var reserveWrite = await client.PutAsJsonAsync("/api/model-selection/policy/reserve", new { points = 10 });
        Assert.Equal(HttpStatusCode.Forbidden, reserveWrite.StatusCode);
        var entries = host.Services.GetRequiredService<ControlPlaneAuditLog>()
            .ReadNewest(e => e.Stream == ControlPlaneAuditStreams.Operator && e.Operation.StartsWith("deploy-record."), 10);
        Assert.Equal(["requested", "reported"], entries.Where(e => e.Operation == "deploy-record.upsert")
            .OrderBy(e => e.Sequence).Select(e => e.Outcome));
        var replay = Assert.Single(entries, e => e.Operation == "deploy-record.import");
        Assert.Equal("reported", replay.Outcome);
        Assert.Equal(0, replay.Details!.Value.GetProperty("Imported").GetInt32());
        Assert.Equal(1, replay.Details.Value.GetProperty("Skipped").GetInt32());
        Assert.All(entries, e => Assert.Contains(tokenId, e.Actor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_history_write_keeps_only_the_request_in_the_operator_stream(bool import)
    {
        await using var host = Factory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(sp => ActivatorUtilities.CreateInstance<DeployRecordStore>(sp, new FailedHistoryWriter()))));
        using var client = LocalClient(host);
        using var response = import
            ? await client.PostAsJsonAsync(Route + "/import", new[] { Open })
            : await client.PostAsJsonAsync(Route, Open);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty((await Page(client)).Items);
        var operation = import ? "deploy-record.import" : "deploy-record.upsert";
        var entry = Assert.Single(host.Services.GetRequiredService<ControlPlaneAuditLog>()
            .ReadNewest(e => e.Stream == ControlPlaneAuditStreams.Operator && e.Operation == operation, 10));
        Assert.Equal("requested", entry.Outcome);
    }

    private sealed class FailedHistoryWriter : IAtomicJsonFileWriter
    {
        public void Write(string path, string content) => throw new IOException("Injected deploy history write failure.");
    }

    [Fact]
    public async Task Open_record_crosses_two_hour_boundary_and_can_close_after_restart()
    {
        var open = Open;
        var step = new DeployRecordStep("build", open.StartedAtUtc, open.StartedAtUtc.AddMinutes(1), "succeeded");
        await using (var host = Factory())
        using (var client = LocalClient(host))
        {
            using var start = await client.PostAsJsonAsync(Route, open);
            start.EnsureSuccessStatusCode();
            using var update = await client.PostAsJsonAsync(Route, open with { Steps = [step] });
            update.EnsureSuccessStatusCode();
            _clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1));
            Assert.Equal("running", Assert.Single((await Page(client)).Items).Outcome);
            _clock.Advance(TimeSpan.FromSeconds(1));
            var unknown = Assert.Single((await Page(client)).Items);
            Assert.Equal("unknown-outcome", unknown.Outcome);
            Assert.Null(unknown.EndedAtUtc);
            Assert.Null(unknown.OutageSeconds);
            Assert.Null(unknown.CardsShipped);
            Assert.Equal(step, Assert.Single(unknown.Steps!));
        }
        await using var restarted = Factory();
        using var resumed = LocalClient(restarted);
        var closed = open with
        {
            EndedAtUtc = _clock.GetUtcNow(), Outcome = "rolled-back", OutageSeconds = 480, Steps = [step],
            Machines = ["host-a"], Components = ["api"], Version = "1.2", Note = "Previous API restored.",
        };
        using var finish = await resumed.PostAsJsonAsync(Route, closed);
        finish.EnsureSuccessStatusCode();
        using var delayedStart = await resumed.PostAsJsonAsync(Route, open);
        Assert.Equal(HttpStatusCode.Conflict, delayedStart.StatusCode);
        var row = Assert.Single((await Page(resumed)).Items);
        Assert.Equal("rolled-back", row.Outcome);
        Assert.Equal(480, row.OutageSeconds);
        Assert.Equal(closed.Note, row.Note);
        Assert.Equal(closed.Machines, row.Machines);
        Assert.Equal(closed.Components, row.Components);
        Assert.Equal(step, Assert.Single(row.Steps!));
        Assert.Null(row.CardsInRange);
        Assert.NotNull(row.CardRangeDetail);
    }

    [Fact]
    public async Task Import_is_atomic_and_idempotent_across_ids_offsets_and_SHA_case_and_pages_newest_first()
    {
        await using var host = Factory();
        using var client = LocalClient(host);
        var first = Open;
        var second = first with { Id = "retry", StartedAtUtc = first.StartedAtUtc.AddMinutes(2) };
        var alias = first with
        {
            Id = "archive-line-1", ToCommit = first.ToCommit.ToUpperInvariant(),
            StartedAtUtc = first.StartedAtUtc.ToOffset(TimeSpan.FromHours(1)),
        };
        async Task<DeployRecordImport?> Import(params DeployRecordReport[] records)
        {
            using var result = await client.PostAsJsonAsync(Route + "/import", records);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<DeployRecordImport>();
        }
        Assert.Equal(new DeployRecordImport(2, 1), await Import(first, second, alias));
        var audit = host.Services.GetRequiredService<ControlPlaneAuditLog>();
        var completed = Assert.Single(audit.ReadNewest(e => e.Stream == ControlPlaneAuditStreams.Operator
            && e.Operation == "deploy-record.import" && e.Outcome == "reported", 10));
        var details = completed.Details!.Value;
        Assert.Equal(2, details.GetProperty("Imported").GetInt32());
        Assert.Equal(1, details.GetProperty("Skipped").GetInt32());
        Assert.Equal([first.Id, second.Id], details.GetProperty("Ids").EnumerateArray().Select(id => id.GetString()));
        Assert.Equal(new DeployRecordImport(0, 3), await Import(alias, first, second));
        Assert.Equal(second.Id, Assert.Single((await Page(client, "?limit=1")).Items).Id);
        var tail = await Page(client, "?offset=1&limit=1");
        Assert.Equal(2, tail.Total);
        Assert.Equal(first.Id, Assert.Single(tail.Items).Id);
        var third = first with { Id = "third", StartedAtUtc = first.StartedAtUtc.AddHours(1) };
        using var conflict = await client.PostAsJsonAsync(Route + "/import", new[] { third, third with { Id = first.Id, ToCommit = new string('a', 40) } });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var invalid = await client.PostAsJsonAsync(Route + "/import", new[] { third, third with { OutageSeconds = -1 } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(2, (await Page(client)).Total);
    }

    [Fact]
    public async Task Invalid_reports_are_rejected_before_writing_history_or_audit()
    {
        await using var host = Factory();
        using var client = LocalClient(host);
        var row = Open;
        foreach (var invalid in new[]
        {
            row with { Id = "" }, row with { StartedAtUtc = default }, row with { ToCommit = "main" },
            row with { FromCommit = "bad" }, row with { Outcome = "succeeded" }, row with { EndedAtUtc = row.StartedAtUtc },
            row with { Outcome = "unknown-outcome" }, row with { OutageSeconds = -2 },
            row with { EndedAtUtc = row.StartedAtUtc.AddSeconds(-1), Outcome = "rolled-back" },
            row with { Steps = [null!] }, row with { Steps = [new("check", row.StartedAtUtc.AddSeconds(-1))] },
            row with { Machines = [" "] },
        })
        {
            using var response = await client.PostAsJsonAsync(Route, invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var badPage = await client.GetAsync(Route + "?offset=-1");
        Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
        Assert.Empty((await Page(client)).Items);
        Assert.Empty(host.Services.GetRequiredService<ControlPlaneAuditLog>().ReadNewest(e => e.Operation.StartsWith("deploy-record."), 10));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Shipped_cards_come_from_distinct_integration_subjects_in_the_exact_repository_range()
    {
        using var repo = new CheckoutFileFixture("deploy-range");
        string Commit(string subject)
        {
            CheckoutFileFixture.Git(repo.Root, "commit", "--allow-empty", "-qm", subject);
            return CheckoutFileFixture.Git(repo.Root, "rev-parse", "HEAD").Trim();
        }
        var from = Commit("Integrate DEMO-1: baseline");
        Commit("Integrate DEMO-2: first delivery mentioning DEMO-8");
        Commit("docs: refer to DEMO-7");
        Commit("merge-recut(DEMO-2): retried delivery");
        var to = Commit("merge(DEMO-3): other delivery");
        Commit("Integrate DEMO-4: after target");
        await using var host = Factory(repository: repo.Root);
        using var client = LocalClient(host);
        var report = Open with { FromCommit = from, ToCommit = to, EndedAtUtc = _clock.GetUtcNow(), Outcome = "succeeded" };
        using var post = await client.PostAsJsonAsync(Route, report);
        post.EnsureSuccessStatusCode();
        var shipped = Assert.Single((await Page(client)).Items).CardsShipped;
        Assert.NotNull(shipped);
        Assert.Equal(["DEMO-2", "DEMO-3"], shipped);
        using var unknown = await client.PostAsJsonAsync(Route, report with { FromCommit = new string('e', 40) });
        unknown.EnsureSuccessStatusCode();
        Assert.Null(Assert.Single((await Page(client)).Items).CardsShipped);
    }

    private static HttpClient LocalClient(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        return client;
    }

    private static async Task<DeployRecordPage> Page(HttpClient client, string query = "")
        => (await client.GetFromJsonAsync<DeployRecordPage>(Route + query))!;

    private WebApplicationFactory<Program> Factory(bool networked = false, string? repository = null)
    {
        var watch = Path.Combine(_root, "projects", "demo");
        Directory.CreateDirectory(watch);
        return new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root, ["Security:Profile"] = networked ? "networked" : "local",
                ["AllowedHosts"] = "*", ["WatchPaths:0:Name"] = "demo", ["WatchPaths:0:Path"] = watch,
                ["WatchPaths:0:RootPath"] = repository ?? watch, ["WatchPaths:0:RepositoryPath"] = repository ?? watch,
                ["InstanceDeploy:SourceProject"] = "demo",
            }));
            builder.ConfigureTestServices(services =>
            {
                TestHostFactory.RemoveBackgroundHostedServices(services);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
        });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
