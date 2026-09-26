using System.Net;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteTaskRunnerClaimGuardTests : IDisposable
{
    private readonly string _workDir = Path.Combine(
        Path.GetTempPath(), "agent-runner-claim-guard-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Project_claim_without_repository_url_is_released_before_clone_creation()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "runner-test");
        var logs = new List<string>();
        var runner = new RemoteTaskRunner(new RunnerOptions
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            GitRemote = Path.Combine(_workDir, "fallback-fetch.git"),
            GitPushRemote = "git@github.com-agentstudio:agent-orc/agent-studio.git",
            WorkDir = _workDir,
            BaseBranch = "main",
            ClaudeCliBin = "test",
        }, client, logs.Add);
        var lease = new RunLeaseInfoDto(
            "QS-42", "runner-test", "runner-test", "test-host", 123, "test",
            "lease-1", 7, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2));

        var exitCode = await runner.RunClaimedAsync(
            "QS-42", lease, CancellationToken.None,
            projectId: "PROJ-016", repositoryUrl: null);

        Assert.Equal(2, exitCode);
        Assert.False(Directory.Exists(Path.Combine(_workDir, "PROJ-016")));
        Assert.Contains(logs, line => line ==
            "remote-runner-project-not-remote-capable projectId=PROJ-016 " +
            "task=QS-42 reason=repository-url-not-configured");
        Assert.Equal(["/api/runner/lease/release"], handler.Paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? "");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"outcome":"Released","granted":false,"lease":null}"""),
            });
        }
    }
}
