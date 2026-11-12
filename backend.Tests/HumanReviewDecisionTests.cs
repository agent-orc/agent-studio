using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Projects;
using AgentStudio.Registry;
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
/// AGT-3670: the Reviewer role reads a card in Human Review, accepts it, or
/// sends it back with a note that becomes the next run's follow-up; it can do
/// nothing else. Each decision is on the card timeline and in the audit.
/// </summary>
public sealed class HumanReviewDecisionTests : IDisposable
{
    private const string Project = "review-project";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-reviewer-role-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Reviewer_holds_reads_and_the_review_decision_only()
    {
        var reviewer = IamRoles.Find(IamRoles.Reviewer)!;
        Assert.Equal(
            [IamPermissions.ProjectCardsReview, IamPermissions.ProjectRead, IamPermissions.WorkspaceRead],
            reviewer.Permissions.Order(StringComparer.Ordinal));
        Assert.StartsWith("Reviews and accepts cards in Human Review.", reviewer.Description);
        Assert.Equal([IamScopeKind.Workspace, IamScopeKind.Project], reviewer.BindableScopes.Order());
        Assert.False(reviewer.ServiceAccountBindable);
        Assert.Equal(StudioRoles.Viewer, IamRoles.LegacyRole(IamRoles.Reviewer));
        foreach (var role in new[] { IamRoles.Owner, IamRoles.Admin, IamRoles.Developer })
            Assert.True(IamRoles.Grants(role, IamPermissions.ProjectCardsReview), role);
        foreach (var role in new[] { IamRoles.Contributor, IamRoles.Viewer, IamRoles.Approver, IamRoles.OperatorAgent })
            Assert.False(IamRoles.Grants(role, IamPermissions.ProjectCardsReview), role);

        // An older binary reads only the summary: a Reviewer stays read-only there.
        var summary = LegacyRoleMapping.Summarize([new RoleBinding
        {
            Id = "rb_1", PrincipalKind = IamPrincipalKind.User, PrincipalId = "usr_1",
            Role = IamRoles.Reviewer, Scope = IamScope.ForProject(Project), Source = IamBindingSources.Manual,
        }]);
        Assert.Equal(StudioRoles.Viewer, summary.Role);
        Assert.Equal([Project], summary.Projects);
        Assert.Empty(summary.ProductionApprovalProjects);
    }

    [Fact]
    public void Send_back_prompt_quotes_the_note_and_asks_for_a_new_delivery()
    {
        var prompt = HumanReviewDecisionEndpoints.FollowUpPrompt("Rui Reviewer", "The header is still loud.\nUse the quiet tone.");

        Assert.Contains("Rui Reviewer reviewed this delivery in Human Review", prompt);
        Assert.Contains("> The header is still loud.\n> Use the quiet tone.", prompt.Replace("\r\n", "\n"));
        Assert.EndsWith("Address the note in this run, then deliver again.", prompt);
    }

    [Fact]
    public async Task Reviewer_reads_accepts_and_sends_back_with_a_note_and_nothing_else()
    {
        var projectPath = Path.Combine(_root, "projects", Project);
        foreach (var lane in new[] { TaskStates.Backlog, TaskStates.Ready, TaskStates.HumanReview, TaskStates.Completed })
            Directory.CreateDirectory(Path.Combine(projectPath, lane));
        var sendBackCard = WriteHumanReviewCard(projectPath, "REV-1", "Calm the header");
        WriteHumanReviewCard(projectPath, "REV-2", "Quiet the footer");

        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["Security:Profile"] = "networked",
                ["AllowedHosts"] = "studio.test",
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = projectPath,
                ["WatchPaths:0:RootPath"] = projectPath,
                ["WatchPaths:0:RepositoryPath"] = projectPath,
            }));
        });
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        factory.Services.GetRequiredService<ProjectRegistry>().EnsureProjectForStorage(projectPath, Project, "default");
        // A networked studio runs cards on remote runners; the follow-up waits for one.
        factory.Services.GetRequiredService<ProjectSettingsService>().SetExecutionSettings(Project, "manual", "runner-a", true);
        var owner = store.Bootstrap(new BootstrapRequest("owner.account", "owner password phrase!", null));
        var invite = store.InviteUser(new InviteUserRequest("rui.reviewer", "Rui Reviewer", "", null,
            [new IamGrantRequest(IamRoles.Reviewer, IamScope.ForProject(Project))]), owner.User.Id);
        var reviewer = store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "reviewer password phrase!"));
        var viewerInvite = store.InviteUser(new InviteUserRequest("vera.viewer", "Vera Viewer", "", null,
            [new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject(Project))]), owner.User.Id);
        var viewer = store.AcceptInvitation(new AcceptInvitationRequest(viewerInvite.Token, "viewer password phrase!"));

        // The session names the projects where the reviewer decides reviews.
        var status = await JsonAsync(HttpMethod.Get, "/api/auth/status", reviewer.SessionToken);
        Assert.Equal(StudioRoles.Viewer, (string?)status["user"]!["role"]);
        Assert.Equal([Project], status["user"]!["reviewerProjects"]!.AsArray().Select(node => (string?)node));
        var ownerStatus = await JsonAsync(HttpMethod.Get, "/api/auth/status", owner.SessionToken);
        Assert.Empty(ownerStatus["user"]!["reviewerProjects"]!.AsArray());

        // Reads: the card, its delivery files, and its timeline.
        Assert.Equal(HttpStatusCode.OK, await ReadUntilIndexedAsync("/api/tasks/REV-1", reviewer.SessionToken));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(HttpMethod.Get, "/api/tasks/REV-1/files/status.md", reviewer.SessionToken));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(HttpMethod.Get, "/api/tasks/REV-1/results/report.html", reviewer.SessionToken));

        // No create, no edit, no lane move, no run control.
        var refused = new (HttpMethod Method, string Path, object Body)[]
        {
            (HttpMethod.Post, "/api/tasks", new { title = "A reviewer card", watchPath = projectPath, targetState = TaskStates.Backlog }),
            (HttpMethod.Post, "/api/tasks/REV-1/move", new { targetState = TaskStates.Completed }),
            (HttpMethod.Post, "/api/tasks/REV-1/move", new { targetState = TaskStates.Ready }),
            (HttpMethod.Put, "/api/tasks/REV-1/title", new { title = "Renamed" }),
            (HttpMethod.Post, "/api/tasks/REV-1/continue", new { prompt = "Run again." }),
            (HttpMethod.Post, "/api/tasks/REV-1/start", new { }),
            (HttpMethod.Post, $"/api/runner/{Project}/orchestrator-chat", new { message = "hello" }),
        };
        foreach (var (method, path, body) in refused)
            Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(method, path, reviewer.SessionToken, reviewer.CsrfToken, body));
        // A Viewer reads the same card and still cannot decide it.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(HttpMethod.Post, "/api/tasks/REV-1/human-review/accept",
            viewer.SessionToken, viewer.CsrfToken, new { }));
        // The decision is a browser mutation like any other: CSRF holds.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(HttpMethod.Post, "/api/tasks/REV-1/human-review/accept",
            reviewer.SessionToken, null, new { }));

        // Send back: the note is required, then it reaches the next run's prompt.
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(HttpMethod.Post, "/api/tasks/REV-1/human-review/send-back",
            reviewer.SessionToken, reviewer.CsrfToken, new { note = "  " }));
        const string note = "The header is still loud. Use the quiet tone from the style guide.";
        using (var sentBack = await SendAsync(HttpMethod.Post, "/api/tasks/REV-1/human-review/send-back",
                   reviewer.SessionToken, reviewer.CsrfToken, new { note }))
        {
            Assert.Equal(HttpStatusCode.OK, sentBack.StatusCode);
            var body = JsonNode.Parse(await sentBack.Content.ReadAsStringAsync())!;
            Assert.Equal(("send-back", TaskStates.Ready), ((string?)body["decision"], (string?)body["state"]));
        }
        var readyFolder = Path.Combine(projectPath, TaskStates.Ready, "REV-1");
        Assert.True(Directory.Exists(readyFolder));
        var intent = JsonNode.Parse(File.ReadAllText(Path.Combine(readyFolder, "pending-intent.json")))!;
        Assert.Contains(note, (string?)intent["prompt"]);
        Assert.Contains("Rui Reviewer reviewed this delivery", (string?)intent["prompt"]);
        Assert.Contains(note, File.ReadAllText(Path.Combine(readyFolder, "prompt.md")));
        var timeline = factory.Services.GetRequiredService<TimelineLog>().ReadAll(readyFolder);
        var decided = Assert.Single(timeline, row => row.Kind == TimelineEventKinds.HumanReviewDecided);
        Assert.Equal(TimelineActors.Human("Rui Reviewer"), decided.Actor);
        Assert.Equal("send-back", decided.Details!["decision"]);
        Assert.Equal(note, decided.Details["note"]);
        Assert.Contains(note, decided.Summary);
        // A card no longer in Human Review cannot be decided again.
        Assert.Equal(HttpStatusCode.Conflict, await StatusAsync(HttpMethod.Post, "/api/tasks/REV-1/human-review/send-back",
            reviewer.SessionToken, reviewer.CsrfToken, new { note }));
        Assert.Equal(HttpStatusCode.Conflict, await StatusAsync(HttpMethod.Post, "/api/tasks/REV-1/human-review/accept",
            reviewer.SessionToken, reviewer.CsrfToken, new { }));

        // Accept moves the other card to Completed.
        using (var accepted = await SendAsync(HttpMethod.Post, "/api/tasks/REV-2/human-review/accept",
                   reviewer.SessionToken, reviewer.CsrfToken, new { }))
            Assert.True(accepted.StatusCode == HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        var completedFolder = Path.Combine(projectPath, TaskStates.Completed, "REV-2");
        Assert.True(Directory.Exists(completedFolder));
        var acceptedRow = Assert.Single(factory.Services.GetRequiredService<TimelineLog>().ReadAll(completedFolder),
            row => row.Kind == TimelineEventKinds.HumanReviewDecided);
        Assert.Equal(("accept", TimelineActors.Human("Rui Reviewer")), (acceptedRow.Details!["decision"], acceptedRow.Actor));

        // Both decisions are audited with the reviewer as actor.
        var audit = store.ControlPlaneAudit.Read(0, 1000).Where(entry => entry.Stream == ControlPlaneAuditStreams.Review).ToList();
        Assert.Equal(
            [(HumanReviewDecisionEndpoints.SendBackOperation, $"project:{Project}/task:REV-1"),
             (HumanReviewDecisionEndpoints.AcceptOperation, $"project:{Project}/task:REV-2")],
            audit.Select(entry => (entry.Operation, entry.Target)));
        Assert.All(audit, entry => Assert.Equal(TimelineActors.Human(reviewer.User.Id), entry.Actor));
        Assert.Equal(ControlPlaneAuditLog.Sha256Hex(note), audit[0].ParameterDigest);

        async Task<HttpStatusCode> ReadUntilIndexedAsync(string path, string session)
        {
            for (var attempt = 0; ; attempt++)
            {
                var code = await StatusAsync(HttpMethod.Get, path, session);
                if (code != HttpStatusCode.Accepted || attempt == 40) return code;
                await Task.Delay(100);
            }
        }

        async Task<JsonNode> JsonAsync(HttpMethod method, string path, string session)
        {
            using var response = await SendAsync(method, path, session);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }

        async Task<HttpStatusCode> StatusAsync(HttpMethod method, string path, string session, string? csrf = null, object? body = null)
        {
            using var response = await SendAsync(method, path, session, csrf, body);
            return response.StatusCode;
        }

        Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string session, string? csrf = null, object? body = null)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={session}");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
            if (body is not null) request.Content = JsonContent.Create(body);
            return http.SendAsync(request);
        }
    }

    /// <summary>
    /// A delivered card that needs no integration, names its deliverable, and
    /// carries a passing content verdict for its current brief.
    /// </summary>
    private static string WriteHumanReviewCard(string projectPath, string key, string title)
    {
        var folder = Path.Combine(projectPath, TaskStates.HumanReview, key);
        Directory.CreateDirectory(Path.Combine(folder, "results"));
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = key, key, title, state = TaskStates.HumanReview, order = 1, agent = "codex", kind = "task",
            requiresIntegration = false,
        }));
        var prompt = $"{title}.";
        File.WriteAllText(Path.Combine(folder, "prompt.md"), prompt);
        var brief = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(folder, "content-review-context.json"), JsonSerializer.Serialize(new { briefVersion = brief }));
        File.WriteAllText(Path.Combine(folder, "aspect-requirement-fit.json"), JsonSerializer.Serialize(new
        {
            status = "pass", summary = "The delivery matches the brief.", evidenceChecked = "Reviewer role test fixture.",
        }));
        File.WriteAllText(Path.Combine(folder, "status.md"), "Human review is ready.");
        File.WriteAllText(Path.Combine(folder, "results", "report.html"), "<p>Delivered.</p>");
        File.WriteAllText(Path.Combine(folder, "results", "deliverables.md"), "# Deliverables\n\nThe calmer header.\n");
        return folder;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
