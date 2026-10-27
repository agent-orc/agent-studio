namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// The one schema of the persisted slim board (docs/git-facts-architecture,
/// slice S5): the last published board card summaries per project, with the
/// task index generation they were built from. The backend keeps these tables
/// in <c>board-summaries.db</c> next to <c>git-facts.db</c>; a Task Server
/// board read model (AGT-3317) can create the same tables by executing
/// <see cref="CreateStatements"/>.
/// <para>
/// Every row is derived from task folders and published facts, so a version
/// mismatch or a corrupt file is answered by discarding the file, never by a
/// partial migration. A missing board only costs the cold-start wait.
/// </para>
/// </summary>
public static class BoardSummaryStoreSchema
{
    /// <summary>Stored in <c>PRAGMA user_version</c>. A different value discards the store.</summary>
    public const int Version = 1;

    /// <summary>File name in the backend state directory.</summary>
    public const string FileName = "board-summaries.db";

    public const string Projects = "board_summary_projects";
    public const string Cards = "board_summary_cards";

    public static IReadOnlyList<string> CreateStatements { get; } =
    [
        // One row per project: the index generation the cards were built from,
        // when they were persisted, the Git freshness stamp the board carried
        // and a digest of the card rows, so an unchanged project is not rewritten.
        """
        CREATE TABLE IF NOT EXISTS board_summary_projects (
          project_name TEXT NOT NULL PRIMARY KEY,
          index_generation INTEGER NOT NULL,
          persisted_at INTEGER NOT NULL,
          git_state_at INTEGER,
          git_stale INTEGER NOT NULL DEFAULT 0,
          content_hash TEXT NOT NULL,
          card_count INTEGER NOT NULL
        )
        """,
        // Replaced as a set with every write of its project. summary is the
        // serialized board card (BoardCardSummary); lane_rank orders it inside
        // its lane under the project's sort, scan_rank inside the list route.
        """
        CREATE TABLE IF NOT EXISTS board_summary_cards (
          project_name TEXT NOT NULL,
          scan_rank INTEGER NOT NULL,
          task_key TEXT NOT NULL,
          watch_path TEXT NOT NULL,
          state TEXT NOT NULL,
          fixture INTEGER NOT NULL,
          lane_rank INTEGER NOT NULL,
          summary BLOB NOT NULL,
          PRIMARY KEY (project_name, scan_rank)
        )
        """,
    ];
}
