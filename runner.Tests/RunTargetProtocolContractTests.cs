using System.Net;
using System.Text;
using System.Text.Json;
using AgentRunner.RunTargets;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// IP-1 outbound-only contract: the run-target host only ever calls Studio,
/// on three declared routes, and holds no listener. Studio being unreachable,
/// or an older Studio that sends no removal signals, leaves the host's apps alone.
/// </summary>
public sealed class RunTargetProtocolContractTests : IDisposable
{
    private static readonly string Artifact = "registry.example/app@sha256:" + new string('d', 64);
    private readonly TempWorkspace _workspace = new("run-target-protocol");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task OneCycle_ReadsDesiredState_ThenReportsTheJob_ThenObservedState_AndNothingElse()
    {
        var handler = new RecordingHandler(_clock);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://studio.example/") };
        var docker = new FakeRunTargetDocker();
        var daemon = Daemon(new RunTargetClient(http, "run-host-1"), docker, []);

        await daemon.CycleAsync(CancellationToken.None);

        Assert.Equal(
        [
            "GET /api/deploy/run-targets/run-host-1/desired-state",
            "POST /api/deploy/jobs/demo/job-7/report",
            "POST /api/deploy/run-targets/run-host-1/observed-state",
        ], handler.Requests.Select(item => $"{item.Method} {item.Path}"));
        Assert.All(handler.Requests, item => Assert.Equal("studio.example", item.Host));
        var report = JsonDocument.Parse(handler.Requests[1].Body).RootElement;
        Assert.Equal("lease-7", report.GetProperty("leaseToken").GetString());
        Assert.True(report.GetProperty("healthy").GetBoolean());
        Assert.Equal("health-ok", report.GetProperty("evidence").GetString());
        var observed = JsonDocument.Parse(handler.Requests[2].Body).RootElement.GetProperty("environments")[0];
        Assert.Equal("running", observed.GetProperty("state").GetString());
        Assert.Equal(Artifact, observed.GetProperty("runningArtifact").GetString());
        Assert.Equal(3, RunTargetClient.RouteTemplates.Count);
    }

    [Fact]
    public async Task StudioOutage_BacksOff_AndTouchesNoContainer()
    {
        var docker = new FakeRunTargetDocker();
        var waits = new List<TimeSpan>();
        using var stop = new CancellationTokenSource();
        var log = new List<string>();
        var daemon = new RunTargetDaemon(new UnreachableStudio(), Reconciler(docker, log), TimeSpan.FromSeconds(15),
            (wait, _) =>
            {
                waits.Add(wait);
                if (waits.Count == 3) stop.Cancel();
                return Task.CompletedTask;
            }, log.Add);

        await daemon.RunAsync(stop.Token);

        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)], waits);
        Assert.Empty(docker.Calls);
        Assert.Contains(log, line => line.Contains("running apps are unchanged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DesiredStateWithoutRemovalFields_FromAnOlderStudio_RemovesNothing()
    {
        var docker = new FakeRunTargetDocker();
        var reconciler = Reconciler(docker, []);
        var spec = new RunTargetEnvironmentSpec("demo", "prod", "production", Artifact, 8080, "/", ["demo.example.com"],
            "acme", new Dictionary<string, string>(), [], new RunTargetResources(1, 512, 256), null, null, false);
        await reconciler.ReconcileAsync(new RunTargetDesiredState("run-host-1", _clock.GetUtcNow().UtcDateTime, [spec]),
            CancellationToken.None);

        var legacy = JsonSerializer.Deserialize<RunTargetDesiredState>(
            """{"hostId":"run-host-1","generatedAt":"2026-10-19T09:00:00Z","environments":[]}""", JsonSerializerOptions.Web)!;
        var result = await reconciler.ReconcileAsync(legacy, CancellationToken.None);

        Assert.Null(legacy.Removals);
        Assert.Null(legacy.Skipped);
        Assert.DoesNotContain(docker.Calls, call => call.StartsWith("down ") || call.StartsWith("stop "));
        Assert.Empty(result.Observations);
    }

    [Fact]
    public void RunTargetSource_OpensNoListener_AndCallsNoHostBack()
    {
        var root = RepositoryRoot.Find();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "runner", "RunTargets"), "*.cs").ToArray();
        Assert.NotEmpty(sources);
        string[] forbidden = ["HttpListener", "TcpListener", "UdpClient", "new Socket(", ".Bind(", ".Listen(",
            "WebApplication", "UseKestrel", "MapGet(", "MapPost("];

        var hits = sources.SelectMany(file => forbidden
            .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
            .Select(token => $"{Path.GetFileName(file)}: {token}")).ToArray();

        Assert.True(hits.Length == 0, "The run-target role must stay outbound-only:\n" + string.Join('\n', hits));
    }

    private RunTargetDaemon Daemon(IRunTargetControlPlane controlPlane, FakeRunTargetDocker docker, List<string> log)
        => new(controlPlane, Reconciler(docker, log), TimeSpan.FromSeconds(15), (_, _) => Task.CompletedTask, log.Add);

    private RunTargetReconciler Reconciler(FakeRunTargetDocker docker, List<string> log) => new(
        docker, new FakeRunTargetSecrets([]), new RunTargetSecretFiles(_workspace.Directory("secrets-runtime"), _ => true),
        new RunTargetStateStore(_workspace.Directory("state")),
        new RunTargetReconcilerSettings(RunTargetOptions.DefaultEdgeImage, new RunTargetEdgeSettings(null, false, []), null),
        _clock, (_, _) => Task.CompletedTask, log.Add);

    private sealed class UnreachableStudio : IRunTargetControlPlane
    {
        public Task<RunTargetDesiredState> GetDesiredStateAsync(CancellationToken ct)
            => throw new HttpRequestException("connection refused");
        public Task PostObservedStateAsync(RunTargetObservedReport report, CancellationToken ct)
            => throw new InvalidOperationException("must not report without a desired state");
        public Task<bool> ReportJobAsync(RunTargetJobReport report, CancellationToken ct)
            => throw new InvalidOperationException("must not report without a desired state");
    }

    private sealed record Recorded(string Method, string Host, string Path, string Body);

    private sealed class RecordingHandler(TimeProvider clock) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new(request.Method.Method, request.RequestUri!.Host, request.RequestUri.AbsolutePath, body));
            if (request.Method != HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK);
            var desired = new RunTargetDesiredState("run-host-1", clock.GetUtcNow().UtcDateTime,
            [
                new RunTargetEnvironmentSpec("demo", "prod", "production", Artifact, 8080, "/", ["demo.example.com"],
                    "acme", new Dictionary<string, string>(), [], new RunTargetResources(1, 512, 256),
                    "job-7", "lease-7", false),
            ]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(desired, JsonSerializerOptions.Web),
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
