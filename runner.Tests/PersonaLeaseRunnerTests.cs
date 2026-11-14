using AgentRunner;
using System.Net;
using Xunit;

namespace AgentRunner.Tests;

public sealed class PersonaLeaseRunnerTests
{
    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Empty_persona_claim_returns_no_lease(HttpStatusCode status)
    {
        using var http = new HttpClient(new EmptyClaimHandler(status))
        { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "runner-idle");

        Assert.Null(await client.ClaimPersonaAsync("runner-idle", CancellationToken.None));
    }

    [Fact]
    public async Task Idle_poll_writes_no_failure_line()
    {
        var handler = new EmptyClaimHandler(HttpStatusCode.NoContent);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "runner-idle");
        var messages = new List<string>();
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server", RunnerId = "runner-idle", RunnerName = "Idle runner",
            Hostname = "host-idle", BackendName = "test", WorkDir = Path.GetTempPath(),
            StateDir = Path.GetTempPath(), BaseBranch = "main", ClaudeCliBin = "claude",
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await new PersonaLeaseRunner(options, client, messages.Add).PollAsync(stop.Token);

        Assert.True(handler.Claims > 0);
        Assert.DoesNotContain(messages, message => message.Contains("persona-lease-poll-failed", StringComparison.Ordinal));
    }

    private sealed class EmptyClaimHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Claims { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("/api/runner/personas/claim", request.RequestUri?.AbsolutePath);
            Claims++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("") });
        }
    }

    [Fact]
    public void Dense_persona_grounds_are_refused_before_a_model_call()
    {
        Assert.False(PersonaLeaseRunner.PromptOverrun(
            new string('a', 8_000), 12_000));
        Assert.True(PersonaLeaseRunner.PromptOverrun(
            new string('!', 12_000), 12_000));
    }

    [Fact]
    public void Persona_child_environment_drops_deploy_credentials_but_keeps_provider_auth()
    {
        var environment = new Dictionary<string, string>
        {
            ["AWS_SECRET_ACCESS_KEY"] = Guid.NewGuid().ToString("N"),
            ["GITHUB_TOKEN"] = Guid.NewGuid().ToString("N"),
            ["ANTHROPIC_API_KEY"] = Guid.NewGuid().ToString("N"),
        };

        WorkerEdgeCredentialBoundary.RemovePersonaDeploymentCredentials(environment);

        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", environment.Keys);
        Assert.DoesNotContain("GITHUB_TOKEN", environment.Keys);
        Assert.Contains("ANTHROPIC_API_KEY", environment.Keys);
    }
}
