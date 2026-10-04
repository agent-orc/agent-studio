using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Prompts;

namespace AgentStudio.Runner;

/// <summary>
/// Boots the singleton <see cref="GlobalOrchestratorSession"/> at app
/// start, reusing the persisted id when one exists. Mirrors the per-
/// project boot flow in <see cref="ProjectRunner.BootOrchestratorSessionAsync"/>
/// so the failure modes and chat output stay consistent; the only
/// differences are scope (one session for the whole app, not per
/// watched project) and the boot prompt (a roll-up of all watched
/// projects, not the contents of one project's docs).
/// </summary>
public sealed class GlobalOrchestratorBootstrap
{
    /// <summary>Runtime prompt template for the singleton boot prompt.</summary>
    public const string BootTemplate = "global-orchestrator-boot.md";

    /// <summary>Conditional note appended to a watched project that is the tool itself.</summary>
    public const string SelfModNoteTemplate = "global-orchestrator-self-mod-note.md";

    /// <summary>Sub-block summarising the current task counts per project.</summary>
    public const string TaskSnapshotTemplate = "global-orchestrator-task-snapshot.md";

    private readonly ILogger<GlobalOrchestratorBootstrap> _logger;
    private readonly GlobalOrchestratorSessionStore _store;
    private readonly OrchestratorRunner _runner;
    private readonly TaskScannerService _scanner;
    private readonly IConfiguration _config;
    private readonly ClientIdentityStore? _identityStore;
    private readonly RuntimePromptService _prompts;

    public GlobalOrchestratorBootstrap(
        ILogger<GlobalOrchestratorBootstrap> logger,
        GlobalOrchestratorSessionStore store,
        OrchestratorRunner runner,
        TaskScannerService scanner,
        IConfiguration config,
        ClientIdentityStore? identityStore = null,
        RuntimePromptService? prompts = null)
    {
        _logger = logger;
        _store = store;
        _runner = runner;
        _scanner = scanner;
        _config = config;
        _identityStore = identityStore;
        _prompts = prompts ?? new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
    }

    public async Task BootAsync(CancellationToken ct)
    {
        var existing = _store.Read();
        if (existing != null && !string.IsNullOrWhiteSpace(existing.SessionId))
        {
            _logger.LogInformation(
                "[global-orchestrator] reusing persisted session {SessionId} (calls so far: {Calls})",
                existing.SessionId, existing.Calls);
            return;
        }

        var modelId = _config["GlobalOrchestrator:Model"] ?? OrchestratorRunner.DefaultModel;
        var prompt = BuildBootPrompt();
        var workingDirectory = ResolveWorkingDirectory();

        _logger.LogInformation("[global-orchestrator] booting on {Model} at {Cwd}", modelId, workingDirectory);
        var result = await _runner.DecideAsync(prompt, modelId, workingDirectory, ct);

        if (!result.Success || string.IsNullOrWhiteSpace(result.CapturedSessionId))
        {
            _logger.LogWarning(
                "[global-orchestrator] boot failed: success={Success}, sessionId={SessionId}, error={Error}",
                result.Success, result.CapturedSessionId, result.ErrorMessage);
            return;
        }

        var session = new GlobalOrchestratorSession(
            SessionId: result.CapturedSessionId!,
            Model: result.Model,
            BootedAt: DateTime.UtcNow,
            BootPromptPreview: TruncatePreview(prompt, 2000),
            BootReplyPreview: TruncatePreview(result.ReplyText, 600),
            CumulativeInputTokens: result.TokenUsage?.InputTokens ?? 0,
            CumulativeOutputTokens: result.TokenUsage?.OutputTokens ?? 0,
            CumulativeCacheReadTokens: result.TokenUsage?.CacheReadTokens ?? 0,
            CumulativeCacheCreationTokens: result.TokenUsage?.CacheCreationTokens ?? 0,
            Calls: 1,
            LastUsedAt: DateTime.UtcNow,
            LastError: null);
        _store.Write(session);
    }

    /// <summary>
    /// Render the boot prompt used to seed the singleton global session.
    /// Exposed so <see cref="OrchestratorChatService"/> can reuse the same
    /// framing when re-bootstrapping after a rejected resume (see
    /// <see cref="OrchestratorRunner.ResumeWithFallbackAsync"/>). The
    /// rendered text is snapshot of watched projects + a brief role
    /// instruction; safe to recompute on demand.
    /// </summary>
    public string BuildBootPrompt()
    {
        var entries = _scanner.GetWatchPaths();
        var (cli, model) = ResolveBootDefaults(_identityStore);

        return _prompts.Render(BootTemplate, new Dictionary<string, string?>
        {
            ["watched_count"] = entries.Count.ToString(),
            ["watched_projects"] = BuildWatchedProjectsBlock(entries),
            ["default_cli"] = cli,
            ["default_model"] = model,
            ["task_snapshot"] = BuildTaskSnapshotBlock(),
        });
    }

    /// <summary>
    /// Assemble the data-only watched-projects list (names, paths, and the
    /// conditional self-modification note). Pure slot-fill; no instruction
    /// prose lives here - the note text comes from
    /// <see cref="SelfModNoteTemplate"/>.
    /// </summary>
    private string BuildWatchedProjectsBlock(IEnumerable<WatchPathEntry> entries)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in entries)
        {
            sb.AppendLf($"- {e.Name}");
            if (!string.IsNullOrWhiteSpace(e.RootPath)) sb.AppendLf($"    working directory: {e.RootPath}");
            if (!string.IsNullOrWhiteSpace(e.RepositoryPath)) sb.AppendLf($"    git repository:    {e.RepositoryPath}");
            sb.AppendLf($"    task folder:       {e.Path}");
            if (IsSelfModificationTarget(e))
                sb.AppendLf(_prompts.Render(SelfModNoteTemplate, EmptyValues).TrimEnd('\r', '\n'));
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Render the light project-state snapshot so the orchestrator is
    /// grounded in current reality. Best-effort: a scan failure yields an
    /// empty block so the boot prompt still renders. The trailing blank
    /// line separates the snapshot from the following section.
    /// </summary>
    private string BuildTaskSnapshotBlock()
    {
        try
        {
            var jobs = _scanner.ScanAllAutomationJobs();
            var byProject = new System.Text.StringBuilder();
            foreach (var grp in jobs.GroupBy(j => j.ProjectName))
            {
                byProject.AppendLf($"  {grp.Key}:");
                foreach (var sg in grp.GroupBy(j => j.State).OrderBy(g => g.Key))
                    byProject.AppendLf($"    {sg.Key}: {sg.Count()}");
            }
            var rendered = _prompts.Render(TaskSnapshotTemplate, new Dictionary<string, string?>
            {
                ["total"] = jobs.Count.ToString(),
                ["by_project"] = byProject.ToString().TrimEnd('\r', '\n'),
            });
            return rendered.TrimEnd('\r', '\n') + "\n\n";
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "GlobalOrchestratorBootstrap: boot is best-effort; missing snapshot is fine");
            return string.Empty;
        }
    }

    private static readonly IReadOnlyDictionary<string, string?> EmptyValues =
        new Dictionary<string, string?>();

    /// <summary>
    /// Resolve the "boot-time" user defaults from the bootstrap identity
    /// (<see cref="DefaultClientIdentity.Id"/>). Falls back to the historic
    /// default CLI plus the orchestrator runner's default model so a fresh
    /// install with no identity record still produces a well-formed block.
    /// </summary>
    internal static (string cli, string model) ResolveBootDefaults(ClientIdentityStore? identityStore)
    {
        var rec = identityStore?.Find(DefaultClientIdentity.Id);
        var cli = !string.IsNullOrWhiteSpace(rec?.DefaultCliType) ? rec!.DefaultCliType! : "claude";
        var model = !string.IsNullOrWhiteSpace(rec?.DefaultModel) ? rec!.DefaultModel! : OrchestratorRunner.DefaultModel;
        return (cli, model);
    }

    /// <summary>
    /// Heuristic: is the watched entry the agent-orchestrator codebase
    /// itself? Matched on either the working directory name or the task
    /// folder name. Kept case-insensitive and tolerant to either checkout
    /// flavour (dev vs stable) so future renames don't silently disable
    /// the warning.
    /// </summary>
    internal static bool IsSelfModificationTarget(WatchPathEntry e)
    {
        bool Matches(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return false;
            var leaf = System.IO.Path.GetFileName(p.TrimEnd('/', '\\'));
            if (string.IsNullOrWhiteSpace(leaf)) return false;
            return leaf.StartsWith("agent-taskboard", StringComparison.OrdinalIgnoreCase)
                || leaf.StartsWith("agent-orchestrator", StringComparison.OrdinalIgnoreCase);
        }
        return Matches(e.RootPath) || Matches(e.RepositoryPath) || Matches(e.Path) || Matches(e.Name);
    }

    private string ResolveWorkingDirectory()
    {
        // Use the first watched project's root as the working directory so
        // claude resolves relative paths somewhere stable. Falls back to
        // the temp directory if no projects are configured.
        var first = _scanner.GetWatchPaths().FirstOrDefault();
        return !string.IsNullOrWhiteSpace(first?.RootPath)
            ? first!.RootPath
            : Path.GetTempPath();
    }

    private static string TruncatePreview(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "...";
    }
}
