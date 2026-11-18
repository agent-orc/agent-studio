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
/// The Operations view reads the built-in OTLP store (M5). The fixtures are the
/// requests the container-service template exported, replayed through the intake.
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
        Assert.Equal(9, metrics.Requests);
        Assert.Equal(3d / 9d, metrics.ErrorRate!.Value, 6);
        Assert.Equal(new[] { ("/", 3L), ("/health", 3L), ("unmatched", 3L) },
            metrics.TopEndpoints.Select(item => (item.Path, item.Requests)).OrderBy(item => item.Path, StringComparer.Ordinal));
        Assert.True(metrics.CpuCores > 0);
        Assert.True(metrics.MemoryBytes > 1024 * 1024);
        var sample = Assert.Single(metrics.Samples);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 22, 0, 0, TimeSpan.Zero), sample.At);
        Assert.Equal(9, sample.Requests);
        Assert.Equal(metrics.MemoryBytes, sample.MemoryBytes);
        // The store keeps no histogram buckets, and the template has no session or disk metric.
        Assert.Null(metrics.ResponseP95Ms);
        Assert.Null(metrics.Sessions);
        Assert.Null(metrics.DiskBytes);
        Assert.Empty(metrics.DailyUsage);
    }

    [Fact]
    public async Task Log_search_filters_by_level_text_and_release()
    {
        var (reader, _) = await ReplayTemplate();
        var until = _clock.GetUtcNow();
        OperationsLogQuery Query(string? level = null, string? release = null, string? text = null)
            => new("shop", "stage", until.AddHours(-1), until, level, release, text, 100);

        var all = reader.SearchLogs(Query())!;
        Assert.Contains(all, line => line is { Level: "info", Message: "Service started", ReleaseId: "rel-template" });
        var warnings = reader.SearchLogs(Query(level: "warn"))!;
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, line => Assert.Equal(("warn", "Health check failed"), (line.Level, line.Message)));
        Assert.Single(reader.SearchLogs(Query(text: "started"))!);
        Assert.Equal(all.Count, reader.SearchLogs(Query(release: "rel-template"))!.Count);
        Assert.Empty(reader.SearchLogs(Query(release: "rel-other"))!);
        Assert.Empty(reader.SearchLogs(Query(level: "loud"))!);
    }

    [Fact]
    public async Task Environment_without_telemetry_stays_unavailable()
    {
        var (reader, _) = await ReplayTemplate();
        var until = _clock.GetUtcNow();
        Assert.Null(reader.ReadMetrics("shop", "production", until.AddDays(-30), until));
        Assert.Null(reader.SearchLogs(new("shop", "production", until.AddHours(-1), until, null, null, null, 100)));
        Assert.Null(reader.ReadMetrics("other", "stage", until.AddDays(-30), until));
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
        Assert.IsType<TelemetryOperationsReader>(factory.Services.GetRequiredService<IOperationsTelemetryReader>());
    }

    private async Task<(TelemetryOperationsReader Reader, TelemetryStore Store)> ReplayTemplate()
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
        return (new TelemetryOperationsReader(store, options, _clock), store);
    }

    private sealed class StageOnly : ITelemetryEnvironmentDirectory
    {
        public IReadOnlyCollection<string>? Environments(string project) => project == "shop" ? ["stage", "production"] : null;
    }
}
