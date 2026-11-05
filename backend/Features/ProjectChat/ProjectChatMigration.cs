using System.Text.Json;

namespace AgentStudio.ProjectChat;

/// <summary>
/// One-shot migration that reads the legacy
/// <c>&lt;watchPath&gt;/.orchestrator/orchestrator-chat.jsonl</c> file
/// and writes one markdown document per turn under the new
/// <c>&lt;projectFolder&gt;/chat/&lt;yyyy-MM&gt;/</c> tree. Idempotent
/// (re-running with no new lines is a no-op) and silent (callers do not
/// need to interact). The legacy file is left in place as belt-and-
/// braces evidence; the new file tree becomes the source of truth as
/// soon as Slice D's endpoints are wired.
/// </summary>
public sealed class ProjectChatMigration
{
    private readonly ProjectChatStore _store;
    private readonly ProjectChatIndex _index;
    private readonly TaskScannerService _scanner;
    private readonly ILogger<ProjectChatMigration> _logger;

    public ProjectChatMigration(
        ProjectChatStore store,
        ProjectChatIndex index,
        TaskScannerService scanner,
        ILogger<ProjectChatMigration> logger)
    {
        _store = store;
        _index = index;
        _scanner = scanner;
        _logger = logger;
    }

    public ProjectChatMigrationReport MigrateAll(CancellationToken ct = default)
    {
        var report = new ProjectChatMigrationReport();
        foreach (var entry in _scanner.GetWatchPaths())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stats = MigrateOne(entry, ct);
                report.Projects[entry.Name] = stats;
                if (stats.Written > 0)
                    _logger.LogInformation("Project chat migration: {Project} wrote {N} new turn files", entry.Name, stats.Written);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Project chat migration failed for {Project}", entry.Name);
            }
        }
        return report;
    }

    public ProjectChatMigrationStats MigrateOne(WatchPathEntry entry, CancellationToken ct = default)
    {
        var stats = new ProjectChatMigrationStats();
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(entry.Path)) return stats;

        var legacy = Path.Combine(entry.RootPath, ".orchestrator", "orchestrator-chat.jsonl");
        if (!File.Exists(legacy)) return stats;

        // Build the existing-id set so we can skip turns already migrated.
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, turn) in _store.EnumerateAll(entry.Path))
        {
            ct.ThrowIfCancellationRequested();
            existing.Add(turn.TurnId);
        }

        foreach (var line in File.ReadLines(legacy))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            ProjectChatTurn? turn;
            try
            {
                turn = LegacyToTurn(line);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping malformed legacy chat line in {Project}", entry.Name);
                stats.Skipped++;
                continue;
            }

            if (turn == null) { stats.Skipped++; continue; }
            if (existing.Contains(turn.TurnId)) { stats.AlreadyMigrated++; continue; }

            var written = _store.Write(entry.Path, turn);
            existing.Add(turn.TurnId);
            stats.Written++;
            // Don't upsert per-row here; we'll trigger one EnsureFresh
            // pass at the end so the index rebuild stays linear instead
            // of paying the open/close + transaction cost per row.
        }

        if (stats.Written > 0)
        {
            _index.EnsureFresh(entry.Path, ct);
        }

        return stats;
    }

    /// <summary>
    /// Map one JSONL row from the legacy <c>orchestrator-chat.jsonl</c>
    /// to a Slice D <see cref="ProjectChatTurn"/>. The legacy schema is
    /// <c>{ id, ts, role, text, attachments, errorMessage, ... }</c>;
    /// only <c>id</c>, <c>ts</c>, <c>role</c>, and <c>text</c> are
    /// load-bearing.
    /// </summary>
    private static ProjectChatTurn? LegacyToTurn(string jsonLine)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var ts = root.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
            ? DateTime.Parse(tsEl.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)
            : DateTime.UtcNow;
        var role = root.TryGetProperty("role", out var rEl) ? rEl.GetString() : null;
        var text = root.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
        var err = root.TryGetProperty("errorMessage", out var eEl) ? eEl.GetString() : null;

        if (string.IsNullOrWhiteSpace(id)) return null;

        var author = role switch
        {
            "user" => ProjectChatTurnAuthors.User,
            "orchestrator" => ProjectChatTurnAuthors.Orchestrator,
            _ => ProjectChatTurnAuthors.Orchestrator
        };

        var body = string.IsNullOrEmpty(text) ? "" : text!;
        if (!string.IsNullOrWhiteSpace(err))
        {
            // Surface the legacy error inline so search hits over migrated
            // failures still expose what went wrong.
            body = string.IsNullOrEmpty(body)
                ? $"_error:_ {err}"
                : body + "\n\n_error:_ " + err;
        }

        return new ProjectChatTurn
        {
            TurnId = id!,
            Author = author,
            Kind = ProjectChatTurnKinds.Turn,
            Ts = DateTime.SpecifyKind(ts, DateTimeKind.Utc),
            Body = body
        };
    }
}

public sealed class ProjectChatMigrationReport
{
    public Dictionary<string, ProjectChatMigrationStats> Projects { get; } = new(StringComparer.Ordinal);
}

public sealed class ProjectChatMigrationStats
{
    public int Written { get; set; }
    public int AlreadyMigrated { get; set; }
    public int Skipped { get; set; }
}
