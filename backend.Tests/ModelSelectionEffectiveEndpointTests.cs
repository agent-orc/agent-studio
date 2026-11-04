using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Cli;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ModelSelectionEffectiveEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "model-effective-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task One_put_moves_12_projects_and_3_pipeline_types_then_expiry_restores_all_review_routes()
    {
        Directory.CreateDirectory(_root);
        var clock = new MutableClock(new DateTime(2030, 11, 4, 12, 0, 0, DateTimeKind.Utc));
        var reset = clock.GetUtcNow().UtcDateTime.AddHours(1);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build();
        new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance).Write([
            new QuotaSnapshot
            {
                CliType = CliTypes.Claude, FetchedAt = clock.GetUtcNow().UtcDateTime,
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 20, ResetAt = reset }],
            },
        ]);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test")
                .ConfigureAppConfiguration((_, settings) => settings.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _root,
                        ["PublicDemo:Enabled"] = "false",
                        [ReviewAspectModelPolicy.CliSetting] = CliTypes.Claude,
                        [ReviewAspectModelPolicy.ModelSetting] = ModelIds.ClaudeOpus5,
                    }))
                .ConfigureTestServices(services => services.Replace(
                    ServiceDescriptor.Singleton<TimeProvider>(clock))));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var projectSettings = factory.Services.GetRequiredService<ProjectSettingsService>();
        var selection = factory.Services.GetRequiredService<ModelSelectionEffectiveService>();
        var projects = Enumerable.Range(1, 12).Select(index => $"review-{index}").ToArray();
        var types = new[] { PipelineTypes.Task, PipelineTypes.Bug, PipelineTypes.Feature };
        foreach (var project in projects)
        foreach (var type in types)
        {
            var setting = new PipelineStepSetting
            {
                CliType = CliTypes.Claude, Model = ModelIds.ClaudeOpus5, ThinkingLevel = "high",
            };
            projectSettings.SetPipelineStep(project, type, "aspect-code-quality", setting);
            projectSettings.SetPipelineStep(project, type, PipelineCatalogue.CodeReviewGradeStepId, setting);
            projectSettings.SetPipelineStep(project, type, "post-deep-review", setting);
        }

        static EffectiveModelSelection[] ReviewRoutes(IReadOnlyList<EffectiveModelSelection> rows) =>
            rows.Where(row => row.StepId is "aspect-code-quality" or "post-code-review-grade" or "post-deep-review")
                .ToArray();
        foreach (var project in projects)
        foreach (var type in types)
            Assert.All(ReviewRoutes(selection.Get(project, type)), row => Assert.Equal(CliTypes.Claude, row.Effective?.CliType));

        var put = await client.PutAsJsonAsync("/api/cli/quota/fallback-preference",
            new { cliType = CliTypes.Claude, preferFallback = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        foreach (var project in projects)
        foreach (var type in types)
        {
            var rows = ReviewRoutes(selection.Get(project, type));
            Assert.Equal(3, rows.Length);
            Assert.All(rows, row =>
            {
                Assert.Equal(CliTypes.Claude, row.Configured.CliType);
                Assert.Equal(CliTypes.Codex, row.Effective?.CliType);
                Assert.Equal("operator-switch", row.Rule);
            });
        }
        using var response = JsonDocument.Parse(await client.GetStringAsync(
            "/api/model-selection/effective?project=review-1&pipelineType=task"));
        Assert.Contains(response.RootElement.GetProperty("routes").EnumerateArray(), row =>
            row.GetProperty("stepId").GetString() == "post-deep-review"
            && row.GetProperty("effective").GetProperty("cliType").GetString() == CliTypes.Codex);

        clock.Advance(TimeSpan.FromHours(2));
        foreach (var project in projects)
        foreach (var type in types)
            Assert.All(ReviewRoutes(selection.Get(project, type)), row => Assert.Equal(CliTypes.Claude, row.Effective?.CliType));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class MutableClock(DateTime now) : TimeProvider
    {
        private DateTime _now = now;
        public override DateTimeOffset GetUtcNow() => new(_now);
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
