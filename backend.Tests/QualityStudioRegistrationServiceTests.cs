using System.Net;
using System.Text;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QualityStudioRegistrationServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "qs-registration-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Registers_once_and_persists_the_repository_mapping()
    {
        var calls = new List<(string Method, string Path, string? Body)>();
        var service = CreateService(request =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            return request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json(HttpStatusCode.Created, "{}");
        }, out var settings);

        var first = await service.ReconcileProjectAsync("PROJ-002");
        var second = await service.ReconcileProjectAsync("PROJ-002");

        Assert.Equal("registered", first.State);
        Assert.Equal("proj-002", second.RepositoryId);
        Assert.Equal("proj-002", settings.Get("Agent Studio").QualityStudio?.RepositoryId);
        Assert.Single(calls.Where(call => call.Method == "POST"));
        Assert.Contains("agent-studio", calls.Single(call => call.Method == "POST").Body);
        Assert.Contains("PROJ-002", calls.Single(call => call.Method == "POST").Body);
        Assert.Contains("/runner/PROJ-002/repo", calls.Single(call => call.Method == "POST").Body);
    }

    [Fact]
    public async Task Adopts_existing_repository_by_external_reference()
    {
        var posts = 0;
        var service = CreateService(request =>
        {
            if (request.Method == HttpMethod.Post) posts++;
            return Json(HttpStatusCode.OK, """{"repository":{"id":"existing-qs"}}""");
        }, out var settings);

        var result = await service.ReconcileProjectAsync("PROJ-002");

        Assert.Equal("existing-qs", result.RepositoryId);
        Assert.Equal("registered", settings.Get("Agent Studio").QualityStudio?.State);
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task Archives_the_mapped_repository_when_project_is_archived()
    {
        var deletes = new List<string>();
        var service = CreateService(request =>
        {
            if (request.Method == HttpMethod.Delete) deletes.Add(request.RequestUri!.AbsolutePath);
            return request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json(HttpStatusCode.OK, "{}");
        }, out var settings, out var projects);
        await service.ReconcileProjectAsync("PROJ-002");
        projects.SetArchived("PROJ-002", true);

        var result = await service.ReconcileProjectAsync("PROJ-002");

        Assert.Equal("archived", result.State);
        Assert.Equal(["/api/repos/proj-002"], deletes);
        Assert.Equal("archived", settings.Get("Agent Studio").QualityStudio?.State);
    }

    [Fact]
    public async Task Registration_failure_records_reason_without_throwing()
    {
        var rootPresent = false;
        var service = CreateService(request => request.Method == HttpMethod.Get
            ? Json(HttpStatusCode.NotFound, "{}")
            : rootPresent
                ? Json(HttpStatusCode.Created, "{}")
                : Json(HttpStatusCode.BadRequest, """{"code":"root-missing"}"""), out var settings);

        var result = await service.ReconcileProjectAsync("PROJ-002");

        Assert.Null(result.RepositoryId);
        Assert.Equal("not-registered", result.State);
        Assert.Equal("root-missing", settings.Get("Agent Studio").QualityStudio?.Reason);
        rootPresent = true;
        Assert.Equal("registered", (await service.ReconcileProjectAsync("PROJ-002")).State);
    }

    private QualityStudioRegistrationService CreateService(Func<HttpRequestMessage, HttpResponseMessage> send,
        out ProjectSettingsService settings)
        => CreateService(send, out settings, out _);

    private QualityStudioRegistrationService CreateService(Func<HttpRequestMessage, HttpResponseMessage> send,
        out ProjectSettingsService settings, out ProjectRegistry projects)
    {
        Directory.CreateDirectory(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = root,
            ["QualityStudio:BaseUrl"] = "http://quality.test",
            ["QualityStudio:ApiToken"] = "local-test-token",
            ["QualityStudio:RunnerWorkdir"] = "/runner",
        }).Build();
        projects = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        projects.Append(new ProjectRecord
        {
            Id = "PROJ-002", DisplayName = "Agent Studio", ShortCode = "AS",
            WorkspaceId = "WS-001", StorageLocation = Path.Combine(root, "tasks"),
        });
        settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        return new QualityStudioRegistrationService(new HttpClient(new StubHandler(send)), config,
            null!, projects, settings, NullLogger<QualityStudioRegistrationService>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
