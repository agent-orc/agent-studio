using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentStudio.Security;
using AgentStudio.Projects;
using AgentStudio.Telemetry;
using AgentStudio.Git;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

[Collection(OperationsTelemetryApiCollection.Name)]
public sealed class OperationsTelemetryApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "operations-api-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Otlp_json_intake_reaches_operations_http_api()
    {
        var repo = Path.Combine(_root, "repo");
        var declarations = Path.Combine(repo, "docs", "deployments", "environments");
        Directory.CreateDirectory(declarations);
        File.WriteAllText(Path.Combine(declarations, "stage.json"), JsonSerializer.Serialize(new
        {
            id = "stage", name = "Staging", kind = "stage", targetHostId = "stage-host",
            address = "https://stage.example", template = "container", destination = "8081",
            healthUrl = "https://stage.example/health", approval = "person", secretRefs = Array.Empty<string>(),
        }));
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "projects", "shop"));
        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("TaskRepository", workspace);
            builder.UseSetting("WatchPaths:0:Name", "shop");
            builder.UseSetting("WatchPaths:0:Path", Path.Combine(workspace, "projects", "shop"));
            builder.UseSetting("WatchPaths:0:RootPath", repo);
            builder.UseSetting("WatchPaths:0:RepositoryPath", repo);
            builder.UseSetting("Logging:BackendFile:LogDirectory", Path.Combine(_root, "logs"));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
                // The intake reaches the store through its single writer, a
                // product worker test hosts start only on request.
                services.AddHostedService(provider => provider.GetRequiredService<TelemetryWriter>());
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var password = $"test-{Guid.NewGuid():N}!";
        using var bootstrap = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "local.owner", password, displayName = "Local Owner",
        });
        bootstrap.EnsureSuccessStatusCode();
        var auth = await bootstrap.Content.ReadFromJsonAsync<AuthStatusResponse>();
        client.DefaultRequestHeaders.Add("X-CSRF-Token", auth!.CsrfToken);
        var tokenResponse = await client.PostAsJsonAsync("/api/projects/shop/environments/stage/telemetry/tokens",
            new { name = "collector" });
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var secret = (await tokenResponse.Content.ReadFromJsonAsync<TelemetryIngestTokenCreatedResponse>())!.Secret;
        var time = TelemetryAdmissionPolicy.UnixNanos(_clock.GetUtcNow().AddMinutes(-1)).ToString();
        var metrics = """
            {"resourceMetrics":[{"scopeMetrics":[{"metrics":[{"name":"http.server.request.duration","unit":"s","histogram":{"aggregationTemporality":1,"dataPoints":[{"timeUnixNano":"__TIME__","count":"2","sum":0.06,"min":0.01,"max":0.05,"bucketCounts":["1","1"],"explicitBounds":[0.02],"attributes":[{"key":"http.response.status_code","value":{"stringValue":"500"}}]}]}},{"name":"system.filesystem.usage","unit":"By","gauge":{"dataPoints":[{"timeUnixNano":"__TIME__","asDouble":8192}]}}]}]}]}
            """.Replace("__TIME__", time);
        var logs = """
            {"resourceLogs":[{"resource":{"attributes":[{"key":"service.version","value":{"stringValue":"v2"}}]},"scopeLogs":[{"logRecords":[{"timeUnixNano":"__TIME__","severityNumber":17,"body":{"stringValue":"upstream timeout"}}]}]}]}
            """.Replace("__TIME__", time);
        async Task Post(string path, string body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        await Post("/api/telemetry/otlp/v1/metrics", metrics);
        await Post("/api/telemetry/otlp/v1/logs", logs);
        var store = factory.Services.GetRequiredService<TelemetryStore>();
        var key = new TelemetryEnvironmentKey("shop", "stage");
        var stored = false;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            stored = store.MetricNames(key).Any(item => item.Name == "http.server.request.duration")
                && store.QueryLogs(key, new LogQuery(TelemetryAdmissionPolicy.UnixNanos(_clock.GetUtcNow().AddHours(-1)),
                    TelemetryAdmissionPolicy.UnixNanos(_clock.GetUtcNow()), null, null,
                    new Dictionary<string, string>(), 1, null)).Records.Count == 1;
            if (stored) break;
            await Task.Delay(100);
        }
        Assert.True(stored, "The intake writer did not commit metrics and logs within five seconds.");

        var direct = factory.Services.GetRequiredService<ProjectOperationsService>().Read("shop");
        Assert.NotNull(direct);
        _ = JsonSerializer.Serialize(direct, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        // Deploy's existing declaration reader still resolves the repository through Git on GET.
        // Match production's warning mode for this HTTP projection until that read is migrated.
        GitReadRequestGuard.Mode = GitReadGuardMode.Warn;
        using var operations = await client.GetAsync("/api/projects/shop/deploy/operations");
        var logDirectory = Path.Combine(_root, "logs");
        var hostLog = Directory.Exists(logDirectory)
            ? string.Join("\n", Directory.EnumerateFiles(logDirectory).Select(File.ReadAllText)) : "";
        Assert.True(operations.StatusCode == HttpStatusCode.OK,
            $"Operations returned {(int)operations.StatusCode}: {await operations.Content.ReadAsStringAsync()}\n{hostLog[^Math.Min(hostLog.Length, 6000)..]}");
        using var document = JsonDocument.Parse(await operations.Content.ReadAsStringAsync());
        var row = Assert.Single(document.RootElement.GetProperty("environments").EnumerateArray());
        Assert.True(row.GetProperty("telemetryAvailable").GetBoolean());
        var values = row.GetProperty("metrics");
        Assert.Equal(2, values.GetProperty("requests").GetInt64());
        Assert.Equal(1, values.GetProperty("errorRate").GetDouble());
        Assert.Equal(20, values.GetProperty("responseP50Ms").GetDouble());
        Assert.Equal(8192, values.GetProperty("diskBytes").GetInt64());

        var since = Uri.EscapeDataString(_clock.GetUtcNow().AddHours(-1).ToString("O"));
        using var result = await client.GetAsync($"/api/projects/shop/deploy/operations/logs?environmentId=stage&since={since}&level=ERROR&releaseId=v2&text=timeout");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        using var logDocument = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal("upstream timeout", Assert.Single(logDocument.RootElement.GetProperty("lines").EnumerateArray())
            .GetProperty("message").GetString());
    }

    public void Dispose()
    {
        GitReadRequestGuard.Mode = GitReadGuardMode.Strict;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

[CollectionDefinition(OperationsTelemetryApiCollection.Name, DisableParallelization = true)]
public sealed class OperationsTelemetryApiCollection
{
    public const string Name = "OperationsTelemetryApi";
}
