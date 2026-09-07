using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentStudio.Tests;

[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class TokenPricingDiagnosticsEndpointTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "token-pricing-diagnostics-endpoint-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Diagnostics_ScopedViewerReadsOnlyAllowedProjectsAndNoWorkspaceAdHoc()
    {
        var alphaPath = Path.Combine(_workspace, "alpha");
        var betaPath = Path.Combine(_workspace, "beta");
        Directory.CreateDirectory(alphaPath);
        Directory.CreateDirectory(betaPath);
        var aggregator = new CapturingTokenAggregator();
        await using var factory = BuildFactory(aggregator, alphaPath, betaPath);
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        store.Bootstrap(new BootstrapRequest(
            "first.owner",
            "correct horse battery staple!",
            "First Owner"));
        var created = store.CreateUser(new CreateUserRequest(
            "scoped.operator",
            "Scoped Operator",
            StudioRoles.Operator,
            ["Alpha Project"],
            "temporary scoped password phrase!"));
        var firstLogin = store.Login(
            created.User.Username,
            created.TemporaryPassword,
            "scoped.operator|setup");
        store.ChangePassword(
            new HumanPrincipal(
                firstLogin.User,
                store.AuthenticateSession(firstLogin.SessionToken)!.Session),
            new ChangePasswordRequest(
                created.TemporaryPassword,
                "permanent scoped password phrase!"));

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test"),
            HandleCookies = true,
        });
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = created.User.Username,
            password = "permanent scoped password phrase!",
        });
        login.EnsureSuccessStatusCode();

        var response = await client.GetAsync("/api/token-pricing/diagnostics");

        response.EnsureSuccessStatusCode();
        var diagnostic = await response.Content.ReadFromJsonAsync<TokenPricingDiagnosticsResponse>();
        Assert.NotNull(diagnostic);
        Assert.Equal(["Alpha Project"], aggregator.LifetimeProjects);
        Assert.Equal(0, aggregator.AdHocCalls);
        Assert.Equal(ModelIds.Gpt55, Assert.Single(diagnostic!.Models).ModelId);
    }

    private WebApplicationFactory<Program> BuildFactory(
        CapturingTokenAggregator aggregator,
        string alphaPath,
        string betaPath)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["Security:Profile"] = "networked",
                    ["AllowedHosts"] = "studio.test",
                    ["WatchPaths:0:Name"] = "Alpha Project",
                    ["WatchPaths:0:Path"] = alphaPath,
                    ["WatchPaths:0:RootPath"] = alphaPath,
                    ["WatchPaths:1:Name"] = "Beta Project",
                    ["WatchPaths:1:Path"] = betaPath,
                    ["WatchPaths:1:RootPath"] = betaPath,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITokenAggregator>();
                services.AddSingleton<ITokenAggregator>(aggregator);
            });
        });

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { /* Best-effort test cleanup. */ }
    }

    private sealed class CapturingTokenAggregator : ITokenAggregator
    {
        public List<string> LifetimeProjects { get; } = [];
        public int AdHocCalls { get; private set; }

        public TokenSummary LifetimeSummary(string projectName, string watchPath)
        {
            LifetimeProjects.Add(projectName);
            return TokenSummaryService.Summarize(projectName, [new OrchestratorLogEntry
            {
                Ts = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc),
                Kind = OrchestratorLogKinds.Decision,
                Topic = OrchestratorLogTopics.General,
                Summary = "endpoint fixture",
                TokenUsage = new OrchestratorTokenUsage
                {
                    Model = ModelIds.Gpt55,
                    InputTokens = 1_000,
                    OutputTokens = 100,
                },
            }]);
        }

        public AdHocUsageAggregate AdHocAggregate(DateTime? since = null)
        {
            AdHocCalls++;
            return AdHocUsageService.Aggregate([], "adhoc.jsonl", 0, null);
        }

        public TokenAggregateResponse ForProject(string project, DateTime? since = null, DateTime? until = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ProjectTokenUsageSummary ProjectSummary(string projectName, string watchPath, DateTime? nowUtc = null)
            => throw new NotSupportedException();
        public ProjectTokenHeatmap ProjectHeatmap(string projectName, string watchPath, int days, DateTime? nowUtc = null)
            => throw new NotSupportedException();
        public IReadOnlyList<ProjectExpensiveJob> ProjectExpensiveJobs(string projectName, string watchPath, int limit)
            => throw new NotSupportedException();
        public ProjectJobTokenDetail? ProjectJobDetail(string projectName, string watchPath, string jobId)
            => throw new NotSupportedException();
        public TokenSummaryAggregate WorkspaceAggregate(IEnumerable<(string Name, string WatchPath)> projects)
            => throw new NotSupportedException();
        public TokenSummaryAggregate? CachedWorkspaceAggregate()
            => throw new NotSupportedException();
        public Dictionary<string, TaskTokenSummary> WorkspacePerJob(string projectName, string watchPath)
            => throw new NotSupportedException();
        public TokenTimeline WorkspaceTimeline(IEnumerable<(string Name, string WatchPath)> projects, int windowHours, int bucketMinutes, DateTime? nowUtc = null)
            => throw new NotSupportedException();
    }
}
