using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentStudio.Runner;

namespace AgentStudio.Tasks;

/// <summary>
/// Field-level writes against an existing job: the per-field setters
/// (model, cli-type, title, useOwnSession, commit, contextUsage),
/// editing of <c>prompt.md</c>, the create-job flow that mints a new
/// job folder, the binary attachment uploader, and the
/// continuation-note appender that records user follow-ups into
/// <c>prompt.md</c>.
///
/// Pattern: every public method does <see cref="TaskScannerService.FindJob"/>
/// → <see cref="TaskJsonFile.UpdateField"/>. Splitting this out of the
/// scanner keeps the read surface focused on read and makes the write
/// surface easy to grep when a "where do we touch this field" question
/// comes up.
/// </summary>
public class TaskMutationService
{
    private readonly TaskScannerService _scanner;
    private readonly ClientIdentityStore _clients;
    private readonly ProjectRegistry _projectRegistry;
    private readonly TaskChangeNotifier _notifier;
    private readonly ILogger<TaskMutationService> _logger;
    // ADR-0049: optional unified-timeline writer. Production DI supplies it;
    // tests that construct TaskMutationService directly may pass null and
    // simply skip the timeline event.
    private readonly TimelineLog? _timeline;
    private readonly GitService? _git;
    // Lane mutex: serialise the slug-uniqueness check + folder create in
    // CreateJob with the other lane writers (move/archive/delete) so two
    // concurrent creates cannot pick the same dedupe suffix and land two
    // folders on the same slug. Optional so test fixtures that build the
    // service directly keep compiling; the NullSingleton still serialises.
    private readonly LaneMutexRegistry _laneMutex;

    public TaskMutationService(TaskScannerService scanner, ClientIdentityStore clients, ProjectRegistry projectRegistry, TaskChangeNotifier notifier, ILogger<TaskMutationService> logger, TimelineLog? timeline = null, LaneMutexRegistry? laneMutex = null, GitService? git = null)
    {
        _scanner = scanner;
        _clients = clients;
        _projectRegistry = projectRegistry;
        _notifier = notifier;
        _logger = logger;
        _timeline = timeline;
        _laneMutex = laneMutex ?? LaneMutexRegistry.NullSingleton;
        _git = git;
    }

    /// <summary>
    /// Cycle 2: every public mutation that writes to disk routes its
    /// success return through this helper so the in-memory snapshot is
    /// invalidated synchronously. Without this, a POST-then-GET sequence
    /// (e.g. SetJobTitle then refresh) could see the pre-write snapshot
    /// for up to 250 ms (the FileSystemWatcher debounce window).
    /// Also publishes a typed <c>jobUpdated</c> event to
    /// <see cref="TaskChangeNotifier"/> so the SignalR hub can push the
    /// change to connected clients without waiting for the next poll.
    /// </summary>
    private bool Updated(TaskInfo info)
    {
        _scanner.InvalidateCache();
        _notifier.PublishUpdated(info.ProjectName, info.Id, info.WatchPath);
        return true;
    }

    /// <summary>
    /// Folder-only invalidation for the internal helpers
    /// (<see cref="SetJobCommitOnFolder"/>, <see cref="AppendJobCommitOnFolder"/>,
    /// <see cref="SetJobLastProgressAt"/>, <see cref="SetJobPhase"/>) whose
    /// callers do not always have a <see cref="TaskInfo"/> in hand. Skips the
    /// SignalR push: those code paths are either user-invisible heartbeats
    /// (<c>lastProgressAt</c>), or land on a job that the user-facing
    /// surface (Move, CreateJob) is about to push for separately.
    /// </summary>
    private bool Updated() { _scanner.InvalidateCache(); return true; }

    public bool SetJobModel(string jobId, string? model, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var previousModel = ModelMetadataRegistry.NormalizeForCli(info.CliType, info.Model);
        var normalizedModel = ModelMetadataRegistry.NormalizeForCli(info.CliType, model);
        TaskJsonFile.UpdateField(info.FolderPath, "model", normalizedModel ?? "", _logger);
        TaskJsonFile.UpdateField(info.FolderPath, "modelExplicit", !string.IsNullOrWhiteSpace(model), _logger);
        TaskJsonFile.UpdateField(
            info.FolderPath,
            "thinkingLevel",
            ModelMetadataRegistry.ResolveThinkingLevel(info.CliType, normalizedModel, info.ThinkingLevel) ?? "",
            _logger);
        AppendModelChangeMarker(info, previousModel, normalizedModel);
        return Updated();
    }

    /// <summary>
    /// Record an operator model switch as a <c>[taskboard] Model changed</c>
    /// line on the <c>[system]</c> stream of <c>logs/cli-output.log</c> so the
    /// conversation projection can surface it as a "Model changed: X → Y"
    /// notice in the chat. Only writes when the normalized model actually
    /// changed and the job already has a conversation log to annotate — a
    /// pre-run config tweak on a never-run job has no chat to mark. Mirrors the
    /// text line format the CLI-output parser consumes
    /// (<c>[HH:mm:ss.fff] [system] …</c>); failures are swallowed so a log
    /// write can never fail the model PUT.
    /// </summary>
    private void AppendModelChangeMarker(TaskInfo info, string? previousModel, string? newModel)
    {
        // Gate on the CANONICAL ids: NormalizeForCli trims but does not
        // canonicalize aliases (e.g. "claude-opus-4.8" vs "claude-opus-4-8"),
        // so comparing the raw spellings would fire a phantom "Model changed"
        // notice for what is the same model. The human spelling is still what
        // gets displayed in the line.
        if (string.Equals(
                ModelMetadataRegistry.NormalizeId(previousModel),
                ModelMetadataRegistry.NormalizeId(newModel),
                StringComparison.OrdinalIgnoreCase)) return;

        var from = string.IsNullOrWhiteSpace(previousModel) ? "default" : previousModel;
        var to = string.IsNullOrWhiteSpace(newModel) ? "default" : newModel;

        try
        {
            var logPath = TaskPaths.CliOutputLog(info.FolderPath);
            // Only annotate an existing conversation; skip when the job never ran.
            if (!File.Exists(logPath) || new FileInfo(logPath).Length == 0) return;

            var ts = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = $"[{ts}] [system] [taskboard] Model changed from={from} to={to}";
            CliOutputLogFile.Append(logPath, line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record model-change marker in CLI log for {JobId}", info.Id);
        }
    }

    public bool SetJobThinkingLevel(string jobId, string? thinkingLevel, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var normalized = ModelMetadataRegistry.ResolveThinkingLevel(info.CliType, info.Model, thinkingLevel);
        TaskJsonFile.UpdateField(info.FolderPath, "thinkingLevel", normalized ?? "", _logger);
        TaskJsonFile.UpdateField(info.FolderPath, "thinkingLevelExplicit", !string.IsNullOrWhiteSpace(thinkingLevel), _logger);
        return Updated();
    }

    /// <summary>
    /// Epics assignment way 2 (post-hoc): attach this task to a parent epic, or
    /// detach it (<paramref name="epicId"/> null/empty clears the link). Writes
    /// the <c>epicId</c> field on task.json via the mutation layer (API-only
    /// job-folder rule, ADR-0024). Returns false when the task is not found.
    /// </summary>
    public bool SetJobEpic(string jobId, string? epicId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        TaskJsonFile.UpdateField(info.FolderPath, "epicId", epicId ?? "", _logger);
        return Updated();
    }

    public bool SetJobCliType(string jobId, string cliType, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var normalized = CliTypes.Normalize(cliType);
        var normalizedModel = ModelMetadataRegistry.NormalizeForCli(normalized, info.Model);
        TaskJsonFile.UpdateField(info.FolderPath, "cliType", normalized, _logger);
        TaskJsonFile.UpdateField(info.FolderPath, "model", normalizedModel ?? "", _logger);
        TaskJsonFile.UpdateField(
            info.FolderPath,
            "thinkingLevel",
            ModelMetadataRegistry.ResolveThinkingLevel(normalized, normalizedModel, info.ThinkingLevel) ?? "",
            _logger);
        // Keep the parallel `agent` field in lockstep with `cliType`. The two
        // were originally meant to address different layers (which CLI vs.
        // which logical agent) but every supported CLI maps 1:1 to one agent
        // value, and the kanban card's text label reads `agent` while the
        // icon reads `cliType`. A mass-flip of cliType without syncing agent
        // produced cards showing "claude" with the Codex icon on 2026-05-12
        // (see job bug-clitype-and-agent-fields-drift-on-mass-flip).
        TaskJsonFile.UpdateField(info.FolderPath, "agent", normalized, _logger);
        // Switching CLI invalidates the previous session - clear it so the next run mints a new one.
        if (!string.Equals(normalized, info.CliType, StringComparison.OrdinalIgnoreCase))
        {
            TaskJsonFile.UpdateField(info.FolderPath, "sessionName", "", _logger);
        }
        return Updated();
    }

    public bool SetJobUseOwnSession(string jobId, bool useOwn, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        TaskJsonFile.UpdateField(info.FolderPath, "useOwnSession", useOwn, _logger);
        return Updated();
    }

    public bool SetJobCommit(string jobId, TaskCommitInfo commit, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        return AppendJobCommitOnFolder(info.FolderPath, commit);
    }

    public bool SetJobCommitOnFolder(string folderPath, TaskCommitInfo commit)
    {
        if (!Directory.Exists(folderPath)) return false;
        AppendJobCommitOnFolder(folderPath, commit);
        return Updated();
    }

    /// <summary>
    /// Append a new commit to the task's commit chain in <c>task.json</c>.
    /// Existing entries are preserved (oldest -&gt; newest); the singular
    /// legacy <c>commit</c> field is also updated to mirror the newest
    /// entry so consumers that still read the old shape see the latest
    /// commit, not a stale first one. Dedupes by SHA so a re-stamp from
    /// the same SHA does not bloat the chain - in that case we replace
    /// the existing entry in place to refresh metadata
    /// (file count, message, timestamp) without re-ordering.
    ///
    /// <para>
    /// Tasks regularly produce more than one commit across iterations:
    /// continue-mode adds a follow-up, crash-recovery leaves a recovery
    /// commit plus a follow-up, operator-driven steers ("change this and
    /// continue") often add a separate commit. Each of those goes
    /// through this method so the detail view can render the full chain.
    /// </para>
    /// </summary>
    public bool AppendJobCommitOnFolder(string folderPath, TaskCommitInfo commit)
    {
        if (!Directory.Exists(folderPath)) return false;
        var jobJsonPath = Path.Combine(folderPath, "task.json");
        if (!File.Exists(jobJsonPath)) return false;
        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, TaskJsonFile.ReadOpts)
                      ?? new Dictionary<string, JsonElement>();

            var chain = new List<TaskCommitInfo>();
            if (doc.TryGetValue("commits", out var commitsEl) && commitsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in commitsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var parsed = JsonSerializer.Deserialize<TaskCommitInfo>(item.GetRawText(), TaskJsonFile.ReadOpts);
                    if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Sha)) chain.Add(parsed);
                }
            }
            else if (doc.TryGetValue("commit", out var legacyEl) && legacyEl.ValueKind == JsonValueKind.Object)
            {
                var parsed = JsonSerializer.Deserialize<TaskCommitInfo>(legacyEl.GetRawText(), TaskJsonFile.ReadOpts);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Sha)) chain.Add(parsed);
            }

            var existingIdx = chain.FindIndex(c => string.Equals(c.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0)
            {
                chain[existingIdx] = commit with
                {
                    SupersededBySha = commit.SupersededBySha
                        ?? chain[existingIdx].SupersededBySha,
                    SupersededByAttempt = commit.SupersededByAttempt
                        ?? chain[existingIdx].SupersededByAttempt,
                };
            }
            else chain.Add(commit);

            return WriteCommitState(folderPath, chain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append commit to {Folder}", folderPath);
            return false;
        }
    }

    /// <summary>
    /// Persist the result of the deterministic commit-attribution post-step
    /// (ADR "Commit-Attribution-Regel"): a replace-all write of the
    /// <c>commits</c> chain (now carrying attribution + confidence). The
    /// singular legacy <c>commit</c> field is kept pointing at the newest
    /// attributed entry so old readers still see the latest commit. Idempotent:
    /// re-running with the same git state rewrites identical content.
    /// </summary>
    public bool SetCommitAttributionOnFolder(
        string folderPath,
        IReadOnlyList<TaskCommitInfo> attributed)
    {
        if (!Directory.Exists(folderPath)) return false;
        var persisted = ReadPersistedCommitChain(folderPath);
        if (persisted is null) return false;
        var producerBySha = persisted
            .Where(commit => !string.IsNullOrWhiteSpace(commit.Sha))
            .GroupBy(commit => commit.Sha, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var ordered = attributed
            .Where(c => !string.IsNullOrWhiteSpace(c.Sha))
            .Select(commit =>
            {
                if (!producerBySha.TryGetValue(commit.Sha, out var producer)) return commit;
                return commit with
                {
                    Branch = commit.Branch ?? producer.Branch,
                    Repository = commit.Repository ?? producer.Repository,
                    RunAttemptId = commit.RunAttemptId ?? producer.RunAttemptId,
                    RunnerId = commit.RunnerId ?? producer.RunnerId,
                    ResultSha = commit.ResultSha ?? producer.ResultSha,
                    SupersededBySha = commit.SupersededBySha ?? producer.SupersededBySha,
                    SupersededByAttempt = commit.SupersededByAttempt ?? producer.SupersededByAttempt,
                };
            })
            .OrderBy(c => c.At)
            .ToList();
        return WriteCommitState(folderPath, ordered);
    }

    /// <summary>
    /// Replaces one generation's attribution from its verified remote delivery
    /// range, then projects <c>commits[]</c> as the SHA-deduplicated union of
    /// every generation. Commits already owned by an earlier generation keep
    /// that producer identity when a continuation delivery inherits them.
    /// An empty value removes only the named generation, so a foreign-commit
    /// rejection cannot erase valid attribution from earlier attempts.
    /// The legacy replacement switch is reserved for the one-time reviewed-card
    /// backfill, whose unscoped entries all came from the generation it repairs.
    /// </summary>
    public bool SetRemoteCommitAttributionOnFolder(
        string folderPath,
        string runAttemptId,
        string runnerId,
        string resultSha,
        IReadOnlyList<TaskCommitInfo> attributed,
        bool replaceUnscopedLegacyAttribution = false)
    {
        if (!Directory.Exists(folderPath)) return false;
        if (string.IsNullOrWhiteSpace(runAttemptId)
            || string.IsNullOrWhiteSpace(runnerId)
            || string.IsNullOrWhiteSpace(resultSha))
        {
            return false;
        }

        var generation = attributed
            .Where(commit => !string.IsNullOrWhiteSpace(commit.Sha))
            .Select(commit => commit with
            {
                RunAttemptId = runAttemptId,
                RunnerId = runnerId,
                ResultSha = resultSha,
            })
            .ToList();

        var persisted = ReadPersistedCommitChain(folderPath);
        if (persisted is null) return false;
        persisted = persisted.Select(commit => string.Equals(
                commit.SupersededByAttempt,
                TaskCommitSupersession.PendingAttempt,
                StringComparison.Ordinal)
            ? commit with { SupersededByAttempt = runAttemptId }
            : commit).ToList();

        var union = new List<TaskCommitInfo>();
        foreach (var commit in persisted)
        {
            if (replaceUnscopedLegacyAttribution
                && string.IsNullOrWhiteSpace(commit.RunAttemptId))
            {
                continue;
            }
            if (string.Equals(
                    commit.RunAttemptId,
                    runAttemptId,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (union.Any(existing => string.Equals(
                    existing.Sha,
                    commit.Sha,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            union.Add(commit);
        }
        foreach (var commit in generation)
        {
            // A later delivery range can contain work inherited from an
            // earlier attempt. Preserve the first producer instead of
            // re-attributing that SHA to the continuation runner.
            if (union.Any(existing => string.Equals(
                    existing.Sha,
                    commit.Sha,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            union.Add(commit);
        }

        return WriteCommitState(folderPath, union, allowEmptyReplacement: true);
    }

    /// <summary>
    /// Replaces one remote attempt's token rows while preserving receipts from
    /// earlier attempts. The attempt-scoped participant id makes completion
    /// replay idempotent and keeps continuation costs visible.
    /// </summary>
    public bool SetRemoteTokenSummaryOnFolder(
        string folderPath,
        string runAttemptId,
        TaskTokenSummary attemptSummary)
    {
        if (!Directory.Exists(folderPath) || string.IsNullOrWhiteSpace(runAttemptId)) return false;
        try
        {
            TaskTokenSummary? persisted = null;
            var taskJsonPath = Path.Combine(folderPath, "task.json");
            using (var document = JsonDocument.Parse(File.ReadAllText(taskJsonPath)))
            {
                if (document.RootElement.TryGetProperty("tokenSummary", out var tokenSummary)
                    && tokenSummary.ValueKind == JsonValueKind.Object)
                {
                    persisted = tokenSummary.Deserialize<TaskTokenSummary>(TaskJsonFile.ReadOpts);
                }
            }

            var participant = $"agent:remote-runner:{runAttemptId}";
            var entries = (persisted?.Entries ?? [])
                .Where(entry => !string.Equals(entry.ParticipantId, participant, StringComparison.Ordinal))
                .Concat((attemptSummary.Entries ?? []).Select(entry => entry with
                {
                    ParticipantId = participant,
                }))
                .OrderBy(entry => entry.Ts)
                .ToList();
            if (entries.Count == 0) return false;

            var summary = new TaskTokenSummary
            {
                Calls = entries.Count,
                InputTokens = entries.Sum(entry => entry.InputTokens),
                OutputTokens = entries.Sum(entry => entry.OutputTokens),
                CacheReadTokens = entries.Sum(entry => entry.CacheReadTokens),
                CacheCreationTokens = entries.Sum(entry => entry.CacheCreationTokens),
                TotalTokens = entries.Sum(entry => entry.InputTokens + entry.OutputTokens
                    + entry.CacheReadTokens + entry.CacheCreationTokens),
                EstimatedApiCostUsd = entries.Sum(entry => entry.EstimatedApiCostUsd),
                AllModelsPriced = entries.All(entry => entry.ModelPriced),
                LastModel = entries
                    .Where(entry => TokenModelDisplay.IsAgentParticipant(entry.ParticipantId)
                                    && !string.IsNullOrWhiteSpace(entry.Model))
                    .OrderBy(entry => entry.Ts)
                    .LastOrDefault()?.Model,
                LastUpdate = entries.Max(entry => entry.Ts),
                Entries = entries,
            };
            TaskJsonFile.UpdateFieldOrThrow(folderPath, "tokenSummary", summary);
            return Updated();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist remote token receipt in {Folder}", folderPath);
            return false;
        }
    }

    /// <summary>
    /// Extends the attributed commit chain after a conflict-free platform rebase.
    /// Each historical SHA remains visible and points at its exact replacement;
    /// the replacement inherits producer attribution because no new agent attempt
    /// authored it. Idempotent for a replay of the same replacement mapping.
    /// </summary>
    public bool RecordMechanicalRebaseOnFolder(
        string folderPath,
        IReadOnlyList<RebasedCommitReplacement> replacements)
    {
        if (!Directory.Exists(folderPath) || replacements.Count == 0) return false;
        var persisted = ReadPersistedCommitChain(folderPath);
        if (persisted is null || persisted.Count == 0) return false;

        var replacementByOriginal = replacements
            .Where(item => !string.IsNullOrWhiteSpace(item.OriginalSha)
                && !string.IsNullOrWhiteSpace(item.RebasedSha))
            .GroupBy(item => item.OriginalSha, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        if (replacementByOriginal.Count == 0) return false;

        var chain = new List<TaskCommitInfo>(persisted.Count + replacementByOriginal.Count);
        var derived = new List<TaskCommitInfo>();
        var matched = 0;
        foreach (var commit in persisted)
        {
            if (!replacementByOriginal.TryGetValue(commit.Sha, out var replacement))
            {
                chain.Add(commit);
                continue;
            }

            matched++;
            chain.Add(commit with { SupersededBySha = replacement.RebasedSha });
            if (persisted.Any(existing => string.Equals(
                    existing.Sha,
                    replacement.RebasedSha,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            derived.Add(commit with
            {
                Sha = replacement.RebasedSha,
                ShortSha = replacement.RebasedSha[..Math.Min(9, replacement.RebasedSha.Length)],
                SupersededBySha = null,
                SupersededByAttempt = null,
            });
        }

        if (matched == 0) return false;
        chain.AddRange(derived);
        return WriteCommitState(folderPath, chain);
    }

    /// <summary>
    /// Retains the current delivery generation as history while removing it
    /// from future integration expectations. When a current review subject is
    /// available, only commits attributed to that fenced attempt or result are
    /// marked. Legacy unscoped chains fall back to every still-active entry.
    /// </summary>
    public CommitSupersessionWriteResult SupersedeCurrentDeliveryOnFolder(
        string folderPath,
        string supersededByAttempt)
    {
        if (!Directory.Exists(folderPath)
            || string.IsNullOrWhiteSpace(supersededByAttempt))
        {
            return new CommitSupersessionWriteResult(false, 0);
        }

        var persisted = ReadPersistedCommitChain(folderPath);
        if (persisted is null) return new CommitSupersessionWriteResult(false, 0);
        if (persisted.Count == 0) return new CommitSupersessionWriteResult(true, 0);

        var subject = ReviewSubjectStore.Read(folderPath);
        var hasAttempt = !string.IsNullOrWhiteSpace(subject?.RunAttemptId);
        var hasResult = !string.IsNullOrWhiteSpace(subject?.ResultSha);
        var scoped = persisted.Where(commit =>
                !TaskCommitSupersession.IsSuperseded(commit)
                && (hasAttempt && string.Equals(
                        commit.RunAttemptId,
                        subject?.RunAttemptId,
                        StringComparison.OrdinalIgnoreCase)
                    || hasResult && string.Equals(
                        commit.ResultSha,
                        subject?.ResultSha,
                        StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var candidates = scoped.Count > 0
            ? scoped.Select(commit => commit.Sha).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : persisted.Where(commit => !TaskCommitSupersession.IsSuperseded(commit))
                .Select(commit => commit.Sha)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (candidates.Count == 0) return new CommitSupersessionWriteResult(true, 0);
        var updated = persisted.Select(commit => candidates.Contains(commit.Sha)
            ? commit with { SupersededByAttempt = supersededByAttempt.Trim() }
            : commit).ToList();
        var written = WriteCommitState(folderPath, updated);
        return new CommitSupersessionWriteResult(written, written ? candidates.Count : 0);
    }

    /// <summary>
    /// Marks an explicit, policy-approved subset during the legacy healing
    /// sweep. The policy supplies the replacement attempt per SHA; this method
    /// owns the bounded task.json write and preserves every history entry.
    /// </summary>
    public CommitSupersessionWriteResult MarkCommitsSupersededOnFolder(
        string folderPath,
        IReadOnlyDictionary<string, string> replacements)
    {
        if (!Directory.Exists(folderPath)) return new CommitSupersessionWriteResult(false, 0);
        if (replacements.Count == 0) return new CommitSupersessionWriteResult(true, 0);
        var persisted = ReadPersistedCommitChain(folderPath);
        if (persisted is null) return new CommitSupersessionWriteResult(false, 0);

        var changed = 0;
        var updated = persisted.Select(commit =>
        {
            if (TaskCommitSupersession.IsSuperseded(commit)
                || !replacements.TryGetValue(commit.Sha, out var replacement)
                || string.IsNullOrWhiteSpace(replacement))
            {
                return commit;
            }
            changed++;
            return commit with { SupersededByAttempt = replacement.Trim() };
        }).ToList();
        if (changed == 0) return new CommitSupersessionWriteResult(true, 0);
        var written = WriteCommitState(folderPath, updated);
        return new CommitSupersessionWriteResult(written, written ? changed : 0);
    }

    public bool SetRunIntegrationBranchOnFolder(string folderPath, string integrationBranch)
    {
        if (!Directory.Exists(folderPath)) return false;
        var normalized = TaskIntegrationBranch.NormalizeRef(integrationBranch);
        if (normalized is null) return false;
        TaskJsonFile.UpdateField(folderPath, "integrationBranch", normalized, _logger);
        return Updated();
    }

    /// <summary>
    /// Appends one stable integration bookkeeping record without changing any
    /// existing row. The record id is the idempotency key: a repeated sweep is
    /// a successful no-op, even if its wall clock or evidence text differs.
    /// </summary>
    public IntegrationRecordWriteResult AppendIntegrationRecordOnFolder(
        string folderPath,
        TaskIntegrationRecord record)
    {
        if (!Directory.Exists(folderPath)
            || string.IsNullOrWhiteSpace(record.Id)
            || !IntegrationRecordClasses.All.Contains(record.Classification, StringComparer.Ordinal))
        {
            return new IntegrationRecordWriteResult(false, false);
        }

        var path = Path.Combine(folderPath, "task.json");
        if (!File.Exists(path)) return new IntegrationRecordWriteResult(false, false);

        try
        {
            var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(path),
                TaskJsonFile.ReadOpts) ?? new Dictionary<string, JsonElement>();
            var records = new List<TaskIntegrationRecord>();
            if (document.TryGetValue("integrationRecords", out var persisted)
                && persisted.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in persisted.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var parsed = item.Deserialize<TaskIntegrationRecord>(TaskJsonFile.ReadOpts);
                    if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.Id)) records.Add(parsed);
                }
            }

            if (records.Any(existing => string.Equals(
                    existing.Id,
                    record.Id,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return new IntegrationRecordWriteResult(true, false);
            }

            records.Add(record);
            TaskJsonFile.UpdateFieldOrThrow(folderPath, "integrationRecords", records);
            Updated();
            return new IntegrationRecordWriteResult(true, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append integration record in {Folder}", folderPath);
            return new IntegrationRecordWriteResult(false, false);
        }
    }

    private bool WriteCommitState(
        string folderPath,
        List<TaskCommitInfo> chain,
        bool allowEmptyReplacement = false)
    {
        try
        {
            // No-wipe guard: never shrink a non-empty persisted chain to empty.
            // A run that crashes seconds into a resume (before it re-derives the
            // commit set) can reach this replace-all write with an empty chain;
            // overwriting would erase the task's landed-commit metadata. An empty
            // attribution result is never legitimate when commits already exist
            // (the aggregator folds the persisted chain in), so refuse and log
            // instead of silently wiping. ADR-0020 / crash-recovery hygiene.
            if (!allowEmptyReplacement && chain.Count == 0 && ReadPersistedCommitCount(folderPath) > 0)
            {
                _logger.LogWarning(
                    "Refused to wipe non-empty commit chain in {Folder}: incoming attribution was empty (likely a resume-crash race). Keeping existing commits.",
                    folderPath);
                return false;
            }

            chain = EnrichCommitMetadataFromGit(folderPath, chain, out _);

            TaskJsonFile.UpdateField(folderPath, "commits", chain, _logger);
            TaskJsonFile.UpdateField(folderPath, "commit", chain.Count > 0 ? chain[^1] : null, _logger);
            // Drop the obsolete operator-override array (removed feature) so the
            // file is not left carrying a dead field after a rewrite.
            TaskJsonFile.RemoveField(folderPath, "excludedCommits", _logger);
            return Updated();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write commit state to {Folder}", folderPath);
            return false;
        }
    }

    /// <summary>
    /// Count of distinct commits currently persisted in a job's
    /// <c>task.json</c> (the <c>commits</c> array, or the legacy singular
    /// <c>commit</c> field). Used by the no-wipe guard in
    /// <see cref="WriteCommitState"/>; a parse failure reports 0 so the guard
    /// fails open to the normal write path.
    /// </summary>
    private static int ReadPersistedCommitCount(string folderPath)
    {
        try
        {
            var jobJsonPath = Path.Combine(folderPath, "task.json");
            if (!File.Exists(jobJsonPath)) return 0;
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(jobJsonPath), TaskJsonFile.ReadOpts);
            if (doc == null) return 0;

            // Mirror AppendJobCommitOnFolder's parsing (case-insensitive via
            // ReadOpts) so the count matches what a reader would actually see.
            if (doc.TryGetValue("commits", out var commitsEl) && commitsEl.ValueKind == JsonValueKind.Array)
            {
                var count = 0;
                foreach (var item in commitsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var parsed = JsonSerializer.Deserialize<TaskCommitInfo>(item.GetRawText(), TaskJsonFile.ReadOpts);
                    if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Sha)) count++;
                }
                return count;
            }
            if (doc.TryGetValue("commit", out var legacyEl) && legacyEl.ValueKind == JsonValueKind.Object)
            {
                var parsed = JsonSerializer.Deserialize<TaskCommitInfo>(legacyEl.GetRawText(), TaskJsonFile.ReadOpts);
                return parsed != null && !string.IsNullOrWhiteSpace(parsed.Sha) ? 1 : 0;
            }
            return 0;
        }
        catch (Exception ex)
        {
            // Fail open to the normal write path: the guard is a safety net, not
            // a gate. A read failure here must not block a legitimate write.
            SilentCatch.Note(ex, "TaskMutationService: best-effort persisted-commit count for the no-wipe guard.");
            return 0;
        }
    }

    /// <summary>
    /// Stamp a UTC progress heartbeat onto the job's <c>task.json</c>. Written
    /// on every CLI-output flush so <see cref="AgentStudio.Runner.CrashRecoveryService"/>
    /// can attribute orphan working-tree changes to the most-recently-active
    /// job in <c>3-progress</c> on the next backend boot. ADR-0020.
    /// </summary>
    public bool SetJobLastProgressAt(string folderPath, DateTime utcNow)
    {
        if (!Directory.Exists(folderPath)) return false;
        TaskJsonFile.UpdateField(folderPath, "lastProgressAt",
            utcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture), _logger);
        // Intentionally NOT calling Updated(): this is the per-CLI-flush
        // heartbeat (called every few seconds during an active run), and
        // lastProgressAt does not surface in TaskInfo (the kanban card
        // reads LastActivity from disk mtime via GetLastActivityTime).
        // Invalidating here would force a full rescan on every CLI line.
        return true;
    }

    public bool SetJobTaskType(string jobId, string taskType, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        TaskJsonFile.UpdateField(info.FolderPath, "taskType", TaskTypes.Normalize(taskType), _logger);
        return Updated();
    }

    /// <summary>
    /// Replace-all write of the per-job tag id array. Tag ids are normalized
    /// via <see cref="NormalizeTagId"/> (lowercase, <c>[a-z0-9-]</c>, max 32
    /// chars), de-duplicated case-insensitively, and the order of the
    /// caller's list is preserved. Empty input clears the field. The registry
    /// is consulted by readers, not writers: an unknown id is accepted at
    /// write time and rendered as a ghost chip until it lands in
    /// <c>tags.json</c> (or the job is re-tagged).
    /// </summary>
    public bool SetJobTags(string jobId, IEnumerable<string> tags, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var clean = (tags ?? Array.Empty<string>())
            .Select(NormalizeTagId)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        TaskJsonFile.UpdateField(info.FolderPath, "tags", clean, _logger);
        return Updated();
    }

    /// <summary>
    /// Merge-add a single tag id without clobbering existing tags. Used by
    /// runner-side typing paths (e.g. the Codex silent-completion detector
    /// stamps <c>outcome:silent-finish</c>) where the call site does not own
    /// the full tag set and must not race with operator-authored tags. The
    /// tag id is normalised through <see cref="NormalizeTagId"/>; an
    /// already-present id (case-insensitive) is a no-op that still returns
    /// <c>true</c>. Returns <c>false</c> when the job cannot be found or the
    /// supplied id normalises to empty.
    /// </summary>
    public bool AddJobTag(string jobId, string tag, string? watchPath = null)
    {
        var normalized = NormalizeTagId(tag);
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var existing = (IReadOnlyList<string>?)info.Tags ?? Array.Empty<string>();
        if (existing.Any(t => string.Equals(NormalizeTagId(t), normalized, StringComparison.OrdinalIgnoreCase)))
            return true;
        var merged = existing
            .Select(NormalizeTagId)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Append(normalized)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        TaskJsonFile.UpdateField(info.FolderPath, "tags", merged, _logger);
        return Updated();
    }

    /// <summary>
    /// F34: replace-all write of the structured <c>references</c> object
    /// (dependsOn / relatedTo / blockedBy / supersedes / followUpOf /
    /// raisedFollowUps / workbenches). The supplied set is
    /// normalised (trim, drop blanks, de-duplicate case-insensitively per kind)
    /// and written atomically. Validation that referenced keys exist, that the
    /// task does not reference itself, and that dependsOn stays a DAG is the
    /// caller's job (the endpoint owns the cross-task graph); this writer is
    /// deliberately thin so it can also persist a programmatically-built set.
    /// </summary>
    public bool SetTaskReferences(string jobId, TaskReferences references, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var clean = TaskReferenceValidator.Normalize(references ?? new TaskReferences());
        TaskJsonFile.UpdateField(info.FolderPath, "references", clean, _logger);
        _logger.LogInformation(
            "task-references-set job={JobId} dependsOn={DependsOn} relatedTo={RelatedTo} blockedBy={BlockedBy} supersedes={Supersedes} followUpOf={FollowUpOf} raisedFollowUps={RaisedFollowUps} workbenches={Workbenches}",
            jobId, clean.DependsOn.Count, clean.RelatedTo.Count, clean.BlockedBy.Count,
            clean.Supersedes.Count, clean.FollowUpOf.Count, clean.RaisedFollowUps.Count,
            clean.Workbenches.Count);
        return Updated();
    }

    /// <summary>
    /// Coerce arbitrary user input into the tag-id grammar: lowercase ASCII
    /// letters, digits, and hyphens, length 1..32. Unknown characters are
    /// dropped (the spec calls for silent server-side stripping; an empty
    /// result is filtered upstream so an all-junk input becomes a no-op).
    /// </summary>
    public static string NormalizeTagId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim().ToLowerInvariant();
        var sb = new StringBuilder(Math.Min(s.Length, 32));
        foreach (var c in s)
        {
            if (sb.Length >= 32) break;
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') sb.Append(c);
            else if (c == ' ' || c == '_') sb.Append('-');
        }
        // collapse runs of '-' and trim
        var collapsed = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        return collapsed;
    }

    public bool SetJobTitle(string jobId, string title, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var trimmed = title.Trim();
        // Only record a history entry when the value actually changes.
        // A no-op rename (re-submitting the same string from a stale UI)
        // would otherwise bloat the audit trail with identical rows.
        var previous = info.Title ?? "";
        if (!string.Equals(previous, trimmed, StringComparison.Ordinal))
        {
            TitleHistoryLog.Append(info.FolderPath, new TaskTitleHistoryEntry
            {
                At = DateTime.UtcNow,
                OldTitle = previous,
                NewTitle = trimmed,
                Source = "api"
            }, _logger);
            _logger.LogInformation(
                "title-changed jobId={JobId} old={Old} new={New}",
                jobId, previous, trimmed);
        }
        TaskJsonFile.UpdateField(info.FolderPath, "title", trimmed, _logger);
        return Updated();
    }

    public bool UpdateContextUsage(string jobId, ContextUsageSnapshot snapshot, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        TaskJsonFile.UpdateField(info.FolderPath, "contextUsage", snapshot, _logger);
        // Skip Updated(): contextUsage is not surfaced on the kanban card and
        // updates fire on every CLI activity flush during an active run.
        return true;
    }

    /// <summary>
    /// Application-owned write of the optional <c>phase</c> field on a job's
    /// <c>task.json</c>. Used by the orchestrator-intake hosted service to
    /// move a 2-ready card through <c>human-ready → intake-running →
    /// intake-passed | intake-blocked</c> without changing the filesystem
    /// state. Pass <see cref="LifecyclePhases.IsAllowed"/> values; an empty
    /// string clears the field. Validation against the state happens at the
    /// scanner so a corrupt write is rendered inert rather than fatal.
    /// </summary>
    public bool SetJobPhase(string folderPath, string? phase)
    {
        if (!Directory.Exists(folderPath)) return false;
        var normalizedPhase = phase ?? "";
        if (TaskJsonFile.TryReadStringField(folderPath, "phase", _logger, out var currentPhase)
            && string.Equals(currentPhase ?? "", normalizedPhase, StringComparison.Ordinal))
            return true;

        TaskJsonFile.UpdateFields(
            folderPath,
            new Dictionary<string, object>
            {
                ["phase"] = normalizedPhase,
                ["phaseEnteredAt"] = string.IsNullOrWhiteSpace(phase)
                    ? ""
                    : DateTime.UtcNow.ToString("o"),
            },
            _logger);
        return Updated();
    }

    /// <summary>
    /// Sets the explicit content-release approval consumed by release-gated
    /// dependsOn edges. Terminal lane movement intentionally does not call this
    /// method: approval must come from an operator or a dedicated release step.
    /// </summary>
    public bool SetJobReleased(string jobId, bool released, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        TaskJsonFile.UpdateField(info.FolderPath, "released", released, _logger);
        _logger.LogInformation(
            "task-release-set job={JobId} released={Released}",
            jobId, released);
        return Updated();
    }

    private static List<TaskCommitInfo>? ReadPersistedCommitChain(string folderPath)
    {
        try
        {
            var taskJsonPath = Path.Combine(folderPath, "task.json");
            if (!File.Exists(taskJsonPath)) return [];
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(taskJsonPath), TaskJsonFile.ReadOpts);
            if (doc == null) return [];

            var chain = new List<TaskCommitInfo>();
            if (doc.TryGetValue("commits", out var commitsEl)
                && commitsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in commitsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var parsed = JsonSerializer.Deserialize<TaskCommitInfo>(
                        item.GetRawText(), TaskJsonFile.ReadOpts);
                    if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Sha))
                        chain.Add(parsed);
                }
                return chain;
            }

            if (doc.TryGetValue("commit", out var legacyEl)
                && legacyEl.ValueKind == JsonValueKind.Object)
            {
                var parsed = JsonSerializer.Deserialize<TaskCommitInfo>(
                    legacyEl.GetRawText(), TaskJsonFile.ReadOpts);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Sha))
                    chain.Add(parsed);
            }
            return chain;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(
                ex,
                "TaskMutationService: best-effort persisted commit chain for generation union.");
            return null;
        }
    }

    /// <summary>
    /// One-time compatibility migration for multi-repository attribution. The
    /// former message-only <c>[repository]</c> convention is parsed once and
    /// persisted as structured commit data; the subject remains unchanged.
    /// </summary>
    public int BackfillLegacyCommitRepositories()
    {
        var migrated = 0;
        foreach (var task in _scanner.ScanAllJobsWithArchive())
        {
            var persisted = ReadPersistedCommitChain(task.FolderPath);
            if (persisted is null || persisted.Count == 0) continue;

            var changed = false;
            var normalized = persisted.Select(commit =>
            {
                var replacement = TaskCommitRepository.NormalizeLegacy(commit);
                changed |= !string.Equals(
                    replacement.Repository,
                    commit.Repository,
                    StringComparison.Ordinal);
                return replacement;
            }).ToList();
            if (!changed || !WriteCommitState(task.FolderPath, normalized)) continue;
            migrated++;
        }

        if (migrated > 0)
            _logger.LogInformation("commit-repository-backfill migratedTasks={Tasks}", migrated);
        return migrated;
    }

    /// <summary>
    /// One-time, idempotent repair for live cards whose persisted commit entries
    /// pre-date Git-derived <c>filesChanged</c> and <c>files</c> metadata. Git is
    /// read-only; the only writes go through the ordinary commit-state boundary.
    /// Archived cards are deliberately excluded from the migration.
    /// </summary>
    public CommitMetadataBackfillResult BackfillMissingCommitMetadata()
    {
        if (_git is null) return new CommitMetadataBackfillResult(0, 0, 0);

        var repairedTasks = 0;
        var repairedCommits = 0;
        var unresolvedTasks = 0;
        foreach (var task in _scanner.ScanAllJobs()
                     .Where(task => !string.Equals(
                         task.State,
                         TaskStates.Archive,
                         StringComparison.OrdinalIgnoreCase)))
        {
            var missing = ReadMissingCommitMetadataShas(task.FolderPath);
            if (missing.Count == 0) continue;

            var persisted = ReadPersistedCommitChain(task.FolderPath);
            if (persisted is null || persisted.Count == 0) continue;
            var enriched = EnrichCommitMetadataFromGit(
                task.FolderPath,
                persisted,
                out var resolved);
            if (missing.Any(sha => !ShaSetContains(resolved, sha)))
            {
                unresolvedTasks++;
                _logger.LogWarning(
                    "commit-metadata-backfill unresolved project={Project} job={JobId} missing={Missing}",
                    task.ProjectName,
                    task.Id,
                    string.Join(",", missing));
                continue;
            }

            if (!WriteCommitState(task.FolderPath, enriched))
            {
                unresolvedTasks++;
                continue;
            }

            repairedTasks++;
            repairedCommits += missing.Count;
        }

        if (repairedTasks > 0)
        {
            _logger.LogInformation(
                "commit-metadata-backfill repairedTasks={Tasks} repairedCommits={Commits}",
                repairedTasks,
                repairedCommits);
        }
        return new CommitMetadataBackfillResult(
            repairedTasks,
            repairedCommits,
            unresolvedTasks);
    }

    /// <summary>
    /// Normalizes every commit object at the persistence boundary from Git
    /// truth. This keeps attribution, salvage, operator, recovery, and sweep
    /// producers from drifting according to which metadata they happened to
    /// supply. If the repository or object is unavailable, the supplied record
    /// is preserved rather than inventing a zero-file result.
    /// </summary>
    private List<TaskCommitInfo> EnrichCommitMetadataFromGit(
        string folderPath,
        IReadOnlyList<TaskCommitInfo> commits,
        out HashSet<string> resolvedShas)
    {
        resolvedShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_git is null || commits.Count == 0) return commits.ToList();

        try
        {
            var normalizedFolder = NormalizePath(folderPath);
            var task = _scanner.ScanAllJobsWithArchive().FirstOrDefault(candidate =>
                string.Equals(
                    NormalizePath(candidate.FolderPath),
                    normalizedFolder,
                    StringComparison.OrdinalIgnoreCase));
            if (task is null) return commits.ToList();

            var metadata = _git.GetCommitMeta(
                task.Id,
                task.WatchPath,
                commits.Select(commit => commit.Sha));
            var enriched = new List<TaskCommitInfo>(commits.Count);
            foreach (var commit in commits)
            {
                var resolvedSha = ResolveSha(metadata.Keys, commit.Sha);
                if (resolvedSha is null)
                {
                    enriched.Add(commit);
                    continue;
                }

                var files = _git.GetCommitFiles(task.Id, task.WatchPath, commit.Sha);
                enriched.Add(commit with
                {
                    FilesChanged = files.Count,
                    Files = files.Select(file => file.Path).ToList(),
                });
                resolvedShas.Add(resolvedSha);
            }
            return enriched;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "commit-metadata-enrichment failed folder={Folder}",
                folderPath);
            return commits.ToList();
        }
    }

    private static HashSet<string> ReadMissingCommitMetadataShas(string folderPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(folderPath, "task.json");
            if (!File.Exists(path)) return result;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (TryGetProperty(root, "commits", out var commits)
                && commits.ValueKind == JsonValueKind.Array)
            {
                foreach (var commit in commits.EnumerateArray())
                    AddWhenMetadataMissing(commit, result);
                return result;
            }

            if (TryGetProperty(root, "commit", out var legacy)
                && legacy.ValueKind == JsonValueKind.Object)
            {
                AddWhenMetadataMissing(legacy, result);
            }
        }
        catch (Exception ex)
        {
            SilentCatch.Note(
                ex,
                "TaskMutationService: best-effort missing commit metadata inspection.");
        }
        return result;
    }

    private static void AddWhenMetadataMissing(
        JsonElement commit,
        HashSet<string> result)
    {
        if (commit.ValueKind != JsonValueKind.Object
            || !TryGetProperty(commit, "sha", out var shaElement))
        {
            return;
        }
        var sha = shaElement.GetString();
        if (string.IsNullOrWhiteSpace(sha)) return;
        if (!TryGetProperty(commit, "filesChanged", out var filesChanged)
            || filesChanged.ValueKind != JsonValueKind.Number
            || !TryGetProperty(commit, "files", out var files)
            || files.ValueKind != JsonValueKind.Array)
        {
            result.Add(sha);
        }
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static string? ResolveSha(IEnumerable<string> fullShas, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return null;
        return fullShas.FirstOrDefault(candidate =>
            string.Equals(candidate, sha, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(sha, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShaSetContains(IReadOnlySet<string> shas, string sha)
        => ResolveSha(shas, sha) is not null;

    private static string NormalizePath(string path)
        => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Application-owned write of the append-only <c>provenance</c> object on a
    /// job's <c>task.json</c> (ASS-1724). The caller
    /// (<see cref="TaskProvenanceService"/>) owns the append semantics - it reads
    /// the current provenance off <see cref="TaskInfo"/>, adds one transition, and
    /// hands the merged record here for a replace-all write. Folder-only
    /// invalidation: provenance is not surfaced on the kanban card, so no SignalR
    /// push is needed; the read endpoint pulls it fresh on demand.
    /// </summary>
    public bool SetProvenanceOnFolder(string folderPath, TaskProvenance provenance)
    {
        if (!Directory.Exists(folderPath)) return false;
        TaskJsonFile.UpdateField(folderPath, "provenance", provenance, _logger);
        return Updated();
    }

    /// <summary>
    /// Application-owned write of the <c>externalCompletion</c> object on a
    /// job's <c>task.json</c> (out-of-band task completion, §3 of
    /// <c>docs/concepts/out-of-band-task-completion.md</c>). Replace-all write:
    /// the external-completion endpoint owns the record and hands the finished
    /// shape here. Invalidates the scanner cache so the "extern erledigt" badge
    /// shows without waiting out the FileSystemWatcher debounce.
    /// </summary>
    public bool SetExternalCompletionOnFolder(string folderPath, ExternalCompletionInfo externalCompletion)
    {
        if (!Directory.Exists(folderPath)) return false;
        TaskJsonFile.UpdateField(folderPath, "externalCompletion", externalCompletion, _logger);
        return Updated();
    }

    public string? CreateJob(CreateTaskRequest req)
    {
        var watchPaths = _scanner.GetWatchPaths();
        // D1: a caller addresses the target project by a path-free handle — a
        // short code / Kürzel (e.g. ASS) or a stable PROJ-NNN id (both survive a
        // folder move that a hard-coded path would not). The preferred `Project`
        // field wins; `WatchPath` is the deprecated fallback for legacy callers
        // and may itself carry a handle or a raw path. Resolution turns any of
        // these into the project's storage location; a plain path passes
        // through unchanged.
        var requestedHandle = string.IsNullOrWhiteSpace(req.Project) ? req.WatchPath : req.Project;
        var requestedPath = ResolveRequestedWatchPath(requestedHandle);
        // Path-aware project resolution. The previous ordinal, case-sensitive
        // `w.Path == req.WatchPath` compared the client's watchPath byte-for-byte
        // against the RESOLVED entry path (Path.GetFullPath, back-slashed on
        // Windows), so a create that posted the same directory with forward
        // slashes / a trailing separator / different drive-letter case matched
        // no entry and returned null → 409 "already exists or invalid input".
        // See WatchPathComparison (AGT-1940).
        var entry = string.IsNullOrEmpty(requestedPath)
            ? watchPaths.FirstOrDefault()
            : watchPaths.FirstOrDefault(w => WatchPathComparison.PathsEqual(w.Path, requestedPath));

        if (entry == null) return null;

        var acceptanceScope = TaskAcceptanceScopes.Normalize(req.AcceptanceScope);
        if (req.AcceptanceScope is not null && acceptanceScope is null) return null;

        // Backlog is the default landing lane when the caller supplies no
        // targetState. An explicit valid lane is authoritative. In particular,
        // operator and automation callers may intentionally create a card in a
        // review lane; silently clamping those requests to Backlog loses the
        // caller's routing decision and lets later guards misclassify the card.
        var targetState = string.IsNullOrWhiteSpace(req.TargetState)
            ? TaskStates.Backlog
            : TaskStates.All.Contains(req.TargetState, StringComparer.Ordinal)
                ? req.TargetState
                : null;
        if (targetState == null) return null;

        // Sanitize ID: transliterate umlauts, lowercase, replace spaces with dashes, only allow safe chars
        var baseSlug = string.IsNullOrWhiteSpace(req.Id)
            ? ToSlug(req.Title)
            : req.Id;
        if (string.IsNullOrEmpty(baseSlug)) return null;

        var taskKey = MintTaskKey(entry.Path);
        var storageId = taskKey ?? baseSlug;
        TaskStorageLayout.TryParseKeyNumber(storageId, out var storageNumber);

        // Root-cause fix for duplicate-slug folders: the external id must be
        // unique across the project even though it is no longer the physical
        // folder name. Reserve the resolved id + create the jobs/<bucket>/<key>
        // folder under the lane mutex so concurrent creates cannot pick the
        // same external id or storage id.
        string jobId;
        string jobDir;
        using (_laneMutex.Acquire(entry.Path))
        {
            jobId = EnsureUniqueJobId(entry.Path, baseSlug);
            jobDir = TaskStorageLayout.JobDir(entry.Path, storageNumber, storageId);
            for (var suffix = 2; Directory.Exists(jobDir); suffix++)
            {
                storageId = $"{taskKey ?? baseSlug}-{suffix}";
                TaskStorageLayout.TryParseKeyNumber(storageId, out storageNumber);
                jobDir = TaskStorageLayout.JobDir(entry.Path, storageNumber, storageId);
            }
            Directory.CreateDirectory(jobDir);
        }
        if (!string.Equals(jobId, baseSlug, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "job-slug-deduped baseSlug={BaseSlug} resolvedSlug={ResolvedSlug} watchPath={WatchPath} targetState={TargetState}",
                baseSlug, jobId, entry.Path, targetState);
        }

        // Land new jobs at the bottom of their target lane so the visible order
        // in the UI matches the backend pickup order (OrderBy(Order) ascending).
        // Falling back to the request's default (999) collides every new job on
        // the same key, so tie-break would depend on filesystem scan order and
        // the user has no way to predict which one runs next.
        var existingMaxOrder = _scanner.ScanAllJobs()
            .Where(j => WatchPathComparison.PathsEqual(j.WatchPath, entry.Path) && j.State == targetState)
            .Select(j => (int?)j.Order)
            .Max();
        var resolvedOrder = req.Order != 999 ? req.Order : (existingMaxOrder ?? 0) + 10;

        var ownerClientId = !string.IsNullOrWhiteSpace(req.OwnerClientId)
            ? req.OwnerClientId!
            : DefaultClientIdentity.Id;

        // Materialize effective agent/cliType/model from the owner's
        // client defaults when the request does not carry explicit values.
        // This prevents the old "agent: human, cliType: null, model: null"
        // triple that was misleading on the card and in the dataset.
        var ownerIdentity = _clients.Find(ownerClientId);
        var effectiveCliType = !string.IsNullOrWhiteSpace(req.CliType)
            ? CliTypes.Normalize(req.CliType)
            : (ownerIdentity?.DefaultCliType is { } dc && CliTypes.IsValid(dc)
                ? CliTypes.Normalize(dc)
                : null);
        var effectiveModel = !string.IsNullOrWhiteSpace(req.Model)
            ? req.Model.Trim()
            : ownerIdentity?.DefaultModel;
        effectiveModel = ModelMetadataRegistry.NormalizeForCli(effectiveCliType, effectiveModel);
        var effectiveThinkingLevel = ModelMetadataRegistry.ResolveThinkingLevel(
            effectiveCliType,
            effectiveModel,
            !string.IsNullOrWhiteSpace(req.ThinkingLevel)
                ? req.ThinkingLevel
                : ownerIdentity?.DefaultThinkingLevel);
        var effectiveAgent = string.Equals(req.Agent, AgentTypes.Human, StringComparison.OrdinalIgnoreCase)
            ? AgentTypes.Human
            : effectiveCliType ?? req.Agent;

        var materializedFromDefaults = effectiveCliType != null && string.IsNullOrWhiteSpace(req.CliType);
        if (materializedFromDefaults)
        {
            _logger.LogInformation(
                "job-defaults-materialized jobId={JobId} owner={Owner} agent={Agent} cliType={CliType} model={Model}",
                jobId, ownerClientId, effectiveAgent, effectiveCliType, effectiveModel);
        }

        var jobJson = new Dictionary<string, object?>
        {
            ["id"] = jobId,
            ["title"] = req.Title,
            ["createdAt"] = DateTime.UtcNow.ToString("o"),
            ["creationSource"] = string.IsNullOrWhiteSpace(req.CreationSource) ? "human" : req.CreationSource.Trim(),
            ["createdBy"] = string.IsNullOrWhiteSpace(req.CreatedBy) ? ownerClientId : req.CreatedBy.Trim(),
            // Lane-entry sort anchor: a freshly created task has just entered
            // its initial lane, so stamp it now. Re-stamped on every move.
            ["enteredLaneAt"] = DateTime.UtcNow.ToString("o"),
            ["state"] = targetState,
            ["order"] = resolvedOrder,
            ["agent"] = effectiveAgent,
            ["ownerClientId"] = ownerClientId
        };
        if (acceptanceScope is not null)
            jobJson["acceptanceScope"] = acceptanceScope;
        if (!string.IsNullOrWhiteSpace(effectiveModel))
            jobJson["model"] = effectiveModel;
        jobJson["modelExplicit"] = req.ModelExplicit ?? !string.IsNullOrWhiteSpace(req.Model);
        if (!string.IsNullOrWhiteSpace(effectiveThinkingLevel))
            jobJson["thinkingLevel"] = effectiveThinkingLevel;
        jobJson["thinkingLevelExplicit"] = req.ThinkingLevelExplicit ?? !string.IsNullOrWhiteSpace(req.ThinkingLevel);
        if (!string.IsNullOrWhiteSpace(effectiveCliType))
            jobJson["cliType"] = effectiveCliType;
        // Epics: card kind (task|epic) + optional parent epic (assignment way 1,
        // at create time). kind is always written so a fresh task.json is explicit.
        jobJson["kind"] = TaskKinds.Normalize(req.Kind);
        if (!string.IsNullOrWhiteSpace(req.EpicId))
            jobJson["epicId"] = req.EpicId;
        // Execution mode (coding|planning|research) + web-access. mode is always
        // written; web access defaults by mode when the request omits it
        // (research on, else off) - see planning-research-task-kinds note.
        var effectiveMode = TaskModes.Normalize(req.Mode);
        jobJson["mode"] = effectiveMode;
        if (req.NoBranchExpected || AcceptanceIntegrationPolicy.IsNoBranchTaskType(req.TaskType))
            jobJson["noBranchExpected"] = true;
        jobJson["allowWebAccess"] = req.AllowWebAccess ?? (effectiveMode == TaskModes.Research);
        if (req.Fixture)
            jobJson["fixture"] = true;

        // taskType is always written so a fresh task.json carries an explicit
        // value. Legacy folders without the field render as Chore (the
        // scanner's lazy default), so we only emit it on create.
        jobJson["taskType"] = TaskTypes.Normalize(req.TaskType);

        if (req.Tags is { Count: > 0 })
        {
            jobJson["tags"] = req.Tags
                .Select(NormalizeTagId)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        jobJson["key"] = storageId;

        File.WriteAllText(Path.Combine(jobDir, "task.json"),
            JsonSerializer.Serialize(jobJson, new JsonSerializerOptions { WriteIndented = true }));

        if (!string.IsNullOrWhiteSpace(req.PromptMarkdown))
            File.WriteAllText(Path.Combine(jobDir, "prompt.md"), req.PromptMarkdown);

        var location = TaskStorageLayout.Location(storageNumber, storageId);
        TaskLayoutIndex.Upsert(entry.Path, storageId, location, targetState, _logger);

        // ADR-0049: open the per-job timeline with prompt_created so the
        // Overview strip has something to render before the first agent run.
        _timeline?.Append(
            jobDir,
            TimelineEventKinds.PromptCreated,
            string.Equals(req.CreationSource, TimelineActors.Orchestrator, StringComparison.OrdinalIgnoreCase)
                ? TimelineActors.Orchestrator
                : string.IsNullOrWhiteSpace(req.Agent) ? TimelineActors.System : TimelineActors.Human(ownerClientId),
            summary: string.IsNullOrWhiteSpace(req.Title) ? $"Task {jobId} created" : $"Task created: {req.Title}",
            payloadRef: "prompt.md",
            details: new()
            {
                ["targetState"] = targetState ?? string.Empty,
                ["agent"] = effectiveAgent ?? string.Empty,
                ["creationSource"] = string.IsNullOrWhiteSpace(req.CreationSource) ? "human" : req.CreationSource.Trim(),
                ["createdBy"] = string.IsNullOrWhiteSpace(req.CreatedBy) ? ownerClientId : req.CreatedBy.Trim(),
            });

        _scanner.InvalidateCache();
        // Push a typed jobCreated to connected clients so other tabs render
        // the new card within ~1s instead of waiting for the next board poll.
        // Resolve the just-written TaskInfo so the bridge can ship the canonical
        // row; on the rare miss the bridge falls back to a bulk re-pull.
        var created = _scanner.FindJob(jobId, entry.Path);
        _notifier.PublishCreated(created?.ProjectName ?? string.Empty, jobId, entry.Path);
        return jobId;
    }

    /// <summary>
    /// Resolves the caller-supplied project handle to a filesystem watchPath.
    /// The external API contract addresses projects by a stable, path-free
    /// handle so the FS layout never leaks into the wire:
    /// <list type="bullet">
    /// <item><c>PROJ-NNN</c> — the stable project id (survives a folder move).</item>
    /// <item>a short code / Kürzel (e.g. <c>ASS</c>) — the human handle.</item>
    /// </list>
    /// A value that is neither (an absolute path from a legacy/deprecated
    /// caller) passes through unchanged so existing consumers keep working
    /// during the watchPath-encapsulation migration.
    /// </summary>
    private string? ResolveRequestedWatchPath(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return requested;
        var trimmed = requested.Trim();

        if (trimmed.StartsWith("PROJ-", StringComparison.OrdinalIgnoreCase))
        {
            var byId = _projectRegistry.FindById(trimmed);
            if (byId is { StorageLocation: { Length: > 0 } storageById })
            {
                _logger.LogInformation(
                    "create-by-project-id projectId={ProjectId} resolvedWatchPath={WatchPath}",
                    trimmed, storageById);
                return storageById;
            }

            _logger.LogWarning("create-by-project-id-unresolved projectId={ProjectId}", trimmed);
            return requested;
        }

        // Short code (Kürzel) — only when the token cannot be a filesystem path.
        // Codes are 2–6 chars of A–Z/0–9, so anything holding a path separator,
        // drive colon, or dot is treated as a raw path and passed through.
        if (LooksLikeShortCode(trimmed))
        {
            var byCode = _projectRegistry.FindByShortCode(trimmed);
            if (byCode is { StorageLocation: { Length: > 0 } storageByCode })
            {
                _logger.LogInformation(
                    "create-by-short-code shortCode={ShortCode} resolvedWatchPath={WatchPath}",
                    trimmed, storageByCode);
                return storageByCode;
            }
            // Not a known code: fall through and treat the value as a raw path.
        }

        return requested;
    }

    /// <summary>
    /// True when <paramref name="value"/> has the shape of a project short code
    /// (Kürzel): 2–6 chars of A–Z/0–9 and no filesystem-path markers. Used to
    /// decide whether a create handle should be resolved via the registry
    /// before falling back to treating it as an absolute watchPath.
    /// </summary>
    private static bool LooksLikeShortCode(string value)
    {
        if (value.Length is < 2 or > 6) return false;
        foreach (var ch in value)
        {
            var isUpper = ch is >= 'A' and <= 'Z';
            var isLower = ch is >= 'a' and <= 'z';
            var isDigit = ch is >= '0' and <= '9';
            if (!isUpper && !isLower && !isDigit) return false;
        }
        return true;
    }

    private string EnsureUniqueJobId(string watchPath, string baseSlug)
    {
        var used = _scanner.ScanAllJobs()
            .Where(j => WatchPathComparison.PathsEqual(j.WatchPath, watchPath))
            .Select(j => j.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(baseSlug)) return baseSlug;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseSlug}-{suffix}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    public bool UpdateJobFile(string jobId, string fileName, string content, string? watchPath = null)
    {
        var allowed = new[] { "prompt.md" };
        if (!allowed.Contains(fileName)) return false;

        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;

        // Liveness (is a CLI actually running?) is checked by the endpoint via
        // TaskRunnerService.IsJobLive - the "3-progress" folder alone is not a
        // reliable signal because jobs stay there after stop / crash / restart.

        var filePath = Path.Combine(info.FolderPath, fileName);
        WriteAllTextWithRetry(filePath, content);
        // prompt.md does not affect kanban-card fields, but UpdateJobFile is
        // user-initiated (edit prompt) and the next read should see the
        // change for any consumer that pulls TaskDetail with the prompt body.
        return Updated();
    }

    /// <summary>
    /// Writes a text file tolerating transient Windows file-locks. The file
    /// can be briefly held by editors (VSCode), search indexers, AV scanners,
    /// or our own readers (status panel, log panel). A short retry loop with
    /// FileShare.ReadWrite avoids surfacing an HTTP 500 to the user for what
    /// is almost always a sub-second contention.
    /// </summary>
    private static void WriteAllTextWithRetry(string filePath, string content)
    {
        const int maxAttempts = 8;
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        IOException? last = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException ex) when (attempt < maxAttempts - 1)
            {
                last = ex;
                Thread.Sleep(50 * (attempt + 1));
            }
        }
        if (last != null) throw last;
    }

    /// <summary>
    /// Saves a binary attachment (typically a pasted/dropped screenshot) into the job folder's
    /// <c>attachments/</c> subdirectory and returns the stored file name. Reused inside the prompt
    /// editor as a relative reference (<c>![alt](attachments/abc.png)</c>) so the CLI agent can
    /// resolve the same image directly from disk via the relative path in <c>prompt.md</c>.
    /// </summary>
    public (string? FileName, string? Error) SaveAttachment(string jobId, string? watchPath, byte[] content, string? originalFileName, string? contentType)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return (null, "Job not found");
        if (content.Length == 0) return (null, "Empty file");
        if (content.Length > 10 * 1024 * 1024) return (null, "File too large (max 10 MB)");

        var ext = ResolveImageExtension(originalFileName, contentType);
        if (ext == null) return (null, "Unsupported file type - only png, jpg, gif, webp allowed");

        var attachmentsDir = Path.Combine(info.FolderPath, "attachments");
        Directory.CreateDirectory(attachmentsDir);

        // Short random ID keeps generated markdown readable; collisions are vanishingly rare
        // inside one job folder (~16M IDs at 4 bytes hex).
        string fileName;
        string fullPath;
        do
        {
            fileName = $"{Guid.NewGuid():N}"[..8] + ext;
            fullPath = Path.Combine(attachmentsDir, fileName);
        } while (File.Exists(fullPath));

        File.WriteAllBytes(fullPath, content);
        return (fileName, null);
    }

    private static string? ResolveImageExtension(string? originalFileName, string? contentType)
    {
        var ext = string.IsNullOrWhiteSpace(originalFileName)
            ? null
            : Path.GetExtension(originalFileName).ToLowerInvariant();

        if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp") return ext == ".jpeg" ? ".jpg" : ext;

        return contentType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => null
        };
    }

    /// <summary>
    /// Appends a "Continuous Session Nachtrag" block to <c>prompt.md</c> so the user's follow-up
    /// stays visible as part of the task description. <c>status.md</c> is intentionally not touched -
    /// it is owned by the post-run summary generator.
    /// </summary>
    /// <summary>
    /// Persist a user follow-up as a saved <see cref="PendingIntent"/> on the
    /// target job. Used by the busy-project queue path: when the user sends a
    /// follow-up to a job that is not the project's current active job, the
    /// intent is saved here, the job is later promoted to <c>2-ready</c>, and
    /// the auto-pickup loop consumes the saved intent on its next tick.
    /// Latest-wins: a second save overwrites the first.
    /// </summary>
    public PendingIntent? SavePendingIntent(
        string jobId,
        string mode,
        string prompt,
        string reason,
        string? activeJobId,
        string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return null;
        var intent = new PendingIntent
        {
            Mode = ContinueModes.Normalize(mode),
            Prompt = prompt ?? string.Empty,
            SavedAt = DateTime.UtcNow,
            SavedReason = string.IsNullOrWhiteSpace(reason) ? "project-busy" : reason,
            SavedAgainstActiveJobId = activeJobId
        };
        try
        {
            var path = Path.Combine(info.FolderPath, "pending-intent.json");
            File.WriteAllText(path,
                JsonSerializer.Serialize(intent, _pendingIntentWriteOpts),
                Encoding.UTF8);
            // PendingIntent appears on TaskInfo (kanban card shows the intent),
            // so the snapshot must be invalidated for the next read to see it.
            _scanner.InvalidateCache();
            return intent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save pending-intent.json for {JobId}", jobId);
            return null;
        }
    }

    private static readonly JsonSerializerOptions _pendingIntentWriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Read and consume a saved pending intent. Returns null when there is
    /// nothing to consume. The file is renamed to
    /// <c>pending-intent.consumed.json</c>, which stays on disk as the
    /// operator-visible proof that a queued follow-up reached a run (paired with
    /// a <c>follow_up_consumed</c> ledger row). If the caller's run fails to
    /// spawn, the rollback rule is to rename it back so the next tick retries
    /// instead of losing the user's input - see
    /// <see cref="RollbackStashedPendingIntent"/>, which only the run that
    /// stashed the intent may call.
    /// </summary>
    public PendingIntent? ReadAndStashPendingIntent(string jobFolder)
    {
        var path = Path.Combine(jobFolder, "pending-intent.json");
        if (!File.Exists(path)) return null;
        try
        {
            var raw = File.ReadAllText(path);
            var intent = JsonSerializer.Deserialize<PendingIntent>(raw, TaskJsonFile.ReadOpts);
            if (intent == null) return null;
            var stash = Path.Combine(jobFolder, "pending-intent.consumed.json");
            if (File.Exists(stash)) File.Delete(stash);
            File.Move(path, stash);
            // pending-intent.json gone → TaskInfo.PendingIntent should be null.
            _scanner.InvalidateCache();
            return intent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read pending-intent.json at {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Drops both forms of a pending intent when authority proves that no
    /// continuation run may be queued. Unlike normal successful consumption,
    /// this also removes the canonical, not-yet-stashed file.
    /// </summary>
    public bool DiscardPendingIntent(string jobFolder)
    {
        try
        {
            var canonical = Path.Combine(jobFolder, "pending-intent.json");
            var stash = Path.Combine(jobFolder, "pending-intent.consumed.json");
            if (File.Exists(canonical)) File.Delete(canonical);
            if (File.Exists(stash)) File.Delete(stash);
            _scanner.InvalidateCache();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discard pending intent at {Folder}", jobFolder);
            return false;
        }
    }

    /// <summary>
    /// Roll back a failed pending-intent consumption: move the stash back to
    /// <c>pending-intent.json</c> so the next pickup tries again. If the
    /// canonical file already exists (rare race), the stash is dropped to
    /// honor latest-wins.
    /// </summary>
    public void RollbackStashedPendingIntent(string jobFolder)
    {
        var stash = Path.Combine(jobFolder, "pending-intent.consumed.json");
        if (!File.Exists(stash)) return;
        var canonical = Path.Combine(jobFolder, "pending-intent.json");
        try
        {
            if (File.Exists(canonical))
            {
                File.Delete(stash);
            }
            else
            {
                File.Move(stash, canonical);
            }
            _scanner.InvalidateCache();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to roll back pending-intent at {Stash}", stash);
        }
    }

    public bool AppendContinuationNote(string jobId, string followupPrompt, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(followupPrompt)) return false;

        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var block = $"\n\n---\n\n## Continuous Session Note - {timestamp}\n\n{followupPrompt.TrimEnd()}\n";

        AppendWithLeadingNewline(Path.Combine(info.FolderPath, "prompt.md"), block);
        return true;
    }

    private static void AppendWithLeadingNewline(string filePath, string block)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var existing = File.ReadAllText(filePath);
                var separator = existing.EndsWith('\n') ? string.Empty : "\n";
                File.AppendAllText(filePath, separator + block);
            }
            else
            {
                File.WriteAllText(filePath, block.TrimStart('\n'));
            }
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "TaskMutationService: Best-effort append - failure to persist the addendum should not block the CLI resume.");
            // Best-effort append - failure to persist the addendum should not block the CLI resume.
        }
    }

    /// <summary>
    /// Boot-time migration: rewrites jobs that carry the misleading
    /// <c>agent: "human" + cliType: null + model: null</c> triple when
    /// the owner client has configured defaults. Idempotent (no-op on
    /// already-migrated jobs). Returns the number of jobs backfilled.
    /// </summary>
    public int BackfillAgentDefaults()
    {
        var count = 0;
        foreach (var job in _scanner.ScanAllJobs())
        {
            if (!string.Equals(job.Agent, AgentTypes.Human, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(job.CliType) || !string.IsNullOrWhiteSpace(job.Model))
                continue;

            var ownerId = job.OwnerClientId;
            if (string.IsNullOrWhiteSpace(ownerId)) continue;

            var owner = _clients.Find(ownerId);
            if (owner == null) continue;

            var cliType = owner.DefaultCliType is { } dc && CliTypes.IsValid(dc)
                ? CliTypes.Normalize(dc)
                : null;
            var model = owner.DefaultModel;
            model = ModelMetadataRegistry.NormalizeForCli(cliType, model);
            var thinkingLevel = ModelMetadataRegistry.ResolveThinkingLevel(cliType, model, owner.DefaultThinkingLevel);

            if (cliType == null && model == null && thinkingLevel == null) continue;

            if (cliType != null)
            {
                TaskJsonFile.UpdateField(job.FolderPath, "cliType", cliType, _logger);
                TaskJsonFile.UpdateField(job.FolderPath, "agent", cliType, _logger);
            }
            if (model != null)
                TaskJsonFile.UpdateField(job.FolderPath, "model", model, _logger);
            if (thinkingLevel != null)
                TaskJsonFile.UpdateField(job.FolderPath, "thinkingLevel", thinkingLevel, _logger);

            count++;
            _logger.LogInformation(
                "backfill-agent-defaults jobId={JobId} owner={Owner} agent={Agent} cliType={CliType} model={Model} thinkingLevel={ThinkingLevel}",
                job.Id, ownerId, cliType ?? job.Agent, cliType, model, thinkingLevel);
        }
        if (count > 0) _scanner.InvalidateCache();
        return count;
    }

    /// <summary>
    /// Mint a <c>SHC-NNN</c> task key for the project that owns
    /// <paramref name="watchPath"/>. Returns null when the project
    /// registry has no record for this path (non-fatal; the job will
    /// simply have <c>key: null</c>).
    ///
    /// <para>Before issuing, the per-project counter floor is re-derived
    /// from the keys actually present on disk. The in-memory
    /// <c>NextTaskKeySeq</c> is the fast path, but it can be rewound under
    /// the registry (e.g. a second backend sharing this workspace persists
    /// a stale snapshot, clobbering the live counter). Deriving the floor
    /// from disk on every mint makes the on-disk keys the source of truth,
    /// so a rewound counter can never re-issue a key that is already in
    /// use. This closed the bug where ASS-594/598 (and a contiguous band
    /// around them) were each minted onto two different tasks.</para>
    /// </summary>
    private string? MintTaskKey(string watchPath)
    {
        try
        {
            var project = _projectRegistry.FindByStorageLocation(watchPath);
            if (project == null) return null;

            var floor = HighestExistingKeyNumber(project.Id, project.ShortCode) + 1;
            _projectRegistry.EnsureTaskKeyFloor(project.Id, floor);

            var seq = _projectRegistry.IssueNextTaskKey(project.Id);
            return $"{project.ShortCode}-{seq}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "task-key-mint-failed watchPath={WatchPath}", watchPath);
            return null;
        }
    }

    /// <summary>
    /// Highest numeric tail among the keys currently on disk for the
    /// project identified by <paramref name="projectId"/> /
    /// <paramref name="shortCode"/>. Returns 0 when the project has no
    /// keyed tasks yet. Reads from the (cached) scanner snapshot.
    /// </summary>
    private int HighestExistingKeyNumber(string projectId, string shortCode)
    {
        var max = 0;
        foreach (var job in _scanner.ScanAllJobs())
        {
            if (string.IsNullOrWhiteSpace(job.Key)) continue;
            var owner = _projectRegistry.FindByStorageLocation(job.WatchPath);
            if (owner == null ||
                !string.Equals(owner.Id, projectId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (TaskKeyNumbers.TryParse(job.Key, shortCode, out var n) && n > max)
                max = n;
        }
        return max;
    }

    /// <summary>
    /// Boot-time backfill: stamps a <c>key</c> on every job whose
    /// <c>task.json</c> currently has no key. Idempotent; jobs that
    /// already carry a key are skipped. The project counter floor is first
    /// raised past the highest key already on disk (across all tasks, not
    /// only the ones being stamped) so a freshly stamped key cannot collide
    /// with an existing one even when the in-memory counter has drifted
    /// behind disk. Returns the number of jobs stamped.
    /// </summary>
    public int BackfillTaskKeys()
    {
        var stamped = 0;

        // Raise every keyed project's floor past its on-disk high-water mark
        // before issuing any new number. This repairs a counter that drifted
        // below disk (the same root cause as the duplicate-key bug) so the
        // stamps below land above existing keys.
        var byProject = new Dictionary<string, (string shortCode, int max)>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in _scanner.ScanAllJobs())
        {
            var project = _projectRegistry.FindByStorageLocation(job.WatchPath);
            if (project == null) continue;
            if (TaskKeyNumbers.TryParse(job.Key, project.ShortCode, out var n))
            {
                if (!byProject.TryGetValue(project.Id, out var cur) || n > cur.max)
                    byProject[project.Id] = (project.ShortCode, n);
            }
        }
        foreach (var (projectId, info) in byProject)
            _projectRegistry.EnsureTaskKeyFloor(projectId, info.max + 1);

        foreach (var job in _scanner.ScanAllJobs())
        {
            if (!string.IsNullOrWhiteSpace(job.Key)) continue;

            var project = _projectRegistry.FindByStorageLocation(job.WatchPath);
            if (project == null) continue;

            var seq = _projectRegistry.IssueNextTaskKey(project.Id);
            var key = $"{project.ShortCode}-{seq}";
            TaskJsonFile.UpdateField(job.FolderPath, "key", key, _logger);
            stamped++;
        }

        if (stamped > 0) _scanner.InvalidateCache();
        return stamped;
    }

    /// <summary>
    /// One-shot sweep that resolves duplicate display keys: two or more
    /// tasks in the same project carrying the identical <c>key</c>. The
    /// oldest task (earliest <c>createdAt</c>, id as tiebreak) keeps the
    /// contested key; every later namesake is re-keyed with a fresh number
    /// minted above the project's on-disk high-water mark. Only the
    /// <c>key</c> field changes - task ids, folders, and content are
    /// untouched. Idempotent: a second run with no collisions returns 0.
    /// Returns the number of tasks re-keyed.
    /// </summary>
    public int DeduplicateTaskKeys()
    {
        // Group keyed tasks by (projectId, key). A group with >1 member is a
        // collision.
        var groups = new Dictionary<(string ProjectId, string Key), List<TaskInfo>>();
        foreach (var job in _scanner.ScanAllJobs())
        {
            if (string.IsNullOrWhiteSpace(job.Key)) continue;
            var project = _projectRegistry.FindByStorageLocation(job.WatchPath);
            if (project == null) continue;
            var gk = (project.Id, job.Key!.Trim());
            if (!groups.TryGetValue(gk, out var list))
                groups[gk] = list = new List<TaskInfo>();
            list.Add(job);
        }

        var collisions = groups
            .Where(kvp => kvp.Value.Count > 1)
            .ToList();
        if (collisions.Count == 0) return 0;

        // Raise each affected project's counter above its on-disk maximum so
        // the replacement keys we mint cannot collide with an existing one.
        foreach (var projectId in collisions.Select(c => c.Key.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var project = _projectRegistry.FindById(projectId);
            if (project == null) continue;
            var floor = HighestExistingKeyNumber(projectId, project.ShortCode) + 1;
            _projectRegistry.EnsureTaskKeyFloor(projectId, floor);
        }

        var rekeyed = 0;
        foreach (var ((projectId, oldKey), members) in collisions)
        {
            var project = _projectRegistry.FindById(projectId);
            if (project == null) continue;

            // Deterministic keeper: the task that has held the key longest.
            var ordered = members
                .OrderBy(m => m.CreatedAt)
                .ThenBy(m => m.Id, StringComparer.Ordinal)
                .ToList();
            var keeper = ordered[0];

            for (var i = 1; i < ordered.Count; i++)
            {
                var seq = _projectRegistry.IssueNextTaskKey(projectId);
                var newKey = $"{project.ShortCode}-{seq}";
                TaskJsonFile.UpdateField(ordered[i].FolderPath, "key", newKey, _logger);
                rekeyed++;
                _logger.LogInformation(
                    "task-key-dedup project={ProjectId} oldKey={OldKey} newKey={NewKey} jobId={JobId} keeper={KeeperId}",
                    projectId, oldKey, newKey, ordered[i].Id, keeper.Id);
            }
        }

        if (rekeyed > 0) _scanner.InvalidateCache();
        return rekeyed;
    }

    private static string ToSlug(string text)
    {
        // Transliterate German umlauts to ASCII equivalents
        var s = text
            .Replace("ä", "ae").Replace("Ä", "ae")
            .Replace("ö", "oe").Replace("Ö", "oe")
            .Replace("ü", "ue").Replace("Ü", "ue")
            .Replace("ß", "ss");
        // Decompose other accented characters and strip combining marks
        s = string.Concat(
            s.Normalize(NormalizationForm.FormD)
             .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark));
        s = s.ToLowerInvariant().Replace(' ', '-');
        return System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9\-]", "");
    }
}

public sealed record IntegrationRecordWriteResult(bool Succeeded, bool Appended);

public sealed record CommitSupersessionWriteResult(bool Succeeded, int MarkedCommits);

public sealed record CommitMetadataBackfillResult(
    int RepairedTasks,
    int RepairedCommits,
    int UnresolvedTasks);
