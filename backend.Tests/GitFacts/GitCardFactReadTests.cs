using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentStudio.Git;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Git facts S3 (AGT-3323) on the 3,000-card board: the card Git reads
/// (provenance, commit list and files, the default diff, single-commit views,
/// run views, the project integration view and the delivery claim) are served
/// from the facts the Git state indexer published and start no Git process,
/// including integrated Human Review, Auto Review, Escalated, Completed and
/// Archive cards. A missing diff object answers warming and is written by the
/// producer; a fast-forward of develop is appended incrementally and
/// reflected in provenance, the integration view and the delivery claim; a
/// task-only change rejoins the integration view without Git. Elapsed-time
/// targets live in <see cref="GitCardFactBenchmarks"/>.
/// </summary>
[Collection(GitFactsCollection.Name)]
public sealed class GitCardFactReadTests(GitFactsBoardFixture board)
{
    [Fact]
    public async Task Card_git_reads_are_joined_from_published_facts_without_git()
    {
        await using var host = await PublishedHost();
        var review = GitFactsBoardFixture.IntegratedReview;

        using var provenance = await Ready(host, host.TaskUrl(review, "provenance"));
        var view = Json(provenance);
        Assert.Equal(LandedStates.MergedToDevelop, view.GetProperty("landedState").GetString());
        Assert.All(view.GetProperty("commits").EnumerateArray(),
            commit => Assert.True(commit.GetProperty("alsoOnIntegration").GetBoolean()));
        Assert.Equal(board.Git(board.Code, "rev-parse", "develop").Trim(),
            view.GetProperty("ladder").GetProperty("integrationHead").GetString());
        Assert.True(provenance.Headers.Contains(GitFactsResults.GenerationHeader));
        Assert.Equal("ready", provenance.Headers.GetValues(GitFactsResults.StateHeader).Single());

        using var commits = await Ready(host, host.TaskUrl(review, "commits"));
        var shas = Json(commits).GetProperty("commits").EnumerateArray()
            .Select(commit => commit.GetProperty("sha").GetString()).ToList();
        // The run range (last delivery..review tip) also lists develop's docs
        // commit, as the live range read did; newest first.
        Assert.Equal(board.ReviewCommits.Reverse(), shas.Take(2));
        Assert.Equal(3, shas.Count);

        using var files = await Ready(host, host.TaskUrl(review, "commits/files"));
        Assert.Contains(Json(files).GetProperty("files").EnumerateArray(),
            file => file.GetProperty("path").GetString() == "src/Facts/Review.cs");

        // The default diff of the multi-commit card is the aggregate object.
        using var diff = await Ready(host, host.TaskUrl(review, "commits/diff", "&path=src/Facts/Review.cs"));
        var text = Json(diff).GetProperty("diff").GetString()!;
        Assert.Contains("public int Id", text);
        Assert.Equal(2, text.Split("diff --git a/src/Facts/Review.cs").Length - 1);

        using var single = await Ready(host, host.TaskUrl(review, $"commits/{board.ReviewCommits[0]}/diff"));
        Assert.Contains("+namespace Facts; public sealed class Review { }", Json(single).GetProperty("diff").GetString());
        using var singleFiles = await Ready(host, host.TaskUrl(review, $"commits/{board.ReviewCommits[1]}/files"));
        Assert.Equal(2, Json(singleFiles).GetProperty("files").GetArrayLength());
        using var recorded = await Ready(host, host.TaskUrl(review, "commit"));
        Assert.NotEmpty(Json(recorded).GetProperty("files").EnumerateArray());
        using var unknown = await host.Get(host.TaskUrl(review, $"commits/{new string('e', 40)}/files"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // Run views read the run's captured SHA range.
        using var runCommits = await Ready(host, host.TaskUrl(review, "runs/1/commits"));
        Assert.Equal("sha-range", Json(runCommits).GetProperty("source").GetString());
        Assert.Equal(3, Json(runCommits).GetProperty("commits").GetArrayLength());
        using var runFiles = await Ready(host, host.TaskUrl(review, "runs/1/files"));
        Assert.Contains(Json(runFiles).GetProperty("files").EnumerateArray(),
            file => file.GetProperty("path").GetString() == "src/Facts/Review.cs");
        using var runDiff = await Ready(host, host.TaskUrl(review, "runs/1/diff", "&path=src/Facts/Review.cs"));
        Assert.Contains("public int Id", Json(runDiff).GetProperty("diff").GetString());

        using var integration = await Ready(host, "/api/git/integration?project=" + GitFactsBoardFixture.ProjectName);
        Assert.Contains(Json(integration).GetProperty("queue").EnumerateArray(),
            item => item.GetProperty("status").GetString() == IntegrationQueueStates.Merged);
        Assert.Contains(Json(integration).GetProperty("publisherMerges").EnumerateArray(),
            merge => merge.GetProperty("taskKey").GetString() == "AGT-9001");

        AssertNoSpawns(host);
    }

    [Theory]
    [InlineData(GitFactsBoardFixture.IntegratedReview, true)]
    [InlineData(GitFactsBoardFixture.IntegratedAutoReview, true)]
    [InlineData(GitFactsBoardFixture.EscalatedWithFiles, true)]
    [InlineData(GitFactsBoardFixture.EscalatedBare, false)]
    [InlineData(TaskStates.Completed, true)]
    [InlineData(TaskStates.Archive, true)]
    public async Task Delivery_claim_reads_published_containment_and_merge_index(string card, bool integrated)
    {
        await using var host = await PublishedHost();
        var id = card.StartsWith("agt-", StringComparison.Ordinal) ? card : board.PlainCard(card);

        using var response = await Claim(host, id);
        var claim = Json(response);

        Assert.Equal(integrated, claim.GetProperty("integrated").GetBoolean());
        // A published verdict, never the unknown a missing fact reads as.
        Assert.NotEqual("unknown", claim.GetProperty("containmentStatus").GetString());
        Assert.True(claim.GetProperty("factsGeneration").GetInt64() > 0);
        if (id is GitFactsBoardFixture.IntegratedReview or GitFactsBoardFixture.EscalatedWithFiles)
            Assert.StartsWith("merge(", claim.GetProperty("mergeSubject").GetString());
        AssertNoSpawns(host);
    }

    [Fact]
    public async Task A_missing_diff_object_answers_warming_and_the_producer_writes_it()
    {
        await using var host = await PublishedHost();
        // Archived cards get their diff objects on demand, not at publication.
        var archived = board.PlainCard(TaskStates.Archive);
        var url = host.TaskUrl(archived, $"commits/{FirstCommit(host, archived)}/diff");
        var store = host.Services.GetRequiredService<GitFactStore>();
        var reader = host.Services.GetRequiredService<GitCardFactReader>();
        var facts = reader.Read(board.WatchPath).Facts!;
        var key = GitDiffObjectKey.Commit(facts.RepoId, facts.Catalog.Commit(FirstCommit(host, archived))!);
        var existed = reader.DiffObject(facts, key.ObjectId) is not null;

        using var first = await host.Get(url);
        if (!existed)
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            Assert.Equal(GitFactsResults.WarmingState, Json(first).GetProperty("state").GetString());
            Assert.Equal("warming", first.Headers.GetValues(GitFactsResults.StateHeader).Single());
        }
        using var ready = await Ready(host, url);
        Assert.True(Json(ready).GetProperty("hasDiff").GetBoolean());
        Assert.NotNull(store.ReadDiffObjects([key.ObjectId]).GetValueOrDefault(key.ObjectId));
        AssertNoSpawns(host);
    }

    [Fact]
    public async Task Responses_carry_generation_age_and_an_etag_that_answers_not_modified()
    {
        await using var host = await PublishedHost();
        var url = host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "provenance");
        using var first = await Ready(host, url);
        Assert.NotNull(first.Headers.ETag);
        Assert.True(long.Parse(first.Headers.GetValues(GitFactsResults.GenerationHeader).Single(),
            System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.True(first.Headers.Contains(GitFactsResults.AgeHeader));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var second = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        AssertNoSpawns(host);
    }

    [Fact]
    public async Task A_fast_forward_of_develop_is_appended_and_reflected_without_request_git()
    {
        await using var host = await PublishedHost();
        var reader = host.Services.GetRequiredService<GitCardFactReader>();
        var reachability = host.Services.GetRequiredService<GitReachabilityIndex>();
        var before = reader.Read(board.WatchPath).Facts!.Generation;
        var fastForwards = reachability.FastForwards;

        var moved = board.MoveDevelop("s3-fast-forward-" + Guid.NewGuid().ToString("N")[..8]);
        host.Services.GetRequiredService<GitStateIndexService>().RequestRefresh(GitFactsBoardFixture.ProjectName, "fs-watch");
        await GitFactsBudgetTests.Until(() => reader.Read(board.WatchPath).Facts is { } facts
            && facts.Generation > before && facts.Tip("develop") == moved);

        Assert.True(reachability.FastForwards > fastForwards, "a fast-forward recomputed the whole ref");
        using var provenance = await Ready(host, host.TaskUrl(GitFactsBoardFixture.IntegratedReview, "provenance"));
        Assert.Equal(moved, Json(provenance).GetProperty("ladder").GetProperty("integrationHead").GetString());
        Assert.Equal(LandedStates.MergedToDevelop, Json(provenance).GetProperty("landedState").GetString());
        using var integration = await Ready(host, "/api/git/integration?project=" + GitFactsBoardFixture.ProjectName);
        Assert.Equal(moved, Json(integration).GetProperty("integrationHeadSha").GetString());
        using var claim = await Claim(host, GitFactsBoardFixture.IntegratedReview);
        Assert.True(Json(claim).GetProperty("integrated").GetBoolean());
        AssertNoSpawns(host);
    }

    [Fact]
    public async Task A_task_only_change_rejoins_the_integration_view_without_git()
    {
        var runs = new IndexRunLog();
        await using var host = await PublishedHost(runs);
        var reader = host.Services.GetRequiredService<GitCardFactReader>();
        var card = board.PlainCard(TaskStates.Completed);
        var taskJson = Path.Combine(board.JobFolder(TaskStates.Completed, card), "task.json");
        var original = File.ReadAllText(taskJson);
        var title = "Renamed " + Guid.NewGuid().ToString("N")[..8];
        var viewBefore = reader.Read(board.WatchPath).Facts!.ProjectView!;
        try
        {
            File.WriteAllText(taskJson, original.Replace($"Git facts card {card}", title, StringComparison.Ordinal));
            var mark = runs.Completed.Count;
            host.Services.GetRequiredService<GitStateIndexService>().RequestRefresh(GitFactsBoardFixture.ProjectName, "task-event");
            var run = await runs.NextAsync(mark);

            Assert.Equal(0, run.Spawns);
            var view = reader.Read(board.WatchPath).Facts!.ProjectView!;
            Assert.True(view.TaskGeneration > viewBefore.TaskGeneration);
            Assert.Contains(view.View.Queue, item => item.Title == title);
        }
        finally
        {
            File.WriteAllText(taskJson, original);
        }
    }

    private async Task<GitReadCensusHost> PublishedHost(IndexRunLog? runs = null)
    {
        var host = new GitReadCensusHost(board, keepGitStateIndex: true, runs);
        var reader = host.Services.GetRequiredService<GitCardFactReader>();
        await GitFactsBudgetTests.IndexedAsync(host);
        await GitFactsBudgetTests.Until(() => reader.Read(board.WatchPath).Facts?.ProjectView is not null);
        return host;
    }

    private static string FirstCommit(GitReadCensusHost host, string id)
        => host.Services.GetRequiredService<TaskScannerService>().FindJob(id)!.Commits[0].Sha;

    /// <summary>Polls a read until it is no longer warming (202); fails on any other status.</summary>
    internal static async Task<HttpResponseMessage> Ready(GitReadCensusHost host, string url)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var response = await host.Get(url);
            if (response.StatusCode == HttpStatusCode.OK) return response;
            Assert.True(response.StatusCode == HttpStatusCode.Accepted,
                $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            response.Dispose();
            if (waited.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException(url + " stayed warming");
            await Task.Delay(25);
        }
    }

    /// <summary>The delivery claim once its card facts are published (ready or restored).</summary>
    internal static async Task<HttpResponseMessage> Claim(GitReadCensusHost host, string id)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var response = await host.Get(host.TaskUrl(id, "delivery-claim"));
            response.EnsureSuccessStatusCode();
            if (Json(response).GetProperty("factsState").GetString() is "ready" or "restored") return response;
            response.Dispose();
            if (waited.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException(id + " claim stayed unpublished");
            await Task.Delay(25);
        }
    }

    internal static JsonElement Json(HttpResponseMessage response)
        => JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();

    private static void AssertNoSpawns(GitReadCensusHost host)
    {
        Assert.NotEmpty(host.Records);
        Assert.All(host.Records, record => Assert.True(record.Spawns == 0,
            $"{record.RouteTemplate} started [{string.Join(",", record.Commands)}]"));
    }
}
