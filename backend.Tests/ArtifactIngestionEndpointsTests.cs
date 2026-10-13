using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ArtifactIngestionEndpointsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "artifact-ingest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Advertised_limit_accounts_for_base64_without_raising_global_body_cap()
    {
        var policy = ArtifactTransferPolicy.Resolve(
            25L * 1024 * 1024,
            projectMaxFileBytes: 20L * 1024 * 1024,
            projectMaxTotalBytes: null);

        Assert.Equal(25L * 1024 * 1024, policy.MaxRequestBodyBytes);
        Assert.True(policy.MaxFileBytes < 20L * 1024 * 1024);
        Assert.True((policy.MaxFileBytes * 4 / 3) < policy.MaxRequestBodyBytes);
        Assert.Equal(100L * 1024 * 1024, policy.MaxTotalBytes);
    }

    [Fact]
    public void Default_per_file_limit_is_eight_mebibytes()
    {
        var limits = ArtifactTransferPolicy.Resolve(25L * 1024 * 1024, null, null);
        Assert.Equal(8L * 1024 * 1024, limits.MaxFileBytes);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(1025)]
    public void Base64_size_calculation_preserves_the_exact_binary_boundary(int size)
    {
        Assert.Equal(size, ArtifactTransferPolicy.DecodedLength(
            Convert.ToBase64String(new byte[size])));
    }

    [Fact]
    public async Task Oversized_artifact_request_returns_typed_limit_and_received_size()
    {
        var called = false;
        var middleware = new ArtifactRequestLimitMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            new ArtifactRequestLimits(1024));
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/runner/artifacts";
        context.Request.ContentLength = 2048;
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection()
            .AddOptions()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { })
            .BuildServiceProvider();

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("artifact-request-too-large", body.RootElement.GetProperty("type").GetString());
        Assert.Equal(1024, body.RootElement.GetProperty("limitBytes").GetInt64());
        Assert.Equal(2048, body.RootElement.GetProperty("receivedBytes").GetInt64());
    }

    [Fact]
    public void Partial_artifact_board_fact_names_file_size_limit_and_non_transfer()
    {
        var fact = ArtifactTransferPolicy.BoardFact(new ArtifactTransferIssue(
            "results/playwright/archive/trace.zip",
            18L * 1024 * 1024,
            "exceeded the 25 MB upload limit"));

        Assert.Equal(
            "result artifact trace.zip 18 MB exceeded the 25 MB upload limit; not transferred",
            fact);
    }

    [Fact]
    public void NormalizeResultsPath_AddsResultsPrefix()
    {
        Assert.Equal(
            "results/screenshots/home--real.png",
            ArtifactIngestionEndpoints.NormalizeResultsPath("screenshots/home--real.png"));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("results/../outside.png")]
    [InlineData("C:/temp/outside.png")]
    [InlineData(@"C:\temp\outside.png")]
    public void NormalizeResultsPath_RejectsEscapes(string path)
    {
        Assert.Throws<ArtifactIngestException>(() => ArtifactIngestionEndpoints.NormalizeResultsPath(path));
    }

    [Fact]
    public void WriteArtifacts_WritesDecodedBytesUnderResults()
    {
        var job = Path.Combine(_root, "projects", "demo", "tasks", "001", "AGT-1");
        Directory.CreateDirectory(job);
        var task = new TaskInfo { Id = "AGT-1", TaskKey = "AGT-1", FolderPath = job };
        var request = new ArtifactIngestRequest(
            "AGT-1",
            [new RunnerArtifactUpload(
                "screenshots/home--real.png",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("png bytes")))]);

        var response = ArtifactIngestionEndpoints.WriteArtifacts(task, request);

        Assert.Equal(1, response.Uploaded);
        Assert.Equal(["results/screenshots/home--real.png"], response.Files);
        Assert.Equal(
            "png bytes",
            File.ReadAllText(Path.Combine(job, "results", "screenshots", "home--real.png")));
    }

    [Fact]
    public void WriteArtifacts_RedactsCredentialsFromTextEvidence()
    {
        var job = Path.Combine(_root, "projects", "demo", "tasks", "001", "AGT-2");
        Directory.CreateDirectory(job);
        var task = new TaskInfo { Id = "AGT-2", TaskKey = "AGT-2", FolderPath = job };
        const string secret = "rnr.credential.abcdefghijklmnopqrstuvwxyz012345";
        var request = new ArtifactIngestRequest(
            "AGT-2",
            [new RunnerArtifactUpload("report.md", Convert.ToBase64String(Encoding.UTF8.GetBytes($"token: {secret}")))]);

        ArtifactIngestionEndpoints.WriteArtifacts(task, request);

        var written = File.ReadAllText(Path.Combine(job, "results", "report.md"));
        Assert.DoesNotContain(secret, written);
        Assert.Contains("REDACTED_CREDENTIAL", written);
    }

    [Fact]
    public void ArtifactUploadCommitMessage_ListsUploadedFiles()
    {
        var message = WorkspaceArtifactCommitService.BuildArtifactUploadMessage(
            "AGT-1",
            ["results/a.png", "results/report.json"]);

        Assert.Contains("record uploaded artifacts for AGT-1", message);
        Assert.Contains("Artifact-Upload-Files: results/a.png,results/report.json", message);
    }

    // ---- AGT-3095: writes refused after settlement ----------------------------

    private static readonly DateTimeOffset SettledAt = new(2026, 10, 12, 18, 19, 52, TimeSpan.Zero);
    private const string RunnerId = "agent-runner-01";

    [Theory]
    [InlineData(AttemptLifecycleState.Completed, true)]
    [InlineData(AttemptLifecycleState.Failed, true)]
    [InlineData(AttemptLifecycleState.Cancelled, true)]
    [InlineData(AttemptLifecycleState.Superseded, false)]
    [InlineData(AttemptLifecycleState.Leased, false)]
    public void Only_an_invalid_state_refusal_of_a_terminal_attempt_is_after_settlement(
        AttemptLifecycleState state,
        bool expected)
    {
        var run = new RunAttemptDto(
            "run-1", "AGT-1", "demo", null, state, null, 1, 1,
            SettledAt.UtcDateTime, SettledAt.UtcDateTime, null, null, null, []);

        Assert.Equal(expected, ArtifactIngestionEndpoints.IsAfterSettlement(
            new AttemptWriteResult(AttemptWriteStatus.InvalidState, "run-1", "refused", run)));
        Assert.False(ArtifactIngestionEndpoints.IsAfterSettlement(
            new AttemptWriteResult(AttemptWriteStatus.StaleFence, "run-1", "refused", run)));
        Assert.False(ArtifactIngestionEndpoints.IsAfterSettlement(
            new AttemptWriteResult(AttemptWriteStatus.InvalidState, "run-1", "refused")));
    }

    [Fact]
    public async Task Artifact_write_for_a_completed_attempt_returns_the_unchanged_409_and_logs_once()
    {
        await using var host = await ArtifactHost.StartAsync(_root, SettledAt);
        var run = host.AcquireRun("AGT-1");
        host.Settle(run);

        using var response = await host.Http.PostAsJsonAsync("/api/runner/artifacts", host.Upload(run, "status.md"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalidState", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(run.AttemptId, body.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal(
            "RunAttempt is Completed and no longer has write authority.",
            body.RootElement.GetProperty("message").GetString());
        Assert.Equal("completed", body.RootElement.GetProperty("runAttempt").GetProperty("state").GetString());
        var logged = Assert.Single(host.Logs.Messages, message => message.StartsWith("runner-artifact-after-settlement", StringComparison.Ordinal));
        Assert.Equal(
            $"runner-artifact-after-settlement taskKey=AGT-1 attemptId={run.AttemptId} runner={RunnerId} " +
            $"state=Completed terminalAt={SettledAt.UtcDateTime:O}",
            logged);
        Assert.DoesNotContain(host.Logs.Messages, message => message.StartsWith("runner-artifact-stale-lease", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Artifact_write_for_a_leased_attempt_is_accepted_without_a_settlement_line()
    {
        await using var host = await ArtifactHost.StartAsync(_root, SettledAt);
        var run = host.AcquireRun("AGT-1");

        using var response = await host.Http.PostAsJsonAsync("/api/runner/artifacts", host.Upload(run, "status.md"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(host.TaskFolder("AGT-1"), "results", "status.md")));
        Assert.DoesNotContain(host.Logs.Messages, message => message.StartsWith("runner-artifact-after-settlement", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Artifact_write_without_the_current_lease_logs_the_stale_lease()
    {
        await using var host = await ArtifactHost.StartAsync(_root, SettledAt, principalRunnerId: "other-runner");
        var run = host.AcquireRun("AGT-1");

        using var response = await host.Http.PostAsJsonAsync("/api/runner/artifacts", host.Upload(run, "status.md"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var logged = Assert.Single(host.Logs.Messages, message => message.StartsWith("runner-artifact-stale-lease", StringComparison.Ordinal));
        Assert.Equal($"runner-artifact-stale-lease taskKey=AGT-1 runner={RunnerId} leaseId={run.Lease!.LeaseId}", logged);
    }

    [Fact]
    public async Task Three_settled_attempts_alarm_once_and_an_accepted_write_clears_it()
    {
        await using var host = await ArtifactHost.StartAsync(_root, SettledAt);
        foreach (var key in new[] { "AGT-1", "AGT-2", "AGT-3" })
        {
            var settled = host.AcquireRun(key);
            host.Settle(settled);
            foreach (var file in new[] { "status.md", "deliverables.md" })
            {
                using var refused = await host.Http.PostAsJsonAsync("/api/runner/artifacts", host.Upload(settled, file));
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            }
        }

        var raised = Assert.Single(host.Alarm.Raised);
        Assert.Equal(RunnerId, raised.RunnerId);
        Assert.Equal(["AGT-1", "AGT-2", "AGT-3"], raised.Attempts.Select(attempt => attempt.TaskKey));
        Assert.Equal("demo", raised.Project);

        var live = host.AcquireRun("AGT-4");
        using var accepted = await host.Http.PostAsJsonAsync("/api/runner/artifacts", host.Upload(live, "status.md"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal([RunnerId], host.Alarm.Recovered);
    }

    private sealed class ArtifactHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _watchPath;
        private readonly AttemptAuthorityService _authority;

        private ArtifactHost(WebApplication app, string watchPath, AttemptAuthorityService authority,
            CapturingLoggerProvider logs, RecordingSettlementAlarm alarm)
        {
            _app = app;
            _watchPath = watchPath;
            _authority = authority;
            Logs = logs;
            Alarm = alarm;
            Http = app.GetTestClient();
        }

        public HttpClient Http { get; }
        public CapturingLoggerProvider Logs { get; }
        public RecordingSettlementAlarm Alarm { get; }

        public static async Task<ArtifactHost> StartAsync(string root, DateTimeOffset now, string? principalRunnerId = null)
        {
            var watchPath = Path.Combine(root, "projects", "demo");
            foreach (var key in new[] { "AGT-1", "AGT-2", "AGT-3", "AGT-4" })
            {
                var folder = Path.Combine(watchPath, TaskStates.Progress, key);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "task.json"),
                    JsonSerializer.Serialize(new { id = key, title = key, state = TaskStates.Progress, order = 1, kind = TaskKinds.Task }));
                File.WriteAllText(Path.Combine(folder, "prompt.md"), "Prompt.");
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "demo",
                    ["WatchPaths:0:Path"] = watchPath,
                    ["WatchPaths:0:RootPath"] = watchPath,
                })
                .Build();
            var clock = new FakeTimeProvider(now);
            var authority = new AttemptAuthorityService(
                configuration,
                NullLogger<AttemptAuthorityService>.Instance,
                utcNow: () => clock.GetUtcNow().UtcDateTime);
            var summaries = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration);
            var scanner = new TaskScannerService(configuration, NullLogger<TaskScannerService>.Instance, summaries);
            var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
            var logs = new CapturingLoggerProvider();
            var alarm = new RecordingSettlementAlarm();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
            builder.Services.AddSingleton<ITaskScanner>(scanner);
            builder.Services.AddSingleton(authority);
            builder.Services.AddSingleton(new RunLeaseService(NullLogger<RunLeaseService>.Instance, authority));
            builder.Services.AddSingleton(timeline);
            builder.Services.AddSingleton(new WorkspaceArtifactCommitService(
                configuration, NullLogger<WorkspaceArtifactCommitService>.Instance));
            builder.Services.AddSingleton(new RemoteResultFinalizationService(
                summaries, scanner, timeline, configuration, NullLogger<RemoteResultFinalizationService>.Instance));
            builder.Services.AddSingleton(new AgentStudio.Projects.ProjectSettingsService(
                NullLogger<AgentStudio.Projects.ProjectSettingsService>.Instance, configuration));
            builder.Services.AddSingleton(new ArtifactRequestLimits(25L * 1024 * 1024));
            builder.Services.AddSingleton(new ArtifactUploadAfterSettlementSensor(clock, alarm));
            var app = builder.Build();
            if (principalRunnerId is not null)
            {
                app.Use(async (context, next) =>
                {
                    context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] = new RunnerPrincipal(
                        principalRunnerId, principalRunnerId, "credential", new HashSet<string>());
                    await next();
                });
            }
            app.MapArtifactIngestionEndpoints();
            await app.StartAsync();
            return new ArtifactHost(app, watchPath, authority, logs, alarm);
        }

        public string TaskFolder(string key) => Path.Combine(_watchPath, TaskStates.Progress, key);

        public RunAttemptDto AcquireRun(string key)
            => _authority.AcquireRun(key, "demo", null, RunnerId, RunnerId, 120, "acquire-" + key).RunAttempt!;

        public void Settle(RunAttemptDto run)
            => Assert.True(_authority.SettleRun(new SettleRunAttemptRequest
            {
                Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "settle-" + run.TaskKey),
                Outcome = "done",
                ResultSha = "26ea075026ea075026ea075026ea075026ea0750",
            }).Accepted);

        public ArtifactIngestRequest Upload(RunAttemptDto run, string file)
        {
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("agent report " + file));
            return new ArtifactIngestRequest(
                run.TaskKey,
                [new RunnerArtifactUpload(file, content)],
                RunnerId: RunnerId,
                LeaseId: run.Lease!.LeaseId,
                FencingToken: run.LastFence,
                AttemptId: run.AttemptId,
                Fence: run.LastFence,
                AuthorityEpoch: run.AuthorityEpoch,
                IdempotencyKey: $"artifact:{run.AttemptId}:{file}");
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class RecordingSettlementAlarm : IArtifactUploadAfterSettlementAlarm
    {
        public List<ArtifactUploadAfterSettlement> Raised { get; } = [];
        public List<string> Recovered { get; } = [];

        public void ArtifactUploadsRejectedAfterSettlement(ArtifactUploadAfterSettlement rejection)
            => Raised.Add(rejection);

        public void ArtifactUploadsRecovered(string runnerId, DateTime recoveredAtUtc) => Recovered.Add(runnerId);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return _messages.ToArray(); }
        }

        public ILogger CreateLogger(string categoryName) => new Capturing(_messages);

        public void Dispose()
        {
        }

        private sealed class Capturing(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                lock (messages) messages.Add(formatter(state, exception));
            }
        }
    }
}
