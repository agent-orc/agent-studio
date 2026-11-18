using AgentStudio.Projects;
using AgentStudio.Security;
using AgentStudio.Telemetry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The Operations view reads the built-in OTLP store (M5) through
/// <see cref="StoreOperationsTelemetryReader"/>. The fixtures are the requests the
/// container-service template exported, replayed through the intake.
/// </summary>
public sealed class TelemetryOperationsReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "telemetry-operations-" + Guid.NewGuid().ToString("N"));
    // Shortly after the fixtures were captured, so every point is inside the admission window.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 17, 22, 25, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Template_telemetry_reaches_operations_trends_endpoints_and_log_search()
    {
        var (reader, _) = await ReplayTemplate();
        var until = _clock.GetUtcNow();

        var metrics = reader.ReadMetrics("shop", "stage", until.AddDays(-30), until)!;

        // The template answered /health (503: its orders dependency was down), / and /missing three times each.
        // The unmatched route of /missing is not a path, so top endpoints leave it out.
        Assert.Equal(9, metrics.Requests);
        Assert.Equal(3d / 9d, metrics.ErrorRate!.Value, 6);
        Assert.Equal(new[] { ("/", 3L), ("/health", 3L) },
            metrics.TopEndpoints.Select(item => (item.Path, item.Requests)).OrderBy(item => item.Path, StringComparer.Ordinal));
        // The capture ran a few minutes into the 22:00 hour; a full-hour divisor read near zero.
        Assert.InRange(metrics.CpuCores!.Value, 0.0001, 4);
        Assert.True(metrics.MemoryBytes > 1024 * 1024);
        var sample = Assert.Single(metrics.Samples);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 22, 0, 0, TimeSpan.Zero), sample.At);
        Assert.Equal(9, sample.Requests);
        Assert.Equal(metrics.MemoryBytes, sample.MemoryBytes);
        // The template has no session or disk metric.
        Assert.Null(metrics.Sessions);
        Assert.Null(metrics.DiskBytes);
        Assert.Empty(metrics.DailyUsage);
    }

    [Fact]
    public void Cpu_turns_the_cumulative_counter_into_cores_per_covered_hour()
    {
        // Review of AGT-3921: process.cpu.time is cumulative. Summing its exported totals
        // or dividing the running hour by a full hour would misreport the cores used.
        var options = new TelemetryOptions { StoreRoot = Path.Combine(_root, "cpu") };
        var store = new TelemetryStore(options, NullLogger<TelemetryStore>.Instance);
        var key = new TelemetryEnvironmentKey("shop", "stage");
        var hour = new DateTimeOffset(2026, 11, 17, 21, 0, 0, TimeSpan.Zero);
        var start = Nanos(hour.AddMinutes(-1));
        var restart = Nanos(hour.AddHours(1).AddMinutes(5));
        MetricSample Counter(string state, DateTimeOffset at, long startNanos, double total)
            => new("process.cpu.time", "s", MetricKind.Sum, MetricTemporality.Cumulative,
                TelemetryAttributes.From([new("process.cpu.state", state)]), Nanos(at), startNanos, total, 1, null, null, true);
        MetricSample Gauge(DateTimeOffset at, double total)
            => new("process.cpu.time", "s", MetricKind.Gauge, MetricTemporality.Unspecified,
                TelemetryAttributes.From([new("collector", "legacy")]), Nanos(at), 0, total, 1, null, null);
        var samples = new List<MetricSample>();
        // Every five minutes for the full 21:00 hour: user 0.5 core, system 0.25 core.
        for (var minute = 0; minute <= 60; minute += 5)
        {
            var at = hour.AddMinutes(minute);
            samples.Add(Counter("user", at, start, 30 + 0.5 * minute * 60));
            samples.Add(Counter("system", at, start, 15 + 0.25 * minute * 60));
            samples.Add(Gauge(at, 1_000_000 + minute * 60));
        }
        // The process restarted at 22:05 and has used one core since; the counter starts over.
        foreach (var minute in new[] { 6, 10, 15, 20 })
            samples.Add(Counter("user", hour.AddHours(1).AddMinutes(minute), restart, (minute - 5) * 60));
        store.Write(key, samples, [], hour);
        var now = hour.AddHours(1).AddMinutes(20);

        var metrics = new StoreOperationsTelemetryReader(store, options).ReadMetrics("shop", "stage", now.AddDays(-1), now)!;

        // 21:00 holds 45 s from before the first export and 11 deltas of 225 s; the 22:00 export
        // (225 s) and 900 s since the restart land in the running 22:00 hour, 20 minutes so far.
        Assert.Equal([(hour, 2520 / 3600d), (hour.AddHours(1), (225 + 900) / 1200d)],
            metrics.Samples.Select(sample => (sample.At, Math.Round(sample.CpuCores!.Value, 6))));
        Assert.Equal((225 + 900) / 1200d, metrics.CpuCores!.Value, 6);
    }

    private static long Nanos(DateTimeOffset at) => at.ToUnixTimeMilliseconds() * 1_000_000;

    [Fact]
    public async Task Log_search_filters_by_level_text_and_release()
    {
        var (reader, _) = await ReplayTemplate();
        var until = _clock.GetUtcNow();
        OperationsLogQuery Query(string? level = null, string? release = null, string? text = null)
            => new("shop", "stage", until.AddHours(-1), until, level, release, text, 100, null);

        // The deploy sets the release as agent_studio.release.id on the workload's resource.
        var all = reader.SearchLogs(Query())!.Lines;
        Assert.Contains(all, line => line is { Level: "INFO", Message: "Service started", ReleaseId: "rel-template" });
        var warnings = reader.SearchLogs(Query(level: "warn"))!.Lines;
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, line => Assert.Equal(("WARN", "Health check failed"), (line.Level, line.Message)));
        Assert.Single(reader.SearchLogs(Query(text: "started"))!.Lines);
        Assert.Equal(all.Count, reader.SearchLogs(Query(release: "rel-template"))!.Lines.Count);
        Assert.Empty(reader.SearchLogs(Query(release: "rel-other"))!.Lines);
    }

    [Fact]
    public async Task Environment_without_telemetry_stays_unavailable()
    {
        var (reader, _) = await ReplayTemplate();
        var until = _clock.GetUtcNow();
        Assert.Null(reader.ReadMetrics("shop", "production", until.AddDays(-30), until)?.Requests);
        Assert.Empty(reader.SearchLogs(new("shop", "production", until.AddHours(-1), until, null, null, null, 100, null))!.Lines);
        Assert.Null(reader.ReadMetrics("other", "stage", until.AddDays(-30), until)?.Requests);
    }

    [Fact]
    public void Studio_registers_the_store_backed_reader()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Test");
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["TaskRepository"] = Path.Combine(_root, "studio") }));
        });
        Assert.IsType<StoreOperationsTelemetryReader>(factory.Services.GetRequiredService<IOperationsTelemetryReader>());
    }

    private async Task<(StoreOperationsTelemetryReader Reader, TelemetryStore Store)> ReplayTemplate()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build();
        var options = new TelemetryOptions { StoreRoot = Path.Combine(_root, "telemetry") };
        var security = new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, _clock);
        var credentials = new AccessStoreTelemetryIngestCredentials(security);
        var (_, secret) = credentials.Create("shop", "stage", "template", null, null, "test");
        var store = new TelemetryStore(options, NullLogger<TelemetryStore>.Instance);
        var queue = new TelemetryIntakeQueue(options);
        var intake = new TelemetryIntakeService(credentials, new StageOnly(), queue, options, _clock,
            NullLogger<TelemetryIntakeService>.Instance);
        var writer = new TelemetryWriter(queue, store, [], new CredentialTelemetryValueMasker(), options, _clock,
            NullLogger<TelemetryWriter>.Instance);
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "otlp", "template");
        foreach (var file in Directory.GetFiles(directory, "*.pb").Order(StringComparer.Ordinal))
        {
            var signal = file.EndsWith("-logs.pb", StringComparison.Ordinal) ? TelemetrySignals.Logs : TelemetrySignals.Metrics;
            await using var body = File.OpenRead(file);
            var outcome = await intake.ReceiveAsync(signal, "application/x-protobuf", null, "Bearer " + secret,
                body.Length, body, CancellationToken.None);
            Assert.Equal(200, outcome.StatusCode);
            await writer.DrainOnceAsync(CancellationToken.None);
        }
        return (new StoreOperationsTelemetryReader(store, options), store);
    }

    private sealed class StageOnly : ITelemetryEnvironmentDirectory
    {
        public IReadOnlyCollection<string>? Environments(string project) => project == "shop" ? ["stage", "production"] : null;
    }
}
