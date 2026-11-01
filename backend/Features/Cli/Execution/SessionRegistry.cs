using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Reads each CLI's native on-disk session store and presents a unified view
/// for the right-hand usage side-sheet.
/// <list type="bullet">
///   <item>Claude: <c>~/.claude/projects/&lt;encoded-cwd&gt;/&lt;uuid&gt;.jsonl</c>.</item>
///   <item>Codex: <c>~/.codex/session_index.jsonl</c> + per-session
///     <c>~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl</c>.</item>
/// </list>
/// All disk reads are best-effort — a missing or unreadable file just means
/// "no sessions to show" rather than an error.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ILogger<SessionRegistry> _logger;
    private readonly ITaskScanner _scanner;
    private readonly SessionToTaskIndex? _sessionIndex;

    public SessionRegistry(ILogger<SessionRegistry> logger, ITaskScanner scanner)
        : this(logger, scanner, sessionIndex: null) { }

    /// <summary>
    /// Production constructor: <paramref name="sessionIndex"/> is the
    /// inverse <c>sessionId -&gt; owning task</c> map used to attach the
    /// <see cref="CliSessionInfo.LinkedJob"/> chip. The parameterless
    /// overload above stays so existing tests that build a registry
    /// without the index keep working (the chip is just empty in that
    /// case, which matches today's behaviour).
    /// </summary>
    public SessionRegistry(ILogger<SessionRegistry> logger, ITaskScanner scanner, SessionToTaskIndex? sessionIndex)
    {
        _logger = logger;
        _scanner = scanner;
        _sessionIndex = sessionIndex;
    }

    public CliUsageReport BuildReport(CliRouter router)
        => BuildReport(router, activeJobByProject: null);

    /// <summary>
    /// Overload used by the HTTP endpoint: pass a snapshot of the runner's
    /// per-project active job so the chip can render <c>active</c> (green)
    /// when the linked session belongs to the project's currently-running
    /// task. <paramref name="activeJobByProject"/> is keyed by project name
    /// and contains the job id the runner reports as active, or null when
    /// nothing is running. A null map disables the active flag entirely
    /// (chip falls back to <c>linked</c>).
    /// </summary>
    public CliUsageReport BuildReport(CliRouter router, IReadOnlyDictionary<string, string?>? activeJobByProject)
    {
        // Rebuild the session->job index lazily on every report build. The
        // cost is bounded by ScanAllJobs + chain walk and is dominated by
        // the disk-cache-warm scan that already happens on this endpoint;
        // the perf test in SessionToJobIndexTests pins the rebuild itself
        // at well under 50 ms for 200 jobs.
        if (_sessionIndex != null)
        {
            try { _sessionIndex.Rebuild(_scanner.ScanAllJobs()); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SessionToTaskIndex rebuild failed; LinkedJob chips will be absent this tick");
            }
        }

        var sections = new List<CliUsageSection>();
        foreach (var cli in router.All)
        {
            var section = BuildSection(cli);
            section = section with { Projects = AttachLinkedJobs(section.Projects, activeJobByProject) };
            sections.Add(section);
        }
        return new CliUsageReport { Sections = sections };
    }

    private List<CliUsageProjectGroup> AttachLinkedJobs(
        List<CliUsageProjectGroup> projects,
        IReadOnlyDictionary<string, string?>? activeJobByProject)
    {
        if (_sessionIndex == null || projects.Count == 0) return projects;
        var result = new List<CliUsageProjectGroup>(projects.Count);
        foreach (var group in projects)
        {
            var sessions = new List<CliSessionInfo>(group.Sessions.Count);
            foreach (var s in group.Sessions)
            {
                var link = _sessionIndex.Lookup(s.Id, s.Cwd);
                if (link == null)
                {
                    sessions.Add(s);
                    continue;
                }
                var isActive = false;
                if (string.Equals(link.Lane, TaskStates.Progress, StringComparison.Ordinal)
                    && activeJobByProject != null
                    && activeJobByProject.TryGetValue(link.ProjectName, out var activeId)
                    && string.Equals(activeId, link.JobId, StringComparison.Ordinal))
                {
                    isActive = true;
                }
                sessions.Add(s with
                {
                    LinkedJob = new LinkedJobRef
                    {
                        JobId       = link.JobId,
                        Title       = link.Title,
                        WatchPath   = link.WatchPath,
                        ProjectName = link.ProjectName,
                        Lane        = link.Lane,
                        IsActive    = isActive
                    }
                });
            }
            result.Add(group with { Sessions = sessions });
        }
        return result;
    }

    private CliUsageSection BuildSection(ICliExecutionService cli)
    {
        var (available, version, path) = cli.TestCliPath();
        var section = new CliUsageSection
        {
            CliType = cli.CliType,
            Available = available,
            Version = version,
            Path = path
        };

        try
        {
            section = section with { Projects = cli.CliType switch
            {
                CliTypes.Claude  => BuildClaudeProjects(),
                CliTypes.Codex   => BuildCodexProjects(),
                CliTypes.Gemini  => BuildGeminiProjects(),
                _ => []
            }};
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate sessions for {Cli}", cli.CliType);
            section = section with { Error = ex.Message };
        }
        return section;
    }

    // ── Claude Code ─────────────────────────────────────────────────────

    private List<CliUsageProjectGroup> BuildClaudeProjects()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");
        if (!Directory.Exists(root)) return [];

        var groups = new List<CliUsageProjectGroup>();
        foreach (var projectDir in Directory.EnumerateDirectories(root))
        {
            var encoded = Path.GetFileName(projectDir);
            // Claude encodes the cwd by replacing path separators with '-' and dropping the leading slash.
            var displayCwd = encoded.Replace('-', Path.DirectorySeparatorChar);

            var sessions = new List<CliSessionInfo>();
            foreach (var jsonl in Directory.EnumerateFiles(projectDir, "*.jsonl"))
            {
                var id = Path.GetFileNameWithoutExtension(jsonl);
                var fi = new FileInfo(jsonl);
                sessions.Add(new CliSessionInfo
                {
                    Id = id,
                    Label = id[..Math.Min(8, id.Length)],
                    UpdatedAt = fi.LastWriteTimeUtc,
                    // FileInfo is already materialised for the timestamp, so the
                    // length is free here — no extra stat per session.
                    SizeBytes = fi.Length,
                    Cwd = displayCwd
                });
            }

            if (sessions.Count == 0) continue;
            groups.Add(new CliUsageProjectGroup
            {
                ProjectName = displayCwd,
                RootPath = displayCwd,
                Sessions = sessions.OrderByDescending(s => s.UpdatedAt).ToList()
            });
        }
        return groups;
    }

    // ── Gemini ──────────────────────────────────────────────────────────
    // Sessions live under ~/.gemini/tmp/<project-slug>/chats/session-*.json.
    // The slug map (absolute cwd → slug) is in ~/.gemini/projects.json.
    private List<CliUsageProjectGroup> BuildGeminiProjects()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".gemini");
        var projectsPath = Path.Combine(root, "projects.json");
        if (!File.Exists(projectsPath)) return [];

        // Build slug → absolute-cwd lookup so the side-sheet can show the real path.
        var slugToCwd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(projectsPath));
            if (doc.RootElement.TryGetProperty("projects", out var projs)
                && projs.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in projs.EnumerateObject())
                {
                    var slug = p.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(slug))
                        slugToCwd[slug!] = p.Name;
                }
            }
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "SessionRegistry: best-effort"); /* best-effort */ }

        var tmpRoot = Path.Combine(root, "tmp");
        if (!Directory.Exists(tmpRoot)) return [];

        var groups = new List<CliUsageProjectGroup>();
        foreach (var projectDir in Directory.EnumerateDirectories(tmpRoot))
        {
            var slug = Path.GetFileName(projectDir);
            var chatsDir = Path.Combine(projectDir, "chats");
            if (!Directory.Exists(chatsDir)) continue;

            var sessions = new List<CliSessionInfo>();
            foreach (var jsonPath in Directory.EnumerateFiles(chatsDir, "session-*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                    var rootEl = doc.RootElement;
                    var id = rootEl.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    DateTime? updated = rootEl.TryGetProperty("lastUpdated", out var lu)
                                        && lu.TryGetDateTime(out var dt) ? dt : null;
                    var label = ExtractFirstUserPrompt(rootEl);
                    var gfi = new FileInfo(jsonPath);
                    sessions.Add(new CliSessionInfo
                    {
                        Id        = id!,
                        Label     = label,
                        UpdatedAt = updated ?? gfi.LastWriteTimeUtc,
                        SizeBytes = gfi.Length,
                        Cwd       = slugToCwd.TryGetValue(slug, out var cwd) ? cwd : slug
                    });
                }
                catch (Exception __ex) { SilentCatch.Note(__ex, "SessionRegistry: skip unreadable session file"); /* skip unreadable session file */ }
            }

            if (sessions.Count == 0) continue;
            groups.Add(new CliUsageProjectGroup
            {
                ProjectName = slug,
                RootPath    = slugToCwd.TryGetValue(slug, out var cwd2) ? cwd2 : slug,
                Sessions    = sessions.OrderByDescending(s => s.UpdatedAt).Take(20).ToList()
            });
        }

        return groups;
    }

    private static string? ExtractFirstUserPrompt(JsonElement sessionRoot)
    {
        if (!sessionRoot.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var msg in msgs.EnumerateArray())
        {
            if (!msg.TryGetProperty("type", out var t) || t.GetString() != "user") continue;
            if (!msg.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in c.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                {
                    var s = txt.GetString() ?? "";
                    var firstLine = s.Replace("\r\n", "\n").Split('\n').FirstOrDefault()?.Trim() ?? "";
                    if (firstLine.Length > 80) firstLine = firstLine[..80] + "…";
                    return firstLine.Length > 0 ? firstLine : null;
                }
            }
        }
        return null;
    }

    // ── Codex ───────────────────────────────────────────────────────────

    private record CodexIndexEntry(string Id, string? ThreadName, DateTime? UpdatedAt, string? Cwd);

    private List<CliUsageProjectGroup> BuildCodexProjects()
    {
        var home = CodexRolloutStore.ResolveSharedHome();
        var indexPath = Path.Combine(home, "session_index.jsonl");
        if (!File.Exists(indexPath)) return [];

        // The index is not proof of a usable session. Codex may append an index
        // row before the first agent output and then die, leaving no rollout.
        // Hide those stillborn rows immediately and compact stale ones so the
        // operator's session list does not grow forever after launch storms.
        var rolloutIds = CodexRolloutStore.EnumerateRolloutIds(home);
        PruneStaleCodexIndexEntries(indexPath, rolloutIds, DateTime.UtcNow);

        var entries = new List<CodexIndexEntry>();
        foreach (var raw in File.ReadAllLines(indexPath))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var name = root.TryGetProperty("thread_name", out var nm) ? nm.GetString() : null;
                DateTime? updated = root.TryGetProperty("updated_at", out var uEl) && uEl.TryGetDateTime(out var dt) ? dt : null;
                var cwd = root.TryGetProperty("cwd", out var cEl) ? cEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id) && rolloutIds.Contains(id))
                    entries.Add(new CodexIndexEntry(id, name, updated, cwd));
            }
            catch (Exception __ex) { SilentCatch.Note(__ex, "SessionRegistry: skip malformed line"); /* skip malformed line */ }
        }

        var grouped = entries
            .GroupBy(e => e.Cwd ?? "(unknown)")
            .Select(grp => new CliUsageProjectGroup
            {
                ProjectName = Path.GetFileName(grp.Key.TrimEnd('/', '\\')) is { Length: > 0 } leaf ? leaf : grp.Key,
                RootPath = grp.Key,
                Sessions = grp
                    .OrderByDescending(e => e.UpdatedAt)
                    .Take(20)
                    .Select(e => new CliSessionInfo
                    {
                        Id = e.Id,
                        Label = e.ThreadName,
                        UpdatedAt = e.UpdatedAt,
                        Cwd = e.Cwd
                    })
                    .ToList()
            })
            .ToList();

        return grouped;
    }

    private static readonly TimeSpan CodexStillbornGrace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Remove old index-only Codex rows while retaining malformed/unknown rows
    /// and recent rows that may still be in the index-before-rollout creation
    /// window. The write is skipped when Codex changed the index during the
    /// scan, avoiding clobbering a concurrently-starting session.
    /// </summary>
    internal void PruneStaleCodexIndexEntries(
        string indexPath,
        IReadOnlySet<string> rolloutIds,
        DateTime nowUtc)
    {
        try
        {
            var observedWrite = File.GetLastWriteTimeUtc(indexPath);
            var lines = File.ReadAllLines(indexPath);
            var kept = new List<string>(lines.Length);
            var removed = 0;

            foreach (var raw in lines)
            {
                if (!TryReadStaleIndexOnlyCodexId(raw, rolloutIds, nowUtc, out _))
                {
                    kept.Add(raw);
                    continue;
                }
                removed++;
            }

            if (removed == 0 || File.GetLastWriteTimeUtc(indexPath) != observedWrite) return;
            var temp = indexPath + $".prune-{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllLines(temp, kept);
                File.Move(temp, indexPath, overwrite: true);
                _logger.LogInformation(
                    "codex_session_stillborn_cleanup index={IndexPath} removed={Removed} retained={Retained}",
                    indexPath, removed, kept.Count);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) { _logger.LogDebug(ex, "Could not delete Codex session-index cleanup temp {Path}", temp); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Codex stillborn session cleanup skipped for {IndexPath}", indexPath);
        }
    }

    internal static bool TryReadStaleIndexOnlyCodexId(
        string raw,
        IReadOnlySet<string> rolloutIds,
        DateTime nowUtc,
        out string? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || rolloutIds.Contains(id)) return false;
            if (!root.TryGetProperty("updated_at", out var updatedEl)
                || !updatedEl.TryGetDateTime(out var updatedAt)) return false;
            return nowUtc - updatedAt.ToUniversalTime() >= CodexStillbornGrace;
        }
        catch
        {
            return false;
        }
    }
}
