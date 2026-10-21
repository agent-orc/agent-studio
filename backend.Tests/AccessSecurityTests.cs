using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

public sealed class AccessSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-security-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Bootstrap_password_session_and_runner_secrets_are_hashed_at_rest()
    {
        var (store, _, _) = NewStore();
        var password = "correct horse battery staple!";
        var owner = store.Bootstrap(new BootstrapRequest("first.owner", password, "First Owner"));
        var enrollment = store.CreateEnrollment(new RunnerEnrollmentRequest("build-runner-01", null, null, null));
        var runner = store.EnrollRunner(enrollment.Code);

        var persisted = File.ReadAllText(Path.Combine(store.SecurityFolder, "access-state.json"));
        Assert.DoesNotContain(password, persisted);
        Assert.DoesNotContain(owner.SessionToken, persisted);
        Assert.DoesNotContain(owner.CsrfToken, persisted);
        Assert.DoesNotContain(enrollment.Code, persisted);
        Assert.DoesNotContain(runner.Secret, persisted);
        Assert.Contains("pbkdf2-sha512$600000$", persisted);
        Assert.NotNull(store.AuthenticateSession(owner.SessionToken));
        Assert.NotNull(store.AuthenticateRunner(runner.Secret));
    }

    [Fact]
    public void Runner_rotation_overlaps_then_individual_and_identity_revoke_fail_closed()
    {
        var (store, _, _) = NewStore();
        store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var enrollment = store.CreateEnrollment(new RunnerEnrollmentRequest("runner-01", [RunnerScopes.Claim], null, null));
        var first = store.EnrollRunner(enrollment.Code);
        var second = store.RotateRunner(first.Runner.Id, new RunnerRotateRequest([RunnerScopes.Claim, RunnerScopes.Logs], null));

        Assert.NotNull(store.AuthenticateRunner(first.Secret));
        Assert.NotNull(store.AuthenticateRunner(second.Secret));
        store.RevokeCredential(first.Runner.Id, first.Credential.Id);
        Assert.Null(store.AuthenticateRunner(first.Secret));
        Assert.NotNull(store.AuthenticateRunner(second.Secret));
        store.RevokeRunner(first.Runner.Id);
        Assert.Null(store.AuthenticateRunner(second.Secret));
    }

    [Fact]
    public void Engine_enrollment_issues_an_exclusive_scoped_principal()
    {
        var (store, _, _) = NewStore();
        var enrollment = store.CreateEnrollment(new RunnerEnrollmentRequest(
            "engine-host", [RunnerScopes.Orchestration], null, null));
        var issued = store.EnrollRunner(enrollment.Code);
        var principal = store.AuthenticateRunner(issued.Secret);

        Assert.StartsWith("engine_", issued.Runner.Id);
        Assert.Equal(issued.Runner.Id, principal!.RunnerId);
        Assert.Contains(RunnerScopes.Orchestration, principal.Scopes);
        Assert.DoesNotContain(RunnerScopes.Claim, principal.Scopes);
        Assert.Throws<SecurityOperationException>(() => store.CreateEnrollment(
            new RunnerEnrollmentRequest("bad-engine", [RunnerScopes.Orchestration, RunnerScopes.Claim], null, null)));
        Assert.Throws<SecurityOperationException>(() => store.RotateRunner(issued.Runner.Id,
            new RunnerRotateRequest([RunnerScopes.Claim], null)));
    }

    [Fact]
    public async Task Pull_gate_credential_is_exclusive_and_fences_its_routes()
    {
        var (store, config, _) = NewStore();
        store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var gate = store.EnrollRunner(store.CreateEnrollment(
            new RunnerEnrollmentRequest("gate-host", [RunnerScopes.Gate], null, null)).Code);
        var ordinary = store.EnrollRunner(store.CreateEnrollment(
            new RunnerEnrollmentRequest("ordinary-runner", [RunnerScopes.Claim], null, null)).Code);
        Assert.Throws<SecurityOperationException>(() => store.CreateEnrollment(
            new RunnerEnrollmentRequest("mixed", [RunnerScopes.Gate, RunnerScopes.Claim], null, null)));
        foreach (var (method, path) in new[]
        {
            ("POST", "/api/pull-gates/register"),
            ("POST", "/api/pull-gates/claim"),
            ("GET", "/api/pull-gates/runs/run/request.json"),
            ("POST", "/api/pull-gates/runs/run/heartbeat"),
            ("POST", "/api/pull-gates/runs/run/progress"),
            ("PUT", "/api/pull-gates/runs/run/artifacts/release.zip"),
            ("POST", "/api/pull-gates/runs/run/result"),
        })
        {
            Assert.True((await Invoke(config, store, method, path, bearer: gate.Secret)).Called.Value, path);
            Assert.Equal(403, (await Invoke(config, store, method, path, bearer: ordinary.Secret)).Context.Response.StatusCode);
        }
        Assert.True((await Invoke(config, store, "GET", "/api/gate-artifacts/run",
            bearer: store.EnrollRunner(store.CreateEnrollment(
                new RunnerEnrollmentRequest("artifact-reader", [RunnerScopes.Artifacts], null, null)).Code).Secret)).Called.Value);
        Assert.Equal(403, (await Invoke(config, store, "GET", "/api/gate-artifacts/run",
            bearer: gate.Secret)).Context.Response.StatusCode);
    }

    [Fact]
    public void Runner_lifecycle_and_activity_share_the_credential_registry()
    {
        var (store, _, _) = NewStore();
        var enrollment = store.CreateEnrollment(new RunnerEnrollmentRequest("runner-managed", null, null, null));
        var enrolled = store.EnrollRunner(enrollment.Code);

        var active = store.RecordRunnerActivity(enrolled.Runner.Id, 1, 0, claimed: true);
        Assert.Equal(1, active!.ActiveSlots);
        Assert.NotNull(active.LastClaimAt);
        store.RequestRunnerDrain(enrolled.Runner.Id, retireAfterDrain: true);
        Assert.False(store.RunnerAcceptsClaims(enrolled.Runner.Id));
        Assert.NotNull(store.AuthenticateRunner(enrolled.Secret));

        var retired = store.RecordRunnerActivity(enrolled.Runner.Id, 0, 1, claimed: false);
        Assert.NotNull(retired!.RetiredAt);
        Assert.Null(store.AuthenticateRunner(enrolled.Secret));
    }

    [Fact]
    public void Expired_enrollment_and_runner_credential_fail_closed()
    {
        var (store, _, clock) = NewStore();
        store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var expiredEnrollment = store.CreateEnrollment(new RunnerEnrollmentRequest(
            "runner-expiring-enrollment", [RunnerScopes.Claim], clock.GetUtcNow().UtcDateTime.AddMinutes(1), null));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Throws<SecurityOperationException>(() => store.EnrollRunner(expiredEnrollment.Code));

        var liveEnrollment = store.CreateEnrollment(new RunnerEnrollmentRequest(
            "runner-expiring-credential", [RunnerScopes.Claim], null, clock.GetUtcNow().UtcDateTime.AddMinutes(1)));
        var enrolled = store.EnrollRunner(liveEnrollment.Code);
        Assert.NotNull(store.AuthenticateRunner(enrolled.Secret));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(store.AuthenticateRunner(enrolled.Secret));
    }

    [Fact]
    public async Task Networked_profile_denies_anonymous_reads_mutations_registration_and_hubs()
    {
        var (store, config, _) = NewStore();
        foreach (var (method, path, expected) in new[]
        {
            ("GET", "/api/tasks", 401),
            ("POST", "/api/tasks", 401),
            ("POST", "/api/clients/register", 404),
            ("POST", "/hubs/jobs/negotiate", 401)
        })
        {
            var (context, called) = await Invoke(config, store, method, path);
            Assert.Equal(expected, context.Response.StatusCode);
            Assert.False(called.Value);
        }
    }

    [Fact]
    public async Task Networked_profile_rejects_cleartext_application_requests()
    {
        var (store, config, _) = NewStore();
        var result = await Invoke(config, store, "GET", "/api/auth/status", scheme: "http");
        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.Context.Response.StatusCode);
        Assert.False(result.Called.Value);
    }

    [Fact]
    public async Task Browser_mutations_require_csrf_and_viewers_cannot_mutate()
    {
        var (store, config, _) = NewStore();
        store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var created = store.CreateUser(new CreateUserRequest("ops.user", "Ops", StudioRoles.Operator, null, "temporary pass phrase!"));
        var login = store.Login(created.User.Username, created.TemporaryPassword, "ops|127.0.0.1");
        store.ChangePassword(new HumanPrincipal(login.User, store.AuthenticateSession(login.SessionToken)!.Session),
            new ChangePasswordRequest(created.TemporaryPassword, "new permanent pass phrase!"));

        var missing = await Invoke(config, store, "POST", "/api/tasks", login.SessionToken);
        Assert.Equal(StatusCodes.Status403Forbidden, missing.Context.Response.StatusCode);
        var valid = await Invoke(config, store, "POST", "/api/tasks", login.SessionToken, login.CsrfToken);
        Assert.True(valid.Called.Value);

        var viewer = store.CreateUser(new CreateUserRequest("view.user", "Viewer", StudioRoles.Viewer, null, "temporary viewer phrase!"));
        var viewerLogin = store.Login(viewer.User.Username, viewer.TemporaryPassword, "viewer|127.0.0.1");
        store.ChangePassword(new HumanPrincipal(viewerLogin.User, store.AuthenticateSession(viewerLogin.SessionToken)!.Session),
            new ChangePasswordRequest(viewer.TemporaryPassword, "new viewer password phrase!"));
        var denied = await Invoke(config, store, "POST", "/api/tasks", viewerLogin.SessionToken, viewerLogin.CsrfToken);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.Context.Response.StatusCode);
        var readOnlyPost = await Invoke(config, store, "POST", "/api/tasks/reference-status", viewerLogin.SessionToken);
        Assert.True(readOnlyPost.Called.Value);
        var logout = await Invoke(config, store, "POST", "/api/auth/logout", viewerLogin.SessionToken, viewerLogin.CsrfToken);
        Assert.True(logout.Called.Value);
    }

    [Fact]
    public async Task Project_membership_and_runner_scope_are_enforced()
    {
        var (store, config, _) = NewStore();
        store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var user = store.CreateUser(new CreateUserRequest("project.user", "Project User", StudioRoles.Operator, ["PROJ-001"], "temporary project phrase!"));
        var login = store.Login(user.User.Username, user.TemporaryPassword, "project|127.0.0.1");
        store.ChangePassword(new HumanPrincipal(login.User, store.AuthenticateSession(login.SessionToken)!.Session),
            new ChangePasswordRequest(user.TemporaryPassword, "new project password phrase!"));
        var allowed = await Invoke(config, store, "GET", "/api/projects/PROJ-001/security", login.SessionToken);
        var denied = await Invoke(config, store, "GET", "/api/projects/PROJ-002/security", login.SessionToken);
        var runnerAllowed = await Invoke(config, store, "GET", "/api/runner/PROJ-001/orchestrator-log", login.SessionToken);
        var runnerDenied = await Invoke(config, store, "GET", "/api/runner/PROJ-002/orchestrator-log", login.SessionToken);
        var contextAllowed = await Invoke(config, store, "GET", "/api/orchestrator/context/project:PROJ-001", login.SessionToken);
        var contextDenied = await Invoke(config, store, "GET", "/api/orchestrator/context/project:PROJ-002", login.SessionToken);
        var globalDenied = await Invoke(config, store, "GET", "/api/orchestrator/context/global", login.SessionToken);
        Assert.True(allowed.Called.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.Context.Response.StatusCode);
        Assert.True(runnerAllowed.Called.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, runnerDenied.Context.Response.StatusCode);
        Assert.True(contextAllowed.Called.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, contextDenied.Context.Response.StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, globalDenied.Context.Response.StatusCode);

        var enrollment = store.CreateEnrollment(new RunnerEnrollmentRequest("runner-scoped", [RunnerScopes.Logs], null, null));
        var runner = store.EnrollRunner(enrollment.Code);
        var logAllowed = await Invoke(config, store, "POST", "/api/runner/logs", bearer: runner.Secret);
        var claimDenied = await Invoke(config, store, "POST", "/api/runner/claim", bearer: runner.Secret);
        Assert.True(logAllowed.Called.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, claimDenied.Context.Response.StatusCode);
    }

    // AGT-3055: a gate host claims and reports batch suites on the monolith
    // authority through /api/v1/gates; the networked profile must admit its
    // runner credential there with the matching runner scope.
    [Fact]
    public async Task Gate_routes_admit_a_scoped_runner_and_reject_everyone_else()
    {
        var (store, config, _) = NewStore();
        var owner = store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var gateRunner = store.EnrollRunner(store.CreateEnrollment(
            new RunnerEnrollmentRequest("gate-runner", null, null, null)).Code);
        var logsOnly = store.EnrollRunner(store.CreateEnrollment(
            new RunnerEnrollmentRequest("logs-runner", [RunnerScopes.Logs], null, null)).Code);

        foreach (var (method, path) in new[]
                 {
                     ("POST", "/api/v1/gates/claims"),
                     ("POST", "/api/v1/gates/attempts/gat_1/renew"),
                     ("POST", "/api/v1/gates/attempts/gat_1/phase"),
                     ("POST", "/api/v1/gates/attempts/gat_1/report"),
                     ("POST", "/api/v1/gates/attempts/gat_1/containment"),
                     ("GET", "/api/v1/gates/subjects/gsub_1"),
                 })
        {
            Assert.True((await Invoke(config, store, method, path, bearer: gateRunner.Secret)).Called.Value, path);
            Assert.Equal(StatusCodes.Status403Forbidden,
                (await Invoke(config, store, method, path, bearer: logsOnly.Secret)).Context.Response.StatusCode);
            Assert.Equal(StatusCodes.Status401Unauthorized,
                (await Invoke(config, store, method, path, owner.SessionToken, owner.CsrfToken)).Context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task TaskServerClient_routes_require_the_matching_runner_scope()
    {
        var (store, config, _) = NewStore();
        store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var scoped = new Dictionary<string, string>();
        foreach (var scope in RunnerScopes.Minimum)
            scoped[scope] = store.EnrollRunner(store.CreateEnrollment(
                new RunnerEnrollmentRequest(scope, [scope], null, null)).Code).Secret;
        var runner = store.EnrollRunner(store.CreateEnrollment(
            new RunnerEnrollmentRequest("full runner", null, null, null)).Code);
        var id = runner.Runner.Id;
        var routes = new (string Method, string Path, string Scope)[]
        {
            ("PUT", $"/api/v1/runners/{id}", RunnerScopes.Claim),
            ("POST", $"/api/v1/runners/{id}/claims", RunnerScopes.Claim),
            ("POST", $"/api/v1/runners/{id}/review-claims", RunnerScopes.Claim),
            ("PUT", $"/api/v1/runners/{id}/capabilities", RunnerScopes.Events),
            ("POST", $"/api/v1/runners/{id}/capability-failures", RunnerScopes.Events),
            ("PUT", $"/api/v1/runners/{id}/outbox-status", RunnerScopes.Events),
            ("POST", $"/api/v1/runners/{id}/reports", RunnerScopes.Events),
            ("GET", $"/api/v1/runners/{id}/cli-update", RunnerScopes.Claim),
            ("POST", $"/api/v1/runners/{id}/cli-update/result", RunnerScopes.Events),
            ("POST", $"/api/v1/runners/{id}/provider-canary/codex", RunnerScopes.Claim),
            ("GET", $"/api/v1/runners/{id}/provider-comparison", RunnerScopes.Claim),
            ("POST", "/api/v1/work-permits/permit/accept", RunnerScopes.Claim),
            ("POST", "/api/v1/runs/run/reconcile", RunnerScopes.Claim),
            ("POST", "/api/v1/runs/run/post-steps/step/claim", RunnerScopes.Claim),
            ("POST", "/api/v1/runs/run/post-steps/step/complete", RunnerScopes.Completion),
            ("POST", "/api/v1/runs/run/lease/renew", RunnerScopes.Lease),
            ("POST", "/api/v1/runs/run/lease/release", RunnerScopes.Lease),
            ("POST", "/api/v1/runs/run/events", RunnerScopes.Events),
            ("POST", "/api/v1/runs/run/artifacts", RunnerScopes.Artifacts),
            ("GET", "/api/v1/runs/run/artifacts/artifact/content", RunnerScopes.Artifacts),
            ("GET", "/api/v1/artifact-limits", RunnerScopes.Claim),
            ("POST", "/api/v1/runs/run/completion", RunnerScopes.Completion),
            ("POST", "/api/v1/runs/run/result-finalization", RunnerScopes.Completion),
            ("PUT", "/api/v1/runs/run/result-handoff", RunnerScopes.Completion),
            ("POST", "/api/v1/runs/run/revoked-reference", RunnerScopes.Completion),
            ("GET", "/api/v1/projects", RunnerScopes.Claim),
            ("GET", "/api/v1/projects/project/tasks/task", RunnerScopes.Claim),
            ("GET", "/api/v1/reviews/attempts/attempt", RunnerScopes.Claim),
            ("POST", "/api/v1/reviews/attempts/attempt/lease/renew", RunnerScopes.Lease),
            ("POST", "/api/v1/reviews/attempts/attempt/reclaim", RunnerScopes.Claim),
            ("POST", "/api/v1/reviews/attempts/attempt/report", RunnerScopes.Completion),
            ("POST", "/api/v1/reviews/attempts/attempt/cleanup", RunnerScopes.Completion),
            ("POST", "/api/v1/integrations/claims", RunnerScopes.Claim),
            ("POST", "/api/v1/integrations/integration/lease/renew", RunnerScopes.Lease),
            ("POST", "/api/v1/integrations/integration/report", RunnerScopes.Completion),
            ("GET", "/api/v1/failure-fingerprints", RunnerScopes.Claim),
            ("POST", "/api/v1/failure-fingerprints", RunnerScopes.Events),
            ("POST", "/api/v1/gates/claims", RunnerScopes.Claim),
            ("GET", "/api/v1/gates/subjects/subject", RunnerScopes.Claim),
            ("POST", "/api/v1/gates/attempts/attempt/renew", RunnerScopes.Lease),
            ("POST", "/api/v1/gates/attempts/attempt/phase", RunnerScopes.Events),
            ("POST", "/api/v1/gates/attempts/attempt/report", RunnerScopes.Completion),
            ("POST", "/api/v1/gates/attempts/attempt/containment", RunnerScopes.Completion),
            ("POST", "/api/runner/claim", RunnerScopes.Claim),
            ("POST", "/api/runner/lease/acquire", RunnerScopes.Lease),
            ("POST", "/api/runner/lease/renew", RunnerScopes.Lease),
            ("POST", "/api/runner/lease/release", RunnerScopes.Lease),
            ("POST", "/api/runner/lease/attempt/revoked-reference", RunnerScopes.Lease),
            ("POST", "/api/runner/logs", RunnerScopes.Logs),
            ("POST", "/api/runner/events", RunnerScopes.Events),
            ("POST", "/api/runner/artifacts", RunnerScopes.Artifacts),
            ("GET", "/api/runner/artifacts/limits", RunnerScopes.Artifacts),
            ("POST", "/api/runner/artifacts/outcome", RunnerScopes.Artifacts),
            ("POST", "/api/runner/completion", RunnerScopes.Completion),
            ("POST", "/api/runner/project-chat/claim", RunnerScopes.Claim),
            ("POST", "/api/runner/project-chat/renew", RunnerScopes.Lease),
            ("POST", "/api/runner/project-chat/complete", RunnerScopes.Completion),
            ("POST", "/api/runner/orchestrator-decisions/claim", RunnerScopes.Claim),
            ("POST", "/api/runner/orchestrator-decisions/complete", RunnerScopes.Completion),
            ("POST", "/api/runner/epic-planning-prompt", RunnerScopes.Claim),
            ("GET", "/api/system/about", RunnerScopes.Claim),
            ("POST", $"/api/clients/{id}/runner-git-capability", RunnerScopes.Events),
        };

        foreach (var (method, path, scope) in routes)
        {
            Assert.True((await Invoke(config, store, method, path, bearer: runner.Secret)).Called.Value, path);
            Assert.Equal(401, (await Invoke(config, store, method, path)).Context.Response.StatusCode);
            var wrongScope = scope == RunnerScopes.Claim ? RunnerScopes.Logs : RunnerScopes.Claim;
            Assert.Equal(403, (await Invoke(config, store, method, path, bearer: scoped[wrongScope])).Context.Response.StatusCode);
        }
        Assert.Equal(403, (await Invoke(config, store, "PUT", "/api/v1/runners/runner_other", bearer: runner.Secret)).Context.Response.StatusCode);
        Assert.True((await Invoke(config, store, "POST", "/api/v1/protocol/compatibility", bearer: runner.Secret)).Called.Value);
        Assert.Equal(401, (await Invoke(config, store, "POST", "/api/v1/protocol/compatibility")).Context.Response.StatusCode);
        Assert.Equal(403, (await Invoke(config, store, "POST", "/api/v1/protocol/compatibility", bearer: scoped[RunnerScopes.Logs])).Context.Response.StatusCode);
    }

    [Fact]
    public async Task Shared_runner_reads_accept_human_sessions_without_weakening_runner_scopes()
    {
        var (store, config, _) = NewStore();
        var owner = store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var logsOnly = store.EnrollRunner(store.CreateEnrollment(
            new RunnerEnrollmentRequest("logs-only", [RunnerScopes.Logs], null, null)).Code);
        var paths = new[]
        {
            "/api/system/about",
            "/api/v1/projects",
            "/api/v1/projects/project/tasks/task",
            "/api/v1/reviews/attempts/attempt",
            "/api/v1/failure-fingerprints"
        };

        foreach (var path in paths)
        {
            Assert.True((await Invoke(config, store, "GET", path, owner.SessionToken)).Called.Value, path);
            Assert.Equal(StatusCodes.Status401Unauthorized,
                (await Invoke(config, store, "GET", path)).Context.Response.StatusCode);
            Assert.Equal(StatusCodes.Status403Forbidden,
                (await Invoke(config, store, "GET", path, bearer: logsOnly.Secret)).Context.Response.StatusCode);
        }

        Assert.Equal(StatusCodes.Status401Unauthorized,
            (await Invoke(config, store, "POST", "/api/v1/reviews/attempts/attempt/report", owner.SessionToken, owner.CsrfToken))
                .Context.Response.StatusCode);
    }

    [Fact]
    public void Session_expiry_and_login_throttling_fail_closed()
    {
        var (store, _, clock) = NewStore(new Dictionary<string, string?>
        {
            ["Security:Session:IdleMinutes"] = "1",
            ["Security:Session:AbsoluteMinutes"] = "2",
            ["Security:LoginThrottle:MaxFailures"] = "2"
        });
        var password = "correct horse battery staple!";
        var owner = store.Bootstrap(new BootstrapRequest("first.owner", password, null));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(store.AuthenticateSession(owner.SessionToken));

        Assert.Throws<SecurityOperationException>(() => store.Login("first.owner", "wrong password 1", "owner|ip"));
        Assert.Throws<SecurityOperationException>(() => store.Login("first.owner", "wrong password 2", "owner|ip"));
        var throttled = Assert.Throws<SecurityOperationException>(() => store.Login("first.owner", password, "owner|ip"));
        Assert.Equal(429, throttled.Status);
    }

    [Fact]
    public void Guessed_usernames_and_addresses_keep_throttle_state_bounded()
    {
        var (store, _, clock) = NewStore(new Dictionary<string, string?>
        {
            ["Security:LoginThrottle:MaxFailuresPerUsername"] = "2",
            ["Security:LoginThrottle:MaxFailures"] = "2",
            ["Security:LoginThrottle:MaxTrackedKeys"] = "8"
        });
        store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));

        var field = typeof(AccessSecurityStore).GetField("_usernameFailures",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var budgets = Assert.IsAssignableFrom<System.Collections.IDictionary>(field!.GetValue(store));
        var clientField = typeof(AccessSecurityStore).GetField("_loginAttempts",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var clients = Assert.IsAssignableFrom<System.Collections.IDictionary>(clientField!.GetValue(store));

        for (var index = 0; index < 16; index++)
        {
            var failure = Assert.Throws<SecurityOperationException>(() =>
                store.Login($"guessed.user.{index}", "wrong password", $"unknown|{index}"));
            Assert.Equal(index < 8 ? 401 : 429, failure.Status);
            Assert.InRange(clients.Count + budgets.Count, 0, 8);
        }
        Assert.Equal(8, clients.Count);
        Assert.Empty(budgets);
        Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
            store.Login("guessed.user.0", "wrong password", "unknown|0")).Status);
        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("guessed.user.0", "wrong password", "unknown|0")).Status);
        Assert.Equal(8, clients.Count);

        clock.Advance(TimeSpan.FromMinutes(16));
        for (var index = 0; index < 2; index++)
            Assert.Equal(401, Assert.Throws<SecurityOperationException>(() =>
                store.Login("first.owner", "wrong password", $"owner|{index}")).Status);
        Assert.Equal(2, clients.Count);
        Assert.Single(budgets);
        Assert.Equal(429, Assert.Throws<SecurityOperationException>(() =>
            store.Login("first.owner", "correct horse battery staple!", "owner|new-address")).Status);
    }

    [Fact]
    public async Task Large_http_login_usernames_keep_throttle_keys_and_state_bounded()
    {
        var (store, config, clock) = NewStore(new Dictionary<string, string?>
        {
            ["Security:LoginThrottle:MaxTrackedKeys"] = "8"
        });
        store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var field = typeof(AccessSecurityStore).GetField("_loginAttempts",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var attempts = Assert.IsAssignableFrom<System.Collections.IDictionary>(field!.GetValue(store));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(store);
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Remote"]!);
            return next();
        });
        app.MapAccessSecurityEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();

        for (var index = 0; index < 16; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new LoginRequest($"guess{index}." + new string('x', 3000), "wrong password"))
            };
            request.Headers.Add("X-Test-Remote", $"198.51.100.{index + 1}");
            using var response = await client.SendAsync(request);
            Assert.Equal(index < 8 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.InRange(attempts.Count, 0, 8);
        }
        Assert.Equal(8, attempts.Count);
        foreach (string key in attempts.Keys) Assert.Equal(64, key.Length);

        foreach (var chunked in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new LoginRequest("oversized." + new string('z', 5000), "wrong password"))
            };
            request.Headers.Add("X-Test-Remote", "198.51.100.200");
            request.Headers.TransferEncodingChunked = chunked;
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("invalid-credentials", payload.RootElement.GetProperty("error").GetString());
            Assert.Equal("Username or password is incorrect.", payload.RootElement.GetProperty("message").GetString());
            Assert.Equal(8, attempts.Count);
        }

        clock.Advance(TimeSpan.FromMinutes(16));
        using var validRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("first.owner", "correct horse battery staple!"))
        };
        validRequest.Headers.Add("X-Test-Remote", "198.51.100.201");
        using var valid = await client.SendAsync(validRequest);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Empty(attempts);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("Authorization: Bearer rnr.abc.abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("password=super-secret")]
    [InlineData("ssn.abc.abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("{\"temporaryPassword\":\"one-time-value\"}")]
    [InlineData("{\"password\":\"correct horse battery staple\"}")]
    [InlineData("https://tasks.test/enroll?access_token=query-secret")]
    [InlineData("X-CSRF-Token: csrf-secret")]
    [InlineData("Cookie: arbitrary-session=value; preference=also-private")]
    public void Credential_redaction_removes_secret_material(string input)
    {
        var output = CredentialRedactor.Redact(input);
        Assert.DoesNotContain("super-secret", output);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz012345", output);
        Assert.DoesNotContain("one-time-value", output);
        Assert.DoesNotContain("query-secret", output);
        Assert.DoesNotContain("csrf-secret", output);
        Assert.DoesNotContain("horse battery", output);
        Assert.DoesNotContain("also-private", output);
        Assert.Contains("REDACT", output);
    }

    [Fact]
    public void Scoped_project_authorization_denies_unaddressed_and_other_projects()
    {
        var user = new StudioUser
        {
            Id = "usr_project", Username = "project.user", DisplayName = "Project User",
            Role = StudioRoles.Operator, PasswordHash = "unused", Projects = ["PROJ-001"],
            CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow
        };

        Assert.True(ProjectAccessAuthorization.Allows(user, "PROJ-001"));
        Assert.False(ProjectAccessAuthorization.Allows(user, "PROJ-002"));
        Assert.False(ProjectAccessAuthorization.Allows(user, null));
    }

    [Fact]
    public void Run_audit_redacts_credentials_before_persistence()
    {
        var (store, _, _) = NewStore();
        const string secret = "rnr.credential.abcdefghijklmnopqrstuvwxyz012345";
        store.AppendRunAudit(new RunSecurityAuditEvent(
            DateTime.UtcNow, "completion", "AGT-1", "PROJ-1",
            "usr_owner", "runner_1", "credential", Outcome: $"done {secret}"));

        var persisted = File.ReadAllText(Path.Combine(store.SecurityFolder, "run-audit.jsonl"));
        Assert.DoesNotContain(secret, persisted);
        Assert.Contains("REDACTED_CREDENTIAL", persisted);
    }

    [Fact]
    public void Reference_caddy_profile_contains_transport_limits_and_websocket_proxy()
    {
        var path = Path.Combine(RepositoryRoot.Find(), "deploy", "networked", "Caddyfile");
        var caddy = File.ReadAllText(path);
        Assert.Contains("http://{$STUDIO_DOMAIN}", caddy);
        Assert.Contains("Strict-Transport-Security", caddy);
        Assert.Contains("max_size 25MB", caddy);
        Assert.Contains("/hubs/*", caddy);
        Assert.Contains("reverse_proxy 127.0.0.1:5030", caddy);
        Assert.Contains("@debug", caddy);
    }

    [Fact]
    public async Task Scoped_task_mutations_fail_closed_or_defer_to_handlers()
    {
        var (store, config, _) = NewStore();
        store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var user = store.CreateUser(new CreateUserRequest("scoped.op", "Scoped", StudioRoles.Operator, ["PROJ-001"], "temporary scoped phrase!"));
        var login = store.Login(user.User.Username, user.TemporaryPassword, "scoped|127.0.0.1");
        store.ChangePassword(new HumanPrincipal(login.User, store.AuthenticateSession(login.SessionToken)!.Session),
            new ChangePasswordRequest(user.TemporaryPassword, "new scoped password phrase!"));

        // A single-task, path-addressed mutation whose project cannot be inferred
        // (no scanner wired here) must fail closed, not fall through to the
        // endpoint. This is the regression guard for the move-to-top / change-project
        // authorization gap: an unresolved project is denied, never allowed.
        var moveTop = await Invoke(config, store, "POST", "/api/tasks/AGT-1/move-to-top", login.SessionToken, login.CsrfToken);
        Assert.Equal(StatusCodes.Status403Forbidden, moveTop.Context.Response.StatusCode);
        Assert.False(moveTop.Called.Value);

        // Body-addressed task-set mutations carry their targets in the request
        // body; the middleware defers to the handler, which enforces per-task
        // membership via ProjectAccessAuthorization.AllowsTasks.
        var reorder = await Invoke(config, store, "POST", "/api/tasks/reorder", login.SessionToken, login.CsrfToken);
        Assert.True(reorder.Called.Value);
        var batch = await Invoke(config, store, "POST", "/api/tasks/batch-move", login.SessionToken, login.CsrfToken);
        Assert.True(batch.Called.Value);
    }

    [Fact]
    public void Body_addressed_task_authorization_enforces_membership_and_fails_closed()
    {
        var owner = new StudioUser
        {
            Id = "usr_owner", Username = "owner", DisplayName = "Owner",
            Role = StudioRoles.Owner, PasswordHash = "unused",
            CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow
        };
        var scoped = new StudioUser
        {
            Id = "usr_scoped", Username = "scoped", DisplayName = "Scoped",
            Role = StudioRoles.Operator, PasswordHash = "unused", Projects = ["PROJ-001"],
            CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow
        };

        static HttpContext Ctx(StudioUser user)
        {
            var context = new DefaultHttpContext();
            var session = new StudioSession
            {
                Id = "sess", UserId = user.Id, TokenHash = "h", CsrfHash = "h",
                CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(1), AbsoluteExpiresAt = DateTime.UtcNow.AddHours(8)
            };
            context.Items[AccessSecurityMiddleware.HumanPrincipalItem] = new HumanPrincipal(user, session);
            return context;
        }

        // Owner is never project-scoped, even across a mixed / partly-unresolved set.
        Assert.True(ProjectAccessAuthorization.AllowsTasks(Ctx(owner), ["PROJ-001", "PROJ-002", null]));
        // Scoped operator: allowed only when every affected task is inside membership.
        Assert.True(ProjectAccessAuthorization.AllowsTasks(Ctx(scoped), ["PROJ-001"]));
        Assert.False(ProjectAccessAuthorization.AllowsTasks(Ctx(scoped), ["PROJ-001", "PROJ-002"]));
        Assert.False(ProjectAccessAuthorization.AllowsTasks(Ctx(scoped), ["PROJ-002"]));
        // A task whose project could not be resolved fails closed for a scoped user.
        Assert.False(ProjectAccessAuthorization.AllowsTasks(Ctx(scoped), new string?[] { null }));
        // No human principal (local profile / Runner route) is not scoped here.
        Assert.True(ProjectAccessAuthorization.AllowsTasks(new DefaultHttpContext(), ["PROJ-002"]));
    }

    private (AccessSecurityStore Store, IConfiguration Configuration, MutableTimeProvider Clock) NewStore(Dictionary<string, string?>? overrides = null)
    {
        Directory.CreateDirectory(_root);
        var values = new Dictionary<string, string?>
        {
            ["TaskRepository"] = Path.Combine(_root, Guid.NewGuid().ToString("N")),
            ["Security:Profile"] = "networked"
        };
        if (overrides is not null) foreach (var pair in overrides) values[pair.Key] = pair.Value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-13T12:00:00Z"));
        return (new AccessSecurityStore(config, NullLogger<AccessSecurityStore>.Instance, clock), config, clock);
    }

    private static async Task<(DefaultHttpContext Context, BoolBox Called)> Invoke(
        IConfiguration config, AccessSecurityStore store, string method, string path,
        string? sessionToken = null, string? csrf = null, string? bearer = null, string scheme = "https")
    {
        var called = new BoolBox();
        var middleware = new AccessSecurityMiddleware(ctx => { called.Value = true; return Task.CompletedTask; }, config, store);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = scheme;
        if (sessionToken is not null)
        {
            context.Request.Headers.Cookie = new StringValues($"{AccessSecurityStore.SessionCookieName}={sessionToken}");
            // A browser session request from a Studio page (AGT-3230 Origin rule).
            context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        }
        if (csrf is not null) context.Request.Headers["X-CSRF-Token"] = new StringValues(csrf);
        if (bearer is not null) context.Request.Headers.Authorization = new StringValues("Bearer " + bearer);
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        return (context, called);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class BoolBox { public bool Value; }
    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
