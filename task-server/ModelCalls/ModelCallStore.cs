using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tokens;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

internal sealed class ModelCallStore(IOptions<TaskServerOptions> options, ITokenPricing pricing, TimeProvider clock)
{
    private const long UsdScale = 1_000_000_000;
    private const int GroupLimit = 100;
    private const int ResponseLimit = 50;
    private readonly SemaphoreSlim writer = new(1);
    private readonly SemaphoreSlim readers = new(4);
    private volatile bool ready;

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var directory = options.Value.ResolveDataDirectory();
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "model-calls.sqlite"),
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString());
        try
        {
            await connection.OpenAsync(ct);
            if (!ready)
            {
                await using var command = Command(connection, null, "PRAGMA journal_mode = WAL;");
                await command.ExecuteNonQueryAsync(ct);
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = ModelCallSchema.Create;
                await command.ExecuteNonQueryAsync(ct);
                transaction.Commit();
                ready = true;
            }
            await using var durability = Command(connection, null, "PRAGMA synchronous = FULL;");
            await durability.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // An empty ledger can answer without creating a file. Serialize schema setup with the writer.
    private async Task<SqliteConnection?> OpenReadAsync(CancellationToken ct)
    {
        if (!ready && !File.Exists(Path.Combine(options.Value.ResolveDataDirectory(), "model-calls.sqlite")))
            return null;
        if (ready) return await OpenAsync(ct);

        await writer.WaitAsync(ct);
        try { return await OpenAsync(ct); }
        finally { writer.Release(); }
    }

    public static bool IsCard(string? card) => Matches(card, "[A-Z][A-Z0-9]{0,11}-[1-9][0-9]{0,9}");
    private static bool IsHash(string? id) => Matches(id, "[0-9a-f]{64}");
    private static bool Matches(string? value, string pattern)
        => value is { Length: > 0 and <= 64 } && Regex.IsMatch(value, $"\\A(?:{pattern})\\z", RegexOptions.CultureInvariant);

    public static void Validate(HistoricalModelCallBatch batch)
    {
        if (!IsHash(batch.Fingerprint) || batch.Calls is not { Count: > 0 and <= 25_000 })
            throw new ArgumentException("A SHA-256 dataset fingerprint and between 1 and 25,000 calls are required.");
        var keys = new HashSet<(string, int, int)>();
        foreach (var call in batch.Calls)
        {
            if (call is null || !IsCard(call.Card) || call.Run is < 1 or > 100_000 ||
                call.Ordinal is < 1 or > 100_000 || call.Revision is < 1 or > 10_000 ||
                call.HappenedAtMs is < 0 or > 253402300799999 ||
                call.Kind is not ("work" or "wait" or "status" or "unknown") ||
                (call.Model is not null && !Matches(call.Model, "gpt-[a-z0-9][a-z0-9.-]*")) ||
                call.ReasoningLevel is not (null or "none" or "minimal" or "low" or "medium" or "high" or "xhigh") ||
                call.ServiceTier is not (null or "standard") ||
                (call.ResponseAlias is not null && !IsHash(call.ResponseAlias)) ||
                new[] { call.PromptTokens, call.CachedInput, call.Output, call.Reasoning }
                    .Any(value => value is < 0 or > 100_000_000) ||
                call.CachedInput > call.PromptTokens || call.Reasoning > call.Output ||
                !keys.Add((call.Card, call.Run, call.Ordinal)))
                throw new ArgumentException("A historical call contains invalid or duplicate fields.");
        }
    }

    public async Task<HistoricalModelCallAck> ImportAsync(
        ProjectDto project, HistoricalModelCallBatch batch, IReadOnlyDictionary<string, string> tasks,
        string actor, CancellationToken ct)
    {
        var received = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (batch.Calls.Any(call => call.HappenedAtMs > received))
            throw new ArgumentException("Historical calls must already have happened.");

        await writer.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction();
            await using var import = Command(connection, transaction, """
                INSERT INTO model_call_import (workspace_id, project_id, fingerprint, first_received_ms, actor_id)
                VALUES ($workspace, $project, $fingerprint, $received, $actor)
                ON CONFLICT (workspace_id, project_id, fingerprint) DO NOTHING;
                """, ("$workspace", project.WorkspaceId), ("$project", project.ProjectId),
                ("$fingerprint", batch.Fingerprint), ("$received", received), ("$actor", actor));
            await import.ExecuteNonQueryAsync(ct);
            using var write = new ImportCommands(connection, transaction, project, batch.Fingerprint, received);
            int inserted = 0, revised = 0, replayed = 0, aliased = 0;
            foreach (var call in batch.Calls)
            {
                ct.ThrowIfCancellationRequested();
                var id = StableId(project, batch.Fingerprint, call);
                var taskId = tasks[call.Card];
                write.Bind(id, taskId, call);
                var existing = await write.FindAsync(ct);
                if (existing is null)
                {
                    if (call.Revision != 1) throw Conflict("A revision requires an existing call.");
                    await write.Insert.ExecuteNonQueryAsync(ct);
                    inserted++;
                }
                else if (existing.Value.Task != taskId || existing.Value.At != call.HappenedAtMs)
                    throw Conflict("Immutable historical call fields conflict.");
                else if (call.Revision <= existing.Value.Revision)
                {
                    if (await write.Identical.ExecuteScalarAsync(ct) is null)
                        throw Conflict("A historical call revision conflicts with recorded usage.");
                    replayed++;
                }
                else
                {
                    if (call.Revision != existing.Value.Revision + 1)
                        throw Conflict("Call revisions must be consecutive.");
                    await write.Advance.ExecuteNonQueryAsync(ct);
                    revised++;
                }
                if (existing is null || call.Revision > existing.Value.Revision)
                {
                    var (status, nanos, evidence) = ResolvePrice(call);
                    write.BindPrice(status, nanos, evidence);
                    await write.Revise.ExecuteNonQueryAsync(ct);
                }
                if (call.ResponseAlias is not null)
                {
                    aliased += await write.Alias.ExecuteNonQueryAsync(ct);
                    if (await write.AliasMatches.ExecuteScalarAsync(ct) is null)
                        throw Conflict("Response alias already identifies a different historical call.");
                }
            }
            transaction.Commit();
            return new HistoricalModelCallAck(inserted, revised, replayed, aliased);
        }
        finally { writer.Release(); }
    }

    private static TaskServerConflictException Conflict(string message)
        => new("model-call-import-conflict", message);

    private static string StableId(ProjectDto project, string fingerprint, HistoricalModelCall call)
    {
        var key = JsonSerializer.Serialize(new object[]
            { "codex-historical-v1", project.WorkspaceId, project.ProjectId, fingerprint, call.Card, call.Run, call.Ordinal });
        return "mc_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private (string Status, long? Nanos, string? Evidence) ResolvePrice(HistoricalModelCall call)
    {
        if (call.Model is null) return ("unknown-model", null, null);
        if (call.PromptTokens is not { } prompt || call.CachedInput is not { } cached || call.Output is not { } output)
            return ("incomplete-usage", null, null);

        var estimate = pricing.EstimateRequest(call.Model, prompt - cached, output, cached, 0, prompt,
            DateTimeOffset.FromUnixTimeMilliseconds(call.HappenedAtMs).UtcDateTime);
        if (!estimate.ModelKnown || estimate.PriceBasis is not { Currency: "USD" } basis)
            return (estimate.Status.ToString(), null, null);
        if (basis.Unconfirmed) return ("unconfirmed-tariff", null, null);
        if (call.ServiceTier is null) return ("unknown-service-tier", null, null);
        // The local seeds document the threshold without providing a computable tier.
        if (prompt > 272_000 && basis.Note?.StartsWith("Standard short-context API tariff", StringComparison.Ordinal) == true)
            return ("unsupported-long-context", null, null);
        var evidence = new ModelCallPriceEvidence(TokenEconomyPriceProvider.Version, "response-time-fallback",
            basis.Currency, basis.ValidFrom, basis.Source, basis.InputPerMillion, basis.CacheReadPerMillion,
            basis.OutputPerMillion, estimate.InputUsd, estimate.CacheReadUsd, estimate.OutputUsd);
        return ("priced", decimal.ToInt64(decimal.Round(estimate.Total * UsdScale, 0)),
            JsonSerializer.Serialize(evidence));
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Key, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (key, value) in parameters)
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }

    private sealed class ImportCommands : IDisposable
    {
        private readonly SqliteCommand find;
        private readonly SqliteCommand[] commands;
        public SqliteCommand Insert { get; }
        public SqliteCommand Identical { get; }
        public SqliteCommand Advance { get; }
        public SqliteCommand Revise { get; }
        public SqliteCommand Alias { get; }
        public SqliteCommand AliasMatches { get; }

        public ImportCommands(SqliteConnection connection, SqliteTransaction transaction, ProjectDto project,
            string fingerprint, long received)
        {
            find = Prepare("SELECT task_id, happened_at_ms, current_revision FROM model_call WHERE id = $id;");
            Insert = Prepare("""
                INSERT INTO model_call (id, workspace_id, project_id, fingerprint, task_id, card, run, ordinal, happened_at_ms, current_revision)
                VALUES ($id, $workspace, $project, $fingerprint, $task, $card, $run, $ordinal, $at, $revision);
                """);
            Identical = Prepare("""
                SELECT 1 FROM model_call_revision WHERE call_id = $id AND revision = $revision
                   AND kind = $kind AND model IS $model AND reasoning_level IS $level AND service_tier IS $tier
                   AND prompt_tokens IS $prompt AND cached_input IS $cached AND output IS $output AND reasoning IS $reasoning;
                """);
            Advance = Prepare("UPDATE model_call SET current_revision = $revision WHERE id = $id;");
            Revise = Prepare("""
                INSERT INTO model_call_revision (call_id, revision, received_ms, kind, model, reasoning_level, service_tier,
                    prompt_tokens, fresh_input, cached_input, output, reasoning, total_tokens, price_status, usd_nanos, price_evidence)
                VALUES ($id, $revision, $received, $kind, $model, $level, $tier, $prompt, $prompt - $cached,
                    $cached, $output, $reasoning, $prompt + $output, $status, $nanos, $evidence);
                """);
            Alias = Prepare("""
                INSERT INTO model_call_alias (workspace_id, project_id, provider, response_alias, call_id)
                VALUES ($workspace, $project, 'openai', $alias, $id) ON CONFLICT DO NOTHING;
                """);
            AliasMatches = Prepare("""
                SELECT 1 FROM model_call_alias
                 WHERE workspace_id = $workspace AND project_id = $project AND provider = 'openai'
                   AND response_alias = $alias AND call_id = $id;
                """);
            commands = [find, Insert, Identical, Advance, Revise, Alias, AliasMatches];

            SqliteCommand Prepare(string sql)
            {
                var cmd = Command(connection, transaction, sql);
                foreach (Match parameter in Regex.Matches(sql, @"\$[a-z]+"))
                    if (!cmd.Parameters.Contains(parameter.Value))
                        cmd.Parameters.AddWithValue(parameter.Value, DBNull.Value);
                Set(cmd, "$workspace", project.WorkspaceId);
                Set(cmd, "$project", project.ProjectId);
                Set(cmd, "$fingerprint", fingerprint);
                Set(cmd, "$received", received);
                cmd.Prepare();
                return cmd;
            }
        }

        private static void Set(SqliteCommand cmd, string parameter, object? value)
        {
            if (cmd.Parameters.Contains(parameter)) cmd.Parameters[parameter].Value = value ?? DBNull.Value;
        }

        public void Bind(string id, string taskId, HistoricalModelCall call)
        {
            var values = new (string Key, object? Value)[]
            {
                ("$id", id), ("$task", taskId), ("$card", call.Card), ("$run", call.Run), ("$ordinal", call.Ordinal),
                ("$at", call.HappenedAtMs), ("$revision", call.Revision), ("$kind", call.Kind), ("$model", call.Model),
                ("$level", call.ReasoningLevel), ("$tier", call.ServiceTier), ("$prompt", call.PromptTokens),
                ("$cached", call.CachedInput), ("$output", call.Output), ("$reasoning", call.Reasoning),
                ("$alias", call.ResponseAlias),
            };
            foreach (var cmd in commands)
            foreach (var (key, value) in values)
                Set(cmd, key, value);
        }

        public void BindPrice(string status, long? nanos, string? evidence)
        {
            Set(Revise, "$status", status);
            Set(Revise, "$nanos", nanos);
            Set(Revise, "$evidence", evidence);
        }

        public async Task<(string Task, long At, int Revision)?> FindAsync(CancellationToken ct)
        {
            await using var row = await find.ExecuteReaderAsync(ct);
            return await row.ReadAsync(ct) ? (row.GetString(0), row.GetInt64(1), row.GetInt32(2)) : null;
        }

        public void Dispose()
        {
            foreach (var command in commands) command.Dispose();
        }
    }

    private const string ScopedCalls = """
        FROM model_call c
        JOIN model_call_revision r ON r.call_id = c.id AND r.revision = c.current_revision
        WHERE c.workspace_id = $workspace AND c.project_id = $project
        """;

    private const string WindowFilter = """
        AND c.happened_at_ms >= $from AND c.happened_at_ms < $until
        AND ($card IS NULL OR c.card = $card)
        AND ($run IS NULL OR c.run = $run)
        """;

    private const string Totals = """
        COUNT(*), COALESCE(SUM(r.total_tokens), 0),
        COUNT(CASE WHEN r.kind = 'wait' THEN 1 END), COUNT(CASE WHEN r.kind = 'status' THEN 1 END),
        COALESCE(SUM(r.fresh_input), 0), COALESCE(SUM(r.cached_input), 0), COALESCE(SUM(r.output), 0),
        COUNT(r.usd_nanos), COALESCE(SUM(CASE WHEN r.usd_nanos IS NULL THEN r.total_tokens END), 0),
        COUNT(*) - COUNT(r.total_tokens), SUM(r.usd_nanos)
        """;

    public static void ValidateWindow(long fromMs, long untilMs, string? card, int? run, string? cursor)
    {
        if (fromMs < 0 || untilMs > 253402300799999 || fromMs >= untilMs ||
            untilMs - fromMs > TimeSpan.FromDays(31).TotalMilliseconds)
            throw new ArgumentException("Choose a UTC window of at most 31 days.");
        if (card is not null && !IsCard(card) || run is <= 0 || run.HasValue && card is null || cursor is not null && !run.HasValue)
            throw new ArgumentException("Choose a valid card and run for response paging.");
        if (cursor is not null) DecodeCursor(cursor);
    }

    public async Task<ModelCallWindow> QueryAsync(ProjectDto project, long fromMs, long untilMs,
        string? card, int? run, string? cursor, CancellationToken ct)
    {
        var (afterMs, afterId) = cursor is null ? (0L, "") : DecodeCursor(cursor);
        await readers.WaitAsync(ct);
        try
        {
            await using var connection = await OpenReadAsync(ct);
            if (connection is null) return new ModelCallWindow(new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null), [], null, [], null);
            using var transaction = connection.BeginTransaction(deferred: true);
            SqliteCommand WindowCommand(string sql) => Command(connection, transaction, sql,
                ("$workspace", project.WorkspaceId), ("$project", project.ProjectId), ("$from", fromMs),
                ("$until", untilMs), ("$card", card), ("$run", run));

            ModelCallTotals totals;
            await using (var summary = WindowCommand($"SELECT {Totals} {ScopedCalls} {WindowFilter};"))
            await using (var row = await summary.ExecuteReaderAsync(ct))
            {
                await row.ReadAsync(ct);
                totals = ReadTotals(row);
            }

            if (run.HasValue)
            {
                var responses = new List<ModelCallRow>();
                await using var command = WindowCommand($"""
                    SELECT c.id, c.ordinal, c.happened_at_ms, r.kind, r.total_tokens, r.price_status
                    {ScopedCalls} {WindowFilter}
                    AND (c.happened_at_ms, c.id) > ($after, $afterId)
                    ORDER BY c.happened_at_ms, c.id LIMIT {ResponseLimit + 1};
                    """);
                command.Parameters.AddWithValue("$after", afterMs);
                command.Parameters.AddWithValue("$afterId", afterId);
                await using var rows = await command.ExecuteReaderAsync(ct);
                while (await rows.ReadAsync(ct))
                    responses.Add(new ModelCallRow(rows.GetString(0), rows.GetInt32(1), rows.GetInt64(2),
                        rows.GetString(3), Long(rows, 4), rows.GetString(5)));
                string? next = null;
                if (responses.Count > ResponseLimit)
                {
                    responses.RemoveAt(ResponseLimit);
                    next = Convert.ToBase64String(Encoding.ASCII.GetBytes(
                        FormattableString.Invariant($"{responses[^1].HappenedAtMs}:{responses[^1].Id}")));
                }
                return new ModelCallWindow(totals, [], null, responses, next);
            }

            var groupKey = card is null ? "c.card" : "c.run";
            var groups = new List<ModelCallGroup>();
            var remaining = totals;
            await using (var command = WindowCommand($"""
                SELECT CAST({groupKey} AS TEXT), {Totals}
                {ScopedCalls} {WindowFilter}
                GROUP BY {groupKey} ORDER BY COALESCE(SUM(r.total_tokens), 0) DESC, {groupKey} LIMIT {GroupLimit};
                """))
            await using (var rows = await command.ExecuteReaderAsync(ct))
            {
                while (await rows.ReadAsync(ct))
                {
                    var group = ReadTotals(rows, 1);
                    groups.Add(new ModelCallGroup(rows.GetString(0), group));
                    remaining = Subtract(remaining, group);
                }
            }
            return new ModelCallWindow(totals, groups, remaining.Calls == 0 ? null : remaining, [], null);
        }
        finally { readers.Release(); }
    }

    private static (long, string) DecodeCursor(string cursor)
    {
        if (cursor.Length <= 128)
        {
            Span<byte> bytes = stackalloc byte[96];
            if (Convert.TryFromBase64String(cursor, bytes, out var written))
            {
                var parts = Encoding.ASCII.GetString(bytes[..written]).Split(':');
                if (parts is [var timestamp, var id] && IsCallId(id) &&
                    long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var at))
                    return (at, id);
            }
        }
        throw new ArgumentException("The response cursor is invalid.");
    }

    public static bool IsCallId(string id) => id.StartsWith("mc_", StringComparison.Ordinal) && IsHash(id[3..]);

    private static long? Long(SqliteDataReader row, int i) => row.IsDBNull(i) ? null : row.GetInt64(i);
    private static string? String(SqliteDataReader row, int i) => row.IsDBNull(i) ? null : row.GetString(i);
    private static decimal? Usd(SqliteDataReader row, int i) => Long(row, i) / (decimal)UsdScale;

    private static ModelCallTotals ReadTotals(SqliteDataReader row, int i = 0)
        => new(row.GetInt64(i), row.GetInt64(i + 1), row.GetInt64(i + 2), row.GetInt64(i + 3),
            row.GetInt64(i + 4), row.GetInt64(i + 5), row.GetInt64(i + 6),
            row.GetInt64(i + 7), row.GetInt64(i + 8), row.GetInt64(i + 9), Usd(row, i + 10));

    private static ModelCallTotals Subtract(ModelCallTotals a, ModelCallTotals b)
        => new(a.Calls - b.Calls, a.Tokens - b.Tokens, a.Waits - b.Waits, a.StatusChecks - b.StatusChecks,
            a.FreshInput - b.FreshInput, a.CachedInput - b.CachedInput, a.Output - b.Output,
            a.PricedCalls - b.PricedCalls, a.UnpricedTokens - b.UnpricedTokens, a.MissingUsage - b.MissingUsage,
            a.PricedCalls == b.PricedCalls ? null : (a.ListUsd ?? 0) - (b.ListUsd ?? 0));

    public async Task<ModelCallDetail?> DetailAsync(ProjectDto project, string id, CancellationToken ct)
    {
        if (!IsCallId(id)) return null;
        await readers.WaitAsync(ct);
        try
        {
            await using var connection = await OpenReadAsync(ct);
            if (connection is null) return null;
            await using var command = Command(connection, null, $"""
                SELECT c.id, c.card, c.task_id, c.run, c.ordinal, c.happened_at_ms,
                       EXISTS(SELECT 1 FROM model_call_alias a WHERE a.call_id = c.id),
                       r.kind, r.model, r.reasoning_level, r.service_tier, r.prompt_tokens,
                       r.fresh_input, r.cached_input, r.output, r.reasoning, r.price_status,
                       r.usd_nanos, r.price_evidence, r.revision, c.fingerprint
                {ScopedCalls} AND c.id = $id;
                """, ("$workspace", project.WorkspaceId), ("$project", project.ProjectId), ("$id", id));
            await using var row = await command.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) return null;
            var aliased = row.GetBoolean(6);
            var kind = row.GetString(7);
            return new ModelCallDetail(row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt32(3),
                row.GetInt32(4), row.GetInt64(5), "response-time-observed",
                row.GetInt32(19) > 1 ? "historical-usage-revision" : aliased ? "codex-response-record" : "codex-export-response",
                "inferred-export-turn-join", kind, kind == "unknown" ? null : "agt-4375-v1",
                String(row, 8), String(row, 9), String(row, 10), Long(row, 11), Long(row, 12), Long(row, 13),
                Long(row, 14), Long(row, 15), row.GetString(16), Usd(row, 17),
                String(row, 18) is { } evidence ? JsonSerializer.Deserialize<ModelCallPriceEvidence>(evidence) : null,
                aliased, row.GetInt32(19), row.GetString(20), null);
        }
        finally { readers.Release(); }
    }
}
