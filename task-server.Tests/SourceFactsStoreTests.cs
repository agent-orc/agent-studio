using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// AGT-3318: the derived source data store keeps diff objects, path history,
/// blame and reachability by immutable key, admits only the current producer
/// epoch, and is bounded by size instead of a TTL.
/// </summary>
public sealed class SourceFactsStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private const string Repository = "prj_source_facts";
    private static readonly string BaseSha = new('a', 40);
    private static readonly string HeadSha = new('b', 40);

    [Fact]
    public void Lease_policy_grants_renews_holds_and_takes_over_with_the_next_epoch()
    {
        var now = Start.UtcDateTime;
        var granted = SourceProducerLeasePolicy.Decide(null, "host-a", 300, now);
        Assert.Equal((SourceProducerLeasePolicy.Outcome.Granted, 1L), (granted.Outcome, granted.Epoch));

        var current = new SourceProducerLeasePolicy.Current("host-a", 4, now.AddMinutes(5));
        var renewed = SourceProducerLeasePolicy.Decide(current, "host-a", 300, now);
        Assert.Equal((SourceProducerLeasePolicy.Outcome.Renewed, 4L), (renewed.Outcome, renewed.Epoch));

        var held = SourceProducerLeasePolicy.Decide(current, "host-b", 300, now);
        Assert.Equal((SourceProducerLeasePolicy.Outcome.Held, 4L), (held.Outcome, held.Epoch));

        var takeover = SourceProducerLeasePolicy.Decide(current, "host-b", 300, now.AddMinutes(6));
        Assert.Equal((SourceProducerLeasePolicy.Outcome.TakenOver, 5L), (takeover.Outcome, takeover.Epoch));

        var clamped = SourceProducerLeasePolicy.Decide(null, "host-a", 1, now);
        Assert.Equal(now.AddSeconds(SourceProducerLeasePolicy.MinimumTtlSeconds), clamped.ExpiresAt);

        Assert.True(SourceProducerLeasePolicy.AdmitsPublish(current, "host-a", 4));
        Assert.False(SourceProducerLeasePolicy.AdmitsPublish(current, "host-a", 3));
        Assert.False(SourceProducerLeasePolicy.AdmitsPublish(current, "host-b", 4));
        Assert.False(SourceProducerLeasePolicy.AdmitsPublish(null, "host-a", 1));
    }

    [Fact]
    public void Eviction_removes_least_recently_read_objects_down_to_the_low_water_mark()
    {
        var at = Start.UtcDateTime;
        var objects = new[]
        {
            new SourceFactEvictionPolicy.StoredObject("source_diff_objects", 1, 400, at.AddHours(3)),
            new SourceFactEvictionPolicy.StoredObject("source_blames", 2, 300, at.AddHours(1)),
            new SourceFactEvictionPolicy.StoredObject("source_path_histories", 3, 300, at.AddHours(2)),
        };

        Assert.Empty(SourceFactEvictionPolicy.SelectVictims(objects, 1000));
        var victims = SourceFactEvictionPolicy.SelectVictims(objects, 900);
        Assert.Equal([2L], victims.Select(victim => victim.RowId));
        var deeper = SourceFactEvictionPolicy.SelectVictims(objects, 500);
        Assert.Equal([2L, 3L], deeper.Select(victim => victim.RowId));
    }

    [Fact]
    public void Reachability_and_percentile_policies()
    {
        var head = new string('c', 40);
        Assert.True(SourceReachabilityPolicy.IsCurrent(new SourceReachabilityFact(HeadSha, "develop", true, head), new string('d', 40)));
        Assert.True(SourceReachabilityPolicy.IsCurrent(new SourceReachabilityFact(HeadSha, "develop", false, head), head));
        Assert.False(SourceReachabilityPolicy.IsCurrent(new SourceReachabilityFact(HeadSha, "develop", false, head), new string('d', 40)));
        Assert.False(SourceReachabilityPolicy.IsCurrent(new SourceReachabilityFact(HeadSha, "develop", false, head), null));

        Assert.Equal(0, SourceTimingPercentile.NearestRank([], 95));
        long[] samples = [10, 20, 30, 40, 50, 60, 70, 80, 90, 1000];
        Assert.Equal(50, SourceTimingPercentile.NearestRank(samples, 50));
        Assert.Equal(1000, SourceTimingPercentile.NearestRank(samples, 95));
    }

    [Fact]
    public async Task Diff_object_is_immutable_and_fenced_by_the_producer_epoch()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        var lease = await store.ClaimSourceProducerLeaseAsync(Repository, new SourceProducerLeaseRequest("host-a:1"));
        Assert.Equal(1, lease.Epoch);

        var request = Diff(lease, ["src/a.cs"], ["diff --git a/src/a.cs b/src/a.cs", "+x"]);
        var published = await store.PublishSourceDiffObjectAsync(Repository, request);
        Assert.Equal(request.ContentSha256, published.ContentSha256);

        // A second publish of the same bytes is idempotent; other bytes under
        // the same key prove two computations disagree and are refused.
        await store.PublishSourceDiffObjectAsync(Repository, request);
        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.PublishSourceDiffObjectAsync(Repository, Diff(lease, ["src/a.cs"], ["diff --git a/src/a.cs b/src/a.cs", "+y"])));
        Assert.Equal("source-object-conflict", conflict.Code);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PublishSourceDiffObjectAsync(Repository, request with { ContentSha256 = new string('0', 64) }));

        var read = await store.GetSourceDiffObjectAsync(
            Repository, BaseSha, HeadSha, SourceDiffObjectOptions.DefaultReview, SourceDiffObjectOptions.RendererVersion);
        Assert.NotNull(read);
        Assert.Equal(request.DiffLines, read!.DiffLines);
        Assert.Null(await store.GetSourceDiffObjectAsync(
            Repository, BaseSha, new string('e', 40), SourceDiffObjectOptions.DefaultReview, SourceDiffObjectOptions.RendererVersion));

        // Another producer waits for expiry, then takes over with epoch 2;
        // the previous holder is fenced from then on.
        var held = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ClaimSourceProducerLeaseAsync(Repository, new SourceProducerLeaseRequest("host-b:1")));
        Assert.Equal("source-producer-lease-held", held.Code);
        clock.Advance(TimeSpan.FromMinutes(6));
        var takeover = await store.ClaimSourceProducerLeaseAsync(Repository, new SourceProducerLeaseRequest("host-b:1"));
        Assert.Equal(2, takeover.Epoch);
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.PublishSourceDiffObjectAsync(Repository, Diff(lease, ["src/b.cs"], ["+b"], head: new string('f', 40))));
        Assert.Equal("stale-source-producer", stale.Code);
        await store.PublishSourceDiffObjectAsync(Repository, Diff(takeover, ["src/b.cs"], ["+b"], head: new string('f', 40)));
    }

    [Fact]
    public async Task Path_history_and_blame_round_trip_by_head_and_path()
    {
        using var temp = new TempDirectory();
        var store = await StoreAsync(temp.Path, new ManualTimeProvider(Start));
        var lease = await store.ClaimSourceProducerLeaseAsync(Repository, new SourceProducerLeaseRequest("host-a:1"));
        var entry = new SourcePathHistoryEntry(HeadSha, Start.UtcDateTime, "Dev <dev@example.invalid>", "feat: a", "feat: a\n\nRun-Index: 2");

        await store.PublishSourcePathHistoryAsync(Repository, new SourcePathHistoryPublishRequest(
            lease.ProducerId, lease.Epoch, HeadSha, "src/a.cs", [entry], false));
        await store.PublishSourceBlameAsync(Repository, new SourceBlamePublishRequest(
            lease.ProducerId, lease.Epoch, HeadSha, "src/a.cs",
            [new SourceBlameRange(1, 3, HeadSha)], [new SourceBlameCommit(HeadSha, "Dev", Start.UtcDateTime, "feat: a")]));

        var history = await store.GetSourcePathHistoryAsync(Repository, HeadSha, "src/a.cs");
        Assert.Equal(entry, Assert.Single(history!.Entries));
        var blame = await store.GetSourceBlameAsync(Repository, HeadSha, "src/a.cs");
        Assert.Equal(new SourceBlameRange(1, 3, HeadSha), Assert.Single(blame!.Ranges));
        Assert.Null(await store.GetSourcePathHistoryAsync(Repository, HeadSha, "src/missing.cs"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.GetSourcePathHistoryAsync(Repository, HeadSha, "../outside.cs"));
    }

    [Fact]
    public async Task Store_is_bounded_by_size_and_evicts_the_least_recently_read_object()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock, maximumBytes: 3_000);
        var lease = await store.ClaimSourceProducerLeaseAsync(Repository, new SourceProducerLeaseRequest("host-a:1"));
        var heads = Enumerable.Range(0, 3).Select(index => new string((char)('1' + index), 40)).ToArray();
        var body = Enumerable.Range(0, 10).Select(line => $"+line {line} {new string('x', 80)}").ToArray();

        await store.PublishSourceDiffObjectAsync(Repository, Diff(lease, ["a"], body, head: heads[0]));
        clock.Advance(TimeSpan.FromHours(2));
        await store.PublishSourceDiffObjectAsync(Repository, Diff(lease, ["a"], body, head: heads[1]));
        clock.Advance(TimeSpan.FromHours(2));
        // Reading the first object refreshes its last-read time, so the
        // second one is now the least recently read.
        Assert.NotNull(await Read(store, heads[0]));
        clock.Advance(TimeSpan.FromHours(2));
        await store.PublishSourceDiffObjectAsync(Repository, Diff(lease, ["a"], body, head: heads[2]));

        Assert.NotNull(await Read(store, heads[0]));
        Assert.Null(await Read(store, heads[1]));
        Assert.NotNull(await Read(store, heads[2]));
    }

    [Fact]
    public async Task Reachability_keeps_positive_facts_and_drops_negative_facts_when_the_head_moves()
    {
        using var temp = new TempDirectory();
        var store = await StoreAsync(temp.Path, new ManualTimeProvider(Start));
        var lease = await store.ClaimSourceProducerLeaseAsync(Repository, new SourceProducerLeaseRequest("host-a:1"));
        var developOne = new string('1', 40);
        var developTwo = new string('2', 40);
        var released = new string('3', 40);
        await store.PublishSourceReachabilityAsync(Repository, new SourceReachabilityPublishRequest(
            lease.ProducerId, lease.Epoch,
            [new SourceRefHead("develop", developOne)],
            [
                new SourceReachabilityFact(BaseSha, "develop", true, developOne),
                new SourceReachabilityFact(HeadSha, "develop", false, developOne),
            ]));

        var first = await store.GetSourceReachabilityAsync(Repository, [BaseSha, HeadSha], ["develop", "main"]);
        Assert.Equal(2, first.Facts.Count);
        Assert.Equal(
            [new SourceReachabilityPair(BaseSha, "main"), new SourceReachabilityPair(HeadSha, "main")],
            first.Missing);

        await store.PublishSourceReachabilityAsync(Repository, new SourceReachabilityPublishRequest(
            lease.ProducerId, lease.Epoch, [new SourceRefHead("develop", developTwo), new SourceRefHead("main", released)], []));
        var moved = await store.GetSourceReachabilityAsync(Repository, [BaseSha, HeadSha], ["develop"]);
        Assert.True(Assert.Single(moved.Facts).Contained);
        Assert.Equal(new SourceReachabilityPair(HeadSha, "develop"), Assert.Single(moved.Missing));
        Assert.Equal(developTwo, Assert.Single(moved.Heads).Sha);
    }

    [Fact]
    public async Task Demands_are_deduplicated_completed_and_requeued_after_completion()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        var demand = new SourceFactDemandRequest(SourceFactKinds.Diff, "review-material-miss", BaseSha, HeadSha);

        var first = await store.RecordSourceFactDemandAsync(Repository, demand);
        var again = await store.RecordSourceFactDemandAsync(Repository, demand with { Reason = "card-open" });
        Assert.Equal(first.DemandId, again.DemandId);
        Assert.Single(await store.ListSourceFactDemandsAsync(Repository, 10));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RecordSourceFactDemandAsync(
            Repository, new SourceFactDemandRequest(SourceFactKinds.PathHistory, "card-open", HeadSha: HeadSha)));

        var lease = await store.ClaimSourceProducerLeaseAsync(Repository, new SourceProducerLeaseRequest("host-a:1"));
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.CompleteSourceFactDemandAsync(
            Repository, first.DemandId, new SourceFactDemandCompleteRequest("host-b:1", lease.Epoch, true)));
        var failed = await store.CompleteSourceFactDemandAsync(
            Repository, first.DemandId, new SourceFactDemandCompleteRequest(lease.ProducerId, lease.Epoch, false, "commits-unavailable"));
        Assert.Equal((1, "commits-unavailable"), (failed.Attempts, failed.LastError));
        await store.CompleteSourceFactDemandAsync(
            Repository, first.DemandId, new SourceFactDemandCompleteRequest(lease.ProducerId, lease.Epoch, true));
        Assert.Empty(await store.ListSourceFactDemandsAsync(Repository, 10));

        clock.Advance(TimeSpan.FromHours(1));
        var requeued = await store.RecordSourceFactDemandAsync(Repository, demand);
        Assert.Equal(first.DemandId, requeued.DemandId);
        Assert.Equal(0, requeued.Attempts);
        Assert.Single(await store.ListSourceFactDemandsAsync(Repository, 10));
    }

    [Fact]
    public async Task Review_material_reads_report_p95_per_source()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        foreach (var elapsed in new long[] { 12, 15, 18, 20, 25, 30, 35, 40, 45, 60 })
            await store.RecordSourceReviewMaterialReadAsync(Repository,
                new SourceReviewMaterialReadReport(BaseSha, HeadSha, SourceReviewMaterialSources.Object, elapsed));
        await store.RecordSourceReviewMaterialReadAsync(Repository,
            new SourceReviewMaterialReadReport(BaseSha, HeadSha, SourceReviewMaterialSources.Fallback, 900, "object-missing"));

        var timing = await store.GetSourceReviewMaterialTimingAsync(Repository, Start.UtcDateTime.AddHours(-1));
        Assert.Equal((11, 10, 1), (timing.Samples, timing.ObjectReads, timing.FallbackReads));
        Assert.Equal(60, timing.ObjectP95Milliseconds);
        Assert.Equal(900, timing.FallbackP95Milliseconds);
        Assert.Equal(900, timing.P95Milliseconds);
        await Assert.ThrowsAsync<ArgumentException>(() => store.RecordSourceReviewMaterialReadAsync(Repository,
            new SourceReviewMaterialReadReport(BaseSha, HeadSha, "cache", 1)));
    }

    [Fact]
    public async Task A_unique_project_name_addresses_the_project_id()
    {
        using var temp = new TempDirectory();
        var store = await StoreAsync(temp.Path, new ManualTimeProvider(Start));
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "SourceProject", "SP"), "test", default);
        var lease = await store.ClaimSourceProducerLeaseAsync(project.ProjectId, new SourceProducerLeaseRequest("host-a:1"));
        await store.PublishSourcePathHistoryAsync(project.ProjectId, new SourcePathHistoryPublishRequest(
            lease.ProducerId, lease.Epoch, HeadSha, "README.md", [], false));

        var byName = await store.GetSourcePathHistoryAsync("SourceProject", HeadSha, "README.md");
        Assert.Equal(project.ProjectId, byName!.RepositoryId);
    }

    private static async Task<SourceDiffObjectDto?> Read(TaskServerStore store, string head)
        => await store.GetSourceDiffObjectAsync(
            Repository, BaseSha, head, SourceDiffObjectOptions.DefaultReview, SourceDiffObjectOptions.RendererVersion);

    private static SourceDiffObjectPublishRequest Diff(
        SourceProducerLeaseDto lease, string[] files, string[] lines, string? head = null)
        => new(
            lease.ProducerId, lease.Epoch, BaseSha, head ?? HeadSha,
            SourceDiffObjectOptions.DefaultReview, SourceDiffObjectOptions.RendererVersion,
            files, lines, files.Length, lines.Length,
            SourceDiffObjectOptions.ReviewMaximumFiles, SourceDiffObjectOptions.ReviewMaximumLines,
            SourceDiffMaterialDigest.Compute(files, lines, files.Length, lines.Length,
                SourceDiffObjectOptions.ReviewMaximumFiles, SourceDiffObjectOptions.ReviewMaximumLines));

    private static async Task<TaskServerStore> StoreAsync(string path, TimeProvider clock, long? maximumBytes = null)
    {
        var options = new TaskServerOptions { DataDirectory = path };
        if (maximumBytes is { } cap) options.SourceFactsMaxStoredBytes = cap;
        var store = new TaskServerStore(Options.Create(options), clock);
        await store.InitializeAsync();
        return store;
    }
}
