extern alias Runner;

using System.Net;
using System.Net.Http.Json;
using AgentStudio.Deploy;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TestQuarantineEndpointsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quarantine-endpoints-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Union_counts_mutations_audit_and_release_hold_share_the_same_store()
    {
        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = _root, ["Security:Profile"] = "local", ["AllowedHosts"] = "*" }));
            builder.ConfigureTestServices(services =>
            {
                TestHostFactory.RemoveBackgroundHostedServices(services);
                services.Replace(ServiceDescriptor.Singleton(new PostMergeRunStore(Path.Combine(_root, "post-merge"))));
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var registry = factory.Services.GetRequiredService<ProjectRegistry>();
        var tasks = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(tasks);
        var project = registry.EnsureProjectForStorage(tasks, "demo", DefaultWorkspace.Id, _clock);
        var role = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/projects/demo/line-guardian/role");
        Assert.Equal("guardian", role.GetProperty("role").GetProperty("kind").GetString());
        var guardian = await client.GetFromJsonAsync<AgentStudio.Personas.GuardianProjectState>("/api/projects/demo/line-guardian");
        Assert.False(guardian!.Live);
        (await client.PutAsJsonAsync("/api/projects/demo/line-guardian/mode",
            new AgentStudio.Personas.GuardianModeRequest(true))).EnsureSuccessStatusCode();
        Assert.True((await client.GetFromJsonAsync<AgentStudio.Personas.GuardianProjectState>("/api/projects/demo/line-guardian"))!.Live);
        (await client.PutAsJsonAsync("/api/projects/demo/line-guardian/mode",
            new AgentStudio.Personas.GuardianModeRequest(false))).EnsureSuccessStatusCode();
        var mutations = factory.Services.GetRequiredService<TaskMutationService>();
        var card = mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
            { Project = project.Id, Title = "Repair", TargetState = TaskStates.Preparation })!;
        using var started = await client.PostAsJsonAsync("/api/projects/demo/test-quarantines/start",
            new StartTestQuarantineRequest("Tests.Fails", card, "held", "fingerprint", ["backend/Features/Pipeline"]));
        started.EnsureSuccessStatusCode();
        var entry = (await started.Content.ReadFromJsonAsync<ProjectTestQuarantine>())!;
        var summary = Assert.Single((await client.GetFromJsonAsync<DevelopStateSummary[]>("/api/develop-states"))!, row => row.Project == "demo");
        Assert.Equal(1, summary.QuarantinedCount);
        Assert.Equal(1, summary.HeldCount);
        var state = await client.GetFromJsonAsync<PostMergeProjectState>("/api/runner/post-merge/projects/demo");
        Assert.Equal(entry.Id, Assert.Single(state!.Quarantines).Id);
        Assert.Equal(entry.Id, Assert.Single(state.Holds).QuarantineId);
        var repo = Path.Combine(_root, "hold-repository");
        Directory.CreateDirectory(repo);
        async Task Git(params string[] args)
        {
            var result = await Runner::AgentRunner.ProcessRunner.RunAsync("git", args, repo, ct: CancellationToken.None);
            Assert.True(result.Success, result.StdErr);
        }
        await Git("init", "-q", "-b", "develop");
        await Git("config", "user.name", "Test");
        await Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(repo, "base.txt"), "base");
        await Git("add", ".");
        await Git("commit", "-qm", "base");
        await Git("checkout", "-qb", "delivery");
        Directory.CreateDirectory(Path.Combine(repo, "backend", "Features", "Pipeline"));
        File.WriteAllText(Path.Combine(repo, "backend", "Features", "Pipeline", "Changed.cs"), "changed");
        await Git("add", ".");
        await Git("commit", "-qm", "delivery");
        var local = factory.Services.GetRequiredService<MergeIntoDevelopRunner>();
        var blocked = local.QuarantineHoldReason("demo", "ordinary", tasks, repo, "develop", "delivery");
        Assert.Contains(entry.Id, blocked);
        Assert.Contains("Tests.Fails", blocked);
        Assert.Contains("backend/Features/Pipeline", blocked);
        Assert.Null(local.QuarantineHoldReason("demo", card, tasks, repo, "develop", "delivery"));
        await Git("checkout", "-qb", "unrelated", "develop");
        Directory.CreateDirectory(Path.Combine(repo, "frontend"));
        File.WriteAllText(Path.Combine(repo, "frontend", "safe.ts"), "safe");
        await Git("add", ".");
        await Git("commit", "-qm", "unrelated");
        Assert.Null(local.QuarantineHoldReason("demo", "ordinary", tasks, repo, "develop", "unrelated"));
        var release = factory.Services.GetRequiredService<DeployService>().CreateRelease("demo", "image", new string('a', 40), "owner");
        Assert.Equal(DeployOutcomeKind.Conflict, release.Kind);
        Assert.Equal("test-quarantine-held", release.Reason);
        using (var refused = await client.PostAsJsonAsync($"/api/projects/demo/test-quarantines/{entry.Id}/renew", new { }))
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        factory.Services.GetRequiredService<TaskStateMachine>().MoveJob(card, TaskStates.Progress, tasks);
        _clock.Advance(TimeSpan.FromHours(24));
        (await client.PostAsJsonAsync($"/api/projects/demo/test-quarantines/{entry.Id}/renew", new { })).EnsureSuccessStatusCode();
        using (var refused = await client.PostAsJsonAsync($"/api/projects/demo/test-quarantines/{entry.Id}/renew", new { }))
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        (await client.PostAsJsonAsync($"/api/projects/demo/test-quarantines/{entry.Id}/lift", new LiftTestQuarantineRequest())).EnsureSuccessStatusCode();
        var lifted = Assert.Single((await client.GetFromJsonAsync<ProjectTestQuarantine[]>("/api/projects/demo/test-quarantines"))!);
        Assert.Equal("owner", lifted.LiftedBy);
        Assert.NotNull(lifted.ResolvedOn);
        Assert.Empty((await client.GetFromJsonAsync<PostMergeProjectState>("/api/runner/post-merge/projects/demo"))!.Quarantines);
        Assert.Equal(["start", "renew", "lift"], factory.Services.GetRequiredService<TestQuarantineStore>().Read(project.Id).Audit.Select(row => row.Operation));
        (await client.PostAsJsonAsync("/api/projects/demo/test-quarantines/start",
            new StartTestQuarantineRequest("Tests.Flaky", card, "report-only", "flaky"))).EnsureSuccessStatusCode();
        var reportOnlyRelease = factory.Services.GetRequiredService<DeployService>().CreateRelease("demo", "image", new string('a', 40), "owner");
        Assert.NotEqual("test-quarantine-held", reportOnlyRelease.Reason);
        Assert.NotEqual("develop-red", reportOnlyRelease.Reason);
    }

    [Fact]
    public void Runner_and_service_account_principals_cannot_lift_without_evidence_as_owner()
    {
        var context = new DefaultHttpContext();
        Assert.True(TestQuarantineEndpoints.Owner(context));
        context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] = new RunnerPrincipal("runner", "runner", "credential", new HashSet<string>());
        Assert.False(TestQuarantineEndpoints.Owner(context));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
