using System.Net;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// Capability failure reports are telemetry about work that has already failed.
/// A server that does not mount the capability route answers 404 - which used to
/// escape as a <see cref="TaskServerException"/> out of the review executor's
/// infrastructure-failure path and replaced the fenced review report with a
/// crash (AGT-2374). These pin the best-effort contract instead.
/// </summary>
public class CapabilityFailureReporterTests
{
    [Fact]
    public async Task An_unmounted_capability_route_is_reported_as_a_deferred_diagnosis()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, "no route for POST /capability-failures");
        using var client = NewClient(handler);
        var log = new List<string>();

        var delivered = await CapabilityFailureReporter.TryReportAsync(
            client,
            log.Add,
            "review:git-materialization",
            "SnapshotUnavailable",
            "The immutable snapshot was unavailable.",
            "review-capability:rat_1:7:review:git-materialization",
            "review",
            "rat_1",
            7,
            CancellationToken.None);

        Assert.False(delivered);
        Assert.Single(handler.Paths);
        Assert.Equal(
            "/api/v1/runners/agent-runner-test/capability-failures",
            handler.Paths[0]);
        var logged = Assert.Single(log);
        Assert.Contains("capability-failure report deferred", logged, StringComparison.Ordinal);
        Assert.Contains("review:git-materialization", logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_report_never_takes_down_the_caller()
    {
        var handler = new StubHandler(HttpStatusCode.Conflict, "capability-failure-conflict");
        using var client = NewClient(handler);

        var delivered = await CapabilityFailureReporter.TryReportAsync(
            client,
            _ => { },
            "toolchain:dotnet",
            "ToolchainUnavailable",
            "dotnet build is unavailable.",
            "review-capability:rat_2:9:toolchain:dotnet",
            "review",
            "rat_2",
            9,
            CancellationToken.None);

        Assert.False(delivered);
    }

    [Fact]
    public async Task An_accepted_report_is_delivered()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """
            {"status":"accepted","capabilityKey":"toolchain:dotnet","healthState":"suspect",
             "cooldownUntil":null,"wholeHostDraining":false}
            """);
        using var client = NewClient(handler);
        var log = new List<string>();

        var delivered = await CapabilityFailureReporter.TryReportAsync(
            client,
            log.Add,
            "toolchain:dotnet",
            "ToolchainUnavailable",
            "dotnet build is unavailable.",
            "review-capability:rat_3:11:toolchain:dotnet",
            "review",
            "rat_3",
            11,
            CancellationToken.None);

        Assert.True(delivered);
        Assert.Empty(log);
    }

    [Fact]
    public async Task An_http_timeout_is_swallowed_because_nothing_asked_the_runner_to_stop()
    {
        // An HttpClient timeout surfaces as TaskCanceledException - an
        // OperationCanceledException without any shutdown behind it. That is the
        // very crash this reporter exists to absorb, so the caller's token, not
        // the exception type, decides whether it propagates.
        var handler = new ThrowingHandler(new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));
        using var client = NewClient(handler);
        var log = new List<string>();

        var delivered = await CapabilityFailureReporter.TryReportAsync(
            client,
            log.Add,
            "review:git-materialization",
            "SnapshotUnavailable",
            "The immutable snapshot was unavailable.",
            "review-capability:rat_5:15:review:git-materialization",
            "review",
            "rat_5",
            15,
            CancellationToken.None);

        Assert.False(delivered);
        var logged = Assert.Single(log);
        Assert.Contains("capability-failure report deferred", logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_shutdown_still_propagates()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, "unmounted");
        using var client = NewClient(handler);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CapabilityFailureReporter.TryReportAsync(
                client,
                _ => { },
                "toolchain:dotnet",
                "ToolchainUnavailable",
                "dotnet build is unavailable.",
                "review-capability:rat_4:13:toolchain:dotnet",
                "review",
                "rat_4",
                13,
                cancelled.Token));
    }

    private static TaskServerClient NewClient(StubHandler handler)
    {
        var (options, _, _, _) = RunnerTestOptions.Parse(
            ["--server", "http://127.0.0.1:5030", "--runner-id", "agent-runner-test"]);
        var http = new HttpClient(handler) { BaseAddress = new Uri(options.ServerUrl) };
        return new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options);
    }

    /// <summary>Reproduces a transport-level fault (e.g. an HttpClient timeout).</summary>
    private sealed class ThrowingHandler(Exception failure) : StubHandler(HttpStatusCode.OK, "{}")
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<HttpResponseMessage>(failure);
        }
    }

    private class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
