using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3322 (Git facts S2): the SQLite Git fact store. One transaction per
/// repository generation, a producer epoch fence, a crash image that keeps
/// the previous generation, and quick_check recovery of a corrupt file.
/// </summary>
public sealed class GitFactStoreTests : IDisposable
{
    private readonly string _root = AgentStudio.TestSupport.TestTempRoot.Create("git-fact-store");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private string DbPath => Path.Combine(_root, "state", GitFactStoreSchema.FileName);

    private GitFactStore NewStore(string? path = null)
        => new(path ?? DbPath, NullLogger.Instance, _clock);

    private GitFactPublication Publication(string repo, long epoch, string fingerprint, params string[] cards)
        => new(
            new GitRepositoryFacts(repo, "/repo/" + repo, "/jobs/" + repo,
                new GitEffectiveConfig("config-signature", "https://example.invalid/origin.git",
                    [new GitConfigFileStamp("/repo/.git/config", "ABC")], ["/home/user/.gitconfig"], false), 2),
            new GitRefGenerationDraft(new Dictionary<string, string> { ["refs/heads/main"] = new('1', 40) },
                fingerprint, 2, "develop\0", "tasks-" + fingerprint, "input-" + fingerprint, "producer-test", epoch,
                _clock.GetUtcNow()),
            cards.Select(card => new CardGitFactRow(card, "sig-" + card, "{\"V\":1}", null)).ToArray());

    [Fact]
    public void Open_CreatesExactlyTheSchemaTablesInWalMode()
    {
        var store = NewStore();
        Assert.Equal("created", store.Open());
        Assert.Equal("opened", store.Open());

        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        connection.Open();
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        var names = new List<string>();
        using (var reader = tables.ExecuteReader())
            while (reader.Read()) names.Add(reader.GetString(0));
        Assert.Equal(GitFactStoreSchema.Tables.Order(StringComparer.Ordinal), names);
        using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", mode.ExecuteScalar() as string);
    }

    [Fact]
    public void FileFacts_SeparateCardsAndInvalidateOnRefMove_AndResultRowsSurviveRestart()
    {
        var store = NewStore();
        store.Open();
        var epoch = store.AcquireProducerEpoch("proj", "producer-test");
        store.Publish(Publication("proj", epoch, "f1", "a"));
        var at = _clock.GetUtcNow().UtcDateTime;
        var entry = new TaskFileHistoryEntry(new string('a', 40), at, 2, "accept", "review", "agent",
            new TaskFileVersionProvenance(TaskFileSources.Workspace, "status.md", "review=pass", null));
        store.WritePathHistory(new GitPathHistoryFact("proj", TaskFileSources.Workspace,
            "/jobs/proj/card-a", "status.md", [entry],
            [new GitFileObjectFact(entry.Sha, "status.md", "first", "hash-a", false)]));
        store.WritePathHistory(new GitPathHistoryFact("proj", TaskFileSources.Workspace,
            "/jobs/proj/card-b", "status.md", [entry with { Message = "other" }],
            [new GitFileObjectFact(entry.Sha, "status.md", "second", "hash-b", false)]));
        store.UpsertResultVersion(new GitResultVersionFact("proj::card-a", "local-0001", "run attempt #2",
            "run-attempt", "review", 2, "accept", "review=pass", at, "result-hash", null,
            "task-folder", "results/history/0001/status.md", "accept", "generic", 1));

        var reopened = NewStore();
        Assert.Equal("opened", reopened.Open());
        Assert.Equal("first", reopened.ReadFileObject("proj", TaskFileSources.Workspace,
            "/jobs/proj/card-a", entry.Sha, "status.md")!.Content);
        Assert.Equal("second", reopened.ReadFileObject("proj", TaskFileSources.Workspace,
            "/jobs/proj/card-b", entry.Sha, "status.md")!.Content);
        Assert.Equal("other", Assert.Single(reopened.ReadPathHistory("proj", TaskFileSources.Workspace,
            "status.md", "/jobs/proj/card-b")!.Entries).Message);
        Assert.Equal("local-0001", Assert.Single(reopened.ReadResultVersions("proj::card-a")).VersionId);
        reopened.Publish(Publication("proj", epoch, "f2", "a"));
        Assert.Null(reopened.ReadPathHistory("proj", TaskFileSources.Workspace,
            "status.md", "/jobs/proj/card-a"));
    }

    [Fact]
    public void Publish_ThenLoadCurrent_RoundTripsTheGenerationRepositoryAndCards()
    {
        var store = NewStore();
        store.Open();
        var epoch = store.AcquireProducerEpoch("proj", "producer-test");
        Assert.Equal(1, epoch);

        Assert.Equal(1, store.Publish(Publication("proj", epoch, "f1", "a", "b")).Generation);
        var second = store.Publish(Publication("proj", epoch, "f2", "a"));
        Assert.Equal(GitFactPublishOutcome.Published, second.Outcome);
        Assert.Equal(2, second.Generation);

        var loaded = Assert.IsType<GitFactSnapshot>(store.LoadCurrent("proj"));
        Assert.Equal(2, loaded.Generation.Generation);
        Assert.Equal("f2", loaded.Generation.RefFingerprint);
        Assert.Equal(new('1', 40), loaded.Generation.RefObjectIds["refs/heads/main"]);
        Assert.Equal(_clock.GetUtcNow(), loaded.Generation.PublishedAt);
        Assert.Equal(epoch, loaded.Generation.ProducerEpoch);
        Assert.Equal("/repo/proj", loaded.Repository.CanonicalRoot);
        Assert.Equal("/jobs/proj", loaded.Repository.WorkspaceRoot);
        Assert.Equal(2, loaded.Repository.ConfigGeneration);
        Assert.Equal("https://example.invalid/origin.git", loaded.Repository.Config!.OriginUrl);
        Assert.Equal(new[] { "/home/user/.gitconfig" }, loaded.Repository.Config.AbsentSources);
        // Card rows are replaced per publish.
        Assert.Equal("a", Assert.Single(loaded.Cards).TaskKey);
        Assert.Null(store.LoadCurrent("other"));
        Assert.Equal(1, CountRows("SELECT COUNT(*) FROM git_ref_generations WHERE state = 'current'"));
    }

    [Fact]
    public void Publish_WithAnOlderProducerEpoch_IsRejectedAndWritesNothing()
    {
        var store = NewStore();
        store.Open();
        var stale = store.AcquireProducerEpoch("proj", "producer-old");
        Assert.Equal(GitFactPublishOutcome.Published, store.Publish(Publication("proj", stale, "f1", "a")).Outcome);
        var current = store.AcquireProducerEpoch("proj", "producer-new");
        Assert.True(current > stale);

        var rejected = store.Publish(Publication("proj", stale, "late", "late-card"));

        Assert.Equal(GitFactPublishOutcome.RejectedStaleEpoch, rejected.Outcome);
        Assert.Equal(current, rejected.StoredEpoch);
        var loaded = store.LoadCurrent("proj")!;
        Assert.Equal("f1", loaded.Generation.RefFingerprint);
        Assert.Equal("a", Assert.Single(loaded.Cards).TaskKey);
        Assert.Equal(GitFactPublishOutcome.Published,
            store.Publish(Publication("proj", current, "f2", "b")).Outcome);
        // A new store instance on the same file continues the epoch sequence.
        Assert.True(NewStore().AcquireProducerEpoch("proj", "producer-restart") > current);
    }

    [Fact]
    public void CrashBeforeCommit_LeavesThePreviousGenerationCurrentAndReadable()
    {
        var store = NewStore();
        store.Open();
        var epoch = store.AcquireProducerEpoch("proj", "producer-test");
        store.Publish(Publication("proj", epoch, "good", "a", "b"));
        var image = Path.Combine(_root, "crash-image");
        Directory.CreateDirectory(image);

        // The on-disk state at the moment of a kill: the generation row, the
        // repository row and every card row are written but not committed.
        Assert.Throws<InvalidOperationException>(() => store.Publish(
            Publication("proj", epoch, "torn", Enumerable.Range(0, 500).Select(i => $"card-{i}").ToArray()),
            beforeCommit: () =>
            {
                // The shared-memory index is not durable state; SQLite rebuilds
                // it from the WAL, which holds no commit frame for this write.
                foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(DbPath)!)
                             .Where(file => !file.EndsWith("-shm", StringComparison.Ordinal)))
                    File.Copy(file, Path.Combine(image, Path.GetFileName(file)));
                throw new InvalidOperationException("process killed");
            }));

        foreach (var reopened in new[] { NewStore(), NewStore(Path.Combine(image, GitFactStoreSchema.FileName)) })
        {
            Assert.Equal("opened", reopened.Open());
            var loaded = reopened.LoadCurrent("proj")!;
            Assert.Equal(1, loaded.Generation.Generation);
            Assert.Equal("good", loaded.Generation.RefFingerprint);
            Assert.Equal(new[] { "a", "b" }, loaded.Cards.Select(card => card.TaskKey).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void CorruptFile_IsMovedAsideWithATypedHealthEventAndTheStoreIsRebuilt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllBytes(DbPath, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        var store = NewStore();
        var events = new List<GitFactStoreHealthEvent>();
        store.HealthEventRaised += events.Add;

        Assert.Equal("recovered", store.Open());

        var evt = Assert.Single(events);
        Assert.Equal(GitFactStoreHealthEvent.CorruptStoreRecovered, evt.Kind);
        Assert.StartsWith("sqlite-error-", evt.Reason, StringComparison.Ordinal);
        Assert.Same(evt, store.LastHealthEvent);
        Assert.True(File.Exists(evt.MovedTo));
        Assert.Equal(8192, new FileInfo(evt.MovedTo!).Length);
        Assert.True(store.IsAvailable);
        Assert.Null(store.LoadCurrent("proj"));
        var epoch = store.AcquireProducerEpoch("proj", "producer-test");
        Assert.Equal(GitFactPublishOutcome.Published, store.Publish(Publication("proj", epoch, "f1", "a")).Outcome);
    }

    [Fact]
    public void ForeignSchemaVersion_IsRebuiltInsteadOfMigrated()
    {
        var store = NewStore();
        store.Open();
        store.Publish(Publication("proj", store.AcquireProducerEpoch("proj", "p"), "f1", "a"));
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {GitFactStoreSchema.Version + 1}";
            command.ExecuteNonQuery();
        }

        var reopened = NewStore();
        Assert.Equal("recovered", reopened.Open());
        Assert.Equal($"schema-version-{GitFactStoreSchema.Version + 1}", reopened.LastHealthEvent!.Reason);
        Assert.Null(reopened.LoadCurrent("proj"));
    }

    [Fact]
    public void Publish_KeepsABoundedNumberOfSupersededGenerations()
    {
        var store = NewStore();
        store.Open();
        var epoch = store.AcquireProducerEpoch("proj", "producer-test");
        for (var i = 0; i < GitFactStoreSchema.RetainedGenerations + 10; i++)
            store.Publish(Publication("proj", epoch, $"f{i}", "a"));

        Assert.Equal(GitFactStoreSchema.RetainedGenerations + 1,
            CountRows("SELECT COUNT(*) FROM git_ref_generations WHERE repo_id = 'proj'"));
        Assert.Equal(GitFactStoreSchema.RetainedGenerations + 10, store.LoadCurrent("proj")!.Generation.Generation);
    }

    [Fact]
    public void Health_RoundTripsPerRepository()
    {
        var store = NewStore();
        store.Open();
        store.RecordHealth(new GitProducerHealth("proj", GitRepositoryHealthPolicy.Degraded, 4,
            _clock.GetUtcNow(), 2, "repository-degraded", 1, 12, "rev-list", 900, _clock.GetUtcNow()));

        var health = store.ReadHealth("proj")!;
        Assert.Equal(GitRepositoryHealthPolicy.Degraded, health.State);
        Assert.Equal(4, health.LastGoodGeneration);
        Assert.Equal(2, health.ConsecutiveFailures);
        Assert.Equal("repository-degraded", health.LastErrorReason);
        Assert.Equal(12, health.SpawnsPerMinute);
        Assert.Equal("rev-list", health.SlowestCommand);
    }

    [Fact]
    public void DisabledStore_IsANoOp()
    {
        var store = new GitFactStore(null, NullLogger.Instance, _clock);
        Assert.Equal("disabled", store.Open());
        Assert.False(store.IsAvailable);
        Assert.Equal(0, store.AcquireProducerEpoch("proj", "p"));
        Assert.Equal(GitFactPublishOutcome.StoreUnavailable, store.Publish(Publication("proj", 1, "f", "a")).Outcome);
        Assert.Null(store.LoadCurrent("proj"));
    }

    [Fact]
    public void StorePath_DefaultsToTheTaskRepositoryMetadataDirectory()
    {
        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        Assert.Equal(Path.Combine(_root, ".metadata", GitFactStoreSchema.FileName), GitFactStore.ResolvePath(configured));
        configured[GitFactStore.StorePathKey] = Path.Combine(_root, "custom.db");
        Assert.Equal(Path.Combine(_root, "custom.db"), GitFactStore.ResolvePath(configured));
        Assert.Null(GitFactStore.ResolvePath(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void CardCodec_RoundTripsEveryPersistedFactAndTheTypedCardError()
    {
        var good = new TaskInfo { TaskKey = "good", Id = "good", WatchPath = "/jobs", FolderPath = "/jobs/good" };
        var failed = new TaskInfo { TaskKey = "failed", Id = "failed", WatchPath = "/jobs", FolderPath = "/jobs/failed" };
        var empty = new TaskInfo { TaskKey = "empty", Id = "empty", WatchPath = "/jobs", FolderPath = "/jobs/empty" };
        var projection = new TaskListGitProjection(
            new Dictionary<string, TaskMergeSignal> { ["good"] = new() { Branch = "task/good", InIntegration = true, IntegrationSha = "abc" } },
            new Dictionary<string, TaskIntegrationStatus> { ["good"] = new() { Status = IntegrationStatuses.NoBranch, Detail = "d",
                Failure = new TaskIntegrationFailure { Code = "c" } } },
            new Dictionary<string, TaskPublishSignal> { ["good"] = new() { TargetIds = ["t1"], Labels = ["L"] } },
            new Dictionary<string, TaskTestRunEvidence> { ["good"] = new() { RunId = "run", Sources = [new() { Kind = "k", Id = "i" }] } },
            new Dictionary<string, AgentStudio.Review.ReviewProjectionView>
            {
                ["good"] = new() { Rounds = 2, LatestOutcome = "pass", RoundBudget = new(1, 3, null),
                    BlockingAspects = [new() { Aspect = "a", Reason = "r" }] },
            },
            Commits: new Dictionary<string, IReadOnlyList<TaskCommitInfo>> { ["good"] = [new() { Sha = "abc", Message = "m" }] },
            Signatures: new Dictionary<string, string> { ["good"] = "sig-good", ["failed"] = "sig-failed" },
            Unavailable: new HashSet<string> { "failed" });

        var rows = CardGitFactsCodec.ToRows(projection, [good, failed, empty]);
        var restored = CardGitFactsCodec.ToProjection(rows, out var unreadable);

        Assert.Equal(0, unreadable);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(projection.ForTask("good")),
            System.Text.Json.JsonSerializer.Serialize(restored.ForTask("good")));
        Assert.Equal("sig-good", restored.TaskSignatures["good"]);
        Assert.Equal(TaskGitSignature.For(empty), restored.TaskSignatures["empty"]);
        Assert.Equal(new[] { "failed" }, restored.UnavailableTasks);
        Assert.Equal(CardGitFactsCodec.UnavailableCode, rows.Single(row => row.TaskKey == "failed").ErrorCode);
        Assert.Null(rows.Single(row => row.TaskKey == "empty").Facts);
        Assert.All(restored.TaskSubjectVersions.Values, version => Assert.Equal(0, version));

        var foreign = CardGitFactsCodec.ToProjection([rows[0] with { Facts = "{\"V\":99}" },
            rows[1] with { Facts = "not json" }], out unreadable);
        Assert.Equal(2, unreadable);
        Assert.Empty(foreign.TaskSignatures);
    }

    private int CountRows(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
