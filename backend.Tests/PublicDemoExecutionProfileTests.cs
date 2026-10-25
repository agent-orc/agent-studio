using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

public sealed class PublicDemoExecutionProfileTests(PublicDemoHostFixture shared, ITestOutputHelper output) : IClassFixture<PublicDemoHostFixture>, IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        "studio-public-demo-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Public_demo_policy_is_deny_only_for_every_execution_path()
    {
        var admission = new StartupExecutionAdmission(ExecutionAdmissionPolicy.PublicDemoProfile);

        foreach (var path in ExecutionAdmissionPolicy.AllPaths)
        {
            var decision = admission.Decide(path);
            Assert.False(decision.Allowed);
            Assert.Equal(ExecutionAdmissionPolicy.ExecutionDisabledCode, decision.Code);
            var exception = Assert.Throws<ExecutionAdmissionDeniedException>(() => admission.Demand(path));
            Assert.Equal(path, exception.Path);
            Assert.Equal(decision, exception.Decision);
        }
    }

    [Fact]
    public void Unknown_security_profile_refuses_startup_identity_resolution()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:Profile"] = "public-demo-typo",
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => SecurityProfiles.ActiveProfile(configuration));

        Assert.Contains("Unsupported Security:Profile", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_security_profile_refuses_server_startup()
    {
        using var factory = BuildFactory(new Dictionary<string, string?>
        {
            ["Security:Profile"] = "public-demo-typo",
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Unsupported Security:Profile", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_inventory_rejects_executable_route_without_public_demo_expectation()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        app.MapPost("/new-executable-route", () => Results.Ok())
            .WithMetadata(new ExecutionRouteMetadata(ExecutionAdmissionPath.Start));

        var error = Assert.Throws<InvalidOperationException>(() =>
            PublicDemoExecutionRouteInventory.ValidateStartup(
                app,
                new StartupExecutionAdmission(ExecutionAdmissionPolicy.PublicDemoProfile)));

        Assert.Contains("lack a public-demo expectation", error.Message, StringComparison.Ordinal);
        Assert.Contains("/new-executable-route", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Access_administration_routes_are_registered_and_denied_in_public_demo()
    {
        var endpoints = shared.Factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToList();
        using var client = shared.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
        foreach (var (method, path, requestPath) in new[]
        {
            ("GET", "/api/auth/api-tokens", "/api/auth/api-tokens"),
            ("POST", "/api/auth/api-tokens", "/api/auth/api-tokens"),
            ("DELETE", "/api/auth/api-tokens/{id}", "/api/auth/api-tokens/token"),
            ("GET", "/api/auth/runner-enrollments", "/api/auth/runner-enrollments"),
            ("DELETE", "/api/auth/runner-enrollments/{id}", "/api/auth/runner-enrollments/code"),
        })
        {
            Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == path
                && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
            using var request = new HttpRequestMessage(new HttpMethod(method), requestPath);
            if (method == "POST") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Every_registered_execution_route_returns_the_same_typed_denial()
    {
        output.WriteLine($"PublicDemoExecutionProfile fixture initialization: {shared.InitializationDuration.TotalSeconds:F3} s");
        var factory = shared.Factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        var admission = factory.Services.GetRequiredService<StartupExecutionAdmission>();
        Assert.True(admission.IsPublicDemo);
        Assert.Equal(ExecutionAdmissionPolicy.PublicDemoProfile, admission.StartupProfile);

        var hostedServices = factory.Services.GetServices<IHostedService>();
        Assert.DoesNotContain(hostedServices, service => HostTimelineHostedService.Unwrap(service) is TaskRunnerService);

        var runner = factory.Services.GetRequiredService<TaskRunnerService>();
        Assert.Throws<ExecutionAdmissionDeniedException>(() =>
            runner.RequestModeChange("forged-project", "auto-continuous"));
        Assert.Throws<ExecutionAdmissionDeniedException>(() =>
            runner.StartRunner("forged-project"));
        await Assert.ThrowsAsync<ExecutionAdmissionDeniedException>(() =>
            runner.StartJobAsync("forged-task"));
        await Assert.ThrowsAsync<ExecutionAdmissionDeniedException>(() =>
            runner.ContinueJobAsync("forged-task", "continue"));

        var turns = factory.Services.GetRequiredService<OrchestratorTurnService>();
        Assert.Throws<ExecutionAdmissionDeniedException>(() =>
            turns.Enqueue("global", new OrchestratorTurnRequest("forged chat")));

        var routes = ExecutionRoutes(factory.Services);
        foreach (var (routePath, admissionPath) in new[]
        {
            ("/api/quota/admission/{project}", ExecutionAdmissionPath.Claim),
            ("/api/workspaces/{workspaceId}/quota/admission", ExecutionAdmissionPath.Preview),
            ("/api/workspaces/{workspaceId}/quota", ExecutionAdmissionPath.Preview),
            ("/api/workspaces/{workspaceId}/projects/{projectId}/quota", ExecutionAdmissionPath.Preview),
            ("/api/workspaces/{workspaceId}/quota/audit", ExecutionAdmissionPath.Preview),
            ("/api/workspaces/{workspaceId}/quota", ExecutionAdmissionPath.Start),
            ("/api/workspaces/{workspaceId}/projects/{projectId}/quota", ExecutionAdmissionPath.Start),
            ("/api/workspaces/{workspaceId}/quota/override", ExecutionAdmissionPath.Start),
            ("/api/workspaces/{workspaceId}/projects/{projectId}/quota/override", ExecutionAdmissionPath.Start)
        })
        {
            Assert.Contains(routes, route => route.RoutePattern.RawText == routePath
                && route.Metadata.GetMetadata<ExecutionRouteMetadata>()?.Path == admissionPath);
        }
        var allRoutes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        Assert.Contains(allRoutes, route => route.RoutePattern.RawText == "/api/auth/invitations");
        Assert.Contains(allRoutes, route => route.RoutePattern.RawText == "/api/auth/invite/accept");
        Assert.Contains(allRoutes, route => route.RoutePattern.RawText == "/api/tasks/{jobId}/preview-comments");
        // Security inventory tripwire: adding, removing, or reclassifying an
        // executable endpoint requires an explicit update to this matrix.
        // AGT-2817 added POST /api/projects/{projectId}/delivery-claims/reconcile
        // on the Review path: it is the acceptance-rail audit that repairs the
        // integration records of already reviewed deliveries, the same admission
        // path its sibling POST /api/projects/{projectId}/completed-lane/audit
        // carries. The two read-only delivery-claim reports stay unmarked,
        // like every other GET that only re-derives git state.
        // AGT-3001 added GET /api/cli/quota/history on Preview: read-only
        // quota history is denied in public demo like GET /api/cli/quota.
        // Decision-card decide, reopen, and prose migration are execution mutations on Start.
        // The revoked-reference post step and older-brief decision are
        // execution mutations and must remain in the denial inventory.
        // Project model migration is an operator-triggered Preview mutation.
        // AGT-3072 added POST /api/attempts/checkpoint: an operator-forced authority
        // checkpoint writes the store, so it is an execution mutation and denied.
        // AGT-3063 added POST /api/runner/orchestrator-decisions/claim (Claim) and
        // POST /api/runner/orchestrator-decisions/complete (Continue): runner hosts
        // claim and complete shadow model decision jobs, so both are denied.
        // AGT-3055 added the claimed batch gate under /api/v1/gates: claims (Claim),
        // attempts/{attemptId}/renew and /phase (Continue), /report and /containment
        // (PostStep). Gate hosts drive suites through them, so all five are denied.
        // AGT-3181 preview feedback queues a same-card continuation, so it is
        // denied on the Continue path before form data or attachments are read.
        // AGT-3175 adds one Start route for new projects from a template.
        // AGT-3176 added the gate release handoff on Start. It records the
        // artifact digest and queues stage, so public demo must deny it.
        // AGT-3201 (BD-1) added deploy runs: deploy a release, redeploy and
        // rollback on Start; approve, reject and cancel a run on Continue.
        // AGT-3264 adds guided-failure intervention on Continue.
        // AGT-3221 added the project.yml schema 2 migration confirm on PostStep:
        // it commits and pushes project.yml through the managed repository write.
        // AGT-3233 streams deploy-host step messages and renews its fenced lease
        // through PostStep routes.
        // AGT-3068 adds three rule mutations (Continue) and one host occurrence (Start),
        // and maps the engine decision-job (Start) and receipt (Continue) routes in
        // every profile.
        // AGT-3177 adds workspace quota admission (Claim), quota views/audit
        // (Preview), and quota settings/owner overrides (Start).
        // AGT-3229 adds a run-target claim, an observed-state report, a
        // removal confirmation, and a domain-suffix settings mutation.
        // AGT-3239 added the operator candidate verification record on PostStep:
        // it appends immutable integrated-verified evidence after a passed suite.
        // AGT-3274 added the authenticated deploy artifact route on Claim.
        // The interactive CLI probe is mapped only in the local profile.
        Assert.Equal(146, routes.Count);
        foreach (var (method, suffix) in new[]
        {
            ("PUT", "/{volume}/schedule"), ("POST", "/{volume}/backups"),
            ("POST", "/{volume}/backups/{backupId}/restore"),
        })
        {
            var route = Assert.Single(routes, candidate => candidate.RoutePattern.RawText ==
                "/api/projects/{projectName}/environments/{environmentId}/volumes" + suffix);
            Assert.Equal(ExecutionAdmissionPath.Start, route.Metadata.GetMetadata<ExecutionRouteMetadata>()!.Path);
            Assert.Contains(method, route.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        }
        // Pin their identities too: an unrelated route must not mask the loss
        // of either decision mutation from the denial inventory.
        foreach (var decisionPath in new[] { "/api/tasks/{jobId}/decision", "/api/tasks/{jobId}/decision/reopen", "/api/tasks/{jobId}/decision/migrate" })
        {
            var decisionRoute = Assert.Single(routes, route => route.RoutePattern.RawText == decisionPath);
            Assert.Equal(ExecutionAdmissionPath.Start,
                decisionRoute.Metadata.GetMetadata<ExecutionRouteMetadata>()!.Path);
            Assert.Contains("POST", decisionRoute.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        }
        foreach (var (routePath, admissionPath) in new[]
        {
            ("/api/runner/lease/{attemptId}/revoked-reference", ExecutionAdmissionPath.PostStep),
            ("/api/projects/{project}/operator-candidate-verifications", ExecutionAdmissionPath.PostStep),
            ("/api/deploy/gate-releases", ExecutionAdmissionPath.Start),
            ("/api/projects/{projectName}/environments/{environmentId}/run-target-removal/confirm", ExecutionAdmissionPath.Start),
            ("/api/deploy/jobs/{project}/{jobId}/log", ExecutionAdmissionPath.PostStep),
            ("/api/deploy/jobs/{project}/{jobId}/renew", ExecutionAdmissionPath.PostStep),
            ("/api/projects/{projectName}/deploy/releases/{releaseId}/deployments", ExecutionAdmissionPath.Start),
            ("/api/projects/{projectName}/deploy/environments/{environmentId}/redeploy", ExecutionAdmissionPath.Start),
            ("/api/projects/{projectName}/deploy/environments/{environmentId}/rollback", ExecutionAdmissionPath.Start),
            ("/api/projects/{projectName}/deploy/deployments/{deploymentId}/approve", ExecutionAdmissionPath.Continue),
            ("/api/projects/{projectName}/deploy/deployments/{deploymentId}/reject", ExecutionAdmissionPath.Continue),
            ("/api/projects/{projectName}/deploy/deployments/{deploymentId}/cancel", ExecutionAdmissionPath.Continue),
            ("/api/projects/{projectName}/environments/{environmentId}/stop", ExecutionAdmissionPath.Continue),
            ("/api/projects/{projectName}/deploy/deployments/{deploymentId}/intervention", ExecutionAdmissionPath.Continue),
            ("/api/projects/{projectName}/pipeline-definition/migration", ExecutionAdmissionPath.PostStep),
            ("/api/tasks/{jobId}/older-brief-delivery/decision", ExecutionAdmissionPath.Continue),
            ("/api/orchestration/decision-receipts/{receiptId}/operator-intent", ExecutionAdmissionPath.Continue),
            ("/api/tasks/{jobId}/preview-feedback", ExecutionAdmissionPath.Continue),
            ("/api/workspaces/{workspaceId}/decision-rules/versions", ExecutionAdmissionPath.Continue),
            ("/api/workspaces/{workspaceId}/decision-rules/versions/{versionId}/activate", ExecutionAdmissionPath.Continue),
            ("/api/workspaces/{workspaceId}/decision-rules/versions/{versionId}/rollback", ExecutionAdmissionPath.Continue),
            ("/api/v1/orchestration/host-decisions", ExecutionAdmissionPath.Start),
            ("/api/v1/orchestration/decision-jobs", ExecutionAdmissionPath.Start)
        })
        {
            var route = Assert.Single(routes, candidate => candidate.RoutePattern.RawText == routePath);
            Assert.Equal(admissionPath, route.Metadata.GetMetadata<ExecutionRouteMetadata>()!.Path);
            Assert.Contains("POST", route.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        }
        var artifactRoute = Assert.Single(routes, route =>
            route.RoutePattern.RawText == "/api/deploy/jobs/{project}/{jobId}/artifact");
        Assert.Equal(ExecutionAdmissionPath.Claim,
            artifactRoute.Metadata.GetMetadata<ExecutionRouteMetadata>()!.Path);
        Assert.Contains("GET", artifactRoute.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        var receiptRoute = Assert.Single(routes, route =>
            route.RoutePattern.RawText == "/api/v1/orchestration/runs/{runId}/decision-receipt");
        Assert.Equal(ExecutionAdmissionPath.Continue,
            receiptRoute.Metadata.GetMetadata<ExecutionRouteMetadata>()!.Path);
        Assert.Contains("PUT", receiptRoute.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        foreach (var (routePath, method, admissionPath) in new[]
        {
            ("/api/projects/{projectName}/run-target-domain-suffixes", "PUT", ExecutionAdmissionPath.Start),
            ("/api/deploy/run-targets/{hostId}/desired-state", "GET", ExecutionAdmissionPath.Claim),
            ("/api/deploy/run-targets/{hostId}/observed-state", "POST", ExecutionAdmissionPath.PostStep),
        })
        {
            var route = Assert.Single(routes, candidate => candidate.RoutePattern.RawText == routePath);
            Assert.Equal(admissionPath, route.Metadata.GetMetadata<ExecutionRouteMetadata>()!.Path);
            Assert.Contains(method, route.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        }
        var migrationRoute = Assert.Single(routes, route =>
            route.RoutePattern.RawText == "/api/projects/{project}/model-migrations/apply");
        Assert.Equal(ExecutionAdmissionPath.Preview,
            migrationRoute.Metadata.GetMetadata<ExecutionRouteMetadata>()!.Path);
        var onboardingRoute = Assert.Single(routes, route =>
            route.RoutePattern.RawText == "/api/projects");
        Assert.Equal(ExecutionAdmissionPath.Start,
            onboardingRoute.Metadata.GetMetadata<ExecutionRouteMetadata>()!.Path);
        Assert.Contains("POST", onboardingRoute.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        Assert.Equal(
            ExecutionAdmissionPolicy.AllPaths.OrderBy(path => path),
            routes.Select(route => route.Metadata.GetMetadata<ExecutionRouteMetadata>()!.Path)
                .Distinct()
                .OrderBy(path => path));
        Assert.Equal(
            new Dictionary<ExecutionAdmissionPath, int>
            {
                [ExecutionAdmissionPath.Claim] = 12,
                [ExecutionAdmissionPath.Start] = 33,
                [ExecutionAdmissionPath.Continue] = 31,
                [ExecutionAdmissionPath.Review] = 9,
                [ExecutionAdmissionPath.Chat] = 9,
                [ExecutionAdmissionPath.Preview] = 32,
                [ExecutionAdmissionPath.PostStep] = 21,
            },
            routes.GroupBy(route => route.Metadata.GetMetadata<ExecutionRouteMetadata>()!.Path)
                .ToDictionary(group => group.Key, group => group.Count()));

        foreach (var route in routes)
        {
            var execution = route.Metadata.GetMetadata<ExecutionRouteMetadata>()!;
            var expectation = route.Metadata.GetMetadata<PublicDemoExecutionExpectationMetadata>();
            Assert.NotNull(expectation);
            Assert.Equal(execution.Path, expectation!.Path);
            Assert.Equal(ExecutionAdmissionPolicy.ExecutionDisabledCode, expectation.ExpectedCode);

            var methods = route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var method in methods)
            {
                using var request = new HttpRequestMessage(
                    new HttpMethod(method),
                    Materialize(route.RoutePattern));
                if (method is not "GET" and not "HEAD")
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(
                    ExecutionAdmissionPolicy.ExecutionDisabledCode,
                    body.RootElement.GetProperty("error").GetString());
                Assert.Equal(
                    ExecutionAdmissionPolicy.PublicDemoProfile,
                    body.RootElement.GetProperty("profile").GetString());
                Assert.Equal(
                    ExecutionAdmissionPolicy.PathName(execution.Path),
                    body.RootElement.GetProperty("admissionPath").GetString());
            }
        }
    }

    private WebApplicationFactory<Program> BuildFactory(
        IReadOnlyDictionary<string, string?>? overrides = null)
        => PublicDemoHostFixture.CreateFactory(_workspace, overrides);

    private static IReadOnlyList<RouteEndpoint> ExecutionRoutes(IServiceProvider services) =>
        services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ExecutionRouteMetadata>() is not null)
            .Distinct()
            .OrderBy(endpoint => endpoint.RoutePattern.RawText, StringComparer.Ordinal)
            .ToList();

    private static string Materialize(RoutePattern pattern)
    {
        var path = pattern.RawText ?? throw new InvalidOperationException("Execution route has no raw pattern.");
        path = path.Replace(
            @"{projId:regex(^PROJ-\d{{3,}}$)}",
            "PROJ-999",
            StringComparison.Ordinal);

        foreach (var parameter in pattern.Parameters)
        {
            var value = parameter.Name switch
            {
                "projId" => "PROJ-999",
                "index" => "1",
                "cliType" => "claude",
                "stepId" => "pre-main-test-gate",
                "targetId" => "package",
                "sha" => new string('a', 40),
                _ => "x",
            };
            path = Regex.Replace(
                path,
                @"\{(?:\*\*)?" + Regex.Escape(parameter.Name) + @"(?::[^{}]*)?\}",
                value,
                RegexOptions.CultureInvariant);
        }

        Assert.DoesNotContain('{', path);
        return path.StartsWith('/') ? path : "/" + path;
    }

    public void Dispose() => TestTempRoot.TryDelete(_workspace);
}
