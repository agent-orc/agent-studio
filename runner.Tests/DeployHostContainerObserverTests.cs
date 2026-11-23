using System.Net;
using System.Text;
using AgentRunner.RunTargets;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DeployHostContainerObserverTests
{
    // clock-independent: the restarting state drives the assertion regardless of StartedAt.
    [Fact]
    public async Task ExistingContainer_ReportsCrashLoopRestartsAndMaskedStdoutAndStderr()
    {
        var secret = "credential-" + Guid.NewGuid().ToString("N");
        var shortSecret = Guid.NewGuid().ToString("N")[..6];
        var artifact = "example/app@sha256:" + new string('a', 64);
        string? report = null;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"[{{\"project\":\"demo\",\"environmentId\":\"prod\",\"container\":\"deploy-demo-prod\",\"artifact\":\"{artifact}\"}}]",
                    Encoding.UTF8, "application/json") };
            report = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        })) { BaseAddress = new Uri("https://studio.test/") };
        Task<ProcessResult> Docker(string file, IReadOnlyList<string> args, CancellationToken _)
        {
            Assert.Equal("docker", file);
            if (args[0] == "container")
                return Task.FromResult(new ProcessResult(0,
                    "{\"State\":{\"Status\":\"restarting\",\"StartedAt\":\"2026-11-17T10:00:00Z\"},\"RestartCount\":102,\"Config\":{\"Env\":[\"API_TOKEN=" + secret + "\",\"API_KEY=" + shortSecret + "\"]}}", ""));
            Assert.Equal("logs", args[0]);
            return Task.FromResult(new ProcessResult(0, "starting\nFATAL: " + secret, "stderr failure " + shortSecret));
        }

        Assert.Equal(1, await DeployHostContainerObserver.ObserveAsync(client, "host", CancellationToken.None, Docker));
        Assert.NotNull(report);
        Assert.Contains("\"restarts\":102", report);
        Assert.Contains("\"state\":\"crash-loop\"", report);
        Assert.Contains("FATAL: [redacted]", report);
        Assert.Contains("stderr failure", report);
        Assert.DoesNotContain(secret, report);
        Assert.DoesNotContain(shortSecret, report);
    }

    private const string Target =
        "[{\"project\":\"demo\",\"environmentId\":\"prod\",\"container\":\"deploy-demo-prod\",\"artifact\":\"example/app\"}]";

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> answer)
        => new(new Handler(answer)) { BaseAddress = new Uri("https://studio.test/") };

    private static HttpResponseMessage Targets(string json = Target)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"State\":\"running\",\"RestartCount\":0}")]
    [InlineData("{\"State\":{\"Status\":\"running\"}}")]
    [InlineData("{\"State\":{\"Status\":\"running\"},\"RestartCount\":\"3\"}")]
    [InlineData("{\"State\":{\"Status\":null},\"RestartCount\":1}")]
    public async Task UnexpectedDockerJson_ReportsFailedStateInsteadOfThrowing(string inspect)
    {
        string? report = null;
        using var client = Client(request =>
        {
            if (request.Method == HttpMethod.Get) return Targets();
            report = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        Task<ProcessResult> Docker(string _, IReadOnlyList<string> args, CancellationToken __)
            => Task.FromResult(new ProcessResult(0, args[0] == "container" ? inspect : "", ""));

        Assert.Equal(1, await DeployHostContainerObserver.ObserveAsync(client, "host", CancellationToken.None, Docker));
        Assert.Contains("\"state\":\"failed\"", report);
    }

    [Fact]
    public async Task MalformedEnvironmentAndStartedAt_StillReportCrashLoop()
    {
        string? report = null;
        using var client = Client(request =>
        {
            if (request.Method == HttpMethod.Get) return Targets();
            report = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        Task<ProcessResult> Docker(string _, IReadOnlyList<string> args, CancellationToken __)
            => Task.FromResult(args[0] == "container"
                ? new ProcessResult(0, "{\"State\":{\"Status\":\"restarting\",\"StartedAt\":7},\"RestartCount\":5,\"Config\":{\"Env\":[null,3,\"A=valueX\"]}}", "")
                : new ProcessResult(0, "boom valueX", ""));

        Assert.Equal(1, await DeployHostContainerObserver.ObserveAsync(client, "host", CancellationToken.None, Docker));
        Assert.Contains("\"state\":\"crash-loop\"", report);
        Assert.Contains("\"restarts\":5", report);
        Assert.DoesNotContain("valueX", report);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RefusedReport_ReturnsZeroInsteadOfThrowing(HttpStatusCode refusal)
    {
        using var client = Client(request => request.Method == HttpMethod.Get ? Targets() : new HttpResponseMessage(refusal));

        Assert.Equal(0, await DeployHostContainerObserver.ObserveAsync(client, "host", CancellationToken.None, RunningDocker));
    }

    [Fact]
    public async Task UnreachableReport_ReturnsZeroInsteadOfThrowing()
    {
        using var client = Client(request => request.Method == HttpMethod.Get
            ? Targets()
            : throw new HttpRequestException("connection refused"));

        Assert.Equal(0, await DeployHostContainerObserver.ObserveAsync(client, "host", CancellationToken.None, RunningDocker));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"project\":\"demo\"}")]
    [InlineData("[null,{\"project\":\"demo\"}]")]
    public async Task UnreadableTargetList_ReturnsZeroWithoutReporting(string targets)
    {
        var posted = false;
        using var client = Client(request =>
        {
            if (request.Method == HttpMethod.Get) return Targets(targets);
            posted = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Assert.Equal(0, await DeployHostContainerObserver.ObserveAsync(client, "host", CancellationToken.None, RunningDocker));
        Assert.False(posted);
    }

    [Fact]
    public async Task CancelledPoll_StillPropagatesCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var client = Client(_ => Targets());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DeployHostContainerObserver.ObserveAsync(client, "host", cancelled.Token, RunningDocker));
    }

    private static Task<ProcessResult> RunningDocker(string _, IReadOnlyList<string> args, CancellationToken __)
        => Task.FromResult(new ProcessResult(0,
            args[0] == "container" ? "{\"State\":{\"Status\":\"running\"},\"RestartCount\":0}" : "", ""));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(answer(request));
    }
}
