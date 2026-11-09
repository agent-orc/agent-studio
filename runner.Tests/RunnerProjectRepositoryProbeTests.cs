using System.Net;
using System.Net.Http.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerProjectRepositoryProbeTests
{
    [Fact]
    public async Task Missing_repository_url_skips_project_and_reports_following_project()
    {
        var server = new ProbeServer([("missing", null), ("ready", "https://example.invalid/ready.git")]);
        var (probe, client, logs, git) = CreateProbe(server);
        using (client)
            await probe.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["ready"], server.ReportedProjects);
        Assert.Equal(["https://example.invalid/ready.git"], git.Origins);
        Assert.Empty(logs);
    }

    [Fact]
    public async Task Project_exception_logs_id_and_type_and_continues()
    {
        var server = new ProbeServer([("broken", "https://example.invalid/broken.git"),
            ("ready", "https://example.invalid/ready.git")]) { FailingProject = "broken" };
        var (probe, client, logs, git) = CreateProbe(server);
        using (client)
            await probe.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["ready"], server.ReportedProjects);
        Assert.Equal(["https://example.invalid/ready.git"], git.Origins);
        Assert.Contains(logs, line => line.Contains("projectId=broken", StringComparison.Ordinal)
            && line.Contains("type=HttpRequestException", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains("example.invalid", StringComparison.Ordinal));
    }

    private static (RunnerProjectRepositoryProbe Probe, TaskServerClient Client,
        List<string> Logs, RecordingGit Git) CreateProbe(ProbeServer server)
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "main",
            StateDir = Path.Combine(Path.GetTempPath(), "repository-probe-test-" + Guid.NewGuid().ToString("N")),
        };
        var http = new HttpClient(server) { BaseAddress = new Uri(options.ServerUrl) };
        var client = new TaskServerClient(http, options.RunnerId);
        var logs = new List<string>();
        var git = new RecordingGit();
        return (new RunnerProjectRepositoryProbe(options, client, git, logs.Add), client, logs, git);
    }

    private sealed class RecordingGit : IRepositoryGitProof
    {
        public List<string> Origins { get; } = [];

        public Task<RepositoryAccessProof> ProveAsync(
            RepositoryAccessBinding binding, string? generation, CancellationToken ct)
        {
            Origins.Add(binding.Origin);
            return Task.FromResult(new RepositoryAccessProof(binding.Purpose, binding.Origin,
                true, true, true, "ready"));
        }
    }

    private sealed class ProbeServer((string Id, string? Origin)[] projects) : HttpMessageHandler
    {
        private static readonly DateTime ObservedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public string? FailingProject { get; init; }
        public List<string> ReportedProjects { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/projects")
                return Reply(projects.Select(project => new ProjectDto(project.Id, "workspace", project.Id,
                    project.Id, 1, ObservedAt, ObservedAt)).ToArray());

            foreach (var project in projects)
            {
                if (path == $"/api/v1/projects/{project.Id}/repository")
                {
                    if (project.Id == FailingProject)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                    return Reply(new ProjectRepositoryStatusDto(
                        new ProjectRepositoryDto(project.Id, project.Id, project.Origin!, "develop",
                            null, "default", ObservedAt, "test"), []));
                }
                if (path == $"/api/v1/runners/runner-test/project-probes/{project.Id}")
                {
                    ReportedProjects.Add(project.Id);
                    return Reply(new ProjectRepositoryProbeDto(project.Id, "runner-test", true,
                        ProjectRepositoryProbeVerdicts.Admitted, null, ObservedAt));
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Reply<T>(T value)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(value),
            });
    }
}
