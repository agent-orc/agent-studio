using Microsoft.Data.Sqlite;
using AgentStudio.Tasks;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Git;

/// <summary>S4 indexes in the shared Git fact database.</summary>
public sealed partial class GitFactStore
{
    public void WritePathHistory(GitPathHistoryFact fact)
    {
        lock (_gate)
        {
            if (!EnsureOpen()) return;
            using var connection = Connect(_path!);
            using var transaction = connection.BeginTransaction(deferred: false);
            var generation = Scalar(connection, transaction,
                "SELECT COALESCE(config_generation, 0) FROM git_repositories WHERE repo_id=$repo",
                ("$repo", fact.RepoId));
            var refGeneration = Scalar(connection, transaction,
                "SELECT COALESCE(MAX(generation), 0) FROM git_ref_generations WHERE repo_id=$repo AND state=$state",
                ("$repo", fact.RepoId), ("$state", GitFactStoreSchema.CurrentGeneration));
            Execute(connection, transaction, """
                INSERT INTO git_path_histories (repo_id, source, repository_root, config_generation,
                  generation, path, entries, indexed_at) VALUES ($repo, $source, $root, $generation,
                  $refGeneration, $path, $entries, $now)
                ON CONFLICT (repo_id, source, repository_root, path) DO UPDATE SET
                  repository_root=excluded.repository_root,
                  config_generation=excluded.config_generation, generation=excluded.generation,
                  entries=excluded.entries,
                  indexed_at=excluded.indexed_at
                """, ("$repo", fact.RepoId), ("$source", fact.Source),
                ("$root", fact.IdentityPath), ("$generation", generation),
                ("$refGeneration", refGeneration),
                ("$path", fact.Path), ("$entries", JsonSerializer.Serialize(fact.Entries)),
                ("$now", Now()));
            foreach (var item in fact.Objects)
                WriteFileObject(connection, transaction, fact.RepoId, fact.Source, fact.IdentityPath, item);
            transaction.Commit();
        }
    }

    private static void WriteFileObject(SqliteConnection connection, SqliteTransaction? transaction,
        string repoId, string source, string identityPath, GitFileObjectFact item)
        => Execute(connection, transaction, """
            INSERT INTO git_file_objects (repo_id, source, identity_path, sha, path,
              content_hash, content, truncated)
            VALUES ($repo, $source, $root, $sha, $path, $hash, $content, $truncated)
            ON CONFLICT (repo_id, source, identity_path, sha, path) DO NOTHING
            """, ("$repo", repoId), ("$source", source), ("$sha", item.Sha),
            ("$root", identityPath), ("$path", item.Path), ("$hash", item.ContentHash),
            ("$content", item.Content), ("$truncated", item.Truncated ? 1 : 0));

    public GitPathHistoryFact? ReadPathHistory(string repoId, string source, string path, string identityPath)
    {
        lock (_gate)
        {
            if (!_opened || _unavailable) return null;
            using var connection = Connect(_path!);
            using var command = Command(connection, null, """
                SELECT h.entries FROM git_path_histories h
                JOIN git_repositories r ON r.repo_id=h.repo_id
                JOIN git_ref_generations g ON g.repo_id=h.repo_id AND g.state=$current
                WHERE h.repo_id=$repo AND h.source=$source AND h.path=$path
                  AND h.repository_root=$root AND h.config_generation=r.config_generation
                  AND h.generation=g.generation
                """, ("$repo", repoId), ("$source", source),
                ("$path", path), ("$root", identityPath),
                ("$current", GitFactStoreSchema.CurrentGeneration));
            var raw = command.ExecuteScalar() as string;
            return raw is null ? null : new GitPathHistoryFact(repoId, source, identityPath, path,
                JsonSerializer.Deserialize<TaskFileHistoryEntry[]>(raw) ?? [], []);
        }
    }

    public GitFileObjectFact? ReadFileObject(string repoId, string source, string identityPath,
        string sha, string path)
    {
        lock (_gate)
        {
            if (!_opened || _unavailable) return null;
            using var connection = Connect(_path!);
            using var command = Command(connection, null, """
                SELECT content_hash, content, truncated FROM git_file_objects
                WHERE repo_id=$repo AND source=$source AND identity_path=$root
                  AND sha=$sha AND path=$path
                """, ("$repo", repoId), ("$source", source),
                ("$root", identityPath), ("$sha", sha), ("$path", path));
            using var reader = command.ExecuteReader();
            return reader.Read() ? new GitFileObjectFact(sha, path, reader.GetString(1),
                reader.GetString(0), reader.GetInt32(2) != 0) : null;
        }
    }

    public void UpsertResultVersion(GitResultVersionFact fact)
    {
        lock (_gate)
        {
            if (!EnsureOpen()) return;
            using var connection = Connect(_path!);
            Execute(connection, null, """
                INSERT INTO result_versions (task_key, version_id, producer, producer_kind, lane,
                  run_index, verdict, steps, timestamp, content_hash, source_commit, source,
                  status_path, result, result_case, version)
                VALUES ($task, $id, $producer, $kind, $lane, $run, $verdict, $steps, $time,
                  $hash, $commit, $source, $path, $result, $case, $version)
                ON CONFLICT (task_key, version_id) DO UPDATE SET
                  producer=excluded.producer, producer_kind=excluded.producer_kind,
                  lane=excluded.lane, run_index=excluded.run_index, verdict=excluded.verdict,
                  steps=excluded.steps, timestamp=excluded.timestamp, content_hash=excluded.content_hash,
                  source_commit=excluded.source_commit, source=excluded.source,
                  status_path=excluded.status_path, result=excluded.result,
                  result_case=excluded.result_case, version=excluded.version
                """,
                ("$task", fact.TaskKey), ("$id", fact.VersionId), ("$producer", fact.Producer),
                ("$kind", fact.ProducerKind), ("$lane", fact.Lane), ("$run", fact.RunIndex),
                ("$verdict", fact.Verdict), ("$steps", fact.Steps),
                ("$time", new DateTimeOffset(fact.Timestamp.ToUniversalTime()).ToUnixTimeMilliseconds()),
                ("$hash", fact.ContentHash), ("$commit", fact.SourceCommit),
                ("$source", fact.Source), ("$path", fact.StatusPath),
                ("$result", fact.Result), ("$case", fact.Case), ("$version", fact.Version));
        }
    }

    public IReadOnlyList<GitResultVersionFact> ReadResultVersions(string taskKey)
    {
        lock (_gate)
        {
            if (!_opened || _unavailable) return [];
            using var connection = Connect(_path!);
            using var command = Command(connection, null, """
                SELECT version_id, producer, producer_kind, lane, run_index, verdict, steps,
                  timestamp, content_hash, source_commit, source, status_path, result,
                  result_case, version FROM result_versions WHERE task_key=$task
                ORDER BY timestamp DESC, version DESC
                """, ("$task", taskKey));
            using var reader = command.ExecuteReader();
            var result = new List<GitResultVersionFact>();
            while (reader.Read())
                result.Add(new GitResultVersionFact(taskKey, reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), OptionalInt(reader, 4),
                    OptionalString(reader, 5), OptionalString(reader, 6),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)).UtcDateTime,
                    reader.GetString(8), OptionalString(reader, 9), reader.GetString(10),
                    OptionalString(reader, 11), reader.GetString(12), reader.GetString(13),
                    OptionalInt(reader, 14)));
            return result;
        }
    }

    private static string? OptionalString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? OptionalInt(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}

public sealed record GitResultVersionFact(
    string TaskKey, string VersionId, string Producer, string ProducerKind, string Lane,
    int? RunIndex, string? Verdict, string? Steps, DateTime Timestamp, string ContentHash,
    string? SourceCommit, string Source, string? StatusPath, string Result, string Case, int? Version);

public sealed record GitPathHistoryFact(string RepoId, string Source, string IdentityPath,
    string Path, IReadOnlyList<TaskFileHistoryEntry> Entries, IReadOnlyList<GitFileObjectFact> Objects);

public sealed record GitFileObjectFact(string Sha, string Path, string Content, string ContentHash,
    bool Truncated);
