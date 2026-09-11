using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer;
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

namespace TaskServer.Tests;

public sealed class PublicDemoExecutionProfileTests
{
    [Fact]
    public void Unknown_task_server_profile_refuses_startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TASK_SERVER_PROFILE"] = "public-demo-typo",
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => new TaskServerStartupExecutionAdmission(configuration));

        Assert.Contains("Unsupported TASK_SERVER_PROFILE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_task_server_profile_refuses_server_startup()
    {
        using var temp = new TempDirectory();
        using var factory = new TaskServerFactory(temp.Path, "public-demo-typo");

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Unsupported TASK_SERVER_PROFILE", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_inventory_rejects_executable_route_without_public_demo_expectation()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["TASK_SERVER_PROFILE"] = ExecutionAdmissionPolicy.PublicDemoProfile;
        var app = builder.Build();
        app.MapPost("/new-executable-route", () => Results.Ok())
            .WithMetadata(new TaskServerExecutionRouteMetadata(ExecutionAdmissionPath.Claim));

        var error = Assert.Throws<InvalidOperationException>(() =>
            TaskServerPublicDemoExecutionRouteInventory.ValidateStartup(
                app,
                new TaskServerStartupExecutionAdmission(builder.Configuration)));

        Assert.Contains("lack a public-demo expectation", error.Message, StringComparison.Ordinal);
        Assert.Contains("/new-executable-route", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_registered_task_server_execution_route_returns_the_same_typed_denial()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();

        var admission = factory.Services.GetRequiredService<TaskServerStartupExecutionAdmission>();
        Assert.True(admission.IsPublicDemo);
        Assert.Equal(ExecutionAdmissionPolicy.PublicDemoProfile, admission.StartupProfile);
        var hostedServices = factory.Services.GetServices<IHostedService>();
        Assert.True(hostedServices.OfType<TaskServerInvariantReconciliationService>().Single().ExecutionSuppressed);
        var resultRefGc = hostedServices.OfType<ResultRefGcHostedService>().Single();
        Assert.True(resultRefGc.ExecutionSuppressed);
        Assert.Empty((await resultRefGc.RunOnceAsync()).Decisions);

        var routes = ExecutionRoutes(factory.Services);
        // Security inventory tripwire: adding, removing, or reclassifying an
        // executable endpoint requires an explicit update to this matrix.
        Assert.Equal(28, routes.Count);
        var requiredPaths = new[]
        {
            ExecutionAdmissionPath.Claim,
            ExecutionAdmissionPath.Start,
            ExecutionAdmissionPath.Continue,
            ExecutionAdmissionPath.Review,
            ExecutionAdmissionPath.Chat,
            ExecutionAdmissionPath.PostStep,
        };
        Assert.Equal(
            requiredPaths.OrderBy(path => path),
            routes.Select(route => route.Metadata.GetMetadata<TaskServerExecutionRouteMetadata>()!.Path)
                .Distinct()
                .OrderBy(path => path));
        Assert.Equal(
            new Dictionary<ExecutionAdmissionPath, int>
            {
                [ExecutionAdmissionPath.Claim] = 4,
                [ExecutionAdmissionPath.Start] = 5,
                [ExecutionAdmissionPath.Continue] = 6,
                [ExecutionAdmissionPath.Review] = 2,
                [ExecutionAdmissionPath.Chat] = 4,
                [ExecutionAdmissionPath.PostStep] = 7,
            },
            routes.GroupBy(route => route.Metadata.GetMetadata<TaskServerExecutionRouteMetadata>()!.Path)
                .ToDictionary(group => group.Key, group => group.Count()));

        foreach (var route in routes)
        {
            var execution = route.Metadata.GetMetadata<TaskServerExecutionRouteMetadata>()!;
            var expectation = route.Metadata.GetMetadata<TaskServerPublicDemoExpectationMetadata>();
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
                    body.RootElement.GetProperty("code").GetString());
                var detail = body.RootElement.GetProperty("detail");
                Assert.Equal(
                    ExecutionAdmissionPolicy.PublicDemoProfile,
                    detail.GetProperty("profile").GetString());
                Assert.Equal(
                    ExecutionAdmissionPolicy.PathName(execution.Path),
                    detail.GetProperty("admissionPath").GetString());
            }
        }
    }

    private static IReadOnlyList<RouteEndpoint> ExecutionRoutes(IServiceProvider services) =>
        services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<TaskServerExecutionRouteMetadata>() is not null)
            .Distinct()
            .OrderBy(endpoint => endpoint.RoutePattern.RawText, StringComparer.Ordinal)
            .ToList();

    private static string Materialize(RoutePattern pattern)
    {
        var path = pattern.RawText ?? throw new InvalidOperationException("Execution route has no raw pattern.");
        foreach (var parameter in pattern.Parameters)
        {
            path = Regex.Replace(
                path,
                @"\{(?:\*\*)?" + Regex.Escape(parameter.Name) + @"(?::[^{}]*)?\}",
                "x",
                RegexOptions.CultureInvariant);
        }
        Assert.DoesNotContain('{', path);
        return path.StartsWith('/') ? path : "/" + path;
    }

    private sealed class TaskServerFactory(
        string dataDirectory,
        string profile = ExecutionAdmissionPolicy.PublicDemoProfile)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TASK_SERVER_PROFILE"] = profile,
                }));
        }
    }
}
