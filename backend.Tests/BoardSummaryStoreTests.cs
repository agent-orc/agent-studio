using System.Text;

using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The persisted board store (Git facts S5): a round trip, project
/// replacement and removal, and recovery from a file it cannot use.
/// </summary>
public sealed class BoardSummaryStoreTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "atp-board-summaries-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, BoardSummaryStoreSchema.FileName);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    private BoardSummaryStore Store() => new(DatabasePath, NullLogger.Instance);

    private static BoardSummaryProject Project(string name, long generation, params string[] ids)
    {
        var cards = ids.Select((id, index) => new BoardSummaryCard(name, $"/w/{name}::{id}", "/w/" + name,
            TaskStates.Ready, id.StartsWith("fx", StringComparison.Ordinal), index, 100 + index,
            Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\"}}"))).ToList();
        return new BoardSummaryProject(name, generation, At, At.AddMinutes(-3), true,
            BoardSummaryPolicy.ContentHash(cards, At.AddMinutes(-3), true), cards);
    }

    [Fact]
    public void Written_projects_load_back_with_their_cards_in_index_order()
    {
        var written = new[] { Project("alpha", 4, "a1", "fx2", "a3"), Project("beta", 4, "b1") };
        Assert.True(Store().Write(written, []));

        var loaded = Store().Load();

        Assert.Equal(["alpha", "beta"], loaded.Select(project => project.ProjectName));
        var alpha = loaded[0];
        Assert.Equal(4, alpha.IndexGeneration);
        Assert.Equal(At, alpha.PersistedAt);
        Assert.Equal(At.AddMinutes(-3), alpha.GitStateAt);
        Assert.True(alpha.GitStale);
        Assert.Equal(written[0].ContentHash, alpha.ContentHash);
        Assert.Equal(written[0].Cards.Select(card => card.TaskKey), alpha.Cards.Select(card => card.TaskKey));
        Assert.Equal([false, true, false], alpha.Cards.Select(card => card.Fixture));
        Assert.Equal([100, 101, 102], alpha.Cards.Select(card => card.ScanRank));
        Assert.Equal(written[0].Cards[1].Summary, alpha.Cards[1].Summary);
    }

    [Fact]
    public void A_write_replaces_changed_projects_and_deletes_removed_ones_only()
    {
        var store = Store();
        store.Write([Project("alpha", 1, "a1", "a2"), Project("beta", 1, "b1"), Project("gamma", 1, "g1")], []);

        Assert.True(store.Write([Project("alpha", 2, "a3")], ["gamma"]));

        var loaded = store.Load();
        Assert.Equal(["alpha", "beta"], loaded.Select(project => project.ProjectName));
        Assert.Equal(2, loaded[0].IndexGeneration);
        Assert.Equal(["/w/alpha::a3"], loaded[0].Cards.Select(card => card.TaskKey));
        Assert.Equal(1, loaded[1].IndexGeneration);
        Assert.Equal(["/w/beta::b1"], loaded[1].Cards.Select(card => card.TaskKey));
    }

    [Fact]
    public void An_absent_or_disabled_store_loads_nothing_and_a_read_creates_no_file()
    {
        Assert.Empty(Store().Load());
        Assert.False(File.Exists(DatabasePath));

        var disabled = new BoardSummaryStore((string?)null, NullLogger.Instance);
        Assert.Empty(disabled.Load());
        Assert.False(disabled.Write([Project("alpha", 1, "a1")], []));
    }

    [Fact]
    public void A_corrupt_file_loads_nothing_and_is_discarded_by_the_next_write()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(DatabasePath, "this is not a SQLite database, it is a board that went wrong");

        Assert.Empty(Store().Load());
        Assert.True(Store().Write([Project("alpha", 3, "a1")], []));

        Assert.Equal(["/w/alpha::a1"], Store().Load().Single().Cards.Select(card => card.TaskKey));
    }

    [Fact]
    public void Another_schema_version_loads_nothing_and_is_rebuilt_by_the_next_write()
    {
        Store().Write([Project("alpha", 1, "a1")], []);
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {BoardSummaryStoreSchema.Version + 1}";
            command.ExecuteNonQuery();
        }

        Assert.Empty(Store().Load());
        Assert.True(Store().Write([Project("beta", 2, "b1")], []));

        Assert.Equal(["beta"], Store().Load().Select(project => project.ProjectName));
    }
}
