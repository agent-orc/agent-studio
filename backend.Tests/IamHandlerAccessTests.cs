using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Deploy;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Secrets;
using AgentStudio.Shared;
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
    private const string AlphaCommit = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    private const string BetaCommit = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "iam-handlers-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Scoped_cli_reads_use_static_models_and_do_not_start_refresh()
    {
        using var factory = BuildFactory();
        using var viewer = Client(factory, Invite(factory, "cli.viewer",
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("alpha"))));

        using var models = await viewer.GetAsync("/api/cli/codex/models");
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        using var catalog = JsonDocument.Parse(await models.Content.ReadAsStringAsync());
        Assert.Equal("scoped-registry", catalog.RootElement.GetProperty("source").GetString());
        using var refresh = await viewer.GetAsync("/api/cli/codex/models?refresh=true");
        Assert.Equal(HttpStatusCode.Forbidden, refresh.StatusCode);
        using var quota = await viewer.GetAsync("/api/cli/quota");
        Assert.Equal(HttpStatusCode.OK, quota.StatusCode);
    }

    [Fact]
    public async Task Project_viewer_sees_only_its_projects_workspace_screenshots()
    {
        using var factory = BuildFactory();
        foreach (var project in new[] { "alpha", "beta" })
        {
            var results = Path.Combine(ProjectPath(project), TaskStates.Backlog, project + "-card", "results");
            Directory.CreateDirectory(results);
            File.WriteAllBytes(Path.Combine(results, project + "-capture.png"), [1, 2, 3]);
        }
        using var viewer = Client(factory, Invite(factory, "screenshots.viewer",
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("alpha"))));

        using var response = await viewer.GetAsync("/api/workspace/screenshots?windowHours=24");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entries = body.RootElement.GetProperty("screenshots").EnumerateArray().ToArray();
        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.Equal("alpha", entry.GetProperty("projectName").GetString()));
    }

    [Fact]
    public async Task Project_viewer_cannot_read_workspace_quota_auto_review_or_host_state()
    {
        using var factory = BuildFactory();
        using var viewer = Client(factory, Invite(factory, "collections.viewer",
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("alpha"))));
        foreach (var path in new[]
        {
            "/api/workspaces/main/projects/beta/quota", "/api/auto-review/status",
            "/api/system/execution-infrastructure", "/api/agent-rules", "/api/adhoc-usage",
            "/api/admin/config/orchestrator"
        })
        {
            using var response = await viewer.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

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
    public async Task Change_project_requires_write_permission_and_a_non_product_role_in_the_destination()
    {
        using var factory = BuildFactory();
        var callers = new[]
        {
            Client(factory, Invite(factory, "move.viewer", new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")),
                new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("beta")))),
            Client(factory, Invite(factory, "move.unbound", new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")))),
            Client(factory, Invite(factory, "move.product", new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")),
                new IamGrantRequest(IamRoles.Contributor, IamScope.ForProject("beta")))),
        };
        foreach (var client in callers)
        {
            using (client)
            using (var response = await client.PostAsJsonAsync("/api/tasks/alpha-card/change-project",
                       new ChangeProjectRequest { TargetProject = "beta" }))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(Directory.Exists(Path.Combine(ProjectPath("alpha"), TaskStates.Backlog, "alpha-card")));
        }
    }

    [Fact]
    public async Task New_operator_and_review_routes_have_permissions_in_the_networked_host()
    {
        using var factory = BuildFactory();
        using var owner = Client(factory, Login(factory, "first.owner"));
        using var developer = Client(factory, Invite(factory, "route.dev",
            new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))));

        var routes = new[]
        {
            "/api/projects/alpha/operator-candidate-verifications",
            "/api/projects/alpha/deploy/deployments/unknown/intervention",
            "/api/projects/alpha/batch-gate/queue-stalls/release",
            "/api/tasks/alpha-card/review-observations",
            "/api/tasks/alpha-card/review-observations/unknown/later-evidence",
            "/api/tasks/alpha-card/review-observations/unknown/finding-outcomes",
        };
        foreach (var route in routes)
        {
            using var response = await owner.PostAsJsonAsync(route, new { });
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }
        foreach (var route in routes.Take(2))
        {
            using var response = await developer.PostAsJsonAsync(route, new { });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using var inProject = await developer.PostAsJsonAsync(routes[2], new { });
        Assert.NotEqual(HttpStatusCode.Forbidden, inProject.StatusCode);
    }

    [Fact]
    public async Task Owner_reaches_not_found_for_an_unknown_task_id()
    {
        using var factory = BuildFactory();
        using var owner = Client(factory, Login(factory, "first.owner"));
        using var response = await owner.GetAsync("/api/tasks/no-such-task-id");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Review_attempt_read_is_limited_to_the_attempts_project()
    {
        using var factory = BuildFactory();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var sha = new string('a', 40);
        var run = Assert.IsType<RunAttemptDto>(authority.AcquireRun(
            "beta-card", "beta", null, "runner", "host", 60, "iam-review-run").RunAttempt);
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "iam-review-settle"),
            Outcome = "done", ResultSha = sha,
        });
        var review = Assert.IsType<ReviewAttemptDto>(authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "beta-card", "beta", sha, run.AttemptId, "req", "policy", [], "iam-review-create")).ReviewAttempt);
        using var owner = Client(factory, Login(factory, "first.owner"));
        using var alpha = Client(factory, Invite(factory, "review.alpha",
            new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))));
        using var beta = Client(factory, Invite(factory, "review.beta",
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("beta"))));

        var route = "/api/v1/reviews/attempts/" + review.AttemptId;
        using var ownerRead = await owner.GetAsync(route);
        using var alphaRead = await alpha.GetAsync(route);
        using var betaRead = await beta.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, alphaRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, betaRead.StatusCode);

        // The authority can retain an attempt whose task folder is gone.
        // Workspace bindings read it without a task scan; project bindings
        // still need a resolvable project from the task.
        var goneRun = Assert.IsType<RunAttemptDto>(authority.AcquireRun(
            "gone-card", "beta", null, "runner", "host", 60, "iam-gone-run").RunAttempt);
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(goneRun.AttemptId, goneRun.LastFence, goneRun.AuthorityEpoch, "iam-gone-settle"),
            Outcome = "done", ResultSha = sha,
        });
        var goneReview = Assert.IsType<ReviewAttemptDto>(authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "gone-card", "beta", sha, goneRun.AttemptId, "req", "policy", [], "iam-gone-review")).ReviewAttempt);
        using var ownerAfterRemoval = await owner.GetAsync("/api/v1/reviews/attempts/" + goneReview.AttemptId);
        using var betaAfterRemoval = await beta.GetAsync("/api/v1/reviews/attempts/" + goneReview.AttemptId);
        Assert.Equal(HttpStatusCode.OK, ownerAfterRemoval.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, betaAfterRemoval.StatusCode);
    }

    [Fact]
    public async Task Canonical_review_attempt_key_resolves_its_project_without_a_task_folder()
    {
        using var factory = BuildFactory();
        var projects = factory.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>();
        var betaId = projects.List().Single(project => project.DisplayName == "beta").Id;
        var taskKey = betaId + "::removed-card";
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var run = Assert.IsType<RunAttemptDto>(authority.AcquireRun(
            taskKey, "beta", null, "runner", "host", 60, "iam-canonical-run").RunAttempt);
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "iam-canonical-settle"),
            Outcome = "done", ResultSha = new string('a', 40),
        });
        var review = Assert.IsType<ReviewAttemptDto>(authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, "beta", new string('a', 40), run.AttemptId, "req", "policy", [], "iam-canonical-review")).ReviewAttempt);
        using var beta = Client(factory, Invite(factory, "canonical.beta",
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("beta"))));
        using var alpha = Client(factory, Invite(factory, "canonical.alpha",
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("alpha"))));

        using var allowed = await beta.GetAsync("/api/v1/reviews/attempts/" + review.AttemptId);
        using var denied = await alpha.GetAsync("/api/v1/reviews/attempts/" + review.AttemptId);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task Project_member_cannot_read_host_cli_state_or_run_the_local_probe()
    {
        using var factory = BuildFactory();
        using var viewer = Client(factory, Invite(factory, "cli.viewer",
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("alpha"))));
        foreach (var route in new[]
                 {
                     "/api/cli/usage",
                     "/api/cli/claude/session-detail?id=missing",
                     "/api/cli/claude/working-memory",
                 })
        {
            using var denied = await viewer.GetAsync(route);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var catalogue = await viewer.GetAsync("/api/cli/types");
        Assert.Equal(HttpStatusCode.OK, catalogue.StatusCode);
        using var probe = await viewer.GetAsync("/api/cli/_probe/claude?cmd=unsafe");
        Assert.Equal(HttpStatusCode.NotFound, probe.StatusCode);
    }

    [Fact]
    public async Task Pipeline_catalogue_checks_its_projectName_query_address()
    {
        using var factory = BuildFactory();
        using var developer = Client(factory, Invite(factory, "catalogue.developer",
            new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))));
        using var own = await developer.GetAsync("/api/projects/pipeline-catalogue?projectName=alpha");
        using var foreign = await developer.GetAsync("/api/projects/pipeline-catalogue?projectName=beta");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task Project_bindings_cannot_read_unaddressed_attempts_or_arbitrary_host_folders()
    {
        using var factory = BuildFactory();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var run = Assert.IsType<RunAttemptDto>(authority.AcquireRun(
            "beta-card", "beta", null, "runner", "host", 60, "iam-attempt-run").RunAttempt);
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "iam-attempt-settle"),
            Outcome = "done", ResultSha = new string('a', 40),
        });
        var review = Assert.IsType<ReviewAttemptDto>(authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "beta-card", "beta", new string('a', 40), run.AttemptId, "req", "policy", [], "iam-attempt-review")).ReviewAttempt);
        using var viewer = Client(factory, Invite(factory, "snapshot.viewer",
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("alpha"))));
        using var developer = Client(factory, Invite(factory, "attempt.developer",
            new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))));

        var path = "/api/filesystem-layer/snapshot?rootPath=" + Uri.EscapeDataString(ProjectPath("beta")) + "&refresh=true";
        using var snapshot = await viewer.GetAsync(path);
        // This endpoint is currently local-profile only. Keep it unavailable
        // in the networked host as well as guarded if it is ever mapped there.
        Assert.Equal(HttpStatusCode.NotFound, snapshot.StatusCode);
        foreach (var route in new[]
                 {
                     "/api/attempts/tasks/beta-card",
                     "/api/attempts/runs/" + run.AttemptId,
                     "/api/attempts/reviews/" + review.AttemptId,
                 })
        {
            using var response = await developer.GetAsync(route);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
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

    [Fact]
    public async Task Literal_task_routes_serve_the_owner_workspace_bindings_api_tokens_and_scoped_developers()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var token = store.CreateApiToken(new CreateApiTokenRequest("board bot", StudioRoles.Viewer,
            [ProjectAccessAuthorization.AllProjectsGrant], now.AddDays(1)), ownerId).Secret;
        var callers = new (string Name, HttpClient Client, bool AlphaOnly)[]
        {
            ("owner", Client(factory, Login(factory, "first.owner")), false),
            ("workspace admin", Client(factory, Invite(factory, "workspace.admin", new IamGrantRequest(IamRoles.Admin, IamScope.Workspace))), false),
            ("alpha developer", Client(factory, Invite(factory, "alpha.dev", new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")))), true),
            ("all-projects api token", BearerClient(factory, token), false),
        };

        foreach (var (name, client, alphaOnly) in callers)
        {
            using (client)
            {
                // The Studio board (/api/v1/studio/board aliases /api/tasks/grouped).
                using var board = await client.GetAsync("/api/v1/studio/board");
                Assert.True(board.StatusCode == HttpStatusCode.OK, $"{name}: board answered {(int)board.StatusCode}");
                var cards = await board.Content.ReadAsStringAsync();
                Assert.Contains("alpha-card", cards, StringComparison.Ordinal);
                Assert.Equal(!alphaOnly, cards.Contains("beta-card", StringComparison.Ordinal));
                using var defaults = await client.GetAsync("/api/tasks/code-review/defaults");
                Assert.True(defaults.StatusCode == HttpStatusCode.OK, $"{name}: code-review defaults answered {(int)defaults.StatusCode}");
                using var alphaOwner = await client.GetAsync("/api/tasks/commit-owner/" + AlphaCommit);
                Assert.True(alphaOwner.StatusCode == HttpStatusCode.OK, $"{name}: commit-owner answered {(int)alphaOwner.StatusCode}");
                using var betaOwner = await client.GetAsync("/api/tasks/commit-owner/" + BetaCommit);
                Assert.Equal(alphaOnly ? HttpStatusCode.NotFound : HttpStatusCode.OK, betaOwner.StatusCode);
            }
        }
    }

    [Fact]
    public async Task Orphan_folder_cleanup_needs_workspace_operate()
    {
        using var factory = BuildFactory();
        using var alphaDeveloper = Client(factory, Invite(factory, "orphan.dev", new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))));
        using var alphaAdmin = Client(factory, Invite(factory, "orphan.project.admin", new IamGrantRequest(IamRoles.Admin, IamScope.ForProject("alpha"))));
        using var workspaceDeveloper = Client(factory, Invite(factory, "orphan.workspace.dev", new IamGrantRequest(IamRoles.Developer, IamScope.Workspace)));
        using var workspaceAdmin = Client(factory, Invite(factory, "orphan.admin", new IamGrantRequest(IamRoles.Admin, IamScope.Workspace)));
        using var owner = Client(factory, Login(factory, "first.owner"));

        foreach (var denied in new[] { alphaDeveloper, alphaAdmin, workspaceDeveloper })
        {
            using var response = await denied.SendAsync(OrphanFolderDelete());
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        foreach (var allowed in new[] { workspaceAdmin, owner })
        {
            // The handler answers (the folder does not exist); access is not refused.
            using var response = await allowed.SendAsync(OrphanFolderDelete());
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task An_environment_binding_does_not_lift_a_contributor_above_the_product_role_in_its_project()
    {
        using var factory = BuildFactory();
        using var client = Client(factory, Invite(factory, "stage.contributor",
            new IamGrantRequest(IamRoles.Contributor, IamScope.ForProject("beta")),
            new IamGrantRequest(IamRoles.Developer, IamScope.ForEnvironment("beta", IamEnvironments.Stage))));

        using var progress = await client.PostAsJsonAsync("/api/tasks/beta-card/move",
            new MoveJobRequest { TargetState = TaskStates.Progress });
        Assert.Equal(HttpStatusCode.Forbidden, progress.StatusCode);
        using var delete = await client.DeleteAsync("/api/tasks/beta-card");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        using var model = await client.PutAsJsonAsync("/api/tasks/beta-card/model", new { model = "gpt-5" });
        Assert.Equal(HttpStatusCode.Forbidden, model.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(ProjectPath("beta"), TaskStates.Backlog, "beta-card")));

        using var ready = await client.PostAsJsonAsync("/api/tasks/beta-card/move",
            new MoveJobRequest { TargetState = TaskStates.Ready });
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Operator_override_completion_needs_admin_or_owner()
    {
        using var factory = BuildFactory();
        using var developer = Client(factory, Invite(factory, "override.dev", new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))));
        using var admin = Client(factory, Invite(factory, "override.admin", new IamGrantRequest(IamRoles.Admin, IamScope.ForProject("alpha"))));
        var request = new MoveJobRequest
        {
            TargetState = TaskStates.Completed, OperatorOverride = true, Reason = "Delivered outside Studio; verified by hand.",
        };

        using var refused = await developer.PostAsJsonAsync("/api/tasks/alpha-card/move", request);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("operator-authority-required", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(ProjectPath("alpha"), TaskStates.Backlog, "alpha-card")));

        using var admitted = await admin.PostAsJsonAsync("/api/tasks/alpha-card/move", request);
        Assert.DoesNotContain("operator-authority-required", await admitted.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.Forbidden, admitted.StatusCode);
    }

    [Theory]
    [InlineData("cancel-run")]
    [InlineData("force-fail")]
    public async Task Supervisor_interventions_stop_only_runs_of_the_route_project(string action)
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var alphaToken = store.CreateApiToken(new CreateApiTokenRequest("alpha operator bot", StudioRoles.Operator,
            ["alpha"], now.AddDays(1)), ownerId).Secret;
        var callers = new (string Name, HttpClient Client)[]
        {
            ("alpha developer", Client(factory, Invite(factory, "supervisor.dev", new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))))),
            ("alpha-scoped operator api token", BearerClient(factory, alphaToken)),
        };
        var interventions = AgentStudio.Supervisor.SupervisorLogPaths.InterventionsFile(_workspace, "alpha");

        foreach (var (name, client) in callers)
        {
            using (client)
            {
                foreach (var foreignJob in new[] { "beta-card", "no-such-card" })
                {
                    using var refused = await client.PostAsJsonAsync($"/api/supervisor/alpha/intervene/{action}",
                        new { jobId = foreignJob, reason = "stop it" });
                    Assert.True(refused.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
                        $"{name}: {foreignJob} answered {(int)refused.StatusCode}");
                }
                Assert.False(File.Exists(interventions) && File.ReadAllText(interventions).Contains("beta-card", StringComparison.Ordinal),
                    $"{name}: an intervention was recorded for a beta run");
                Assert.True(Directory.Exists(Path.Combine(ProjectPath("beta"), TaskStates.Backlog, "beta-card")));

                using var allowed = await client.PostAsJsonAsync($"/api/supervisor/alpha/intervene/{action}",
                    new { jobId = "alpha-card", reason = "stop it" });
                Assert.True(allowed.StatusCode == HttpStatusCode.OK, $"{name}: alpha-card answered {(int)allowed.StatusCode}");
            }
        }
        Assert.Contains("alpha-card", File.ReadAllText(interventions), StringComparison.Ordinal);
        Assert.False(File.Exists(AgentStudio.Supervisor.SupervisorLogPaths.InterventionsFile(_workspace, "beta")));
    }

    [Fact]
    public async Task Bus_and_runtime_reads_address_their_project()
    {
        using var factory = BuildFactory();
        using var client = Client(factory, Invite(factory, "bus.dev", new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))));

        foreach (var path in new[] { "/api/bus/beta/messages", "/api/bus/beta/summary", "/api/runtime/beta/events" })
        {
            using var refused = await client.GetAsync(path);
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{path} answered {(int)refused.StatusCode}");
        }
        foreach (var path in new[] { "/api/bus/alpha/messages", "/api/runtime/alpha/events" })
        {
            using var allowed = await client.GetAsync(path);
            Assert.True(allowed.StatusCode != HttpStatusCode.Forbidden, $"{path} answered {(int)allowed.StatusCode}");
        }
    }

    [Fact]
    public async Task Secret_routes_are_decided_by_the_secret_permissions_of_the_bindings()
    {
        using var factory = BuildFactory();
        using var developer = Client(factory, Invite(factory, "secret.dev", new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))));
        using var admin = Client(factory, Invite(factory, "secret.admin", new IamGrantRequest(IamRoles.Admin, IamScope.ForProject("alpha"))));
        var put = new { name = "DEPLOY_KEY", value = "fixture-value" };

        using (var read = await developer.GetAsync("/api/projects/alpha/secrets"))
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using (var foreign = await developer.GetAsync("/api/projects/beta/secrets"))
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using (var write = await developer.PostAsJsonAsync("/api/projects/alpha/secrets", put))
        {
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
            Assert.Contains("permission-denied", await write.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        using (var workspace = await admin.PostAsJsonAsync("/api/secrets/workspace", put))
            Assert.Equal(HttpStatusCode.Forbidden, workspace.StatusCode);
        using (var write = await admin.PostAsJsonAsync("/api/projects/alpha/secrets", put))
            Assert.DoesNotContain("permission-denied", await write.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_lists_filter_each_scope_and_workspace_metadata_needs_a_workspace_binding()
    {
        using var factory = BuildFactory();
        var provider = factory.Services.GetRequiredService<ISecretProvider>();
        var actor = SecretActor.Human("first.owner");
        await provider.PutAsync(SecretScope.Workspace, "WORKSPACE_KEY", "fixture-value", actor);
        await provider.PutAsync(SecretScope.ForProject("alpha"), "ALPHA_KEY", "fixture-value", actor);
        await provider.PutAsync(SecretScope.ForProject("beta"), "BETA_KEY", "fixture-value", actor);
        using var client = Client(factory, Invite(factory, "alpha.reader",
            new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))));

        using var workspace = await client.GetAsync("/api/secrets/workspace");
        Assert.Equal(HttpStatusCode.Forbidden, workspace.StatusCode);
        using var all = await client.GetAsync("/api/secrets");
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        var listing = await all.Content.ReadAsStringAsync();
        Assert.Contains("ALPHA_KEY", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKSPACE_KEY", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("BETA_KEY", listing, StringComparison.Ordinal);
        using var audit = await client.GetAsync("/api/secrets/audit");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var entries = await audit.Content.ReadAsStringAsync();
        Assert.Contains("ALPHA_KEY", entries, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKSPACE_KEY", entries, StringComparison.Ordinal);
        Assert.DoesNotContain("BETA_KEY", entries, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_account_secret_write_keeps_a_non_human_actor_in_metadata_and_audit()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        var account = store.CreateServiceAccount(new CreateServiceAccountRequest("secret-bot", null,
            [new IamGrantRequest(IamRoles.Admin, IamScope.ForProject("alpha"))]), ownerId);
        var token = store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest(null, null)).Secret;
        using var client = BearerClient(factory, token);

        using var response = await client.PostAsJsonAsync("/api/projects/alpha/secrets",
            new { name = "SA_KEY", value = "fixture-value" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var actor = "service-account:" + account.Id;
        var provider = factory.Services.GetRequiredService<ISecretProvider>();
        var record = Assert.Single((await provider.ListAsync(SecretScope.ForProject("alpha"))).Records);
        Assert.Equal(actor, record.CreatedBy);
        Assert.Equal(actor, record.UpdatedBy);
        Assert.Contains(await provider.ReadAuditAsync(SecretScope.ForProject("alpha"), 20),
            entry => entry.Action == SecretAuditActions.Put && entry.Actor == actor && entry.Outcome == "ok");
    }

    [Fact]
    public async Task Production_approval_routes_accept_scoped_human_approvers_and_deny_service_accounts()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        foreach (var (name, scope) in new[]
                 {
                     ("workspace.approver", IamScope.Workspace),
                     ("project.approver", IamScope.ForProject("alpha")),
                     ("environment.approver", IamScope.ForEnvironment("alpha", IamEnvironments.Production)),
                 })
        {
            using var client = Client(factory, Invite(factory, name, new IamGrantRequest(IamRoles.Approver, scope)));
            using var approved = await client.PostAsync("/api/projects/alpha/deploy/deployments/missing/approve", null);
            Assert.NotEqual(HttpStatusCode.Forbidden, approved.StatusCode);
            using var promotion = await client.PostAsJsonAsync("/api/projects/alpha/environments/promotions/approve",
                new { artifact = "missing" });
            Assert.NotEqual(HttpStatusCode.Forbidden, promotion.StatusCode);
            using var foreign = await client.PostAsync("/api/projects/beta/deploy/deployments/missing/approve", null);
            Assert.Equal(scope.Kind == IamScopeKind.Workspace ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden,
                foreign.StatusCode);
        }
        var account = store.CreateServiceAccount(new CreateServiceAccountRequest("approval-bot", null,
            [new IamGrantRequest(IamRoles.Admin, IamScope.ForProject("alpha"))]), ownerId);
        var token = store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest(null, null)).Secret;
        using var service = BearerClient(factory, token);
        using var refused = await service.PostAsync("/api/projects/alpha/deploy/deployments/missing/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var refusedPromotion = await service.PostAsJsonAsync("/api/projects/alpha/environments/promotions/approve",
            new { artifact = "missing" });
        Assert.Equal(HttpStatusCode.Forbidden, refusedPromotion.StatusCode);
    }

    [Fact]
    public async Task Deployment_reads_and_operations_use_distinct_permissions()
    {
        using var factory = BuildFactory();
        using var viewer = Client(factory, Invite(factory, "deploy.viewer",
            new IamGrantRequest(IamRoles.Viewer, IamScope.ForProject("alpha"))));
        using var developer = Client(factory, Invite(factory, "deploy.developer",
            new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))));

        using var alphaRead = await viewer.GetAsync("/api/projects/alpha/deploy/status");
        Assert.NotEqual(HttpStatusCode.Forbidden, alphaRead.StatusCode);
        using var betaRead = await viewer.GetAsync("/api/projects/beta/deploy/status");
        Assert.Equal(HttpStatusCode.Forbidden, betaRead.StatusCode);
        foreach (var collection in new[] { "releases", "deployments" })
        {
            using var alphaPage = await viewer.GetAsync($"/api/projects/alpha/deploy/{collection}/page");
            Assert.NotEqual(HttpStatusCode.Forbidden, alphaPage.StatusCode);
            using var betaPage = await viewer.GetAsync($"/api/projects/beta/deploy/{collection}/page");
            Assert.Equal(HttpStatusCode.Forbidden, betaPage.StatusCode);
        }
        using var refused = await viewer.PostAsJsonAsync("/api/projects/alpha/deploy/releases/missing/deployments",
            new { environmentId = "stage" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var admitted = await developer.PostAsJsonAsync("/api/projects/alpha/deploy/releases/missing/deployments",
            new { environmentId = "stage" });
        Assert.NotEqual(HttpStatusCode.Forbidden, admitted.StatusCode);
        using var deniedApproval = await developer.PostAsync("/api/projects/alpha/deploy/deployments/missing/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, deniedApproval.StatusCode);
    }

    [Fact]
    public async Task Environment_developer_can_request_only_its_declared_deployment_environment()
    {
        using var factory = BuildFactory();
        WriteEnvironmentDeclaration("alpha", "staging-id", "stage");
        WriteEnvironmentDeclaration("alpha", "preview-id", "other");
        var declarations = factory.Services.GetRequiredService<ProjectEnvironmentService>().Declarations("alpha")!;
        Assert.Equal(2, declarations.Count);
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        factory.Services.GetRequiredService<AgentStudio.Deploy.DeployStore>().AppendRelease("alpha",
            new AgentStudio.Deploy.ReleaseRecord(new AgentStudio.Deploy.Release("stage-release", "alpha", "1.0.0",
                new string('a', 40), ["registry.test/app@sha256:" + new string('b', 64)], "process", "variables", [],
                now, AgentStudio.Deploy.ReleaseSnapshot.DefaultChannel), declarations));
        using var client = Client(factory, Invite(factory, "stage.deployer",
            new IamGrantRequest(IamRoles.Developer, IamScope.ForEnvironment("alpha", IamEnvironments.Stage))));

        using var stage = await client.PostAsJsonAsync("/api/projects/alpha/deploy/releases/stage-release/deployments",
            new DeploymentRequest("staging-id"));
        Assert.Equal(HttpStatusCode.Accepted, stage.StatusCode);

        using var other = await client.PostAsJsonAsync("/api/projects/alpha/deploy/releases/stage-release/deployments",
            new DeploymentRequest("preview-id"));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        using var foreign = await client.PostAsJsonAsync("/api/projects/beta/deploy/releases/missing/deployments",
            new DeploymentRequest("staging-id"));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task Telemetry_queries_and_ingest_token_management_follow_project_permissions()
    {
        using var factory = BuildFactory();
        WriteEnvironmentDeclaration("alpha", "stage", "stage");
        WriteEnvironmentDeclaration("beta", "stage", "stage");
        using var developer = Client(factory, Invite(factory, "telemetry.dev",
            new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))));
        using var admin = Client(factory, Invite(factory, "telemetry.admin",
            new IamGrantRequest(IamRoles.Admin, IamScope.ForProject("alpha"))));

        using var alphaRead = await developer.GetAsync("/api/projects/alpha/environments/stage/telemetry/metrics/names");
        Assert.Equal(HttpStatusCode.OK, alphaRead.StatusCode);
        using var betaRead = await developer.GetAsync("/api/projects/beta/environments/stage/telemetry/metrics/names");
        Assert.Equal(HttpStatusCode.Forbidden, betaRead.StatusCode);
        using var developerWrite = await developer.PostAsJsonAsync(
            "/api/projects/alpha/environments/stage/telemetry/tokens", new { name = "collector", signals = new[] { "logs" } });
        Assert.Equal(HttpStatusCode.Forbidden, developerWrite.StatusCode);
        using var adminWrite = await admin.PostAsJsonAsync(
            "/api/projects/alpha/environments/stage/telemetry/tokens", new { name = "collector", signals = new[] { "logs" } });
        Assert.Equal(HttpStatusCode.OK, adminWrite.StatusCode);
        using var foreignWrite = await admin.PostAsJsonAsync(
            "/api/projects/beta/environments/stage/telemetry/tokens", new { name = "collector", signals = new[] { "logs" } });
        Assert.Equal(HttpStatusCode.Forbidden, foreignWrite.StatusCode);
    }

    private HttpRequestMessage OrphanFolderDelete() => new(HttpMethod.Delete, "/api/tasks/orphan-folder")
    {
        Content = JsonContent.Create(new { watchPath = ProjectPath("alpha"), lane = TaskStates.Archive, folder = "no-such-folder" }),
    };

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
                commits = new[] { new { sha = project == "alpha" ? AlphaCommit : BetaCommit, message = project + " change" } },
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

    private string ProjectPath(string project) => Path.Combine(_workspace, "projects", project);

    private void WriteEnvironmentDeclaration(string project, string id, string kind)
    {
        var declarations = Path.Combine(ProjectPath(project), "docs", "deployments", "environments");
        Directory.CreateDirectory(declarations);
        File.WriteAllText(Path.Combine(declarations, id + ".json"), JsonSerializer.Serialize(new
        {
            id, name = id, kind, targetHostId = "test-host", address = "https://example.test",
            template = "container", destination = "8081", healthUrl = "https://example.test/health",
            approval = "person", secretRefs = Array.Empty<string>(),
        }));
    }

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

    private static (string Session, string Csrf) Login(WebApplicationFactory<Program> factory, string username)
    {
        var login = factory.Services.GetRequiredService<AccessSecurityStore>().Login(username, Password, $"{username}|127.0.0.1");
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
