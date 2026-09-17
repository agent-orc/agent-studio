using System.Net;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the contract that defines the backend's "what just crashed?"
/// surface. Two layers are exercised:
///
/// <list type="number">
///   <item>The recorder + sink in isolation: a flattened
///   <see cref="AggregateException"/> produces both a structured log
///   line and a populated <c>last-crash.json</c> marker.</item>
///   <item>The full hosting path: a hosted service that fires off a
///   throwing fire-and-forget task surfaces through the wired
///   <c>TaskScheduler.UnobservedTaskException</c> handler in
///   Program.cs, and the resulting marker is served by
///   <c>/api/diagnostics/last-crash</c>.</item>
/// </list>
///
/// Together these are the proof asked for by the
/// backend-observability-real-logs task: an agent woken to "the dev
/// backend crashed" has a single, machine-readable place to look.
/// </summary>
public class BackendCrashSurfaceTests
{
    [Fact]
    public void Recorder_FlattensAggregate_AndWritesMarkerPlusStructuredLogLine()
    {
        using var temp = new TempDir();
        var options = new BackendFileLoggerOptions { LogDirectory = temp.Path, RetentionDays = 14 };
        using var sink = new BackendFileLogSink(options);
        var recorder = new CrashRecorder(options, sink);

        var inner = new InvalidOperationException("simulated hosted-service crash sk-ant-AAAAAAAAAAAAAAAAAAAA");
        var thrown = TryThrow(() => throw new AggregateException("outer", inner));

        var record = recorder.Record("HostedServiceTest", thrown!, isTerminating: false);

        Assert.Equal("System.InvalidOperationException", record.ExceptionType);
        Assert.Contains("simulated hosted-service crash", record.Message);
        Assert.Contains("[REDACTED]", record.Message); // anthropic key was scrubbed
        Assert.False(string.IsNullOrEmpty(record.TopFrame));

        var markerPath = Path.Combine(temp.Path, "last-crash.json");
        Assert.True(File.Exists(markerPath), "last-crash.json must be written");

        var json = JsonDocument.Parse(File.ReadAllText(markerPath));
        Assert.Equal("HostedServiceTest", json.RootElement.GetProperty("source").GetString());
        Assert.Equal("System.InvalidOperationException", json.RootElement.GetProperty("exceptionType").GetString());
        Assert.Contains("simulated hosted-service crash", json.RootElement.GetProperty("message").GetString());
        Assert.False(json.RootElement.GetProperty("isTerminating").GetBoolean());

        var logFile = Path.Combine(temp.Path, $"{DateTime.UtcNow:yyyy-MM-dd}.log");
        Assert.True(File.Exists(logFile), "daily log file must be written");
        var logContent = File.ReadAllText(logFile);
        Assert.Contains("Backend.Crash", logContent);
        Assert.Contains("[HostedServiceTest]", logContent);
        Assert.Contains("InvalidOperationException", logContent);
        Assert.DoesNotContain("sk-ant-AAAA", logContent); // redaction applied to log line too
    }

    [Fact]
    public void Sink_RotatesByDay_AndPrunesPastRetention()
    {
        using var temp = new TempDir();
        // Drop a 30-day-old log file; the sink must reap it on construction.
        var oldDay = DateTime.UtcNow.AddDays(-30);
        var oldFile = Path.Combine(temp.Path, $"{oldDay:yyyy-MM-dd}.log");
        Directory.CreateDirectory(temp.Path);
        File.WriteAllText(oldFile, "stale");

        using var sink = new BackendFileLogSink(new BackendFileLoggerOptions
        {
            LogDirectory = temp.Path,
            RetentionDays = 14,
            MinimumLevel = LogLevel.Information,
        });
        sink.Write(LogLevel.Information, "Demo", "hello world", null);

        Assert.False(File.Exists(oldFile), "files older than retention must be pruned");
        Assert.True(File.Exists(sink.CurrentLogPath));
        Assert.Contains("hello world", File.ReadAllText(sink.CurrentLogPath));
    }

    [Fact]
    public void Sink_redacts_product_sessions_runner_credentials_and_password_fields()
    {
        using var temp = new TempDir();
        using var sink = new BackendFileLogSink(new BackendFileLoggerOptions
        {
            LogDirectory = temp.Path,
            MinimumLevel = LogLevel.Information,
        });
        const string runnerSecret = "rnr.credential.abcdefghijklmnopqrstuvwxyz012345";
        const string sessionSecret = "ssn.session.abcdefghijklmnopqrstuvwxyz012345";

        sink.Write(
            LogLevel.Information,
            "Security.Test",
            $"runner={runnerSecret} session={sessionSecret} password=do-not-log-this",
            null);

        var persisted = File.ReadAllText(sink.CurrentLogPath);
        Assert.DoesNotContain(runnerSecret, persisted);
        Assert.DoesNotContain(sessionSecret, persisted);
        Assert.DoesNotContain("do-not-log-this", persisted);
        Assert.Contains("REDACTED", persisted);
    }

    // MachineBound: this test deliberately drives a fire-and-forget faulted Task
    // to the GC finalizer (GC.WaitForPendingFinalizers below) to verify crash
    // surfacing end-to-end through the wired handler. Under
    // DOTNET_ThrowUnobservedTaskExceptions=1 - which the card gate sets to
    // surface fire-and-forget bugs - that finalizer rethrow is a fatal,
    // uncatchable host death that aborts the ENTIRE run, not just this test. So
    // it is excluded from the gate (filter Category!=MachineBound) and additionally
    // skips itself under that flag. The product guarantee - that an unobserved
    // task exception never kills the backend - lives in ProcessGlobalTaskSafety
    // and is asserted non-fatally elsewhere; this test only adds the
    // deliberately-fatal end-to-end path and must run in isolation.
    [Trait("Category", "MachineBound")]
    [Xunit.SkippableFact]
    public async Task UnobservedTaskException_FromHostedService_SurfacesViaDiagnosticsEndpoint()
    {
        Skip.If(ThrowUnobservedTaskExceptionsEnabled(),
            "Deliberately drives an unobserved task exception to the finalizer, which is a fatal "
            + "host crash under ThrowUnobservedTaskExceptions. Runs only when that flag is off.");
        using var temp = new TempDir();
        // The marker file is keyed on the resolved log directory at
        // process startup, so we point the WebApplicationFactory at the
        // temp dir before it boots.
        Environment.SetEnvironmentVariable("Logging__BackendFile__LogDirectory", temp.Path);
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.UseEnvironment("Test");
                    b.ConfigureAppConfiguration((_, cfg) =>
                    {
                        cfg.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Logging:BackendFile:LogDirectory"] = temp.Path,
                            ["Logging:BackendFile:RetentionDays"] = "14",
                        });
                    });
                    b.ConfigureServices(s =>
                    {
                        s.AddHostedService<ThrowingHostedService>();
                    });
                });

            using var client = factory.CreateClient();

            // Give the hosted service time to fire the throwing task and
            // for the GC to surface the UnobservedTaskException.
            CrashRecord? crash = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var resp = await client.GetAsync("/api/diagnostics/last-crash");
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    crash = JsonSerializer.Deserialize<CrashRecord>(body, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
                    if (crash != null && crash.Source == "UnobservedTaskException") break;
                }
                await Task.Delay(150);
            }

            Assert.NotNull(crash);
            Assert.Equal("UnobservedTaskException", crash!.Source);
            Assert.Contains("synthetic-hosted-service-crash", crash.Message);

            // The full stack must also have landed in the daily log file.
            var logFile = Path.Combine(temp.Path, $"{DateTime.UtcNow:yyyy-MM-dd}.log");
            Assert.True(File.Exists(logFile));
            Assert.Contains("UnobservedTaskException", File.ReadAllText(logFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Logging__BackendFile__LogDirectory", null);
        }
    }

    /// <summary>
    /// Pins the host-survival contract: a HostedService whose
    /// <c>ExecuteAsync</c> faults must not stop the host. .NET's default
    /// <see cref="BackgroundServiceExceptionBehavior"/> is
    /// <see cref="BackgroundServiceExceptionBehavior.StopHost"/>; the dev
    /// backend silently went unreachable on 2026-05-04 because a tick
    /// inside a <see cref="BackgroundService.ExecuteAsync"/> threw and took
    /// the whole API down with it. <c>Program.cs</c> now configures
    /// <see cref="BackgroundServiceExceptionBehavior.Ignore"/>; this test
    /// asserts that configuration by running a generic host with the same
    /// option set and checking <see cref="IHostApplicationLifetime.ApplicationStopping"/>
    /// stays unsignaled even after a hosted service faults.
    /// </summary>
    [Fact]
    public async Task ConfiguredBehaviour_KeepsHostRunning_WhenHostedServiceFaults()
    {
        using var host = new HostBuilder()
            .ConfigureServices(s =>
            {
                // Same config Program.cs applies. Removing this line reverts
                // to the StopHost default and the assertion below fails - so
                // this test is the lock that keeps the configuration in place.
                s.Configure<HostOptions>(o =>
                    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
                s.AddHostedService<ExecuteAsyncThrowingHostedService>();
            })
            .Build();

        await host.StartAsync();

        // Give the faulting service time to throw and the host's BackgroundService
        // exception observer time to act on it.
        await Task.Delay(500);

        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.False(
            lifetime.ApplicationStopping.IsCancellationRequested,
            "Host stopped because of a faulted BackgroundService - " +
            "BackgroundServiceExceptionBehavior must be Ignore.");

        await host.StopAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Regression for the promotion-gate test-host crash (trains 40/41/42):
    /// the crash-recovery / crash-surface tests build a rolling logger under a
    /// per-run temp dir and delete that dir at teardown. A leaked writer - the
    /// process-global UnobservedTaskException handler still pinned to the boot's
    /// recorder - then fires on the finalizer thread against a path that no
    /// longer exists ("[BackendFileLogger] WriteRaw failed: Could not find a
    /// part of the path ...atp-crash-&lt;guid&gt;/&lt;day&gt;.log"). A logger is
    /// not allowed to crash the process it serves, so neither the sink nor the
    /// recorder may throw once their directory is gone.
    /// </summary>
    [Fact]
    public void Sink_And_Recorder_SurviveTheirLogDirectoryVanishingMidRun()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atp-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var options = new BackendFileLoggerOptions { LogDirectory = dir, RetentionDays = 14 };
        using var sink = new BackendFileLogSink(options);
        var recorder = new CrashRecorder(options, sink);

        // Warm the sink so a same-day rollover short-circuit is in effect, then
        // pull the directory out from under it exactly like a teardown cleanup.
        sink.WriteRaw("before the directory is deleted");
        Directory.Delete(dir, recursive: true);

        var threw = false;
        try
        {
            sink.WriteRaw("written after the log directory was deleted");
            recorder.Record(
                "UnobservedTaskException",
                new InvalidOperationException("post-teardown finalizer-thread throw"),
                isTerminating: false);
        }
        catch
        {
            threw = true;
        }

        Assert.False(threw, "logging/crash-recording after the log dir vanished must never throw");
    }

    /// <summary>
    /// The crash recorder is invoked from <c>TaskScheduler.UnobservedTaskException</c>,
    /// which is raised on the finalizer thread where an escaping exception is
    /// fatal. Even a hostile exception whose own <c>Message</c>/<c>StackTrace</c>
    /// accessors throw must not turn the reporter into the thing that kills the
    /// host - <see cref="CrashRecorder.Record"/> has to return, not throw.
    /// </summary>
    [Fact]
    public void Record_NeverThrows_EvenWhenTheExceptionAccessorsThrow()
    {
        using var temp = new TempDir();
        var options = new BackendFileLoggerOptions { LogDirectory = temp.Path, RetentionDays = 14 };
        using var sink = new BackendFileLogSink(options);
        var recorder = new CrashRecorder(options, sink);

        var hostile = new AggregateException("outer", new HostileException());

        CrashRecord? record = null;
        var threw = false;
        try { record = recorder.Record("UnobservedTaskException", hostile, isTerminating: false); }
        catch { threw = true; }

        Assert.False(threw, "Record must swallow a misbehaving exception, not rethrow it onto the finalizer thread");
        Assert.NotNull(record);
        Assert.Equal("UnobservedTaskException", record!.Source);
    }

    /// <summary>An exception whose accessors blow up - the edge the reporter must survive.</summary>
    private sealed class HostileException : Exception
    {
        public override string Message => throw new InvalidOperationException("Message accessor blew up");
        public override string? StackTrace => throw new InvalidOperationException("StackTrace accessor blew up");
        public override string ToString() => throw new InvalidOperationException("ToString blew up");
    }

    /// <summary>
    /// Regression for the train-44 host abort: an unobserved task exception
    /// (the synthetic-hosted-service-crash the crash-surface path deliberately
    /// produces) was rethrown by the finalizer thread - fatal when the runtime
    /// throws unobserved task exceptions (dev/CI set
    /// DOTNET_ThrowUnobservedTaskExceptions=1) - because the per-run handler that
    /// calls SetObserved is detached when a test host stops, leaving a window
    /// with no subscriber. The permanent <see cref="ProcessGlobalTaskSafety"/>
    /// net must mark every such exception observed regardless, so the finalizer
    /// never rethrows and the process survives.
    ///
    /// MachineBound + skips under DOTNET_ThrowUnobservedTaskExceptions=1: like the
    /// end-to-end test above it forces a fire-and-forget faulted Task to the
    /// finalizer, so if the safety net ever regressed this would abort the whole
    /// run under that flag. It is excluded from the card gate and runs with the
    /// flag off (the mode that still proves the net calls SetObserved).
    /// </summary>
    [Trait("Category", "MachineBound")]
    [Xunit.SkippableFact]
    public void UnobservedTaskException_IsMarkedObserved_ByTheProcessGlobalSafetyNet()
    {
        Skip.If(ThrowUnobservedTaskExceptionsEnabled(),
            "Forces an unobserved task exception to the finalizer; only the non-fatal (flag-off) "
            + "mode is safe to run in-process.");
        ProcessGlobalTaskSafety.EnsureUnobservedTaskExceptionsAreObserved();

        // The safety net is registered before this probe, so by the time the
        // probe runs on the finalizer thread the exception must already be
        // observed. A TaskCompletionSource captures that across threads.
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<UnobservedTaskExceptionEventArgs> probe = (_, e) =>
        {
            if (e.Observed) observed.TrySetResult(true);
        };
        TaskScheduler.UnobservedTaskException += probe;
        try
        {
            FaultAndAbandonATask();
            for (var attempt = 0; attempt < 60 && !observed.Task.IsCompleted; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (observed.Task.IsCompleted) break;
                Thread.Sleep(50);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= probe;
        }

        Assert.True(
            observed.Task.IsCompletedSuccessfully && observed.Task.Result,
            "the process-global safety net must mark an unobserved task exception observed so the "
            + "finalizer never rethrows it (a fatal, uncatchable host death under "
            + "ThrowUnobservedTaskExceptions). Reaching this assertion proves the finalizer pass "
            + "did not abort the host.");
    }

    // Kept out-of-line so the faulted Task has no live stack reference and the GC
    // can finalize it, raising TaskScheduler.UnobservedTaskException.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FaultAndAbandonATask()
    {
        _ = Task.Run(() => throw new InvalidOperationException("synthetic-unobserved-safety-net-probe"));
    }

    /// <summary>
    /// True when the runtime rethrows unobserved task exceptions on the finalizer
    /// thread (env <c>DOTNET_ThrowUnobservedTaskExceptions=1</c> or the equivalent
    /// runtimeconfig switch). The two tests that deliberately drive a fire-and-forget
    /// task to the finalizer skip themselves in that mode so they can never abort
    /// the shared test host.
    /// </summary>
    private static bool ThrowUnobservedTaskExceptionsEnabled()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_ThrowUnobservedTaskExceptions");
        if (env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase)) return true;
        return AppContext.TryGetSwitch("System.Threading.Tasks.ThrowUnobservedTaskExceptions", out var on) && on;
    }

    private static Exception? TryThrow(Action action)
    {
        try { action(); }
        catch (Exception ex) { return ex; }
        return null;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "atp-crash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose() => TestTempRoot.TryDelete(Path);
    }

    /// <summary>
    /// Mimics a real bug-class hosted service that fires off a Task it
    /// never awaits. The thrown exception becomes
    /// <see cref="TaskScheduler.UnobservedTaskException"/> when the GC
    /// finalises the orphaned task - which is exactly the silent-crash
    /// path this task is here to make visible.
    /// </summary>
    private sealed class ThrowingHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(() => throw new InvalidOperationException("synthetic-hosted-service-crash"));
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Mimics the May 4 dev-backend crash class: a
    /// <see cref="BackgroundService"/> whose <c>ExecuteAsync</c> escapes a
    /// throw. With the default <see cref="BackgroundServiceExceptionBehavior.StopHost"/>
    /// the whole host shuts down. Program.cs flips this to Ignore so the
    /// rest of the API stays reachable.
    /// </summary>
    private sealed class ExecuteAsyncThrowingHostedService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            throw new InvalidOperationException("synthetic-execute-async-crash");
    }
}
