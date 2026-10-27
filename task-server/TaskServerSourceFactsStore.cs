using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Tier 2 of the pipeline-delivery target (AGT-3318, D7 A-B): diff objects
/// against the merge base, path history, blame and reachability facts that one
/// fenced producer on a runner host publishes. Rows are keyed by immutable
/// inputs and never expire; the store is bounded by size instead. The Task
/// Server keeps its no-Git boundary: nothing here starts a process.
/// </summary>
public sealed partial class TaskServerStore
{
    private static readonly JsonSerializerOptions SourceFactJson = new(JsonSerializerDefaults.Web);
    internal const int MaximumPendingSourceDemands = 1000;
    internal const int MaximumSourceDemandAttempts = 5;
    private static readonly TimeSpan SourceReadTouchInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan SourceDemandRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan SourceReadReportRetention = TimeSpan.FromDays(30);

    private static async Task ApplySourceFactsMigrationAsync(SqliteConnection connection, CancellationToken ct)
        => await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS source_fact_producers(
                repository_id TEXT PRIMARY KEY,
                producer_id TEXT NOT NULL,
                producer_epoch INTEGER NOT NULL,
                expires_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS source_diff_objects(
                row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                repository_id TEXT NOT NULL,
                base_sha TEXT NOT NULL,
                head_sha TEXT NOT NULL,
                options TEXT NOT NULL,
                renderer_version INTEGER NOT NULL,
                body_json TEXT NOT NULL,
                content_sha256 TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                producer_id TEXT NOT NULL,
                producer_epoch INTEGER NOT NULL,
                produced_at TEXT NOT NULL,
                last_read_at TEXT NOT NULL,
                UNIQUE(repository_id, base_sha, head_sha, options, renderer_version)
            );
            CREATE TABLE IF NOT EXISTS source_path_histories(
                row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                repository_id TEXT NOT NULL,
                head_sha TEXT NOT NULL,
                path TEXT NOT NULL,
                body_json TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                producer_id TEXT NOT NULL,
                producer_epoch INTEGER NOT NULL,
                produced_at TEXT NOT NULL,
                last_read_at TEXT NOT NULL,
                UNIQUE(repository_id, head_sha, path)
            );
            CREATE TABLE IF NOT EXISTS source_blames(
                row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                repository_id TEXT NOT NULL,
                head_sha TEXT NOT NULL,
                path TEXT NOT NULL,
                body_json TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                producer_id TEXT NOT NULL,
                producer_epoch INTEGER NOT NULL,
                produced_at TEXT NOT NULL,
                last_read_at TEXT NOT NULL,
                UNIQUE(repository_id, head_sha, path)
            );
            CREATE TABLE IF NOT EXISTS source_ref_heads(
                repository_id TEXT NOT NULL,
                ref_name TEXT NOT NULL,
                sha TEXT NOT NULL,
                producer_epoch INTEGER NOT NULL,
                observed_at TEXT NOT NULL,
                PRIMARY KEY(repository_id, ref_name)
            );
            CREATE TABLE IF NOT EXISTS source_reachability(
                repository_id TEXT NOT NULL,
                target_ref TEXT NOT NULL,
                sha TEXT NOT NULL,
                contained INTEGER NOT NULL,
                ref_head_sha TEXT NOT NULL,
                producer_epoch INTEGER NOT NULL,
                observed_at TEXT NOT NULL,
                PRIMARY KEY(repository_id, target_ref, sha)
            );
            CREATE TABLE IF NOT EXISTS source_fact_demands(
                demand_id INTEGER PRIMARY KEY AUTOINCREMENT,
                repository_id TEXT NOT NULL,
                demand_key TEXT NOT NULL UNIQUE,
                kind TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                reason TEXT NOT NULL,
                requested_at TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                completed_at TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_source_fact_demands_pending
                ON source_fact_demands(repository_id, completed_at, demand_id);
            CREATE TABLE IF NOT EXISTS source_review_material_reads(
                read_id INTEGER PRIMARY KEY AUTOINCREMENT,
                repository_id TEXT NOT NULL,
                base_sha TEXT NOT NULL,
                head_sha TEXT NOT NULL,
                source TEXT NOT NULL,
                elapsed_ms INTEGER NOT NULL,
                miss_reason TEXT,
                recorded_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_source_review_material_reads_recorded
                ON source_review_material_reads(recorded_at);
            """, ct);

    public async Task<SourceProducerLeaseDto> ClaimSourceProducerLeaseAsync(
        string repositoryId, SourceProducerLeaseRequest request, CancellationToken ct = default)
    {
        var repository = await CanonicalSourceRepositoryAsync(repositoryId, ct);
        var producer = RequireSourceText(request.ProducerId, "producerId", 256);
        RequireWritable();
        SourceProducerLeaseDto? granted = null;
        SourceProducerLeasePolicy.Decision? held = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadSourceProducerAsync(connection, transaction, repository, ct);
            var decision = SourceProducerLeasePolicy.Decide(current, producer, request.RequestedTtlSeconds, UtcNow);
            if (decision.Outcome == SourceProducerLeasePolicy.Outcome.Held)
            {
                held = decision;
                return;
            }
            await ExecuteAsync(connection, """
                INSERT INTO source_fact_producers(repository_id, producer_id, producer_epoch, expires_at, updated_at)
                VALUES ($repository, $producer, $epoch, $expires, $now)
                ON CONFLICT(repository_id) DO UPDATE SET
                    producer_id = excluded.producer_id,
                    producer_epoch = excluded.producer_epoch,
                    expires_at = excluded.expires_at,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$repository", repository), ("$producer", producer), ("$epoch", decision.Epoch),
                ("$expires", Iso(decision.ExpiresAt)), ("$now", Iso(UtcNow)));
            granted = new SourceProducerLeaseDto(repository, producer, decision.Epoch, decision.ExpiresAt);
        }, ct);
        return granted ?? throw new TaskServerConflictException(
            "source-producer-lease-held",
            $"Another producer holds the source facts lease of '{repository}' at epoch {held!.Epoch} until {Iso(held.ExpiresAt)}.");
    }

    public async Task<SourceDiffObjectDto> PublishSourceDiffObjectAsync(
        string repositoryId, SourceDiffObjectPublishRequest request, CancellationToken ct = default)
    {
        var repository = await CanonicalSourceRepositoryAsync(repositoryId, ct);
        var baseSha = RequireSourceSha(request.BaseSha, "baseSha");
        var headSha = RequireSourceSha(request.HeadSha, "headSha");
        var options = RequireSourceText(request.Options, "options", 256);
        if (request.RendererVersion <= 0) throw new ArgumentException("rendererVersion must be positive.");
        if (request.ChangedFiles.Count > request.MaximumFiles || request.ChangedFiles.Count > request.TotalFiles)
            throw new ArgumentException("The changed-file list exceeds its budget or total.");
        if (request.DiffLines.Count > request.MaximumLines || request.DiffLines.Count > request.TotalDiffLines)
            throw new ArgumentException("The diff lines exceed their budget or total.");
        var digest = SourceDiffMaterialDigest.Compute(
            request.ChangedFiles, request.DiffLines, request.TotalFiles, request.TotalDiffLines,
            request.MaximumFiles, request.MaximumLines);
        if (!string.Equals(digest, request.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("contentSha256 does not match the published diff body.");
        RequireWritable();

        var body = JsonSerializer.Serialize(new DiffBody(
            request.ChangedFiles, request.DiffLines, request.TotalFiles, request.TotalDiffLines,
            request.MaximumFiles, request.MaximumLines), SourceFactJson);
        var size = Encoding.UTF8.GetByteCount(body);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await RequireSourceProducerAsync(connection, transaction, repository, request.ProducerId, request.ProducerEpoch, ct);
            var existing = Convert.ToString(await ScalarAsync(connection, """
                SELECT content_sha256 FROM source_diff_objects
                 WHERE repository_id = $repository AND base_sha = $base AND head_sha = $head
                   AND options = $options AND renderer_version = $renderer;
                """, ct, transaction,
                ("$repository", repository), ("$base", baseSha), ("$head", headSha),
                ("$options", options), ("$renderer", request.RendererVersion)), CultureInfo.InvariantCulture);
            if (existing is { Length: > 0 })
            {
                if (!string.Equals(existing, digest, StringComparison.Ordinal))
                    throw new TaskServerConflictException(
                        "source-object-conflict",
                        "A diff object with this key already exists with different content.");
                return;
            }
            await ExecuteAsync(connection, """
                INSERT INTO source_diff_objects(
                    repository_id, base_sha, head_sha, options, renderer_version, body_json,
                    content_sha256, size_bytes, producer_id, producer_epoch, produced_at, last_read_at)
                VALUES ($repository, $base, $head, $options, $renderer, $body,
                    $digest, $size, $producer, $epoch, $now, $now);
                """, ct, transaction,
                ("$repository", repository), ("$base", baseSha), ("$head", headSha),
                ("$options", options), ("$renderer", request.RendererVersion), ("$body", body),
                ("$digest", digest), ("$size", size), ("$producer", request.ProducerId.Trim()),
                ("$epoch", request.ProducerEpoch), ("$now", Iso(UtcNow)));
            await EvictSourceFactsAsync(connection, transaction, ct);
        }, ct);
        return await GetSourceDiffObjectAsync(repository, baseSha, headSha, options, request.RendererVersion, ct)
            ?? throw new InvalidOperationException("The published diff object was evicted immediately; raise SourceFactsMaxStoredBytes.");
    }

    public async Task<SourceDiffObjectDto?> GetSourceDiffObjectAsync(
        string repositoryId, string baseSha, string headSha, string options, int rendererVersion,
        CancellationToken ct = default)
    {
        var repository = await CanonicalSourceRepositoryAsync(repositoryId, ct);
        var normalizedBase = RequireSourceSha(baseSha, "base");
        var normalizedHead = RequireSourceSha(headSha, "head");
        var normalizedOptions = RequireSourceText(options, "options", 256);
        SourceDiffObjectDto? found = null;
        long rowId = 0;
        DateTime lastRead = default;
        await using (var connection = await OpenReadyAsync(ct))
        await using (var command = Command(connection, """
            SELECT row_id, body_json, content_sha256, size_bytes, producer_id, producer_epoch, produced_at, last_read_at
              FROM source_diff_objects
             WHERE repository_id = $repository AND base_sha = $base AND head_sha = $head
               AND options = $options AND renderer_version = $renderer;
            """, ("$repository", repository), ("$base", normalizedBase), ("$head", normalizedHead),
            ("$options", normalizedOptions), ("$renderer", rendererVersion)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                rowId = reader.GetInt64(0);
                var body = JsonSerializer.Deserialize<DiffBody>(reader.GetString(1), SourceFactJson)
                    ?? throw new InvalidDataException("Stored diff object body is empty.");
                lastRead = Parse(reader.GetString(7));
                found = new SourceDiffObjectDto(
                    repository, normalizedBase, normalizedHead, normalizedOptions, rendererVersion,
                    body.ChangedFiles, body.DiffLines, body.TotalFiles, body.TotalDiffLines,
                    body.MaximumFiles, body.MaximumLines, reader.GetString(2), reader.GetInt64(3),
                    reader.GetString(4), reader.GetInt64(5), Parse(reader.GetString(6)));
            }
        }
        if (found is not null) await TouchSourceRowAsync("source_diff_objects", rowId, lastRead, ct);
        return found;
    }

    public Task<SourcePathHistoryDto> PublishSourcePathHistoryAsync(
        string repositoryId, SourcePathHistoryPublishRequest request, CancellationToken ct = default)
        => PublishSourcePathRowAsync(
            "source_path_histories", repositoryId, request.ProducerId, request.ProducerEpoch,
            request.HeadSha, request.Path,
            new PathHistoryBody(request.Entries, request.Truncated),
            async () => (await GetSourcePathHistoryAsync(repositoryId, request.HeadSha, request.Path, ct))!,
            ct);

    public async Task<SourcePathHistoryDto?> GetSourcePathHistoryAsync(
        string repositoryId, string headSha, string path, CancellationToken ct = default)
    {
        var row = await ReadSourcePathRowAsync<PathHistoryBody>("source_path_histories", repositoryId, headSha, path, ct);
        return row is null
            ? null
            : new SourcePathHistoryDto(row.Value.Repository, row.Value.Head, row.Value.Path,
                row.Value.Body.Entries, row.Value.Body.Truncated, row.Value.ProducedAt);
    }

    public Task<SourceBlameDto> PublishSourceBlameAsync(
        string repositoryId, SourceBlamePublishRequest request, CancellationToken ct = default)
        => PublishSourcePathRowAsync(
            "source_blames", repositoryId, request.ProducerId, request.ProducerEpoch,
            request.HeadSha, request.Path,
            new BlameBody(request.Ranges, request.Commits),
            async () => (await GetSourceBlameAsync(repositoryId, request.HeadSha, request.Path, ct))!,
            ct);

    public async Task<SourceBlameDto?> GetSourceBlameAsync(
        string repositoryId, string headSha, string path, CancellationToken ct = default)
    {
        var row = await ReadSourcePathRowAsync<BlameBody>("source_blames", repositoryId, headSha, path, ct);
        return row is null
            ? null
            : new SourceBlameDto(row.Value.Repository, row.Value.Head, row.Value.Path,
                row.Value.Body.Ranges, row.Value.Body.Commits, row.Value.ProducedAt);
    }

    public async Task<SourceReachabilityView> PublishSourceReachabilityAsync(
        string repositoryId, SourceReachabilityPublishRequest request, CancellationToken ct = default)
    {
        var repository = await CanonicalSourceRepositoryAsync(repositoryId, ct);
        if (request.Heads.Count > 64 || request.Facts.Count > 4096)
            throw new ArgumentException("A reachability publish carries at most 64 heads and 4096 facts.");
        var heads = request.Heads
            .Select(head => new SourceRefHead(RequireSourceRef(head.Ref), RequireSourceSha(head.Sha, "head sha")))
            .ToArray();
        var facts = request.Facts
            .Select(fact => new SourceReachabilityFact(
                RequireSourceSha(fact.Sha, "sha"), RequireSourceRef(fact.TargetRef), fact.Contained,
                RequireSourceSha(fact.RefHeadSha, "refHeadSha")))
            .ToArray();
        RequireWritable();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await RequireSourceProducerAsync(connection, transaction, repository, request.ProducerId, request.ProducerEpoch, ct);
            foreach (var head in heads)
                await ExecuteAsync(connection, """
                    INSERT INTO source_ref_heads(repository_id, ref_name, sha, producer_epoch, observed_at)
                    VALUES ($repository, $ref, $sha, $epoch, $now)
                    ON CONFLICT(repository_id, ref_name) DO UPDATE SET
                        sha = excluded.sha, producer_epoch = excluded.producer_epoch, observed_at = excluded.observed_at;
                    """, ct, transaction,
                    ("$repository", repository), ("$ref", head.Ref), ("$sha", head.Sha),
                    ("$epoch", request.ProducerEpoch), ("$now", Iso(UtcNow)));
            foreach (var fact in facts)
                await ExecuteAsync(connection, """
                    INSERT INTO source_reachability(
                        repository_id, target_ref, sha, contained, ref_head_sha, producer_epoch, observed_at)
                    VALUES ($repository, $ref, $sha, $contained, $head, $epoch, $now)
                    ON CONFLICT(repository_id, target_ref, sha) DO UPDATE SET
                        contained = excluded.contained, ref_head_sha = excluded.ref_head_sha,
                        producer_epoch = excluded.producer_epoch, observed_at = excluded.observed_at;
                    """, ct, transaction,
                    ("$repository", repository), ("$ref", fact.TargetRef), ("$sha", fact.Sha),
                    ("$contained", fact.Contained ? 1 : 0), ("$head", fact.RefHeadSha),
                    ("$epoch", request.ProducerEpoch), ("$now", Iso(UtcNow)));
        }, ct);
        return await GetSourceReachabilityAsync(
            repository,
            facts.Select(fact => fact.Sha).Distinct(StringComparer.Ordinal).ToArray(),
            facts.Select(fact => fact.TargetRef).Concat(heads.Select(head => head.Ref))
                .Distinct(StringComparer.Ordinal).ToArray(),
            ct);
    }

    public async Task<SourceReachabilityView> GetSourceReachabilityAsync(
        string repositoryId, IReadOnlyList<string> shas, IReadOnlyList<string> targetRefs,
        CancellationToken ct = default)
    {
        var repository = await CanonicalSourceRepositoryAsync(repositoryId, ct);
        if (shas.Count > 256 || targetRefs.Count > 16)
            throw new ArgumentException("A reachability read asks for at most 256 SHAs and 16 refs.");
        var wantedShas = shas.Select(sha => RequireSourceSha(sha, "sha")).Distinct(StringComparer.Ordinal).ToArray();
        var wantedRefs = targetRefs.Select(RequireSourceRef).Distinct(StringComparer.Ordinal).ToArray();
        var heads = new Dictionary<string, string>(StringComparer.Ordinal);
        var stored = new Dictionary<(string Sha, string Ref), SourceReachabilityFact>();
        await using var connection = await OpenReadyAsync(ct);
        foreach (var target in wantedRefs)
        {
            var head = Convert.ToString(await ScalarAsync(connection, """
                SELECT sha FROM source_ref_heads WHERE repository_id = $repository AND ref_name = $ref;
                """, ct, ("$repository", repository), ("$ref", target)), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(head)) heads[target] = head;
            foreach (var sha in wantedShas)
            {
                await using var command = Command(connection, """
                    SELECT contained, ref_head_sha FROM source_reachability
                     WHERE repository_id = $repository AND target_ref = $ref AND sha = $sha;
                    """, ("$repository", repository), ("$ref", target), ("$sha", sha));
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    stored[(sha, target)] = new SourceReachabilityFact(sha, target, reader.GetInt64(0) == 1, reader.GetString(1));
            }
        }
        var facts = new List<SourceReachabilityFact>();
        var missing = new List<SourceReachabilityPair>();
        foreach (var sha in wantedShas)
        foreach (var target in wantedRefs)
        {
            heads.TryGetValue(target, out var head);
            if (stored.TryGetValue((sha, target), out var fact) && SourceReachabilityPolicy.IsCurrent(fact, head))
                facts.Add(fact);
            else
                missing.Add(new SourceReachabilityPair(sha, target));
        }
        return new SourceReachabilityView(
            repository,
            wantedRefs.Where(heads.ContainsKey).Select(target => new SourceRefHead(target, heads[target])).ToArray(),
            facts,
            missing);
    }

    public async Task<SourceFactDemandDto> RecordSourceFactDemandAsync(
        string repositoryId, SourceFactDemandRequest request, CancellationToken ct = default)
    {
        var repository = await CanonicalSourceRepositoryAsync(repositoryId, ct);
        var normalized = NormalizeSourceDemand(request);
        var payload = JsonSerializer.Serialize(normalized, SourceFactJson);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            repository + "\n" + JsonSerializer.Serialize(normalized with { Reason = string.Empty }, SourceFactJson))))
            .ToLowerInvariant();
        RequireWritable();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                DELETE FROM source_fact_demands WHERE completed_at IS NOT NULL AND completed_at < $cutoff;
                """, ct, transaction, ("$cutoff", Iso(UtcNow - SourceDemandRetention)));
            var exists = await ScalarAsync(connection,
                "SELECT 1 FROM source_fact_demands WHERE demand_key = $key;", ct, transaction, ("$key", key));
            if (exists is null)
            {
                var pending = Convert.ToInt64(await ScalarAsync(connection, """
                    SELECT count(*) FROM source_fact_demands WHERE repository_id = $repository AND completed_at IS NULL;
                    """, ct, transaction, ("$repository", repository)), CultureInfo.InvariantCulture);
                if (pending >= MaximumPendingSourceDemands)
                    throw new TaskServerConflictException(
                        "source-demand-backlog",
                        $"Repository '{repository}' already has {pending} pending source fact demands.");
            }
            // A completed demand whose fact was evicted since then is queued again.
            await ExecuteAsync(connection, """
                INSERT INTO source_fact_demands(repository_id, demand_key, kind, payload_json, reason, requested_at)
                VALUES ($repository, $key, $kind, $payload, $reason, $now)
                ON CONFLICT(demand_key) DO UPDATE SET
                    reason = excluded.reason,
                    requested_at = excluded.requested_at,
                    attempts = CASE WHEN source_fact_demands.completed_at IS NULL THEN source_fact_demands.attempts ELSE 0 END,
                    last_error = CASE WHEN source_fact_demands.completed_at IS NULL THEN source_fact_demands.last_error ELSE NULL END,
                    completed_at = NULL;
                """, ct, transaction,
                ("$repository", repository), ("$key", key), ("$kind", normalized.Kind),
                ("$payload", payload), ("$reason", normalized.Reason), ("$now", Iso(UtcNow)));
        }, ct);
        await using var read = await OpenReadyAsync(ct);
        await using var command = Command(read, SourceDemandSelect + " WHERE demand_key = $key;", ("$key", key));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("The source fact demand was not stored.");
        return ReadSourceDemand(reader);
    }

    public async Task<IReadOnlyList<SourceFactDemandDto>> ListSourceFactDemandsAsync(
        string repositoryId, int limit, CancellationToken ct = default)
    {
        var repository = await CanonicalSourceRepositoryAsync(repositoryId, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, SourceDemandSelect + """
             WHERE repository_id = $repository AND completed_at IS NULL AND attempts < $attempts
             ORDER BY demand_id LIMIT $limit;
            """, ("$repository", repository), ("$attempts", MaximumSourceDemandAttempts),
            ("$limit", Math.Clamp(limit, 1, 100)));
        var demands = new List<SourceFactDemandDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) demands.Add(ReadSourceDemand(reader));
        return demands;
    }

    public async Task<SourceFactDemandDto> CompleteSourceFactDemandAsync(
        string repositoryId, long demandId, SourceFactDemandCompleteRequest request, CancellationToken ct = default)
    {
        var repository = await CanonicalSourceRepositoryAsync(repositoryId, ct);
        RequireWritable();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await RequireSourceProducerAsync(connection, transaction, repository, request.ProducerId, request.ProducerEpoch, ct);
            var updated = await ExecuteAsync(connection, """
                UPDATE source_fact_demands
                   SET attempts = attempts + 1,
                       last_error = $error,
                       completed_at = CASE WHEN $succeeded = 1 THEN $now ELSE NULL END
                 WHERE demand_id = $id AND repository_id = $repository;
                """, ct, transaction,
                ("$error", request.Succeeded ? null : Bounded(request.Error, 1024) ?? "failed"),
                ("$succeeded", request.Succeeded ? 1 : 0), ("$now", Iso(UtcNow)),
                ("$id", demandId), ("$repository", repository));
            if (updated == 0) throw new KeyNotFoundException($"Source fact demand {demandId} was not found.");
        }, ct);
        await using var read = await OpenReadyAsync(ct);
        await using var command = Command(read, SourceDemandSelect + " WHERE demand_id = $id;", ("$id", demandId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException($"Source fact demand {demandId} was not found.");
        return ReadSourceDemand(reader);
    }

    public async Task RecordSourceReviewMaterialReadAsync(
        string repositoryId, SourceReviewMaterialReadReport report, CancellationToken ct = default)
    {
        var repository = await CanonicalSourceRepositoryAsync(repositoryId, ct);
        if (report.Source is not (SourceReviewMaterialSources.Object or SourceReviewMaterialSources.Fallback))
            throw new ArgumentException("source must be 'object' or 'fallback'.");
        if (report.ElapsedMilliseconds < 0) throw new ArgumentException("elapsedMilliseconds must not be negative.");
        var baseSha = RequireSourceSha(report.BaseSha, "baseSha");
        var headSha = RequireSourceSha(report.HeadSha, "headSha");
        RequireWritable();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                DELETE FROM source_review_material_reads WHERE recorded_at < $cutoff;
                INSERT INTO source_review_material_reads(
                    repository_id, base_sha, head_sha, source, elapsed_ms, miss_reason, recorded_at)
                VALUES ($repository, $base, $head, $source, $elapsed, $reason, $now);
                """, ct, transaction,
                ("$cutoff", Iso(UtcNow - SourceReadReportRetention)),
                ("$repository", repository), ("$base", baseSha), ("$head", headSha),
                ("$source", report.Source), ("$elapsed", report.ElapsedMilliseconds),
                ("$reason", Bounded(report.MissReason, 512)), ("$now", Iso(UtcNow)));
        }, ct);
    }

    public async Task<SourceReviewMaterialTimingDto> GetSourceReviewMaterialTimingAsync(
        string? repositoryId, DateTime? sinceUtc, CancellationToken ct = default)
    {
        var repository = string.IsNullOrWhiteSpace(repositoryId) ? null : await CanonicalSourceRepositoryAsync(repositoryId, ct);
        var since = (sinceUtc ?? UtcNow.AddDays(-7)).ToUniversalTime();
        var objectReads = new List<long>();
        var fallbackReads = new List<long>();
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT source, elapsed_ms FROM source_review_material_reads
             WHERE recorded_at >= $since AND ($repository IS NULL OR repository_id = $repository);
            """, ("$since", Iso(since)), ("$repository", repository));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            (reader.GetString(0) == SourceReviewMaterialSources.Object ? objectReads : fallbackReads)
                .Add(reader.GetInt64(1));
        var all = objectReads.Concat(fallbackReads).ToArray();
        return new SourceReviewMaterialTimingDto(
            repository, since, all.Length, objectReads.Count, fallbackReads.Count,
            SourceTimingPercentile.NearestRank(all, 50), SourceTimingPercentile.NearestRank(all, 95),
            SourceTimingPercentile.NearestRank(objectReads, 95), SourceTimingPercentile.NearestRank(fallbackReads, 95));
    }

    private const string SourceDemandSelect = """
        SELECT demand_id, repository_id, payload_json, attempts, last_error, requested_at
          FROM source_fact_demands
        """;

    private static SourceFactDemandDto ReadSourceDemand(SqliteDataReader reader)
    {
        var payload = JsonSerializer.Deserialize<SourceFactDemandRequest>(reader.GetString(2), SourceFactJson)
            ?? throw new InvalidDataException("Stored source fact demand is empty.");
        return new SourceFactDemandDto(
            reader.GetInt64(0), reader.GetString(1), payload.Kind, payload.Reason, payload.BaseSha,
            payload.HeadSha, payload.Path, payload.Shas ?? [], payload.TargetRefs ?? [], payload.FetchRef,
            (int)reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4), Parse(reader.GetString(5)));
    }

    private static SourceFactDemandRequest NormalizeSourceDemand(SourceFactDemandRequest request)
    {
        if (!SourceFactKinds.IsKnown(request.Kind))
            throw new ArgumentException($"Unknown source fact kind '{request.Kind}'.");
        var reason = RequireSourceText(request.Reason, "reason", 256);
        var shas = (request.Shas ?? []).Select(sha => RequireSourceSha(sha, "shas")).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var refs = (request.TargetRefs ?? []).Select(RequireSourceRef).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (shas.Length > 256 || refs.Length > 16)
            throw new ArgumentException("A demand names at most 256 SHAs and 16 refs.");
        var normalized = new SourceFactDemandRequest(
            request.Kind,
            reason,
            string.IsNullOrWhiteSpace(request.BaseSha) ? null : RequireSourceSha(request.BaseSha, "baseSha"),
            string.IsNullOrWhiteSpace(request.HeadSha) ? null : RequireSourceSha(request.HeadSha, "headSha"),
            string.IsNullOrWhiteSpace(request.Path) ? null : RequireSourcePath(request.Path),
            shas,
            refs,
            string.IsNullOrWhiteSpace(request.FetchRef) ? null : RequireSourceRef(request.FetchRef));
        var complete = normalized.Kind switch
        {
            SourceFactKinds.Diff => normalized.BaseSha is not null && normalized.HeadSha is not null,
            SourceFactKinds.PathHistory or SourceFactKinds.Blame => normalized.HeadSha is not null && normalized.Path is not null,
            SourceFactKinds.Reachability => shas.Length > 0 && refs.Length > 0,
            _ => false,
        };
        return complete
            ? normalized
            : throw new ArgumentException($"A '{normalized.Kind}' demand is missing its key fields.");
    }

    private async Task<TDto> PublishSourcePathRowAsync<TBody, TDto>(
        string table, string repositoryId, string producerId, long producerEpoch, string headSha, string path,
        TBody body, Func<Task<TDto>> read, CancellationToken ct)
    {
        var repository = await CanonicalSourceRepositoryAsync(repositoryId, ct);
        var head = RequireSourceSha(headSha, "headSha");
        var normalizedPath = RequireSourcePath(path);
        var json = JsonSerializer.Serialize(body, SourceFactJson);
        RequireWritable();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await RequireSourceProducerAsync(connection, transaction, repository, producerId, producerEpoch, ct);
            // The row is a pure function of (head, path); a second publish of
            // the same key keeps the first answer.
            await ExecuteAsync(connection, $"""
                INSERT INTO {table}(
                    repository_id, head_sha, path, body_json, size_bytes, producer_id, producer_epoch,
                    produced_at, last_read_at)
                VALUES ($repository, $head, $path, $body, $size, $producer, $epoch, $now, $now)
                ON CONFLICT(repository_id, head_sha, path) DO NOTHING;
                """, ct, transaction,
                ("$repository", repository), ("$head", head), ("$path", normalizedPath), ("$body", json),
                ("$size", Encoding.UTF8.GetByteCount(json)), ("$producer", producerId.Trim()),
                ("$epoch", producerEpoch), ("$now", Iso(UtcNow)));
            await EvictSourceFactsAsync(connection, transaction, ct);
        }, ct);
        return await read();
    }

    private async Task<(string Repository, string Head, string Path, TBody Body, DateTime ProducedAt)?> ReadSourcePathRowAsync<TBody>(
        string table, string repositoryId, string headSha, string path, CancellationToken ct)
    {
        var repository = await CanonicalSourceRepositoryAsync(repositoryId, ct);
        var head = RequireSourceSha(headSha, "head");
        var normalizedPath = RequireSourcePath(path);
        (string, string, string, TBody, DateTime)? found = null;
        long rowId = 0;
        DateTime lastRead = default;
        await using (var connection = await OpenReadyAsync(ct))
        await using (var command = Command(connection, $"""
            SELECT row_id, body_json, produced_at, last_read_at FROM {table}
             WHERE repository_id = $repository AND head_sha = $head AND path = $path;
            """, ("$repository", repository), ("$head", head), ("$path", normalizedPath)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                rowId = reader.GetInt64(0);
                var body = JsonSerializer.Deserialize<TBody>(reader.GetString(1), SourceFactJson)
                    ?? throw new InvalidDataException($"Stored {table} body is empty.");
                lastRead = Parse(reader.GetString(3));
                found = (repository, head, normalizedPath, body, Parse(reader.GetString(2)));
            }
        }
        if (found is not null) await TouchSourceRowAsync(table, rowId, lastRead, ct);
        return found;
    }

    /// <summary>
    /// Records a read for least-recently-read eviction. The write happens at
    /// most once per hour per row, so a hot object does not turn every read
    /// into a write transaction.
    /// </summary>
    private async Task TouchSourceRowAsync(string table, long rowId, DateTime lastRead, CancellationToken ct)
    {
        if (UtcNow - lastRead < SourceReadTouchInterval
            || _mode is TaskServerMode.ReadOnly or TaskServerMode.Maintenance) return;
        await InWriteTransactionAsync(async (connection, transaction) =>
            await ExecuteAsync(connection, $"UPDATE {table} SET last_read_at = $now WHERE row_id = $id;",
                ct, transaction, ("$now", Iso(UtcNow)), ("$id", rowId)), ct);
    }

    private async Task EvictSourceFactsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var maximum = _options.SourceFactsMaxStoredBytes;
        var total = Convert.ToInt64(await ScalarAsync(connection, """
            SELECT (SELECT coalesce(sum(size_bytes), 0) FROM source_diff_objects)
                 + (SELECT coalesce(sum(size_bytes), 0) FROM source_path_histories)
                 + (SELECT coalesce(sum(size_bytes), 0) FROM source_blames);
            """, ct, transaction), CultureInfo.InvariantCulture);
        if (maximum <= 0 || total <= maximum) return;
        var stored = new List<SourceFactEvictionPolicy.StoredObject>();
        await using (var command = Command(connection, """
            SELECT 'source_diff_objects', row_id, size_bytes, last_read_at FROM source_diff_objects
            UNION ALL SELECT 'source_path_histories', row_id, size_bytes, last_read_at FROM source_path_histories
            UNION ALL SELECT 'source_blames', row_id, size_bytes, last_read_at FROM source_blames;
            """, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                stored.Add(new SourceFactEvictionPolicy.StoredObject(
                    reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), Parse(reader.GetString(3))));
        }
        var victims = SourceFactEvictionPolicy.SelectVictims(stored, maximum);
        foreach (var victim in victims)
            await ExecuteAsync(connection, $"DELETE FROM {victim.Table} WHERE row_id = $id;",
                ct, transaction, ("$id", victim.RowId));
        if (victims.Count > 0)
            _logger?.LogInformation(
                "source-facts-evicted objects={Objects} bytes={Bytes} cap={Cap}",
                victims.Count, victims.Sum(victim => victim.SizeBytes), maximum);
    }

    private static async Task<SourceProducerLeasePolicy.Current?> ReadSourceProducerAsync(
        SqliteConnection connection, SqliteTransaction transaction, string repository, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT producer_id, producer_epoch, expires_at FROM source_fact_producers WHERE repository_id = $repository;
            """, transaction, ("$repository", repository));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new SourceProducerLeasePolicy.Current(reader.GetString(0), reader.GetInt64(1), Parse(reader.GetString(2)))
            : null;
    }

    private static async Task RequireSourceProducerAsync(
        SqliteConnection connection, SqliteTransaction transaction, string repository,
        string producerId, long epoch, CancellationToken ct)
    {
        var current = await ReadSourceProducerAsync(connection, transaction, repository, ct);
        if (!SourceProducerLeasePolicy.AdmitsPublish(current, producerId?.Trim() ?? string.Empty, epoch))
            throw new TaskServerConflictException(
                "stale-source-producer",
                current is null
                    ? $"No producer holds the source facts lease of '{repository}'."
                    : $"Producer '{producerId}' at epoch {epoch} is not the current holder of '{repository}' (epoch {current.Epoch}).");
    }

    /// <summary>
    /// Runner hosts address a repository by its Task Server project id. The
    /// monolith Studio only knows the project name, so a name that matches
    /// exactly one project resolves to that project's id; any other value is
    /// used as given.
    /// </summary>
    private async Task<string> CanonicalSourceRepositoryAsync(string? repositoryId, CancellationToken ct)
    {
        var requested = RequireSourceRepository(repositoryId);
        await using var connection = await OpenReadyAsync(ct);
        if (await ScalarAsync(connection, "SELECT 1 FROM projects WHERE id = $value;", ct, ("$value", requested)) is not null)
            return requested;
        await using var command = Command(connection, "SELECT id FROM projects WHERE name = $value LIMIT 2;", ("$value", requested));
        var matches = new List<string>(2);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) matches.Add(reader.GetString(0));
        return matches.Count == 1 ? matches[0] : requested;
    }

    private static string RequireSourceRepository(string? repositoryId)
        => RequireSourceText(repositoryId, "repositoryId", 128);

    private static string RequireSourceText(string? value, string name, int maximumLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maximumLength)
            throw new ArgumentException($"{name} must be nonempty and at most {maximumLength} characters.");
        return trimmed;
    }

    private static string RequireSourceSha(string? value, string name)
    {
        var trimmed = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (trimmed.Length is not (40 or 64) || !trimmed.All(Uri.IsHexDigit))
            throw new ArgumentException($"{name} must be a full 40 or 64 character commit SHA.");
        return trimmed;
    }

    private static string RequireSourceRef(string? value)
    {
        var trimmed = RequireSourceText(value, "ref", 256);
        if (trimmed.Contains("..", StringComparison.Ordinal) || trimmed.Any(char.IsWhiteSpace)
            || trimmed.StartsWith('-') || trimmed.Contains(':'))
            throw new ArgumentException($"'{trimmed}' is not a valid ref name.");
        return trimmed;
    }

    private static string RequireSourcePath(string? value)
    {
        var trimmed = RequireSourceText(value, "path", 4096).Replace('\\', '/');
        if (trimmed.StartsWith('/') || trimmed.Split('/').Any(segment => segment is ".." or ""))
            throw new ArgumentException("path must be a repository-relative path.");
        return trimmed;
    }

    private static string? Bounded(string? value, int maximumLength)
        => string.IsNullOrWhiteSpace(value) ? null
            : value.Length <= maximumLength ? value : value[..maximumLength];

    private sealed record DiffBody(
        IReadOnlyList<string> ChangedFiles,
        IReadOnlyList<string> DiffLines,
        int TotalFiles,
        int TotalDiffLines,
        int MaximumFiles,
        int MaximumLines);

    private sealed record PathHistoryBody(IReadOnlyList<SourcePathHistoryEntry> Entries, bool Truncated);

    private sealed record BlameBody(IReadOnlyList<SourceBlameRange> Ranges, IReadOnlyList<SourceBlameCommit> Commits);
}
