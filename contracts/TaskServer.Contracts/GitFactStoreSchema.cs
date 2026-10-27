namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// The one schema of the Git fact store (docs/git-facts-architecture, section
/// 03, decision D3). Placement B keeps these tables in <c>git-facts.db</c> in
/// the backend state directory; placement C (slice S7) creates the same tables
/// in <c>task-server.db</c> by executing <see cref="CreateStatements"/>. Later
/// slices extend the schema here instead of bootstrapping their own copy: S3
/// (version 2) added commit facts, reachability, commit ranges, the run trailer
/// index, curated integration merges, SHA-keyed diff objects and the project
/// integration view; S4 adds result versions and path histories.
/// <para>
/// The store holds derived facts only. Every row can be rebuilt from Git, so a
/// version mismatch or a corrupt file is answered by moving the file aside and
/// rebuilding, never by a partial migration.
/// </para>
/// </summary>
public static class GitFactStoreSchema
{
    /// <summary>Stored in <c>PRAGMA user_version</c>. A different value rebuilds the store.</summary>
    public const int Version = 2;

    /// <summary>File name of placement B.</summary>
    public const string FileName = "git-facts.db";

    public const string Repositories = "git_repositories";
    public const string RefGenerations = "git_ref_generations";
    public const string CardFacts = "card_git_facts";
    public const string ProducerHealth = "fact_producer_health";
    public const string CommitFacts = "git_commit_facts";
    public const string Reachability = "git_reachability";
    public const string ReachabilityTips = "git_reachability_tips";
    public const string CommitRanges = "git_commit_ranges";
    public const string RunCommits = "git_run_commits";
    public const string IntegrationMerges = "git_integration_merges";
    public const string ProducerCursors = "git_producer_cursors";
    public const string DiffObjects = "git_diff_objects";
    public const string DiffObjectFiles = "git_diff_object_files";
    public const string ProjectViews = "git_project_views";

    /// <summary>Every table of this schema version, for placement checks.</summary>
    public static IReadOnlyList<string> Tables { get; } =
    [
        Repositories, RefGenerations, CardFacts, ProducerHealth, CommitFacts, Reachability, ReachabilityTips,
        CommitRanges, RunCommits, IntegrationMerges, ProducerCursors, DiffObjects, DiffObjectFiles, ProjectViews,
    ];

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
        // S3. Immutable and content addressed: one row per commit the producer
        // read, with parents, tree, author time, subject, trailers and the
        // per-path name-status and numstat (JSON). Upserts are idempotent.
        """
        CREATE TABLE IF NOT EXISTS git_commit_facts (
          repo_id TEXT NOT NULL,
          sha TEXT NOT NULL,
          short_sha TEXT NOT NULL,
          parents TEXT NOT NULL,
          tree TEXT NOT NULL,
          author_time INTEGER NOT NULL,
          author TEXT NOT NULL,
          subject TEXT NOT NULL,
          trailers TEXT,
          files TEXT NOT NULL,
          PRIMARY KEY (repo_id, sha)
        ) WITHOUT ROWID
        """,
        // S3. Commits reachable from one target ref, with the generation at
        // which each became reachable. A fast-forward appends; a
        // non-fast-forward move replaces the ref's rows.
        """
        CREATE TABLE IF NOT EXISTS git_reachability (
          repo_id TEXT NOT NULL,
          target_ref TEXT NOT NULL,
          sha TEXT NOT NULL,
          generation INTEGER NOT NULL,
          PRIMARY KEY (repo_id, target_ref, sha)
        ) WITHOUT ROWID
        """,
        // S3. The tip each git_reachability set describes.
        """
        CREATE TABLE IF NOT EXISTS git_reachability_tips (
          repo_id TEXT NOT NULL,
          target_ref TEXT NOT NULL,
          tip_sha TEXT,
          generation INTEGER NOT NULL,
          updated_at INTEGER NOT NULL,
          PRIMARY KEY (repo_id, target_ref)
        )
        """,
        // S3. Commit membership of a SHA range (base..head, no merges) or of a
        // closed wall-clock window. Immutable for a SHA pair.
        """
        CREATE TABLE IF NOT EXISTS git_commit_ranges (
          repo_id TEXT NOT NULL,
          range_key TEXT NOT NULL,
          shas TEXT NOT NULL,
          generation INTEGER NOT NULL,
          PRIMARY KEY (repo_id, range_key)
        )
        """,
        // S3. The durable worktree run trailer index: job id to the commits
        // that carry its trailer. Replaces log --all --grep on read paths.
        """
        CREATE TABLE IF NOT EXISTS git_run_commits (
          repo_id TEXT NOT NULL,
          job_id TEXT NOT NULL,
          sha TEXT NOT NULL,
          PRIMARY KEY (repo_id, job_id, sha)
        ) WITHOUT ROWID
        """,
        // S3. Curated merge(KEY) commits on an integration ref, indexed once
        // per ref move and joined by task key (R12 publisher merges, R14).
        """
        CREATE TABLE IF NOT EXISTS git_integration_merges (
          repo_id TEXT NOT NULL,
          integration_ref TEXT NOT NULL,
          sha TEXT NOT NULL,
          task_key TEXT NOT NULL,
          short_sha TEXT NOT NULL,
          committed_at INTEGER NOT NULL,
          publisher TEXT NOT NULL,
          subject TEXT NOT NULL,
          ordinal INTEGER NOT NULL,
          PRIMARY KEY (repo_id, integration_ref, sha)
        )
        """,
        "CREATE INDEX IF NOT EXISTS ix_git_integration_merges_task ON git_integration_merges (repo_id, integration_ref, task_key)",
        // S3. Small producer cursors (ref tips a scan covered, cached promotion
        // file lists), one JSON value per name.
        """
        CREATE TABLE IF NOT EXISTS git_producer_cursors (
          repo_id TEXT NOT NULL,
          cursor TEXT NOT NULL,
          value TEXT NOT NULL,
          PRIMARY KEY (repo_id, cursor)
        )
        """,
        // S3. Immutable SHA-keyed diff objects (docs/studio-route-ownership D1):
        // the object id hashes repository, view, base and head SHA, diff
        // options and renderer version. The manifest lists every path with
        // status, rename source, numstat and binary and truncation flags.
        """
        CREATE TABLE IF NOT EXISTS git_diff_objects (
          object_id TEXT NOT NULL PRIMARY KEY,
          repo_id TEXT NOT NULL,
          view TEXT NOT NULL,
          base_sha TEXT NOT NULL,
          head_sha TEXT NOT NULL,
          options TEXT NOT NULL,
          renderer_version INTEGER NOT NULL,
          state TEXT NOT NULL,
          reason TEXT,
          manifest TEXT NOT NULL,
          created_at INTEGER NOT NULL
        )
        """,
        // S3. One bounded patch per file of a diff object.
        """
        CREATE TABLE IF NOT EXISTS git_diff_object_files (
          object_id TEXT NOT NULL,
          path TEXT NOT NULL,
          patch TEXT NOT NULL,
          truncated INTEGER NOT NULL,
          PRIMARY KEY (object_id, path)
        ) WITHOUT ROWID
        """,
        // S3. The published project integration view (R12), keyed by the
        // ref/config generation and the task generation it joined. The
        // producer replaces the row in one transaction.
        """
        CREATE TABLE IF NOT EXISTS git_project_views (
          repo_id TEXT NOT NULL,
          generation INTEGER NOT NULL,
          task_generation INTEGER NOT NULL,
          task_signature TEXT NOT NULL,
          view TEXT NOT NULL,
          published_at INTEGER NOT NULL,
          PRIMARY KEY (repo_id, generation, task_generation)
        )
        """,
    ];
}
