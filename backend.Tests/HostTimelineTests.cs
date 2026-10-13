using System.Text.Json;
using AgentStudio.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3107: the opt-in host timeline. The recorder writes one JSON line per
/// host only when a directory is configured, and the hosted-service decorator
/// is transparent: same start and stop order, same exceptions, same
/// <see cref="BackgroundServiceExceptionBehavior"/>. Time comes from a fake
/// clock; waits are bounded and driven by host events, never by sleeps.
/// </summary>
public sealed class HostTimelineTests : IDisposable
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "host-timeline-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Without_the_setting_the_recorder_is_disabled_and_writes_nothing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [HostTimelineRecorder.DirectoryKey] = " " })
            .Build();

        var recorder = HostTimelineRecorder.Create(
            _clock.GetTimestamp(), configuration, _clock, "local", "Test", readEnvironment: _ => null);
        var swept = false;
        recorder.Checkpoint("entryToBuild");
        recorder.Sweep("Sweep", () => swept = true);
        new HostTimelineDisposalProbe(recorder).Dispose();

        Assert.False(recorder.IsEnabled);
        Assert.Same(HostTimelineRecorder.Disabled, recorder);
        Assert.True(swept);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void The_environment_variable_or_the_configuration_key_enables_the_recorder()
    {
        var empty = new ConfigurationBuilder().Build();
        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [HostTimelineRecorder.DirectoryKey] = _directory })
            .Build();

        Assert.True(HostTimelineRecorder.Create(
            0, empty, _clock, "local", "Test",
            readEnvironment: name => name == HostTimelineRecorder.EnvironmentVariable ? _directory : null).IsEnabled);
        Assert.True(HostTimelineRecorder.Create(0, configured, _clock, "local", "Test", readEnvironment: _ => null).IsEnabled);
        Assert.False(HostTimelineRecorder.Create(0, empty, _clock, "local", "Test", readEnvironment: _ => null).IsEnabled);
    }

    [Fact]
    public void Phases_and_sweeps_are_measured_with_the_injected_clock()
    {
        var recorder = Enabled();
        _clock.Advance(TimeSpan.FromMilliseconds(1200));
        recorder.Checkpoint("entryToBuild");
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        recorder.Checkpoint("build");
        var count = recorder.Sweep("Backfill", () =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(40));
            return 7;
        });
        Assert.Throws<InvalidOperationException>(() => recorder.Sweep(
            "Failing",
            () =>
            {
                _clock.Advance(TimeSpan.FromMilliseconds(5));
                throw new InvalidOperationException("sweep failed");
            }));
        recorder.OnApplicationStarted();
        new HostTimelineDisposalProbe(recorder).Dispose();
        new HostTimelineDisposalProbe(recorder).Dispose();

        var line = Assert.Single(ReadLines());
        Assert.Equal(7, count);
        Assert.Equal(
            [("entryToBuild", 1200.0), ("build", 300.0), ("start", 45.0)],
            Entries(line, "phases"));
        Assert.Equal([("Backfill", 40.0), ("Failing", 5.0)], Entries(line, "sweeps"));
        Assert.Equal(1545.0, line.GetProperty("applicationStartedMs").GetDouble());
        Assert.Equal("local", line.GetProperty("profile").GetString());
        Assert.Equal(3, line.GetProperty("countersAtStart").GetProperty("activeTimers").GetInt64());
        Assert.Equal(3, line.GetProperty("countersAtStop").GetProperty("activeTimers").GetInt64());
    }

    [Fact]
    public async Task A_timed_host_keeps_start_and_stop_order_and_writes_one_line_on_dispose()
    {
        var order = new List<string>();
        var recorder = Enabled();
        var host = BuildHost(recorder, services =>
        {
            // AddHostedService would collapse same-type registrations (TryAddEnumerable).
            services.AddSingleton<IHostedService>(_ => new OrderedService("A", order, _clock));
            services.AddSingleton<IHostedService>(_ => new OrderedService("B", order, _clock));
            services.AddSingleton<OrderedService>(_ => new OrderedService("C", order, _clock));
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<OrderedService>());
        });

        await host.StartAsync();
        await host.StopAsync();
        Assert.Empty(ReadLines());
        Assert.All(
            host.Services.GetServices<IHostedService>(),
            service => Assert.IsType<HostTimelineHostedService>(service));
        Assert.Same(
            host.Services.GetRequiredService<OrderedService>(),
            HostTimelineHostedService.Unwrap(host.Services.GetServices<IHostedService>().Last()));
        host.Dispose();

        Assert.Equal(
            ["start A", "start B", "start C", "stop C", "stop B", "stop A"],
            order);
        var line = Assert.Single(ReadLines());
        Assert.Equal(
            [(typeof(OrderedService).FullName!, 10.0), (typeof(OrderedService).FullName!, 10.0), (typeof(OrderedService).FullName!, 10.0)],
            Entries(line, "starts"));
        Assert.Equal(3, Entries(line, "stops").Count);
        Assert.Equal(30.0, line.GetProperty("stopTotalMs").GetDouble());
        Assert.True(line.TryGetProperty("disposeMs", out _));
        Assert.Contains(Entries(line, "phases"), phase => phase.Name == "start");
    }

    [Fact]
    public async Task The_decorator_passes_start_and_stop_exceptions_through_unchanged()
    {
        var startFailure = new InvalidOperationException("start failed");
        var stopFailure = new InvalidOperationException("stop failed");

        using var startHost = BuildHost(Enabled(), services =>
            services.AddHostedService(_ => new ThrowingService(startFailure, null)));
        var thrownOnStart = await Assert.ThrowsAsync<InvalidOperationException>(() => startHost.StartAsync());
        Assert.Same(startFailure, thrownOnStart);

        using var stopHost = BuildHost(Enabled(), services =>
            services.AddHostedService(_ => new ThrowingService(null, stopFailure)));
        await stopHost.StartAsync();
        var thrownOnStop = await Assert.ThrowsAnyAsync<Exception>(() => stopHost.StopAsync());
        Assert.Contains(stopFailure, Flatten(thrownOnStop));
    }

    [Theory]
    [InlineData(BackgroundServiceExceptionBehavior.Ignore, false)]
    [InlineData(BackgroundServiceExceptionBehavior.StopHost, true)]
    public async Task A_faulted_background_service_follows_the_configured_exception_behavior(
        BackgroundServiceExceptionBehavior behavior,
        bool hostStops)
    {
        var faultLogged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(Enabled(), services =>
        {
            services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = behavior);
            services.AddLogging(logging => logging.AddProvider(new FaultLogProvider(faultLogged)));
            services.AddHostedService<FaultingBackgroundService>();
        });
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());

        await host.StartAsync();
        await faultLogged.Task.WaitAsync(BoundedWait);
        if (hostStops) await stopping.Task.WaitAsync(BoundedWait);

        Assert.Equal(hostStops, lifetime.ApplicationStopping.IsCancellationRequested);
        var timed = Assert.IsType<HostTimelineHostedService>(Assert.Single(host.Services.GetServices<IHostedService>()));
        Assert.True(timed.ExecuteTask!.IsFaulted);
        await host.StopAsync();
    }

    private HostTimelineRecorder Enabled()
        => HostTimelineRecorder.Create(
            _clock.GetTimestamp(),
            new ConfigurationBuilder().Build(),
            _clock,
            "local",
            "Test",
            readEnvironment: name => name == HostTimelineRecorder.EnvironmentVariable ? _directory : null,
            captureCounters: live => new HostProcessCounters(3, 4, 0, 1024, 10, live));

    private IHost BuildHost(HostTimelineRecorder recorder, Action<IServiceCollection> configure)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            EnvironmentName = "Test",
        });
        configure(builder.Services);
        builder.ConfigureContainer(new HostTimelineServiceProviderFactory(recorder, new ServiceProviderOptions()));
        var host = builder.Build();
        recorder.Attach(host.Services);
        return host;
    }

    private List<JsonElement> ReadLines()
        => !Directory.Exists(_directory)
            ? []
            : Directory.GetFiles(_directory, "host-timeline-*.jsonl")
                .SelectMany(File.ReadAllLines)
                .Where(line => line.Length > 0)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToList();

    private static List<(string Name, double Ms)> Entries(JsonElement line, string property)
        => line.GetProperty(property).EnumerateArray()
            .Select(entry => (entry.GetProperty("name").GetString()!, entry.GetProperty("ms").GetDouble()))
            .ToList();

    private static IEnumerable<Exception> Flatten(Exception exception)
        => exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.Prepend(exception) : [exception];

    private sealed class OrderedService(string name, List<string> order, FakeTimeProvider clock) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            order.Add("start " + name);
            clock.Advance(TimeSpan.FromMilliseconds(10));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            order.Add("stop " + name);
            clock.Advance(TimeSpan.FromMilliseconds(10));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingService(Exception? onStart, Exception? onStop) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
            => onStart is null ? Task.CompletedTask : throw onStart;

        public Task StopAsync(CancellationToken cancellationToken)
            => onStop is null ? Task.CompletedTask : throw onStop;
    }

    private sealed class FaultingBackgroundService : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("loop failed");
        }
    }

    /// <summary>Completes when the host logs that a background service faulted.</summary>
    private sealed class FaultLogProvider(TaskCompletionSource faultLogged) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new FaultLogger(faultLogged);

        public void Dispose() => GC.SuppressFinalize(this);

        private sealed class FaultLogger(TaskCompletionSource faultLogged) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (exception is InvalidOperationException { Message: "loop failed" }) faultLogged.TrySetResult();
            }
        }
    }
}
