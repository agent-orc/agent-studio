namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// The one schema of the Git fact store (docs/git-facts-architecture, section
/// 03, decision D3). Placement B keeps these tables in <c>git-facts.db</c> in
/// the backend state directory; placement C (slice S7) creates the same tables
/// in <c>task-server.db</c> by executing <see cref="CreateStatements"/>. Later
/// slices extend the schema here (S3 commit facts and reachability, S4 result
/// versions and path histories) instead of bootstrapping their own copy.
/// <para>
/// The store holds derived facts only. Every row can be rebuilt from Git, so a
/// version mismatch or a corrupt file is answered by moving the file aside and
/// rebuilding, never by a partial migration.
/// </para>
/// </summary>
public static class GitFactStoreSchema
{
    /// <summary>Stored in <c>PRAGMA user_version</c>. A different value rebuilds the store.</summary>
    public const int Version = 1;

    /// <summary>File name of placement B.</summary>
    public const string FileName = "git-facts.db";

    public const string Repositories = "git_repositories";
    public const string RefGenerations = "git_ref_generations";
    public const string CardFacts = "card_git_facts";
    public const string ProducerHealth = "fact_producer_health";

    /// <summary>
    /// <c>git_ref_generations.state</c>: exactly one <see cref="CurrentGeneration"/>
    /// row per repository; earlier rows are <see cref="SupersededGeneration"/>.
    /// </summary>
    public const string CurrentGeneration = "current";
    public const string SupersededGeneration = "superseded";

    /// <summary>Superseded generations kept per repository after a publish.</summary>
    public const int RetainedGenerations = 32;

    public static IReadOnlyList<string> CreateStatements { get; } =
    [
        // One row per repository: root mappings, the configuration Git resolved
        // and its generation, and the producer lease (monotonic epoch).
        """
        CREATE TABLE IF NOT EXISTS git_repositories (
          repo_id TEXT NOT NULL PRIMARY KEY,
          canonical_root TEXT,
          workspace_root TEXT,
          origin_url TEXT,
          config_signature TEXT,
          config_sources TEXT,
          config_generation INTEGER NOT NULL DEFAULT 0,
          producer_id TEXT,
          producer_epoch INTEGER NOT NULL DEFAULT 0,
          updated_at INTEGER NOT NULL DEFAULT 0
        )
        """,
        // Append only. A generation names the exact Git input of every card row
        // published with it: ref object IDs, ref fingerprint, configuration
        // generation, settings version and the durable task input signature.
        """
        CREATE TABLE IF NOT EXISTS git_ref_generations (
          repo_id TEXT NOT NULL,
          generation INTEGER NOT NULL,
          ref_object_ids TEXT NOT NULL,
          ref_fingerprint TEXT NOT NULL,
          config_generation INTEGER NOT NULL,
          settings_version TEXT NOT NULL,
          task_input_signature TEXT NOT NULL,
          input_version TEXT NOT NULL,
          producer_id TEXT NOT NULL,
          producer_epoch INTEGER NOT NULL,
          published_at INTEGER NOT NULL,
          state TEXT NOT NULL,
          PRIMARY KEY (repo_id, generation)
        )
        """,
        "CREATE INDEX IF NOT EXISTS ix_git_ref_generations_current ON git_ref_generations (repo_id, state)",
        // Replaced per card with every publish of its repository. error_code is
        // the typed per-card failure; facts is null for a card without facts.
        """
        CREATE TABLE IF NOT EXISTS card_git_facts (
          repo_id TEXT NOT NULL,
          task_key TEXT NOT NULL,
          input_signature TEXT NOT NULL,
          generation INTEGER NOT NULL,
          facts TEXT,
          error_code TEXT,
          PRIMARY KEY (repo_id, task_key)
        )
        """,
        // Updated per run. Kept separately from generations so a failing
        // repository records its health while its last good generation serves.
        """
        CREATE TABLE IF NOT EXISTS fact_producer_health (
          repo_id TEXT NOT NULL PRIMARY KEY,
          state TEXT NOT NULL,
          last_good_generation INTEGER,
          last_good_at INTEGER,
          consecutive_failures INTEGER NOT NULL DEFAULT 0,
          last_error_reason TEXT,
          queue_length INTEGER NOT NULL DEFAULT 0,
          spawns_per_minute REAL NOT NULL DEFAULT 0,
          slowest_command TEXT,
          slowest_ms INTEGER,
          updated_at INTEGER NOT NULL
        )
        """,
    ];
}
