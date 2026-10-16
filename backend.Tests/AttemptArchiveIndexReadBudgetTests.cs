using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3109: the card detail reads archived attempts on every poll. These
/// tests pin which archive shards such a read opens, and that the indexed read
/// answers exactly what a scan of every shard would.
/// </summary>
public sealed class AttemptArchiveIndexReadBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "attempt-archive-index-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, int> _opens = new(StringComparer.Ordinal);

    private string Metadata => Path.Combine(_root, ".metadata");

    [Fact]
    public void A_key_without_archives_opens_no_shard_and_a_key_in_two_of_five_opens_exactly_those_two()
    {
        WriteShard("01", Run("run-b1", "AGT-B"));
        WriteShard("02", Run("run-a1", "AGT-A"), Run("run-c1", "AGT-C"));
        WriteShard("03", Run("run-c2", "AGT-C"));
        WriteShard("04", Run("run-d1", "AGT-D"));
        WriteShard("05", Run("run-a2", "AGT-A"));
        var service = NewService();

        Assert.Equal(5, service.WarmArchiveIndex());
        Assert.Equal(5, _opens.Count);
        _opens.Clear();

        var none = service.GetTaskProjection("AGT-3085", includeArchived: true);
        Assert.True(none.LegacyTask);
        Assert.Empty(_opens);

        var history = service.GetTaskProjection("AGT-A", includeArchived: true);
        Assert.Equal(["run-a1", "run-a2"], history.RunAttempts.Select(run => run.AttemptId));
        Assert.Equal(
            new Dictionary<string, int> { [ShardName("02")] = 1, [ShardName("05")] = 1 },
            _opens);

        _opens.Clear();
        Assert.Equal(2, service.GetIndexedDeliveryRefs(["AGT-A", "AGT-3085"], includeArchived: true).Count);
        Assert.Empty(_opens);
    }

    [Fact]
    public void Archived_attempt_ids_open_only_their_shard_and_reuse_a_bounded_cached_read()
    {
        WriteShard("01", Run("run-a", "AGT-A"), Review("review-a", "AGT-A", "run-a", 1));
        WriteShard("02", Run("run-b", "AGT-B"));
        var service = NewService();
        Assert.Equal(2, service.WarmArchiveIndex());
        _opens.Clear();

        Assert.Equal("run-a", service.GetRun("run-a")!.AttemptId);
        Assert.Equal("review-a", service.GetReview("review-a")!.AttemptId);
        Assert.Equal("run-a", service.GetRun("RUN-A")!.AttemptId);
        Assert.Null(service.GetReview("review-missing"));
        Assert.Equal(new Dictionary<string, int> { [ShardName("01")] = 1 }, _opens);

        var projection = service.GetTaskProjection("AGT-A", includeArchived: true);
        Assert.Single(projection.RunAttempts);
        Assert.Single(projection.ReviewAttempts);
        Assert.Equal(new Dictionary<string, int> { [ShardName("01")] = 1 }, _opens);
    }

    [Fact]
    public void A_read_that_beats_the_warmup_builds_the_index_once_and_the_next_read_opens_nothing()
    {
        WriteShard("01", Run("run-a1", "AGT-A"));
        WriteShard("02", Run("run-b1", "AGT-B"));
        var service = NewService();

        Assert.True(service.GetTaskProjection("AGT-3085", includeArchived: true).LegacyTask);
        Assert.Equal(2, _opens.Count);
        _opens.Clear();

        Assert.Equal(2, service.WarmArchiveIndex());
        Assert.True(service.GetTaskProjection("AGT-3085", includeArchived: true).LegacyTask);
        Assert.Empty(_opens);
    }

    [Fact]
    public void Indexed_projection_equals_a_scan_of_every_shard_with_duplicate_ids_and_unnormalized_keys()
    {
        // The same attempt id in two shards (an interrupted rotation) resolves
        // to the copy in the earliest shard, as the full scan did.
        WriteShard("01",
            Run("run-1", "agt-a", createdHour: 1, reason: "first copy"),
            Run("run-x", "AGT-X", createdHour: 1));
        WriteShard("02", Run("run-y", "AGT-Y", createdHour: 2));
        WriteShard("03",
            Run("run-1", "AGT-A", createdHour: 1, reason: "second copy"),
            Run("run-2", "AGT-A", createdHour: 3, sha: new string('c', 40)));
        WriteShard("04", Run("run-0", "Agt-A", createdHour: 0), Review("review-1", "AGT-A", "run-2", createdHour: 4));
        var service = NewService();
        service.WarmArchiveIndex();

        var projection = service.GetTaskProjection("  AGT-A ", includeArchived: true);

        var scanned = ScanEveryShard("AGT-A");
        Assert.Equal(scanned.Runs, projection.RunAttempts.Select(run => (run.AttemptId, run.TerminalReason)));
        Assert.Equal(scanned.Reviews, projection.ReviewAttempts.Select(review => review.AttemptId));
        Assert.Equal("first copy", projection.RunAttempts.Single(run => run.AttemptId == "run-1").TerminalReason);
        Assert.Equal(
            [new string('c', 40), new string('a', 40), new string('a', 40)],
            service.GetIndexedDeliveryRefs([" agt-a "], includeArchived: true)
                .Select(item => item.ResultSha));
    }

    [Fact]
    public void An_archive_write_updates_the_index_and_a_deleted_shard_is_dropped()
    {
        var service = NewService(terminalRetentionCount: 1);
        Assert.Equal(0, service.WarmArchiveIndex());

        var first = SupersededRun(service, "AGT-1");
        var firstShard = CurrentHourShard();
        Assert.Equal(first, service.GetRun(first)!.AttemptId);
        _opens.Clear();

        Assert.True(service.GetTaskProjection("AGT-9", includeArchived: true).LegacyTask);
        Assert.Empty(_opens);
        Assert.Contains(first,
            service.GetTaskProjection("AGT-1", includeArchived: true).RunAttempts.Select(run => run.AttemptId));
        Assert.Empty(_opens);

        _time.Advance(TimeSpan.FromHours(1));
        var second = SupersededRun(service, "AGT-2");
        var secondShard = CurrentHourShard();
        _opens.Clear();
        Assert.Contains(second,
            service.GetTaskProjection("AGT-2", includeArchived: true).RunAttempts.Select(run => run.AttemptId));
        Assert.Equal(new Dictionary<string, int> { [secondShard] = 1 }, _opens);

        File.Delete(Path.Combine(Metadata, firstShard));
        _opens.Clear();
        Assert.DoesNotContain(first,
            service.GetTaskProjection("AGT-1", includeArchived: true).RunAttempts.Select(run => run.AttemptId));
        Assert.Empty(_opens);

        var restarted = NewService(terminalRetentionCount: 1);
        _opens.Clear();
        Assert.Equal(1, restarted.WarmArchiveIndex());
        Assert.Equal(new Dictionary<string, int> { [secondShard] = 1 }, _opens);
    }

    [Fact]
    public void A_failed_warmup_leaves_reads_failing_closed_until_the_shard_is_readable()
    {
        WriteShard("01", Run("run-a1", "AGT-A"));
        var damaged = Path.Combine(Metadata, ShardName("02"));
        File.WriteAllText(damaged, "{ damaged shard");
        var service = NewService();

        Assert.Throws<InvalidDataException>(() => service.WarmArchiveIndex());
        Assert.Throws<InvalidDataException>(() => service.GetTaskProjection("AGT-A", includeArchived: true));

        File.Delete(damaged);
        WriteShard("02", Run("run-b1", "AGT-B"));
        Assert.Single(service.GetTaskProjection("AGT-A", includeArchived: true).RunAttempts);
        _opens.Clear();
        Assert.Equal(2, service.WarmArchiveIndex());
        Assert.Empty(_opens);
    }

    [Fact]
    public async Task The_warmup_service_builds_the_index_and_logs_an_unreadable_shard_instead_of_throwing()
    {
        WriteShard("01", Run("run-a1", "AGT-A"));
        var service = NewService();
        using (var warmup = new AttemptArchiveIndexWarmupService(
                   service, NullLogger<AttemptArchiveIndexWarmupService>.Instance))
        {
            await warmup.StartAsync(CancellationToken.None);
            await warmup.ExecuteTask!;
        }
        _opens.Clear();
        Assert.True(service.GetTaskProjection("AGT-3085", includeArchived: true).LegacyTask);
        Assert.Empty(_opens);

        File.WriteAllText(Path.Combine(Metadata, ShardName("02")), "{ damaged shard");
        using var failing = new AttemptArchiveIndexWarmupService(
            NewService(), NullLogger<AttemptArchiveIndexWarmupService>.Instance);
        await failing.StartAsync(CancellationToken.None);
        await failing.ExecuteTask!;
        Assert.True(failing.ExecuteTask.IsCompletedSuccessfully);
    }

    private AttemptAuthorityService NewService(int? terminalRetentionCount = null)
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["AttemptAuthority:TerminalRetentionCount"] = terminalRetentionCount?.ToString(),
        }).Build();
        return new AttemptAuthorityService(
            config,
            NullLogger<AttemptAuthorityService>.Instance,
            () => _time.GetUtcNow().UtcDateTime,
            writer: null,
            readArchiveText: CountingRead);
    }

    private string CountingRead(string path)
    {
        var name = Path.GetFileName(path);
        _opens[name] = _opens.GetValueOrDefault(name) + 1;
        return File.ReadAllText(path);
    }

    private string SupersededRun(AttemptAuthorityService service, string taskKey)
    {
        string Settled(string? previous)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            var stamp = _time.GetUtcNow().UtcDateTime.ToString("HHmm");
            var run = service.AcquireRun(
                taskKey, "PROJ-1", previous, "runner", "host", 300, $"{taskKey}-{stamp}-create").RunAttempt!;
            _time.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(AttemptWriteStatus.Accepted, service.SettleRun(new SettleRunAttemptRequest
            {
                Write = new AttemptWriteReference(
                    run.AttemptId, run.LastFence, run.AuthorityEpoch, $"{taskKey}-{stamp}-settle"),
                Outcome = "done",
                ResultSha = "sha-" + taskKey,
            }).Status);
            return run.AttemptId;
        }

        var superseded = Settled(null);
        Settled(superseded);
        return superseded;
    }

    private static string ShardName(string hour) => $"attempt-authority.archive-2026-10-12-{hour}.json";

    private string CurrentHourShard() => $"attempt-authority.archive-{_time.GetUtcNow().UtcDateTime:yyyy-MM-dd-HH}.json";

    private void WriteShard(string hour, params object[] records)
    {
        Directory.CreateDirectory(Metadata);
        File.WriteAllText(Path.Combine(Metadata, ShardName(hour)), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            archivedAt = _time.GetUtcNow().UtcDateTime,
            runAttempts = records.OfType<RunFixture>().Select(run => run.Json).ToArray(),
            reviewAttempts = records.OfType<ReviewFixture>().Select(review => review.Json).ToArray(),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private RunFixture Run(string attemptId, string taskKey, int createdHour = 0, string? reason = null, string? sha = null)
    {
        var createdAt = _time.GetUtcNow().UtcDateTime.AddDays(-2).AddHours(createdHour);
        sha ??= new string('a', 40);
        return new RunFixture(new
        {
            attemptId,
            taskKey,
            repositoryId = "PROJ-1",
            state = AttemptLifecycleState.Completed,
            createdAt,
            terminalAt = createdAt.AddMinutes(30),
            terminalReason = reason,
            resultEnvelope = new
            {
                repositoryId = "PROJ-1",
                sourceRunAttemptId = attemptId,
                baseSha = sha,
                resultSha = sha,
                immutableRemoteRef = $"refs/heads/agent-studio/results/{attemptId}/fence-1/{sha}",
                artifactManifestDigest = "manifest",
            },
        });
    }

    private ReviewFixture Review(string attemptId, string taskKey, string sourceRunAttemptId, int createdHour)
    {
        var createdAt = _time.GetUtcNow().UtcDateTime.AddDays(-2).AddHours(createdHour);
        return new ReviewFixture(new
        {
            attemptId,
            taskKey,
            repositoryId = "PROJ-1",
            sourceRunAttemptId,
            state = AttemptLifecycleState.Completed,
            createdAt,
            terminalAt = createdAt.AddMinutes(30),
        });
    }

    /// <summary>The pre-index read: every shard in ordinal order, first copy of an attempt id wins.</summary>
    private (List<(string, string?)> Runs, List<string> Reviews) ScanEveryShard(string taskKey)
    {
        var runs = new List<(string Id, string? Reason, DateTime CreatedAt)>();
        var reviews = new List<(string Id, DateTime CreatedAt)>();
        foreach (var path in Directory.GetFiles(Metadata, "attempt-authority.archive-*.json").Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var run in document.RootElement.GetProperty("runAttempts").EnumerateArray())
                if (string.Equals(run.GetProperty("taskKey").GetString(), taskKey, StringComparison.OrdinalIgnoreCase))
                    runs.Add((run.GetProperty("attemptId").GetString()!,
                        run.GetProperty("terminalReason").GetString(),
                        run.GetProperty("createdAt").GetDateTime()));
            foreach (var review in document.RootElement.GetProperty("reviewAttempts").EnumerateArray())
                if (string.Equals(review.GetProperty("taskKey").GetString(), taskKey, StringComparison.OrdinalIgnoreCase))
                    reviews.Add((review.GetProperty("attemptId").GetString()!, review.GetProperty("createdAt").GetDateTime()));
        }
        return (
            runs.DistinctBy(run => run.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(run => run.CreatedAt).Select(run => (run.Id, run.Reason)).ToList(),
            reviews.DistinctBy(review => review.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(review => review.CreatedAt).Select(review => review.Id).ToList());
    }

    private sealed record RunFixture(object Json);

    private sealed record ReviewFixture(object Json);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
