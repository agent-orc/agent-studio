using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Git;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// Elapsed-time targets of Git facts S3 (AGT-3323) on the 3,000-card board,
/// <c>Category=MachineBound</c> like the S1 benchmarks:
/// <list type="bullet">
///   <item>a 30-switch replay of the S3 card reads (provenance, commit list
///   and files, the default diff, single-commit files, run views, the
///   integration view and the delivery claim) over integrated Human Review,
///   Auto Review, Escalated, Completed and Archive cards: every route p95
///   under 200 ms with 0 Git processes, measured warm after the producer
///   published (a miss would answer warming, not be timed as a read);</item>
///   <item>a single fast-forward of develop reflected in published facts
///   within 2 s.</item>
/// </list>
/// <c>GIT_FACTS_BENCH_OUT</c> names a directory for the JSON results.
/// </summary>
[Collection(GitFactsCollection.Name)]
[Trait("Category", "MachineBound")]
public sealed class GitCardFactBenchmarks(GitFactsBoardFixture board, ITestOutputHelper output)
{
    private const int Switches = 30;
    private static readonly TimeSpan P95Budget = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FastForwardBudget = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Thirty_card_switches_serve_s3_reads_under_200_ms_p95_with_zero_spawns()
    {
        await using var host = new GitReadCensusHost(board, keepGitStateIndex: true);
        await GitFactsBudgetTests.IndexedAsync(host);
        var cards = new[]
        {
            GitFactsBoardFixture.IntegratedReview, GitFactsBoardFixture.IntegratedAutoReview,
            GitFactsBoardFixture.EscalatedWithFiles, GitFactsBoardFixture.EscalatedBare,
            board.PlainCard(TaskStates.Completed), board.PlainCard(TaskStates.Archive),
        };
        // Warm: every read once, until published (archived diff objects are
        // written on their first read).
        foreach (var card in cards)
        foreach (var (_, url) in Reads(host, card))
            (await GitCardFactReadTests.Ready(host, url)).Dispose();

        var samples = new List<(string Route, double Ms, int Spawns, int Status)>();
        for (var turn = 0; turn < Switches; turn++)
        {
            foreach (var (route, url) in Reads(host, cards[turn % cards.Length]))
            {
                var before = host.Records.Count;
                var watch = Stopwatch.StartNew();
                using var response = await host.Get(url);
                watch.Stop();
                samples.Add((route, watch.Elapsed.TotalMilliseconds,
                    host.Records.Skip(before).Sum(record => record.Spawns), (int)response.StatusCode));
            }
        }

        var routes = new JsonArray();
        foreach (var group in samples.GroupBy(sample => sample.Route).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var ms = group.Select(sample => sample.Ms).ToList();
            routes.Add(new JsonObject
            {
                ["route"] = group.Key,
                ["requests"] = ms.Count,
                ["p50Ms"] = Math.Round(Percentile(ms, 0.50), 1),
                ["p95Ms"] = Math.Round(Percentile(ms, 0.95), 1),
                ["maxMs"] = Math.Round(ms.Max(), 1),
                ["spawnsTotal"] = group.Sum(sample => sample.Spawns),
                ["statuses"] = string.Join(",", group.Select(sample => sample.Status).Distinct().Order()),
            });
        }
        var result = new JsonObject
        {
            ["switches"] = Switches,
            ["cards"] = new JsonArray(cards.Select(card => (JsonNode)card).ToArray()),
            ["measurement"] = "in-process TestServer round trip per request after publication, Stopwatch; spawns from the read guard",
            ["routes"] = routes,
            ["host"] = Host(),
        };
        output.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Write("s3-card-switch.json", result);

        Assert.All(samples, sample => Assert.Equal(200, sample.Status));
        Assert.All(samples, sample => Assert.Equal(0, sample.Spawns));
        foreach (var group in samples.GroupBy(sample => sample.Route))
        {
            var p95 = Percentile(group.Select(sample => sample.Ms).ToList(), 0.95);
            Assert.True(p95 < P95Budget.TotalMilliseconds, $"{group.Key}: p95 {p95:0.0} ms");
        }
    }

    [Fact]
    public async Task A_single_fast_forward_is_reflected_within_two_seconds_for_3000_cards()
    {
        var runs = new IndexRunLog();
        await using var host = new GitReadCensusHost(board, keepGitStateIndex: true, runs);
        await GitFactsBudgetTests.IndexedAsync(host);
        var reader = host.Services.GetRequiredService<GitCardFactReader>();
        await GitFactsBudgetTests.Until(() => reader.Read(board.WatchPath).Facts?.ProjectView is not null);

        var watch = Stopwatch.StartNew();
        var moved = board.MoveDevelop("s3-benchmark-" + Guid.NewGuid().ToString("N")[..8]);
        var pushed = watch.Elapsed;
        await GitFactsBudgetTests.Until(() => reader.Read(board.WatchPath).Facts is { } facts
            && facts.Tip("develop") == moved && facts.Tip("origin/develop") == moved
            && facts.ProjectView?.View.IntegrationHeadSha == moved);
        watch.Stop();
        var reflected = watch.Elapsed - pushed;
        var run = runs.Completed.LastOrDefault(entry => entry.Outcome == "published");

        var result = new JsonObject
        {
            ["cards"] = board.CardCount,
            ["reflectedMs"] = Math.Round(reflected.TotalMilliseconds, 1),
            ["publishingRunMs"] = run?.ElapsedMs,
            ["publishingRunSpawns"] = run?.Spawns,
            ["publishingRunTrigger"] = run?.Trigger,
            ["host"] = Host(),
        };
        output.WriteLine(result.ToJsonString());
        Write("s3-fast-forward.json", result);
        Assert.True(reflected < FastForwardBudget, $"fast-forward reflected after {reflected.TotalMilliseconds:0} ms");
    }

    /// <summary>The S3 reads a card open issues.</summary>
    private IEnumerable<(string Route, string Url)> Reads(GitReadCensusHost host, string id)
    {
        const string Tasks = "/api/tasks/{jobId}";
        var card = host.Services.GetRequiredService<TaskScannerService>().FindJob(id)!;
        var sha = card.Commits[^1].Sha;
        yield return (Tasks + "/provenance", host.TaskUrl(id, "provenance"));
        yield return (Tasks + "/commits", host.TaskUrl(id, "commits"));
        yield return (Tasks + "/commits/files", host.TaskUrl(id, "commits/files"));
        yield return (Tasks + "/commits/diff", host.TaskUrl(id, "commits/diff", "&path=" + Uri.EscapeDataString(card.Commits[^1].Files.FirstOrDefault() ?? "")));
        yield return (Tasks + "/commit", host.TaskUrl(id, "commit"));
        yield return (Tasks + "/commits/{sha}/files", host.TaskUrl(id, $"commits/{sha}/files"));
        yield return (Tasks + "/commits/{sha}/diff", host.TaskUrl(id, $"commits/{sha}/diff"));
        yield return (Tasks + "/delivery-claim", host.TaskUrl(id, "delivery-claim"));
        if (id == GitFactsBoardFixture.IntegratedReview)
        {
            foreach (var resource in new[] { "commits", "files", "diff" })
                yield return (Tasks + "/runs/{index:int}/" + resource, host.TaskUrl(id, "runs/1/" + resource));
        }
        yield return ("/api/git/integration", "/api/git/integration?project=" + GitFactsBoardFixture.ProjectName);
    }

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
    }

    private static JsonObject Host() => new()
    {
        ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        ["processors"] = Environment.ProcessorCount,
        ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        ["machine"] = Environment.MachineName,
    };

    private static void Write(string name, JsonObject result)
    {
        var directory = Environment.GetEnvironmentVariable("GIT_FACTS_BENCH_OUT");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name),
            result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
