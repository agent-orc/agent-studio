using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Git;
using AgentStudio.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// Elapsed-time measurements of Git facts S1 (AGT-3321) on the 3,000-card
/// board. They read real time and depend on the host, so they carry
/// <c>Category=MachineBound</c> and run on the documented reference host
/// before delivery, not in the routine gate.
/// <list type="bullet">
///   <item>The index benchmark measures 30 unchanged-input
///   <see cref="GitStateIndexService"/> runs after warm-up, plus the first
///   build and a changed-ref run, and enforces the frozen ceiling of
///   <c>git-facts-s1-benchmark.json</c>: measured p95 plus 10 percent, at most
///   the 90 s run deadline. A failed or timed-out run fails the benchmark.</item>
///   <item>The card-switch baseline replays 30 card switches with the Studio's
///   card-open requests and records p50, p95 and spawns per route. It is the
///   S1 baseline the later slices improve on; it asserts no latency.</item>
/// </list>
/// <c>GIT_FACTS_BENCH_OUT</c> names a directory for the JSON results.
/// </summary>
[Collection(GitFactsCollection.Name)]
[Trait("Category", "MachineBound")]
public sealed class GitFactsBenchmarks(GitFactsBoardFixture board, ITestOutputHelper output)
{
    private const string ManifestPath = "backend.Tests/GitFacts/git-facts-s1-benchmark.json";
    private const int Runs = 30;
    private const int WarmUpRuns = 3;
    private static readonly TimeSpan RunDeadline = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task Unchanged_input_index_runs_stay_under_the_frozen_s1_ceiling()
    {
        var runs = new IndexRunLog();
        await using var host = new GitReadCensusHost(board, keepGitStateIndex: true, runs);
        await GitFactsBudgetTests.IndexedAsync(host);
        var indexer = host.Services.GetRequiredService<GitStateIndexService>();
        // The first complete run; its trigger can be startup, a coalesced
        // watcher event or the rerun of a superseded start-up run. With the
        // fact store warm from an earlier host on the shared board it is a
        // confirmed restore rather than a cold build; the outcome says which.
        var firstBuild = await runs.FirstAsync(run => run.Outcome is "published" or "confirmed");

        var measured = new List<IndexRunLog.IndexRun>();
        for (var index = 0; index < WarmUpRuns + Runs; index++)
        {
            var before = runs.Completed.Count;
            indexer.RequestRefresh(GitFactsBoardFixture.ProjectName, "task-event");
            var run = await runs.NextAsync(before);
            if (index >= WarmUpRuns) measured.Add(run);
        }

        var beforeMove = runs.Completed.Count;
        board.MoveDevelop("index-benchmark");
        indexer.RequestRefresh(GitFactsBoardFixture.ProjectName, "ref-moved");
        IndexRunLog.IndexRun changedRef;
        do changedRef = await runs.NextAsync(beforeMove++);
        while (changedRef.Outcome != "published");

        Assert.Empty(runs.Failures);
        Assert.All(measured, run => Assert.Contains(run.Outcome, IndexRunLog.CompleteOutcomes));
        Assert.All(measured, run => Assert.True(run.ElapsedMs < RunDeadline.TotalMilliseconds));
        var status = indexer.GetRepositoryStatuses().Single(entry => entry.ProjectName == GitFactsBoardFixture.ProjectName);
        Assert.Null(status.ReasonCode);

        var durations = measured.Select(run => (double)run.ElapsedMs).ToList();
        var result = new JsonObject
        {
            ["cards"] = board.CardCount,
            ["runs"] = Runs,
            ["warmUpRuns"] = WarmUpRuns,
            ["p50Ms"] = Percentile(durations, 0.50),
            ["p95Ms"] = Percentile(durations, 0.95),
            ["maxMs"] = durations.Max(),
            ["durationsMs"] = new JsonArray(measured.Select(run => (JsonNode)run.ElapsedMs).ToArray()),
            ["spawnsPerRun"] = measured.Max(run => run.Spawns),
            ["firstBuildMs"] = firstBuild.ElapsedMs,
            ["firstBuildTrigger"] = firstBuild.Trigger,
            ["firstBuildOutcome"] = firstBuild.Outcome,
            ["firstBuildSpawns"] = firstBuild.Spawns,
            ["changedRefMs"] = changedRef.ElapsedMs,
            ["changedRefSpawns"] = changedRef.Spawns,
            ["host"] = ReferenceHost(),
        };
        output.WriteLine(result.ToJsonString());
        Write("index-benchmark.json", result);

        var frozen = ReadManifest()?["index"];
        Assert.True(frozen is not null, $"{ManifestPath} has no frozen index budget");
        var ceiling = frozen!["ceilingMs"]!.GetValue<double>();
        Assert.True(ceiling <= RunDeadline.TotalMilliseconds, "the frozen ceiling may not exceed the run deadline");
        Assert.True(Percentile(durations, 0.95) < ceiling,
            $"unchanged-input index p95 {Percentile(durations, 0.95):0} ms breaches the frozen S1 ceiling {ceiling:0} ms");
        Assert.True(measured.Max(run => run.Spawns) <= frozen["spawnsPerRun"]!.GetValue<int>());
    }

    [Fact]
    public async Task Thirty_card_switches_record_the_s1_card_request_baseline()
    {
        await using var host = new GitReadCensusHost(board, keepGitStateIndex: true);
        await GitFactsBudgetTests.IndexedAsync(host);
        var cards = new (string Id, string Lane)[]
        {
            (GitFactsBoardFixture.IntegratedReview, TaskStates.HumanReview),
            (GitFactsBoardFixture.EscalatedWithFiles, TaskStates.Escalated),
            (GitFactsBoardFixture.EscalatedBare, TaskStates.Escalated),
            (board.PlainCard(TaskStates.Completed), TaskStates.Completed),
            (board.PlainCard(TaskStates.Archive), TaskStates.Archive),
            (GitFactsBoardFixture.InProgress, TaskStates.Progress),
        };
        var samples = new List<(string Route, double Ms, int Spawns, int Status)>();

        for (var turn = 0; turn < Runs; turn++)
        {
            var (id, lane) = cards[turn % cards.Length];
            foreach (var (route, url) in CardOpen(host, id, lane))
            {
                var before = host.Records.Count;
                var watch = Stopwatch.StartNew();
                using var response = await host.Get(url);
                watch.Stop();
                var spawns = host.Records.Skip(before).Sum(record => record.Spawns);
                samples.Add((route, watch.Elapsed.TotalMilliseconds, spawns, (int)response.StatusCode));
            }
        }

        Assert.DoesNotContain(samples, sample => sample.Status >= 500);
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
                ["spawnsP50"] = Percentile(group.Select(sample => (double)sample.Spawns).ToList(), 0.50),
                ["spawnsMax"] = group.Max(sample => sample.Spawns),
                ["spawnsTotal"] = group.Sum(sample => sample.Spawns),
                ["ceiling"] = GitReadSpawnExceptionManifest.Find("GET", group.Key.Split(' ')[0])?.SpawnCeiling,
            });
        }
        var result = new JsonObject
        {
            ["switches"] = Runs,
            ["cards"] = new JsonArray(cards.Select(card => (JsonNode)$"{card.Id} ({card.Lane})").ToArray()),
            ["measurement"] = "in-process TestServer round trip per request, Stopwatch; spawns from the read guard",
            ["routes"] = routes,
            ["host"] = ReferenceHost(),
        };
        output.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Write("card-switch-baseline.json", result);
    }

    /// <summary>The Studio's requests when a card opens, by lane (git pane, files, delivery claim).</summary>
    private static IEnumerable<(string Route, string Url)> CardOpen(GitReadCensusHost host, string id, string lane)
    {
        const string Tasks = "/api/tasks/{jobId}";
        var project = "?project=" + host.ProjectId;
        yield return (Tasks + "/core", $"/api/tasks/{id}/core{project}");
        foreach (var resource in new[] { "overview", "git", "review" })
            yield return (Tasks + "/details/" + resource, $"/api/tasks/{id}/details/{resource}{project}");
        yield return (Tasks + "/provenance", host.TaskUrl(id, "provenance"));
        if (lane != TaskStates.Progress)
        {
            yield return (Tasks + "/commits/files", host.TaskUrl(id, "commits/files"));
            yield return (Tasks + "/commits/diff", host.TaskUrl(id, "commits/diff"));
        }
        yield return (Tasks + "/result-history", host.TaskUrl(id, "result-history"));
        if (lane == TaskStates.Escalated)
        {
            yield return (Tasks + "/files/{**path} orchestrator-follow-up.md", host.TaskUrl(id, "files/orchestrator-follow-up.md"));
            yield return (Tasks + "/files/{**path} results/decision.html", host.TaskUrl(id, "files/results/decision.html", "&scope=workspace"));
            yield return (Tasks + "/files/{**path} results/decision.json", host.TaskUrl(id, "files/results/decision.json", "&scope=workspace"));
        }
        if (lane is TaskStates.HumanReview or TaskStates.Escalated or TaskStates.Completed or TaskStates.Archive)
            yield return (Tasks + "/delivery-claim", host.TaskUrl(id, "delivery-claim"));
    }

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
    }

    private static JsonObject ReferenceHost() => new()
    {
        ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        ["processors"] = Environment.ProcessorCount,
        ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        ["machine"] = Environment.MachineName,
    };

    private static JsonNode? ReadManifest()
    {
        var path = Path.Combine(RepositoryRoot.Find(), ManifestPath);
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
    }

    private static void Write(string name, JsonObject result)
    {
        var directory = Environment.GetEnvironmentVariable("GIT_FACTS_BENCH_OUT");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name),
            result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
