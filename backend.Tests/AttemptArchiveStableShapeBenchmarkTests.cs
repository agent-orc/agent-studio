using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3109 measurement on Stable-shaped attempt archives (13.10.2026: 62
/// files, 226 MiB, 2,975 run and 13,937 review attempts for 463 task keys; a
/// key sits in 1 file at the median, 13 at p95 and 22 at most). Times the card
/// detail reads over HTTP for a card without archived attempts and for the
/// card with the widest history. Uses only the HTTP surface, so the same file
/// measures an older commit for the before numbers. Set
/// <c>ARCHIVE_BENCH_LABEL</c> to name the report written to
/// <c>JOB_RESULTS_DIR</c>.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class AttemptArchiveStableShapeBenchmarkTests : IDisposable
{
    private const int FileCount = 62;
    private const int KeyCount = 463;
    private const int RunCount = 2_975;
    private const int ReviewCount = 13_937;
    private const string FreshKey = "AGT-3085";
    private const string WideKey = "AGT-1462";
    private static readonly long[] LargeFiles = [32_200_000, 30_500_000];
    private const long TotalBytes = 226L * 1024 * 1024;

    private readonly ITestOutputHelper _output;
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "attempt-archive-bench-" + Guid.NewGuid().ToString("N"));
    private readonly string _projectPath;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 6, 0, 0, TimeSpan.Zero));

    public AttemptArchiveStableShapeBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
        _projectPath = Path.Combine(_workspace, "agent-studio");
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Card_detail_reads_on_stable_shaped_archives()
    {
        var generation = Stopwatch.StartNew();
        var shape = WriteArchives();
        generation.Stop();
        SeedTask("fresh-card", FreshKey);
        SeedTask("wide-card", WideKey);
        _output.WriteLine(
            $"archives: {shape.Files} files, {shape.Bytes / 1024.0 / 1024.0:F1} MiB, {shape.Runs} runs, "
            + $"{shape.Reviews} reviews, {shape.Keys} keys; files per key p50={shape.P50} p95={shape.P95} "
            + $"max={shape.Max}; generated in {generation.Elapsed.TotalSeconds:F1} s");

        var startup = Stopwatch.StartNew();
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var project = factory.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>()
            .FindByStorageLocation(_projectPath)!;
        var hostReady = startup.Elapsed.TotalMilliseconds;
        string Url(string card, string surface)
            => $"/api/tasks/{card}/{surface}?project={Uri.EscapeDataString(project.Id)}";

        var rows = new List<object>();
        var report = new StringBuilder();
        report.AppendLine($"host ready after {hostReady:F0} ms");
        foreach (var (card, key) in new[] { ("fresh-card", FreshKey), ("wide-card", WideKey) })
        {
            foreach (var surface in new[] { "steering-feedback", "runs", "timeline" })
            {
                var samples = new List<double>();
                for (var i = 0; i < 6; i++)
                {
                    var timer = Stopwatch.StartNew();
                    using var response = await client.GetAsync(Url(card, surface));
                    await response.Content.ReadAsByteArrayAsync();
                    timer.Stop();
                    response.EnsureSuccessStatusCode();
                    samples.Add(timer.Elapsed.TotalMilliseconds);
                }
                var warm = samples.Skip(1).Order().ToArray();
                var row = new
                {
                    card = key,
                    surface,
                    firstMs = Math.Round(samples[0], 1),
                    warmMedianMs = Math.Round(warm[warm.Length / 2], 1),
                    warmMaxMs = Math.Round(warm[^1], 1),
                };
                rows.Add(row);
                var line = $"{key,-9} {surface,-18} first {row.firstMs,9:F1} ms  warm median {row.warmMedianMs,9:F1} ms  warm max {row.warmMaxMs,9:F1} ms";
                report.AppendLine(line);
                _output.WriteLine(line);
            }
        }

        var label = Environment.GetEnvironmentVariable("ARCHIVE_BENCH_LABEL");
        var results = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR");
        if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(results))
        {
            Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(results, $"archive-read-benchmark-{label}.json"), JsonSerializer.Serialize(new
            {
                label,
                shape,
                hostReadyMs = Math.Round(hostReady, 1),
                rows,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = "Agent Studio",
                    ["WatchPaths:0:Path"] = _projectPath,
                    ["WatchPaths:0:RootPath"] = _projectPath,
                }));
        });

    private sealed record Shape(int Files, long Bytes, int Runs, int Reviews, int Keys, int P50, int P95, int Max);

    /// <summary>
    /// Writes daily archive files with the Stable distribution. Most keys sit
    /// in one file; a tail spreads over up to 22. Each record is padded with
    /// idempotency keys, the field that makes real records large, so every
    /// file reaches its byte budget with realistic parse work.
    /// </summary>
    private Shape WriteArchives()
    {
        var random = new Random(3109);
        var metadata = Path.Combine(_workspace, ".metadata");
        Directory.CreateDirectory(metadata);
        var keys = Enumerable.Range(0, KeyCount).Select(i => $"AGT-{1000 + i}").ToArray();
        var filesPerKey = keys.Select((_, i) =>
            i == KeyCount - 1 ? 22
            : i < 300 ? 1
            : i < 400 ? random.Next(2, 6)
            : i < 440 ? random.Next(6, 14)
            : random.Next(14, 22)).ToArray();
        // The two large files are the busiest days, so the wide keys pass through them.
        var fileSets = filesPerKey.Select((count, i) =>
        {
            var set = new HashSet<int>();
            if (count >= 6) { set.Add(0); set.Add(1); }
            while (set.Count < count) set.Add(random.Next(FileCount));
            return set.ToArray();
        }).ToArray();

        var weight = filesPerKey.Select(count => (double)count).ToArray();
        var weightSum = weight.Sum();
        var records = new List<(int File, string Key, bool Review)>();
        void Distribute(int total, bool review)
        {
            // The widest key goes first so it always reaches all of its files.
            var assigned = 0;
            for (var k = KeyCount - 1; k >= 0; k--)
            {
                var share = k == 0
                    ? total - assigned
                    : Math.Max(fileSets[k].Length, (int)Math.Round(total * weight[k] / weightSum));
                share = Math.Min(share, total - assigned);
                for (var r = 0; r < share; r++)
                    records.Add((fileSets[k][r % fileSets[k].Length], keys[k], review));
                assigned += share;
            }
        }
        Distribute(RunCount, review: false);
        Distribute(ReviewCount, review: true);

        var smallBudget = (TotalBytes - LargeFiles.Sum()) / (FileCount - LargeFiles.Length);
        long written = 0;
        var day = new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc);
        for (var file = 0; file < FileCount; file++)
        {
            var inFile = records.Where(record => record.File == file).ToList();
            var budget = file < LargeFiles.Length ? LargeFiles[file] : smallBudget;
            var padPerRecord = (int)Math.Max(0, budget / Math.Max(1, inFile.Count) - 900);
            var path = Path.Combine(metadata, $"attempt-authority.archive-{day.AddDays(file):yyyy-MM-dd}.json");
            using (var stream = File.Create(path))
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteNumber("schemaVersion", 1);
                json.WriteString("archivedAt", day.AddDays(file));
                json.WriteStartArray("runAttempts");
                var n = 0;
                foreach (var record in inFile.Where(record => !record.Review))
                    WriteRun(json, record.Key, $"run_{file}_{n++}", day.AddDays(file), padPerRecord);
                json.WriteEndArray();
                json.WriteStartArray("reviewAttempts");
                foreach (var record in inFile.Where(record => record.Review))
                    WriteReview(json, record.Key, $"review_{file}_{n++}", day.AddDays(file), padPerRecord);
                json.WriteEndArray();
                json.WriteEndObject();
            }
            written += new FileInfo(path).Length;
        }

        var spread = filesPerKey.Order().ToArray();
        return new Shape(
            FileCount, written,
            records.Count(record => !record.Review), records.Count(record => record.Review),
            KeyCount, spread[spread.Length / 2], spread[(int)(spread.Length * 0.95)], spread[^1]);
    }

    private static void WriteRun(Utf8JsonWriter json, string key, string id, DateTime at, int pad)
    {
        json.WriteStartObject();
        json.WriteString("attemptId", id);
        json.WriteString("taskKey", key);
        json.WriteString("repositoryId", "PROJ-002");
        json.WriteNumber("state", 2);
        json.WriteNumber("lastFence", 3);
        json.WriteNumber("authorityEpoch", 1);
        json.WriteString("createdAt", at);
        json.WriteString("terminalAt", at.AddMinutes(40));
        json.WriteString("terminalOutcome", "done");
        json.WriteString("terminalReason", "Run settled by the remote runner.");
        Pad(json, id, pad);
        json.WriteEndObject();
    }

    private static void WriteReview(Utf8JsonWriter json, string key, string id, DateTime at, int pad)
    {
        json.WriteStartObject();
        json.WriteString("attemptId", id);
        json.WriteString("taskKey", key);
        json.WriteString("repositoryId", "PROJ-002");
        json.WriteString("sourceRunAttemptId", "run_" + id);
        json.WriteNumber("state", 2);
        json.WriteNumber("lastFence", 2);
        json.WriteNumber("authorityEpoch", 1);
        json.WriteString("createdAt", at);
        json.WriteString("terminalAt", at.AddMinutes(25));
        json.WriteString("terminalReason", "Review settled.");
        Pad(json, id, pad);
        json.WriteEndObject();
    }

    private static void Pad(Utf8JsonWriter json, string id, int bytes)
    {
        json.WriteStartArray("idempotencyKeys");
        for (var i = 0; i < bytes / 80; i++)
            json.WriteStringValue($"lease-renew:{id}:{i:D6}:0123456789abcdef0123456789abcdef0123456789");
        json.WriteEndArray();
    }

    private void SeedTask(string id, string key)
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_projectPath, state));
        var folder = Path.Combine(_projectPath, TaskStates.Progress, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            key,
            title = "Card " + key,
            state = TaskStates.Progress,
            order = 1,
            agent = "codex",
            createdAt = _time.GetUtcNow().UtcDateTime,
        }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }
}
