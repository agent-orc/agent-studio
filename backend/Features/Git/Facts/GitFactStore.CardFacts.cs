using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Git;

/// <summary>
/// Git facts S3 tables of the store: commit facts, reachability, commit
/// ranges, the run trailer index, curated integration merges, producer
/// cursors, diff objects and the project integration view. The producer is
/// the only writer. Request handlers never open the store except to read a
/// diff object's patch by key, which is an indexed read without Git.
/// </summary>
public sealed partial class GitFactStore
{
    /// <summary>
    /// Writes one producer run's S3 facts in one transaction. Commit facts,
    /// ranges and run commits are immutable inserts; reachability and merges
    /// append or replace per ref; the project view row is replaced.
    /// </summary>
    internal void WriteCardFacts(GitCardFactsWrite write, long generation)
    {
        lock (_gate)
        {
            if (!EnsureOpen()) return;
            using var connection = Connect(_path!);
            using var transaction = connection.BeginTransaction(deferred: false);
            WriteCardFacts(connection, transaction, write, generation);
            transaction.Commit();
        }
    }

    /// <summary>The S3 rows of one run inside an open transaction, stamped with <paramref name="generation"/>.</summary>
    private static void WriteCardFacts(SqliteConnection connection, SqliteTransaction transaction,
        GitCardFactsWrite write, long generation)
    {
        var repo = write.RepoId;
        using (var insert = Command(connection, transaction, """
            INSERT OR IGNORE INTO git_commit_facts (repo_id, sha, short_sha, parents, tree, author_time, author,
              subject, trailers, files)
            VALUES ($repo, $sha, $short, $parents, $tree, $time, $author, $subject, $trailers, $files)
            """, ("$repo", repo), ("$sha", ""), ("$short", ""), ("$parents", ""), ("$tree", ""), ("$time", 0L),
            ("$author", ""), ("$subject", ""), ("$trailers", null), ("$files", "")))
        {
            foreach (var commit in write.Commits)
            {
                insert.Parameters["$sha"].Value = commit.Sha;
                insert.Parameters["$short"].Value = commit.ShortSha;
                insert.Parameters["$parents"].Value = string.Join(' ', commit.Parents);
                insert.Parameters["$tree"].Value = commit.Tree;
                insert.Parameters["$time"].Value = new DateTimeOffset(commit.AuthorDateUtc).ToUnixTimeSeconds();
                insert.Parameters["$author"].Value = commit.Author;
                insert.Parameters["$subject"].Value = commit.Subject;
                insert.Parameters["$trailers"].Value = commit.Trailers.Count == 0
                    ? DBNull.Value : JsonSerializer.Serialize(commit.Trailers, Json);
                insert.Parameters["$files"].Value = JsonSerializer.Serialize(
                    commit.Files.Select(file => new StoredFile(file.Status, file.Path, file.OldPath, file.Added,
                        file.Removed, file.Binary)), Json);
                insert.ExecuteNonQuery();
            }
        }

        foreach (var change in write.Reachability)
        {
            if (change.Replace)
                Execute(connection, transaction,
                    "DELETE FROM git_reachability WHERE repo_id = $repo AND target_ref = $ref",
                    ("$repo", repo), ("$ref", change.FullRef));
            using (var insert = Command(connection, transaction, """
                INSERT OR IGNORE INTO git_reachability (repo_id, target_ref, sha, generation)
                VALUES ($repo, $ref, $sha, $generation)
                """, ("$repo", repo), ("$ref", change.FullRef), ("$sha", ""), ("$generation", generation)))
            {
                foreach (var sha in change.Shas)
                {
                    insert.Parameters["$sha"].Value = sha;
                    insert.ExecuteNonQuery();
                }
            }
            Execute(connection, transaction, """
                INSERT INTO git_reachability_tips (repo_id, target_ref, tip_sha, generation, updated_at)
                VALUES ($repo, $ref, $tip, $generation, $now)
                ON CONFLICT (repo_id, target_ref) DO UPDATE SET tip_sha = excluded.tip_sha,
                  generation = excluded.generation, updated_at = excluded.updated_at
                """, ("$repo", repo), ("$ref", change.FullRef), ("$tip", change.Tip),
                ("$generation", generation), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }

        using (var insert = Command(connection, transaction, """
            INSERT OR REPLACE INTO git_commit_ranges (repo_id, range_key, shas, generation)
            VALUES ($repo, $key, $shas, $generation)
            """, ("$repo", repo), ("$key", ""), ("$shas", ""), ("$generation", generation)))
        {
            foreach (var (key, shas) in write.Ranges)
            {
                insert.Parameters["$key"].Value = key;
                insert.Parameters["$shas"].Value = string.Join(' ', shas);
                insert.ExecuteNonQuery();
            }
        }

        using (var insert = Command(connection, transaction, """
            INSERT OR IGNORE INTO git_run_commits (repo_id, job_id, sha) VALUES ($repo, $job, $sha)
            """, ("$repo", repo), ("$job", ""), ("$sha", "")))
        {
            foreach (var (jobId, sha) in write.RunCommits)
            {
                insert.Parameters["$job"].Value = jobId;
                insert.Parameters["$sha"].Value = sha;
                insert.ExecuteNonQuery();
            }
        }

        foreach (var change in write.Merges)
        {
            if (change.Replace)
                Execute(connection, transaction,
                    "DELETE FROM git_integration_merges WHERE repo_id = $repo AND integration_ref = $ref",
                    ("$repo", repo), ("$ref", change.RefName));
            var top = change.Replace ? 0 : Scalar(connection, transaction,
                "SELECT COALESCE(MAX(ordinal), 0) FROM git_integration_merges WHERE repo_id = $repo AND integration_ref = $ref",
                ("$repo", repo), ("$ref", change.RefName));
            using var insert = Command(connection, transaction, """
                INSERT OR IGNORE INTO git_integration_merges (repo_id, integration_ref, sha, task_key, short_sha,
                  committed_at, publisher, subject, ordinal)
                VALUES ($repo, $ref, $sha, $key, $short, $at, $publisher, $subject, $ordinal)
                """, ("$repo", repo), ("$ref", change.RefName), ("$sha", ""), ("$key", ""), ("$short", ""),
                ("$at", 0L), ("$publisher", ""), ("$subject", ""), ("$ordinal", 0L));
            // Log order is newest first; a higher ordinal is newer.
            for (var index = 0; index < change.Merges.Count; index++)
            {
                var merge = change.Merges[index];
                insert.Parameters["$sha"].Value = merge.Sha;
                insert.Parameters["$key"].Value = merge.TaskKey;
                insert.Parameters["$short"].Value = merge.ShortSha;
                insert.Parameters["$at"].Value = new DateTimeOffset(merge.CommittedAtUtc).ToUnixTimeSeconds();
                insert.Parameters["$publisher"].Value = merge.Publisher;
                insert.Parameters["$subject"].Value = merge.Subject;
                insert.Parameters["$ordinal"].Value = top + change.Merges.Count - index;
                insert.ExecuteNonQuery();
            }
        }

        foreach (var (cursor, value) in write.Cursors)
        {
            Execute(connection, transaction, """
                INSERT INTO git_producer_cursors (repo_id, cursor, value) VALUES ($repo, $cursor, $value)
                ON CONFLICT (repo_id, cursor) DO UPDATE SET value = excluded.value
                """, ("$repo", repo), ("$cursor", cursor), ("$value", value));
        }

        if (write.ProjectView is { } view)
        {
            Execute(connection, transaction, "DELETE FROM git_project_views WHERE repo_id = $repo", ("$repo", repo));
            Execute(connection, transaction, """
                INSERT INTO git_project_views (repo_id, generation, task_generation, task_signature, view, published_at)
                VALUES ($repo, $generation, $taskGeneration, $signature, $view, $published)
                """, ("$repo", repo), ("$generation", generation), ("$taskGeneration", view.TaskGeneration),
                ("$signature", view.TaskSignature), ("$view", JsonSerializer.Serialize(view.View, Json)),
                ("$published", view.PublishedAt.ToUnixTimeMilliseconds()));
        }
    }

    /// <summary>Every S3 fact of a repository, for a restart. Null when the store is unavailable.</summary>
    internal GitCardFactsStored? LoadCardFacts(string repoId)
    {
        lock (_gate)
        {
            if (!EnsureOpen()) return null;
            using var connection = Connect(_path!);
            var commits = new List<GitCommitFact>();
            using (var command = Command(connection, null, """
                SELECT sha, short_sha, parents, tree, author_time, author, subject, trailers, files
                FROM git_commit_facts WHERE repo_id = $repo
                """, ("$repo", repoId)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var files = JsonSerializer.Deserialize<StoredFile[]>(reader.GetString(8), Json) ?? [];
                    commits.Add(new GitCommitFact(reader.GetString(0), reader.GetString(1),
                        reader.GetString(2).Split(' ', StringSplitOptions.RemoveEmptyEntries), reader.GetString(3),
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).UtcDateTime, reader.GetString(5),
                        reader.GetString(6),
                        reader.IsDBNull(7) ? new Dictionary<string, string>()
                            : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(7), Json) ?? [],
                        files.Select(file => new GitCommitFileFact(file.S, file.P, file.O, file.A, file.R, file.B)).ToArray()));
                }
            }

            var tips = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var command = Command(connection, null,
                "SELECT target_ref, tip_sha FROM git_reachability_tips WHERE repo_id = $repo AND tip_sha IS NOT NULL",
                ("$repo", repoId)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read()) tips[reader.GetString(0)] = reader.GetString(1);
            }
            var reach = tips.Keys.ToDictionary(key => key, _ => new List<string>(), StringComparer.Ordinal);
            using (var command = Command(connection, null,
                "SELECT target_ref, sha FROM git_reachability WHERE repo_id = $repo", ("$repo", repoId)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    if (reach.TryGetValue(reader.GetString(0), out var list)) list.Add(reader.GetString(1));
            }

            var ranges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            using (var command = Command(connection, null,
                "SELECT range_key, shas FROM git_commit_ranges WHERE repo_id = $repo", ("$repo", repoId)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    ranges[reader.GetString(0)] = reader.GetString(1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }

            var runCommits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using (var command = Command(connection, null,
                "SELECT job_id, sha FROM git_run_commits WHERE repo_id = $repo", ("$repo", repoId)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (!runCommits.TryGetValue(reader.GetString(0), out var list))
                        runCommits[reader.GetString(0)] = list = [];
                    list.Add(reader.GetString(1));
                }
            }

            var merges = new Dictionary<string, List<GitIntegrationMergeCommit>>(StringComparer.Ordinal);
            using (var command = Command(connection, null, """
                SELECT integration_ref, sha, task_key, short_sha, committed_at, publisher, subject
                FROM git_integration_merges WHERE repo_id = $repo ORDER BY integration_ref, ordinal DESC
                """, ("$repo", repoId)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (!merges.TryGetValue(reader.GetString(0), out var list))
                        merges[reader.GetString(0)] = list = [];
                    list.Add(new GitIntegrationMergeCommit(reader.GetString(2), reader.GetString(1), reader.GetString(3),
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).UtcDateTime, reader.GetString(5),
                        reader.GetString(6)));
                }
            }

            var cursors = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var command = Command(connection, null,
                "SELECT cursor, value FROM git_producer_cursors WHERE repo_id = $repo", ("$repo", repoId)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read()) cursors[reader.GetString(0)] = reader.GetString(1);
            }

            GitStoredProjectView? view = null;
            using (var command = Command(connection, null, """
                SELECT generation, task_generation, task_signature, view, published_at FROM git_project_views
                WHERE repo_id = $repo ORDER BY generation DESC, task_generation DESC LIMIT 1
                """, ("$repo", repoId)))
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read() && JsonSerializer.Deserialize<ProjectIntegrationView>(reader.GetString(3), Json) is { } stored)
                    view = new GitStoredProjectView(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), stored,
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)));
            }

            return new GitCardFactsStored(commits,
                reach.Select(pair => (pair.Key, tips[pair.Key], (IReadOnlyCollection<string>)pair.Value)).ToList(),
                ranges,
                runCommits.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal),
                merges.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<GitIntegrationMergeCommit>)pair.Value,
                    StringComparer.Ordinal),
                cursors, view);
        }
    }

    /// <summary>Stores one diff object and its patches; an existing object is kept (immutable).</summary>
    internal bool WriteDiffObject(GitDiffObject diff)
    {
        lock (_gate)
        {
            if (!EnsureOpen()) return false;
            using var connection = Connect(_path!);
            using var transaction = connection.BeginTransaction(deferred: false);
            var manifest = diff.Manifest;
            var inserted = Execute(connection, transaction, """
                INSERT OR IGNORE INTO git_diff_objects (object_id, repo_id, view, base_sha, head_sha, options,
                  renderer_version, state, reason, manifest, created_at)
                VALUES ($id, $repo, $view, $base, $head, $options, $renderer, $state, $reason, $manifest, $created)
                """, ("$id", manifest.ObjectId), ("$repo", manifest.RepoId), ("$view", manifest.View),
                ("$base", manifest.BaseSha), ("$head", manifest.HeadSha), ("$options", GitDiffObjectKey.Options),
                ("$renderer", GitDiffObjectKey.RendererVersion), ("$state", manifest.State), ("$reason", manifest.Reason),
                ("$manifest", JsonSerializer.Serialize(new StoredManifest(manifest.Files, manifest.Members,
                    manifest.FilesTruncated), Json)),
                ("$created", manifest.CreatedAt.ToUnixTimeMilliseconds()));
            if (inserted > 0)
            {
                using var insert = Command(connection, transaction, """
                    INSERT OR IGNORE INTO git_diff_object_files (object_id, path, patch, truncated)
                    VALUES ($id, $path, $patch, $truncated)
                    """, ("$id", manifest.ObjectId), ("$path", ""), ("$patch", ""), ("$truncated", 0));
                foreach (var file in diff.Patches)
                {
                    insert.Parameters["$path"].Value = file.Path;
                    insert.Parameters["$patch"].Value = file.Patch;
                    insert.Parameters["$truncated"].Value = file.Truncated ? 1 : 0;
                    insert.ExecuteNonQuery();
                }
            }
            transaction.Commit();
            return true;
        }
    }

    /// <summary>The manifests of <paramref name="objectIds"/> that exist. Indexed reads, no Git.</summary>
    internal IReadOnlyDictionary<string, GitDiffObjectManifest> ReadDiffObjects(IReadOnlyCollection<string> objectIds)
    {
        var found = new Dictionary<string, GitDiffObjectManifest>(StringComparer.Ordinal);
        if (objectIds.Count == 0 || !CanRead()) return found;
        using var connection = Connect(_path!);
        using var command = Command(connection, null, """
            SELECT object_id, repo_id, view, base_sha, head_sha, state, reason, manifest, created_at
            FROM git_diff_objects WHERE object_id = $id
            """, ("$id", ""));
        foreach (var id in objectIds.Distinct(StringComparer.Ordinal))
        {
            command.Parameters["$id"].Value = id;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) continue;
            var stored = JsonSerializer.Deserialize<StoredManifest>(reader.GetString(7), Json);
            found[id] = new GitDiffObjectManifest(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                stored?.Files ?? [], stored?.Members ?? [], stored?.FilesTruncated ?? false,
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)));
        }
        return found;
    }

    /// <summary>
    /// The stored patches of one object, all of them or only <paramref name="path"/>,
    /// in manifest path order. Indexed read, no Git.
    /// </summary>
    internal IReadOnlyList<GitPatchFile> ReadDiffObjectPatches(string objectId, string? path)
    {
        var patches = new List<GitPatchFile>();
        if (!CanRead()) return patches;
        using var connection = Connect(_path!);
        using var command = path is null
            ? Command(connection, null,
                "SELECT path, patch, truncated FROM git_diff_object_files WHERE object_id = $id ORDER BY path",
                ("$id", objectId))
            : Command(connection, null,
                "SELECT path, patch, truncated FROM git_diff_object_files WHERE object_id = $id AND path = $path",
                ("$id", objectId), ("$path", path));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            patches.Add(new GitPatchFile(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0));
        return patches;
    }

    /// <summary>
    /// True when the store can serve a read. Readers do not take the writer
    /// gate: WAL lets them read the last committed state while the producer
    /// writes.
    /// </summary>
    private bool CanRead()
    {
        lock (_gate) return _path is not null && (_opened || EnsureOpen()) && !_unavailable;
    }

    private sealed record StoredFile(string S, string P, string? O, int A, int R, bool B);
    private sealed record StoredManifest(IReadOnlyList<GitDiffManifestFile> Files, IReadOnlyList<string> Members,
        bool FilesTruncated);
}

/// <summary>One producer run's S3 facts for <see cref="GitFactStore.WriteCardFacts"/>.</summary>
internal sealed record GitCardFactsWrite(
    string RepoId,
    IReadOnlyCollection<GitCommitFact> Commits,
    IReadOnlyCollection<GitReachabilityChange> Reachability,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Ranges,
    IReadOnlyCollection<(string JobId, string Sha)> RunCommits,
    IReadOnlyCollection<GitMergeIndexChange> Merges,
    IReadOnlyDictionary<string, string> Cursors,
    GitStoredProjectView? ProjectView)
{
    public bool IsEmpty => Commits.Count == 0 && Reachability.Count == 0 && Ranges.Count == 0 && RunCommits.Count == 0
        && Merges.Count == 0 && Cursors.Count == 0 && ProjectView is null;
}

/// <summary>The curated merges a ref gained (newest first), or its whole list on a replace.</summary>
internal sealed record GitMergeIndexChange(string RefName, bool Replace, IReadOnlyList<GitIntegrationMergeCommit> Merges);

/// <summary>A published project integration view with the generations it joined.</summary>
public sealed record GitStoredProjectView(
    long Generation, long TaskGeneration, string TaskSignature, ProjectIntegrationView View, DateTimeOffset PublishedAt);

/// <summary>Everything <see cref="GitFactStore.LoadCardFacts"/> restores for one repository.</summary>
internal sealed record GitCardFactsStored(
    IReadOnlyList<GitCommitFact> Commits,
    IReadOnlyList<(string FullRef, string Tip, IReadOnlyCollection<string> Shas)> Reachability,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Ranges,
    IReadOnlyDictionary<string, IReadOnlyList<string>> RunCommits,
    IReadOnlyDictionary<string, IReadOnlyList<GitIntegrationMergeCommit>> Merges,
    IReadOnlyDictionary<string, string> Cursors,
    GitStoredProjectView? ProjectView);
