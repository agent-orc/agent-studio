using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;
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

namespace AgentStudio.Tests;

[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class PublicDemoExecutionProfileTests : IDisposable
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
    public async Task Every_registered_execution_route_returns_the_same_typed_denial()
    {
        var previousProfile = Environment.GetEnvironmentVariable("Security__Profile");
        Environment.SetEnvironmentVariable(
            "Security__Profile",
            ExecutionAdmissionPolicy.PublicDemoProfile);
        try
        {
            using var factory = BuildFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });

            var admission = factory.Services.GetRequiredService<StartupExecutionAdmission>();
            Assert.True(admission.IsPublicDemo);
            Assert.Equal(ExecutionAdmissionPolicy.PublicDemoProfile, admission.StartupProfile);

            var hostedServices = factory.Services.GetServices<IHostedService>();
            Assert.DoesNotContain(hostedServices, service => service is TaskRunnerService);

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
            // Security inventory tripwire: adding, removing, or reclassifying an
            // executable endpoint requires an explicit update to this matrix.
            Assert.Equal(81, routes.Count);
            Assert.Equal(
                ExecutionAdmissionPolicy.AllPaths.OrderBy(path => path),
                routes.Select(route => route.Metadata.GetMetadata<ExecutionRouteMetadata>()!.Path)
                    .Distinct()
                    .OrderBy(path => path));
            Assert.Equal(
                new Dictionary<ExecutionAdmissionPath, int>
                {
                    [ExecutionAdmissionPath.Claim] = 6,
                    [ExecutionAdmissionPath.Start] = 12,
                    [ExecutionAdmissionPath.Continue] = 12,
                    [ExecutionAdmissionPath.Review] = 8,
                    [ExecutionAdmissionPath.Chat] = 9,
                    [ExecutionAdmissionPath.Preview] = 24,
                    [ExecutionAdmissionPath.PostStep] = 10,
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
        finally
        {
            Environment.SetEnvironmentVariable("Security__Profile", previousProfile);
        }
    }

    [Fact]
    public async Task Public_demo_denies_unsafe_standalone_task_server_proxy_requests_before_forwarding()
    {
        var previousProfile = Environment.GetEnvironmentVariable("Security__Profile");
        Environment.SetEnvironmentVariable(
            "Security__Profile",
            ExecutionAdmissionPolicy.PublicDemoProfile);
        try
        {
            using var factory = BuildFactory(new Dictionary<string, string?>
            {
                ["TaskServer:BaseUrl"] = "http://127.0.0.1:1",
            });
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/v1/runners/forged-runner/claims")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(
                ExecutionAdmissionPolicy.ExecutionDisabledCode,
                body.RootElement.GetProperty("error").GetString());
            Assert.Equal(
                ExecutionAdmissionPolicy.PublicDemoProfile,
                body.RootElement.GetProperty("profile").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("Security__Profile", previousProfile);
        }
    }

    private WebApplicationFactory<Program> BuildFactory(
        IReadOnlyDictionary<string, string?>? overrides = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["Security:Profile"] = ExecutionAdmissionPolicy.PublicDemoProfile,
                };
                if (overrides is not null)
                {
                    foreach (var (key, value) in overrides)
                        values[key] = value;
                }
                configuration.AddInMemoryCollection(values);
            });
        });

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

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
    }
}
