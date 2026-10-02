using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static async Task ApplyFailureFingerprintMigrationAsync(
        SqliteConnection connection, CancellationToken ct)
        => await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS failure_fingerprint_events(
                report_key TEXT PRIMARY KEY,
                fingerprint TEXT NOT NULL,
                seen_at TEXT NOT NULL,
                executor TEXT NOT NULL,
                card_key TEXT NOT NULL,
                source TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_failure_fingerprint_events_lookup
                ON failure_fingerprint_events(fingerprint, seen_at);
            """, ct);

    public async Task<FailureFingerprintHistoryDto> RecordFailureFingerprintAsync(
        RecordFailureFingerprintRequest request, CancellationToken ct = default)
    {
        ValidateFingerprintEvent(request);
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenReadyAsync(ct);
            await using (var existing = Command(connection, """
                SELECT fingerprint, card_key, executor, source
                  FROM failure_fingerprint_events WHERE report_key = $report;
                """, ("$report", request.ReportKey.Trim())))
            await using (var reader = await existing.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct)
                    && (!string.Equals(reader.GetString(0), request.Fingerprint.Trim(), StringComparison.Ordinal)
                        || !string.Equals(reader.GetString(1), request.CardKey.Trim(), StringComparison.Ordinal)
                        || !string.Equals(reader.GetString(2), request.Executor.Trim(), StringComparison.Ordinal)
                        || !string.Equals(reader.GetString(3), request.Source.Trim(), StringComparison.Ordinal)))
                    throw new InvalidOperationException("Failure fingerprint report key is bound to different evidence.");
            }
            await ExecuteAsync(connection, """
                INSERT INTO failure_fingerprint_events(
                    report_key, fingerprint, seen_at, executor, card_key, source)
                VALUES ($report, $fingerprint, $seen, $executor, $card, $source)
                ON CONFLICT(report_key) DO NOTHING;
                """, ct,
                ("$report", request.ReportKey.Trim()),
                ("$fingerprint", request.Fingerprint.Trim()),
                ("$seen", Iso(UtcNow)),
                ("$executor", request.Executor.Trim()),
                ("$card", request.CardKey.Trim()),
                ("$source", request.Source.Trim()));
        }
        finally
        {
            _writeGate.Release();
        }
        return (await ReadFailureFingerprintsAsync(request.Fingerprint, null, ct)).Single();
    }

    public async Task<IReadOnlyList<FailureFingerprintHistoryDto>> ReadFailureFingerprintsAsync(
        string? fingerprint = null, DateTime? sinceUtc = null, CancellationToken ct = default)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT fingerprint, seen_at, executor, card_key
              FROM failure_fingerprint_events
             WHERE ($fingerprint IS NULL OR fingerprint = $fingerprint)
               AND ($since IS NULL OR seen_at >= $since)
             ORDER BY fingerprint, seen_at;
            """,
            ("$fingerprint", string.IsNullOrWhiteSpace(fingerprint) ? null : fingerprint.Trim()),
            ("$since", sinceUtc.HasValue ? Iso(sinceUtc.Value.ToUniversalTime()) : null));
        var rows = new Dictionary<string, List<(DateTime Seen, string Executor, string Card)>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = reader.GetString(0);
            if (!rows.TryGetValue(key, out var events)) rows[key] = events = [];
            events.Add((DateTime.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind), reader.GetString(2), reader.GetString(3)));
        }
        return rows.Select(pair => new FailureFingerprintHistoryDto(
            pair.Key,
            pair.Value[0].Seen,
            pair.Value[^1].Seen,
            pair.Value.Count,
            pair.Value.Select(item => item.Executor).Distinct(StringComparer.Ordinal).ToArray(),
            pair.Value.Select(item => item.Card).Distinct(StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    private static void ValidateFingerprintEvent(RecordFailureFingerprintRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Fingerprint) || request.Fingerprint.Length > 256
            || string.IsNullOrWhiteSpace(request.CardKey) || request.CardKey.Length > 128
            || string.IsNullOrWhiteSpace(request.Executor) || request.Executor.Length > 128
            || string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > 64
            || string.IsNullOrWhiteSpace(request.ReportKey) || request.ReportKey.Length > 256)
            throw new ArgumentException("Failure fingerprint event fields must be nonempty and bounded.");
    }
}
