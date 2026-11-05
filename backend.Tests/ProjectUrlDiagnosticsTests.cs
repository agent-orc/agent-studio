using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectUrlDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "url-diagnostics-" + Guid.NewGuid().ToString("N"));

    public ProjectUrlDiagnosticsTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Redact_BoundsOutputAndRemovesCommonSecrets()
    {
        var raw = new string('x', 20_000) + "\ntoken=super-secret\nAuthorization: Bearer abc.def\nhttps://user:pass@example.test/\n";

        var result = ProjectUrlProcessService.Redact(raw);

        Assert.DoesNotContain("super-secret", result);
        Assert.DoesNotContain("abc.def", result);
        Assert.DoesNotContain("user:pass", result);
        Assert.Contains("[REDACTED]", result);
        Assert.True(result.Length <= ProjectUrlProcessService.OutputTailLimit + 1);
    }

    [Fact]
    public async Task StartAsync_InvalidCwdHasDistinctDiagnosisWithoutSpawning()
    {
        var service = new ProjectUrlProcessService(
            NullLogger<ProjectUrlProcessService>.Instance,
            new UnusedHttpClientFactory());
        var project = new ProjectRecord { Id = "PROJ-001", RepositoryPath = _root };
        var url = new ProjectUrlRecord
        {
            Id = "url-1", Url = "http://127.0.0.1:4184",
            StartRule = new ProjectUrlStartRule { Command = "npm start", Cwd = "missing-folder", Port = 4184 },
        };

        var result = await service.StartAsync(project, url, CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.InvalidCwd, result.Classification);
        Assert.False(result.ProcessCreated);
        Assert.Contains("does not exist", result.Summary);
    }

    [Fact]
    public async Task StartAsync_InvalidCwdRedactsSecretPathSegments()
    {
        var service = new ProjectUrlProcessService(
            NullLogger<ProjectUrlProcessService>.Instance,
            new UnusedHttpClientFactory());
        var url = new ProjectUrlRecord
        {
            Id = "url-1", Url = "http://127.0.0.1:4184",
            StartRule = new ProjectUrlStartRule { Command = "npm start", Cwd = "password=private", Port = 4184 },
        };

        var result = await service.StartAsync(Project(), url, CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.InvalidCwd, result.Classification);
        Assert.DoesNotContain("private", result.Cwd);
        Assert.Contains("[REDACTED]", result.Cwd);
    }

    // MachineBound 22.07.: startet einen echten Kindprozess; Dispose loescht den Temp-Root,
    // den der noch nicht beendete Prozess unter Last haelt (IOException "used by another process" im Gate belegt).
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task StartAsync_FailedCommandIsCommandUnavailableWithBoundedEvidence()
    {
        var service = Service();
        var result = await service.StartAsync(Project(), Url("agent-studio-command-that-does-not-exist"), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.CommandUnavailable, result.Classification);
        Assert.True(result.ProcessCreated);
        Assert.NotNull(result.ExitCode);
        Assert.True(result.StderrTail.Length <= ProjectUrlProcessService.OutputTailLimit);
    }

    // MachineBound 22.07.: startet einen echten Kindprozess (exit-Command); Temp-Root-Cleanup
    // rennt unter Last gegen den Prozess-Exit (IOException im Gate belegt).
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task StartAsync_ProcessExitIsNotRunning()
    {
        var command = OperatingSystem.IsWindows() ? "exit /b 7" : "exit 7";
        var result = await Service().StartAsync(Project(), Url(command), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.ProcessExited, result.Classification);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(ProjectUrlStartupFailureReasons.ProcessExit, result.StartupFailureReason);
        Assert.False(result.ContentReady);
    }

    [Fact]
    public async Task StartAsync_MissingCommandIsInvalidConfiguration()
    {
        var result = await Service().StartAsync(Project(), Url(""), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.InvalidConfiguration, result.Classification);
        Assert.False(result.ProcessCreated);
    }

    // MachineBound 22.07.: startet einen echten Kindprozess und reserviert einen Port; timing-/lastabhaengig.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task TestAsync_LiveProcessWhosePortNeverOpensIsBoundedAndStopped()
    {
        using var closed = new ClosedLoopbackPort();
        var port = closed.Port;
        var command = OperatingSystem.IsWindows() ? "ping 127.0.0.1 -n 5 >nul" : "sleep 4";
        var candidate = UrlAt(port) with
        {
            StartRule = new ProjectUrlStartRule { Command = command, Port = port, ReadinessTimeoutSeconds = 2 },
        };

        var result = await Service().TestAsync(Project(), candidate, CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.PortNeverOpened, result.Classification);
        Assert.True(result.ProcessCreated);
        Assert.True(result.TimedOut);
        Assert.False(result.PortReachable);
    }

    // MachineBound 22.07.: startet einen echten Kindprozess, reserviert einen Port und misst per
    // Wanduhr (Task.Delay 3.5s) - flakt unter Parallellast.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task StartAsync_KeepsWaitingWhileConsoleStaysActivePastIdleWindow()
    {
        // Idle window is 2s but the command keeps emitting output for ~5s while
        // the port never opens. Startup must NOT be abandoned at 2s: as long as
        // the console is active the wait continues (still "starting").
        var service = Service();
        var project = Project();
        using var closed = new ClosedLoopbackPort();
        var port = closed.Port;
        var command = OperatingSystem.IsWindows()
            ? "for /l %i in (1,1,6) do @(echo working %i & ping -n 2 127.0.0.1 >nul)"
            : "for i in 1 2 3 4 5 6; do echo working $i; sleep 0.8; done";
        var candidate = UrlAt(port) with
        {
            StartRule = new ProjectUrlStartRule { Command = command, Port = port, ReadinessTimeoutSeconds = 2 },
        };
        using var cancellation = new CancellationTokenSource();

        var run = service.StartAsync(project, candidate, cancellation.Token);
        // Wait past the 2s idle window; the active console must keep it starting.
        await Task.Delay(3500);
        Assert.False(run.IsCompleted, "startup was abandoned even though the console was still active");
        Assert.Equal(ProjectUrlDiagnosisClasses.Starting, service.Latest(project, candidate)?.Classification);

        cancellation.Cancel();
        var result = await run;
        Assert.Equal(ProjectUrlDiagnosisClasses.Timeout, result.Classification);
        service.Stop(project.Id, candidate.Id);
    }

    // MachineBound 22.07.: startet einen echten Kindprozess, reserviert einen Port und prueft ein
    // Wanduhr-Zeitbudget (< 30s) - lastabhaengig.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task StartAsync_SilentUnreachableProcessFailsAfterIdleWindow()
    {
        // A live-but-silent process whose port never opens must fail once the
        // idle window elapses — well before the 5-minute hard cap.
        var service = Service();
        var project = Project();
        using var closed = new ClosedLoopbackPort();
        var port = closed.Port;
        var command = OperatingSystem.IsWindows() ? "ping 127.0.0.1 -n 8 >nul" : "sleep 7";
        var candidate = UrlAt(port) with
        {
            StartRule = new ProjectUrlStartRule { Command = command, Port = port, ReadinessTimeoutSeconds = 2 },
        };
        var started = DateTime.UtcNow;

        var result = await service.TestAsync(project, candidate, CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.PortNeverOpened, result.Classification);
        Assert.True(result.TimedOut);
        Assert.False(result.PortReachable);
        Assert.Contains("no console output", result.Summary);
        Assert.Equal(ProjectUrlStartupFailureReasons.SilenceTimeout, result.StartupFailureReason);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), "idle failure should trip long before the hard cap");
    }

    [Fact]
    public void StartupFailure_HardCapMessageWinsOverIdleAndDefaultsToTenMinutes()
    {
        var rule = new ProjectUrlStartRule { Command = "npm start", Port = 4216 };

        var reachable = ProjectUrlProcessService.StartupFailure(
            rule, "http://127.0.0.1:4216", "/cwd", hardCapReached: true, everPortReachable: true,
            idleSeconds: 0, startupLimitSeconds: ProjectUrlProcessService.DefaultStartupTimeoutSeconds, "", "");
        var unreachable = ProjectUrlProcessService.StartupFailure(
            rule, "http://127.0.0.1:4216", "/cwd", hardCapReached: true, everPortReachable: false,
            idleSeconds: 0, startupLimitSeconds: ProjectUrlProcessService.DefaultStartupTimeoutSeconds, "", "");

        Assert.Equal(600, ProjectUrlProcessService.DefaultStartupTimeoutSeconds);
        Assert.Contains("10-minute", reachable.Summary);
        Assert.Contains("10-minute", unreachable.Summary);
        Assert.Equal(ProjectUrlDiagnosisClasses.Timeout, reachable.Classification);
        Assert.Equal(ProjectUrlDiagnosisClasses.PortNeverOpened, unreachable.Classification);
        Assert.Equal(ProjectUrlStartupFailureReasons.StartupLimit, reachable.StartupFailureReason);
        Assert.True(reachable.TimedOut);
    }

    [Fact]
    public void StartupFailure_IdleMessageReportsSilenceSeconds()
    {
        var rule = new ProjectUrlStartRule { Command = "npm start", Port = 4216 };

        var result = ProjectUrlProcessService.StartupFailure(
            rule, "http://127.0.0.1:4216", "/cwd", hardCapReached: false, everPortReachable: false,
            idleSeconds: 23, startupLimitSeconds: 600, "", "");

        Assert.Equal(ProjectUrlDiagnosisClasses.PortNeverOpened, result.Classification);
        Assert.Contains("no console output for 23s", result.Summary);
        Assert.Equal(ProjectUrlStartupFailureReasons.SilenceTimeout, result.StartupFailureReason);
        Assert.True(result.TimedOut);
    }

    // MachineBound: starts a real child process which emits often enough to
    // avoid the silence timeout, proving that only the independent startup cap
    // ends an active cold build.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task StartAsync_ActiveConsoleFailsOnlyAtConfiguredStartupLimit()
    {
        using var closed = new ClosedLoopbackPort();
        var port = closed.Port;
        var command = OperatingSystem.IsWindows()
            ? "for /l %i in (1,1,12) do @(echo building %i & ping -n 2 127.0.0.1 >nul)"
            : "while true; do echo building; sleep 0.25; done";
        var candidate = UrlAt(port) with
        {
            StartRule = new ProjectUrlStartRule
            {
                Command = command,
                Port = port,
                ReadinessTimeoutSeconds = 1,
                StartupTimeoutSeconds = 2,
            },
        };

        var result = await Service().TestAsync(Project(), candidate, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(ProjectUrlStartupFailureReasons.StartupLimit, result.StartupFailureReason);
        Assert.Contains("2-second startup limit", result.Summary);
    }

    // MachineBound 22.07.: startet einen echten Kindprozess und reserviert einen Port; timing-/lastabhaengig.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task StartAsync_PublishesStartingWhileReadinessIsInFlight()
    {
        var service = Service();
        var project = Project();
        using var closed = new ClosedLoopbackPort();
        var port = closed.Port;
        var command = OperatingSystem.IsWindows() ? "ping 127.0.0.1 -n 5 >nul" : "sleep 4";
        var candidate = UrlAt(port) with
        {
            StartRule = new ProjectUrlStartRule { Command = command, Port = port, ReadinessTimeoutSeconds = 5 },
        };
        using var cancellation = new CancellationTokenSource();

        var run = service.TestAsync(project, candidate, cancellation.Token);
        for (var attempt = 0; attempt < 20 && service.Latest(project, candidate)?.Classification != ProjectUrlDiagnosisClasses.Starting; attempt++)
            await Task.Delay(20);

        Assert.Equal(ProjectUrlDiagnosisClasses.Starting, service.Latest(project, candidate)?.Classification);
        cancellation.Cancel();
        var result = await run;
        Assert.Equal(ProjectUrlDiagnosisClasses.Timeout, result.Classification);
    }

    // MachineBound 22.07.: bindet einen echten Loopback-TCP-Port und macht einen echten TCP-Connect.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task ProbeAsync_HttpErrorIsNotRunning()
    {
        using var listener = ListeningPort(out var port);
        var service = Service(() => HtmlResponse(HttpStatusCode.ServiceUnavailable, "<main>Unavailable</main>"));

        var result = await service.ProbeAsync(Project(), UrlAt(port), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.HttpError, result.Classification);
        Assert.Equal(503, result.HttpStatus);
        Assert.False(result.ContentReady);
    }

    // MachineBound 22.07.: bindet einen echten Loopback-TCP-Port und macht einen echten TCP-Connect.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task ProbeAsync_ExplicitFramePolicyIsNotRenderable()
    {
        using var listener = ListeningPort(out var port);
        var service = Service(() =>
        {
            var response = HtmlResponse(HttpStatusCode.OK, "<main>Ready</main>");
            response.Headers.TryAddWithoutValidation("X-Frame-Options", "DENY");
            return response;
        });

        var result = await service.ProbeAsync(Project(), UrlAt(port), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.ContentNotRenderable, result.Classification);
        Assert.True(result.ContentReady);
        Assert.False(result.IframeReady);
        Assert.Contains("X-Frame-Options", result.FramePolicy);
    }

    // MachineBound 22.07.: bindet einen echten Loopback-TCP-Port und macht einen echten TCP-Connect.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task ProbeAsync_BlankHtmlIsNotRenderable()
    {
        using var listener = ListeningPort(out var port);
        var service = Service(() => HtmlResponse(HttpStatusCode.OK, "<html><head><title>Blank</title></head><body></body></html>"));

        var result = await service.ProbeAsync(Project(), UrlAt(port), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.ContentNotRenderable, result.Classification);
        Assert.False(result.ContentReady);
        Assert.False(result.IframeReady);
    }

    // MachineBound 22.07.: bindet einen echten Loopback-TCP-Port und macht einen echten TCP-Connect.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task ProbeAsync_RenderableHtmlRequiresTcpAndHttpEvidence()
    {
        using var listener = ListeningPort(out var port);
        var service = Service(() => HtmlResponse(HttpStatusCode.OK, "<main>Ready</main>"));

        var result = await service.ProbeAsync(Project(), UrlAt(port), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.Running, result.Classification);
        Assert.True(result.PortReachable);
        Assert.Equal(200, result.HttpStatus);
        Assert.True(result.ContentReady);
    }

    // MachineBound 22.07.: bindet einen echten Loopback-TCP-Port und macht einen echten TCP-Connect.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task ProbeAsync_HttpTimeoutIsNotRunning()
    {
        using var listener = ListeningPort(out var port);
        var service = Service(() => throw new TaskCanceledException("bounded timeout"));

        var result = await service.ProbeAsync(Project(), UrlAt(port), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.Timeout, result.Classification);
        Assert.True(result.TimedOut);
        Assert.False(result.ContentReady);
    }

    // A bound socket that never listens keeps the refused port reserved during the probe.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task ProbeAsync_NoListenerIsNotStarted()
    {
        using var closed = new ClosedLoopbackPort();
        var port = closed.Port;
        var result = await Service().ProbeAsync(Project(), UrlAt(port), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.NotStarted, result.Classification);
        Assert.False(result.PortReachable);
    }

    [Fact]
    public void Detection_FindsAgentStudioWebsiteReadmeRuleWithCwdAndPort()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), """
            ## Preview
            ```sh
            cd 04-angular-static-final
            npm start -- --host 127.0.0.1 --port 4184
            ```
            """);
        Directory.CreateDirectory(Path.Combine(_root, "04-angular-static-final"));
        var service = new ProjectUrlDetectionService(NullLogger<ProjectUrlDetectionService>.Instance);

        var result = service.Detect(new ProjectRecord { Id = "PROJ-012", RepositoryPath = _root });

        var suggestion = Assert.Single(result);
        Assert.Equal("npm start -- --host 127.0.0.1 --port 4184", suggestion.Command);
        // Suggestions carry absolute working directories (develop convention:
        // NormaliseStartRule validates cwd as an absolute, existing path).
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "04-angular-static-final")), suggestion.Cwd);
        Assert.Equal(4184, suggestion.Port);
        Assert.Equal("http://localhost:4184", suggestion.Url);
        Assert.Equal("readme", suggestion.Source);
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("HTTP should not be used for invalid cwd.");
    }

    private sealed class StubHttpClientFactory(Func<HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(response), disposeHandler: true);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response());
    }

    private ProjectUrlProcessService Service() => new(
        NullLogger<ProjectUrlProcessService>.Instance,
        new UnusedHttpClientFactory());

    private ProjectUrlProcessService Service(Func<HttpResponseMessage> response) => new(
        NullLogger<ProjectUrlProcessService>.Instance,
        new StubHttpClientFactory(response));

    private ProjectRecord Project() => new() { Id = "PROJ-001", RepositoryPath = _root };

    private static ProjectUrlRecord Url(string command) => new()
    {
        Id = "url-1", Url = "http://127.0.0.1:4184",
        StartRule = new ProjectUrlStartRule { Command = command, Port = 4184, ReadinessTimeoutSeconds = 2 },
    };

    private static ProjectUrlRecord UrlAt(int port) => new()
    {
        Id = "url-1", Url = $"http://127.0.0.1:{port}",
        StartRule = new ProjectUrlStartRule { Command = "unused", Port = port, ReadinessTimeoutSeconds = 2 },
    };

    private static TcpListener ListeningPort(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static HttpResponseMessage HtmlResponse(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html"),
    };
}
