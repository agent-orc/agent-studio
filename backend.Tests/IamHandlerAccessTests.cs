using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Handler-level checks on the real networked host (AGT-3228 review items 1
/// and 2): body-addressed task writes are decided per affected project against
/// the role bindings, and handler role checks see the role that covers the
/// addressed project, not the strongest role the user holds anywhere.
/// </summary>
public sealed class IamHandlerAccessTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "iam-handlers-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_viewer_of_beta_who_develops_alpha_cannot_write_beta_through_body_addressed_routes()
    {
        using var factory = BuildFactory();
        var user = Invite(factory, "split.user",
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("beta")),
            new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")));
        using var client = Client(factory, user);

        using var batchBeta = await client.PostAsJsonAsync("/api/tasks/batch-move", BatchMove("beta-card", "beta", TaskStates.Preparation));
        Assert.Equal(HttpStatusCode.Forbidden, batchBeta.StatusCode);
        using var batchMixed = await client.PostAsJsonAsync("/api/tasks/batch-move", new BatchMoveRequest
        {
            Items =
            [
                new() { JobId = "alpha-card", WatchPath = ProjectPath("alpha"), TargetState = TaskStates.Preparation },
                new() { JobId = "beta-card", WatchPath = ProjectPath("beta"), TargetState = TaskStates.Preparation },
            ],
        });
        Assert.Equal(HttpStatusCode.Forbidden, batchMixed.StatusCode);
        using var reorderBeta = await client.PostAsJsonAsync("/api/tasks/reorder", new { jobIds = new[] { "beta-card" } });
        Assert.Equal(HttpStatusCode.Forbidden, reorderBeta.StatusCode);
        using var createBeta = await client.PostAsJsonAsync("/api/tasks", new { title = "Beta write", watchPath = ProjectPath("beta") });
        Assert.Equal(HttpStatusCode.Forbidden, createBeta.StatusCode);
        Assert.False(Directory.EnumerateDirectories(ProjectPath("beta"), "beta-write", SearchOption.AllDirectories).Any());

        using var batchAlpha = await client.PostAsJsonAsync("/api/tasks/batch-move", BatchMove("alpha-card", "alpha", TaskStates.Preparation));
        Assert.Equal(HttpStatusCode.Accepted, batchAlpha.StatusCode);
        using var createAlpha = await client.PostAsJsonAsync("/api/tasks", new { title = "Alpha write", watchPath = ProjectPath("alpha") });
        Assert.Equal(HttpStatusCode.OK, createAlpha.StatusCode);
    }

    [Fact]
    public async Task A_contributor_of_beta_who_develops_alpha_keeps_the_product_transitions_in_beta()
    {
        using var factory = BuildFactory();
        var user = Invite(factory, "product.dev",
            new IamGrantRequest(IamRoles.Contributor, IamScope.ForProject("beta")),
            new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")));
        using var client = Client(factory, user);

        // The middleware lets a Product move of a backlog card through; the
        // move handler's own Product check must still see Contributor in beta.
        using var outside = await client.PostAsJsonAsync("/api/tasks/beta-card/move",
            new MoveJobRequest { TargetState = TaskStates.Progress });
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(ProjectPath("beta"), TaskStates.Backlog, "beta-card")));
        using var stateOutside = await client.PutAsJsonAsync("/api/tasks/beta-card/state",
            new MoveJobRequest { TargetState = TaskStates.Preparation, TargetIndex = 0 });
        Assert.Equal(HttpStatusCode.Forbidden, stateOutside.StatusCode);
        using var createOutside = await client.PostAsJsonAsync("/api/tasks",
            new { title = "Beta in progress", watchPath = ProjectPath("beta"), targetState = TaskStates.Progress });
        Assert.Equal(HttpStatusCode.Forbidden, createOutside.StatusCode);
        using var batch = await client.PostAsJsonAsync("/api/tasks/batch-move", BatchMove("beta-card", "beta", TaskStates.Ready));
        Assert.Equal(HttpStatusCode.Forbidden, batch.StatusCode);

        using var allowed = await client.PostAsJsonAsync("/api/tasks/beta-card/move",
            new MoveJobRequest { TargetState = TaskStates.Ready });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var developer = await client.PostAsJsonAsync("/api/tasks/alpha-card/move",
            new MoveJobRequest { TargetState = TaskStates.Preparation });
        Assert.Equal(HttpStatusCode.OK, developer.StatusCode);
    }

    private WebApplicationFactory<Program> BuildFactory()
    {
        foreach (var project in new[] { "alpha", "beta" })
        {
            foreach (var state in TaskStates.All)
                Directory.CreateDirectory(Path.Combine(ProjectPath(project), state));
            var card = Path.Combine(ProjectPath(project), TaskStates.Backlog, project + "-card");
            Directory.CreateDirectory(card);
            File.WriteAllText(Path.Combine(card, "task.json"), JsonSerializer.Serialize(new
            {
                id = project + "-card", title = project + " card", state = TaskStates.Backlog, order = 1, agent = "codex", kind = "task",
            }));
            File.WriteAllText(Path.Combine(card, "prompt.md"), "Fixture card.");
        }
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = "networked",
                ["AllowedHosts"] = "studio.test",
                ["DeliveryChain:Guarded"] = "false",
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

    private string ProjectPath(string project) => Path.Combine(_workspace, "projects", project);

    private BatchMoveRequest BatchMove(string jobId, string project, string target) => new()
    {
        Items = [new() { JobId = jobId, WatchPath = ProjectPath(project), TargetState = target }],
    };

    private static (string Session, string Csrf) Invite(WebApplicationFactory<Program> factory, string username, params IamGrantRequest[] grants)
    {
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        var invite = store.InviteUser(new InviteUserRequest(username, username, "", null, grants), owner);
        var accepted = store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "accepted pass phrase!"));
        return (accepted.SessionToken, accepted.CsrfToken);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, (string Session, string Csrf) user)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={user.Session}");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", user.Csrf);
        return client;
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
    }
}
