using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// Git facts S5 board budgets on the 3,000-card board, measured on this
/// machine (<c>Category=MachineBound</c>):
/// <list type="bullet">
///   <item>list and grouped reads, each after a new index generation (a full
///   body build) and repeated on an unchanged generation (the shared body),
///   p95 under 200 ms with 0 Git processes;</item>
///   <item>after a restart with a persisted board and the first scan held,
///   board reads answer restored, the first within 2 s and p95 under 200 ms;
///   the time the first publication takes once released is recorded as the
///   wait those reads no longer pay.</item>
/// </list>
/// With <c>GIT_FACTS_BENCH_OUT</c> set, the result is written as
/// <c>board-s5-benchmark.json</c>.
/// </summary>
[Collection(GitFactsCollection.Name)]
[Trait("Category", "MachineBound")]
public sealed class PersistedBoardBenchmarks(GitFactsBoardFixture board, ITestOutputHelper output)
{
    private const int Runs = 30;
    private const double ReadBudgetMs = 200;
    private const double FirstReadBudgetMs = 2_000;

    [Fact]
    public async Task Board_reads_and_the_first_read_after_a_restart_meet_the_s5_budgets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "atp-board-s5-" + Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string?> { ["BoardSummaries:StorePath"] = Path.Combine(directory, "board.db") };
        try
        {
            var routes = new JsonArray();
            double persistMs;
            int liveCards, liveGroupedCards;
            await using (var host = new GitReadCensusHost(board, keepGitStateIndex: true, settings: settings))
            {
                await GitFactsBudgetTests.IndexedAsync(host);
                var index = host.Services.GetRequiredService<TaskIndexCache>();
                foreach (var url in new[] { "/api/tasks/", "/api/tasks/grouped" })
                {
                    var rebuilt = new List<double>();
                    var shared = new List<double>();
                    int spawns;
                    for (var run = 0; run < Runs; run++)
                    {
                        // Not timed: a new generation, so the timed read builds its body.
                        index.ForceRefresh();
                        rebuilt.Add(await TimedRead(host, url));
                        shared.Add(await TimedRead(host, url));
                    }
                    spawns = host.Records.Sum(record => record.Spawns);
                    routes.Add(Route(url, "new generation (body built)", rebuilt, spawns));
                    routes.Add(Route(url, "unchanged generation (shared body)", shared, spawns));
                    Assert.Equal(0, spawns);
                    Assert.True(Percentile(rebuilt, 0.95) < ReadBudgetMs, $"{url} rebuilt p95 {Percentile(rebuilt, 0.95):0.0} ms");
                    Assert.True(Percentile(shared, 0.95) < ReadBudgetMs, $"{url} shared p95 {Percentile(shared, 0.95):0.0} ms");
                }

                var persister = host.Services.GetRequiredService<BoardSummaryPersister>();
                var watch = Stopwatch.StartNew();
                Assert.Equal(1, persister.PersistPublished());
                persistMs = watch.Elapsed.TotalMilliseconds;
                liveCards = host.Services.GetRequiredService<TaskScannerService>().TryGetPublishedBoard()!.Live.Count;
                using var grouped = await host.Get("/api/tasks/grouped");
                liveGroupedCards = GitFactsBudgetTests.Cards(JsonDocument.Parse(await grouped.Content.ReadAsStringAsync()).RootElement).Count();
            }

            // Restart with the first index scan held, so every read below is
            // the restored path whatever the speed of this machine's scan.
            using var scanOpen = new ManualResetEventSlim(false);
            await using var restarted = new GitReadCensusHost(board, settings: settings,
                wrapIndexScan: scan => () =>
                {
                    scanOpen.Wait();
                    return scan();
                });
            var restoredMs = new List<double>();
            string? firstState = null;
            var firstCards = 0;
            try
            {
                // Builds the endpoint matcher, which the first request would pay otherwise.
                using (await restarted.Get("/healthz")) { }
                for (var run = 0; run < Runs; run++)
                {
                    var watch = Stopwatch.StartNew();
                    using var response = await restarted.Get("/api/tasks/grouped");
                    restoredMs.Add(watch.Elapsed.TotalMilliseconds);
                    response.EnsureSuccessStatusCode();
                    if (run > 0) continue;
                    firstState = Header(response, RestoredBoard.StateHeader);
                    firstCards = GitFactsBudgetTests.Cards(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement).Count();
                }
            }
            finally
            {
                scanOpen.Set();
            }
            // The wait a cold read paid before S5: the first publication.
            var scanReleased = Stopwatch.StartNew();
            var restoredSpawns = restarted.Records.Sum(record => record.Spawns);
            string? state = firstState;
            while (state != RestoredBoard.Ready && scanReleased.Elapsed < TimeSpan.FromMinutes(2))
            {
                await Task.Delay(10);
                using var response = await restarted.Get("/api/tasks/grouped");
                state = Header(response, RestoredBoard.StateHeader);
            }
            var publicationMs = scanReleased.Elapsed.TotalMilliseconds;

            var result = new JsonObject
            {
                ["board"] = $"{GitFactsBoardFixture.Shape.Sum(lane => lane.Count) + GitFactsBoardFixture.DuplicateIds} folders, {liveCards} live cards",
                ["measurement"] = "in-process TestServer round trip per request, Stopwatch; spawns from the read guard",
                ["routes"] = routes,
                ["persist"] = new JsonObject { ["ms"] = Math.Round(persistMs, 1), ["cards"] = liveCards },
                ["restart"] = new JsonObject
                {
                    ["firstGroupedReadMs"] = Math.Round(restoredMs[0], 1),
                    ["firstGroupedReadState"] = firstState,
                    ["firstGroupedReadCards"] = firstCards,
                    ["restoredGroupedReads"] = Route("/api/tasks/grouped", "restored (first scan held)", restoredMs, restoredSpawns),
                    ["firstPublicationMs"] = Math.Round(publicationMs, 1),
                    ["note"] = "first scan held until the restored reads were measured; firstPublicationMs is the cold wait a board read paid before S5 (scan plus publish, polled every 10 ms)",
                },
                ["host"] = new JsonObject
                {
                    ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    ["processors"] = Environment.ProcessorCount,
                    ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                },
            };
            output.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (Environment.GetEnvironmentVariable("GIT_FACTS_BENCH_OUT") is { Length: > 0 } outDirectory)
            {
                Directory.CreateDirectory(outDirectory);
                File.WriteAllText(Path.Combine(outDirectory, "board-s5-benchmark.json"),
                    result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }

            Assert.Equal(RestoredBoard.Restored, firstState);
            Assert.True(restoredMs[0] < FirstReadBudgetMs, $"first board read after restart took {restoredMs[0]:0} ms");
            Assert.True(Percentile(restoredMs, 0.95) < ReadBudgetMs, $"restored grouped p95 {Percentile(restoredMs, 0.95):0.0} ms");
            Assert.Equal(0, restoredSpawns);
            Assert.Equal(liveGroupedCards, firstCards);
            Assert.Equal(RestoredBoard.Ready, state);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    private static async Task<double> TimedRead(GitReadCensusHost host, string url)
    {
        var watch = Stopwatch.StartNew();
        using var response = await host.Get(url);
        watch.Stop();
        response.EnsureSuccessStatusCode();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static JsonObject Route(string url, string mode, List<double> ms, int spawns) => new()
    {
        ["route"] = "GET " + url,
        ["mode"] = mode,
        ["requests"] = ms.Count,
        ["p50Ms"] = Math.Round(Percentile(ms, 0.50), 1),
        ["p95Ms"] = Math.Round(Percentile(ms, 0.95), 1),
        ["maxMs"] = Math.Round(ms.Max(), 1),
        ["spawnsTotal"] = spawns,
    };

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
    }
}
