using System.Net.Http.Json;
using AgentStudio.Cli;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CombinedOneShotJobTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "combined-one-shot-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Combined_routes_wait_then_complete_a_capability_gated_job()
    {
        Directory.CreateDirectory(_root);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IStartupFilter>(new RunnerPrincipalStartupFilter("runner-a"));
            });
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["Security:Profile"] = "local",
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    ["TaskServer:BaseUrl"] = string.Empty,
                }));
        });
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var request = new OneShotJobRequest("codex", "gpt-6-sol", "Review the report.", "medium", 30, 128);
        var waiting = await PostJobAsync(http, request);
        Assert.Equal("waiting", waiting.Status);
        Assert.Equal("waiting for a host with model-acceptance:codex:gpt-6-sol", waiting.Error);

        using var registration = await http.PutAsJsonAsync("/api/v1/runners/runner-a",
            new RegisterRunnerRequest("runner", "host-a", "instance-a", "1.0.0",
                TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]));
        registration.EnsureSuccessStatusCode();
        using var advertisement = await http.PutAsJsonAsync("/api/v1/runners/runner-a/capabilities",
            new CapabilityAdvertisementRequest("runner-a", "instance-a",
                CapabilityProtocol.CurrentSchemaVersion, clock.GetUtcNow().UtcDateTime, 300, 1,
                [new AdvertisedCapabilityDto(CapabilityProtocol.ModelAcceptance("codex", "gpt-6-sol"),
                    "model-acceptance", "ready", CredentialGeneration: "fixture-login",
                    CredentialObservedAt: clock.GetUtcNow().UtcDateTime, ProbeSequence: 1)]));
        advertisement.EnsureSuccessStatusCode();
        var advertised = await advertisement.Content.ReadFromJsonAsync<RunnerCapabilitySnapshotDto>();
        Assert.Contains(advertised?.ConfiguredModelRoutes ?? [], route =>
            route.CliType == "codex" && route.Model == "gpt-6-sol");

        var queued = await PostJobAsync(http, request);
        Assert.Equal("pending", queued.Status);
        using var wrongRunner = await http.PostAsJsonAsync("/api/v1/one-shot-jobs/claim",
            new OneShotJobClaimRequest("runner-b", "instance-a"));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, wrongRunner.StatusCode);
        var claim = await http.PostAsJsonAsync("/api/v1/one-shot-jobs/claim",
            new OneShotJobClaimRequest("runner-a", "instance-a"));
        claim.EnsureSuccessStatusCode();
        var work = await claim.Content.ReadFromJsonAsync<OneShotJobWork>();
        Assert.NotNull(work);
        Assert.Equal("Review the report.", work.Request.Prompt);
        var wrongInstance = await http.PostAsJsonAsync("/api/v1/one-shot-jobs/complete",
            new OneShotJobCompletion("runner-a", "instance-b", work.JobId, work.ClaimToken,
                true, "wrong", null, null));
        Assert.False(await wrongInstance.Content.ReadFromJsonAsync<bool>());
        var completion = await http.PostAsJsonAsync("/api/v1/one-shot-jobs/complete",
            new OneShotJobCompletion("runner-a", "instance-a", work.JobId, work.ClaimToken,
                true, "Reviewed report.", null, new(1, 2, 0, 0, "gpt-6-sol")));
        completion.EnsureSuccessStatusCode();
        Assert.True(await completion.Content.ReadFromJsonAsync<bool>());
        var result = await http.GetFromJsonAsync<OneShotJobDto>($"/api/v1/one-shot-jobs/{queued.JobId}");
        Assert.Equal("completed", result?.Status);
        Assert.Equal("Reviewed report.", result?.Text);
        Assert.Equal(2, result?.Usage?.OutputTokens);
    }

    private static async Task<OneShotJobDto> PostJobAsync(HttpClient http, OneShotJobRequest request)
    {
        using var response = await http.PostAsJsonAsync("/api/v1/one-shot-jobs", request);
        Assert.True(response.IsSuccessStatusCode,
            $"One-shot create returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<OneShotJobDto>())!;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class RunnerPrincipalStartupFilter(string runnerId) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path.StartsWithSegments("/api/v1/one-shot-jobs/claim")
                    || context.Request.Path.StartsWithSegments("/api/v1/one-shot-jobs/complete"))
                    context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] = new RunnerPrincipal(
                        runnerId, runnerId, "fixture-credential", new HashSet<string>());
                await nextMiddleware();
            });
            next(app);
        };
    }
}
