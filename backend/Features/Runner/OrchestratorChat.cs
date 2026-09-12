using System.Text;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Orchestrator;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

/// <summary>
/// Context-keyed machine-local Orchestrator Chat history. The monolith profile
/// uses this store directly; a configured standalone Task Server replaces it
/// as transcript authority.
///
/// <para>
/// Production turns flow through <see cref="IOrchestratorChatPersistence"/>,
/// which selects either this in-process store or the remote Task Server.
/// </para>
/// </summary>
public class OrchestratorChat
{
    private readonly ILogger<OrchestratorChat> _logger;
    private readonly ProjectChatStore? _projectStore;
    private readonly ProjectChatIndex? _projectIndex;
    private readonly TaskScannerService? _scanner;

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OrchestratorChat(
        ILogger<OrchestratorChat> logger,
        ProjectChatStore? projectStore = null,
        ProjectChatIndex? projectIndex = null,
        TaskScannerService? scanner = null)
    {
        _logger = logger;
        _projectStore = projectStore;
        _projectIndex = projectIndex;
        _scanner = scanner;
    }

    public bool Append(string watchPath, OrchestratorChatTurn turn) => Append(watchPath, turn, context: null);

    /// <summary>
    /// Append one turn to the transcript for a specific navigation context
    /// (MC-2, Concept §4). A <see cref="OrchestratorContextKey.TaskKind"/>
    /// context is persisted to its own per-task file so a task page and the
    /// board no longer share one history; <c>project</c> / <c>global</c> /
    /// <c>null</c> resolve to the canonical per-project
    /// <c>orchestrator-chat.jsonl</c>, so existing project chats are
    /// unaffected. Only project-scoped turns mirror into the project chat
    /// tree; task threads stay out of the project-level FTS index.
    /// </summary>
    public bool Append(string watchPath, OrchestratorChatTurn turn, OrchestratorContextKey? context)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return false;
        try
        {
            var path = ResolveContextPath(watchPath, context);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            // Strip inline-base64 / mime before persisting: the jsonl log is
            // the long-lived audit trail and embedding multi-MB base64 blobs
            // in it makes every subsequent read O(N) over the image bytes.
            // The frontend can still render the picture via RelativePath
            // through the existing GET attachments route.
            var persisted = StripInlineBytes(turn);
            var line = JsonSerializer.Serialize(persisted, WriteOpts) + Environment.NewLine;
            File.AppendAllText(path, line, Encoding.UTF8);

            // Slice D mirror: also write the per-turn markdown file so the
            // new file-tree + FTS index stay current as turns are appended.
            // Best-effort; legacy JSONL remains the fallback if this fails.
            // Only the project-scoped thread mirrors — the project chat tree
            // is per-project, so folding an isolated task or Dossier turn
            // into it would cross-contaminate the board's history.
            if (!IsIsolatedContext(context))
                MirrorToProjectChat(watchPath, persisted);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append orchestrator chat turn under {WatchPath}", watchPath);
            return false;
        }
    }

    /// <summary>
    /// Drop the in-flight inline base64 / mime fields so the on-disk audit
    /// log stays small. Returns the input unchanged when no attachment
    /// carries inline bytes (the common case before the multimodal path).
    /// </summary>
    internal static OrchestratorChatTurn StripInlineBytes(OrchestratorChatTurn turn)
    {
        if (turn.Attachments == null || turn.Attachments.Count == 0) return turn;
        var hasInline = turn.Attachments.Any(a => !string.IsNullOrEmpty(a.InlineBase64));
        if (!hasInline) return turn;
        var stripped = turn.Attachments
            .Select(a => new OrchestratorChatAttachment { Alt = a.Alt, RelativePath = a.RelativePath })
            .ToList();
        return turn with { Attachments = stripped };
    }

    private void MirrorToProjectChat(string watchPath, OrchestratorChatTurn turn)
    {
        if (_projectStore == null || _scanner == null) return;
        try
        {
            var entry = _scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Path, watchPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.RootPath, watchPath, StringComparison.OrdinalIgnoreCase));
            var projectFolder = entry?.Path;
            if (string.IsNullOrWhiteSpace(projectFolder)) return;

            var author = turn.Role switch
            {
                "user" => ProjectChatTurnAuthors.User,
                "orchestrator" => ProjectChatTurnAuthors.Orchestrator,
                _ => ProjectChatTurnAuthors.Orchestrator
            };
            var body = turn.Text ?? "";
            if (!string.IsNullOrWhiteSpace(turn.ErrorMessage))
            {
                body = string.IsNullOrEmpty(body)
                    ? "_error:_ " + turn.ErrorMessage
                    : body + "\n\n_error:_ " + turn.ErrorMessage;
            }

            var pTurn = new ProjectChatTurn
            {
                TurnId = turn.Id,
                Author = author,
                Kind = ProjectChatTurnKinds.Turn,
                Ts = DateTime.SpecifyKind(turn.Ts, DateTimeKind.Utc),
                Body = body
            };
            var written = _projectStore.Write(projectFolder, pTurn);
            _projectIndex?.Upsert(projectFolder, pTurn, written);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mirror to project chat tree failed for {WatchPath}", watchPath);
        }
    }

    public List<OrchestratorChatTurn> Read(string watchPath) => Read(watchPath, context: null);

    /// <summary>
    /// Read the transcript for a specific navigation context (MC-2). Returns
    /// the per-task thread for a task context and the canonical per-project
    /// thread otherwise. An absent file is an empty (not failed) transcript.
    /// </summary>
    public List<OrchestratorChatTurn> Read(string watchPath, OrchestratorContextKey? context)
    {
        var result = new List<OrchestratorChatTurn>();
        if (string.IsNullOrWhiteSpace(watchPath)) return result;
        var path = ResolveContextPath(watchPath, context);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var turn = JsonSerializer.Deserialize<OrchestratorChatTurn>(line, ReadOpts);
                if (turn != null) result.Add(turn);
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "OrchestratorChat: Best-effort: skip torn / malformed lines.");
                // Best-effort: skip torn / malformed lines.
            }
        }
        return result;
    }

    internal bool EnsureContext(string watchPath, OrchestratorContextKey? context)
    {
        if (!IsIsolatedContext(context)) return true;
        try
        {
            var path = ResolveContextPath(watchPath, context);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            using var _ = File.Open(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to materialize local orchestrator context under {WatchPath}",
                watchPath);
            return false;
        }
    }

    private static string ResolvePath(string watchPath) =>
        Path.Combine(watchPath, ".orchestrator", "orchestrator-chat.jsonl");

    /// <summary>
    /// True for a context that owns its own isolated transcript file (task or
    /// workbench/Dossier), as opposed to the shared per-project thread.
    /// </summary>
    private static bool IsIsolatedContext(OrchestratorContextKey? context) =>
        context != null
        && (context.Kind == OrchestratorContextKey.TaskKind || context.Kind == OrchestratorContextKey.WorkbenchKind);

    /// <summary>
    /// Resolve the on-disk transcript file for a navigation context. Task and
    /// workbench (Dossier) contexts each get a dedicated file under
    /// <c>.orchestrator/context-chats/</c> keyed by the reversible
    /// <see cref="OrchestratorContextKey.Encode"/> folder-safe form; every
    /// other context (including <c>null</c>) resolves to the legacy
    /// per-project <c>orchestrator-chat.jsonl</c> so the board thread and
    /// older callers are byte-for-byte unchanged.
    /// </summary>
    internal static string ResolveContextPath(string watchPath, OrchestratorContextKey? context)
    {
        if (!IsIsolatedContext(context))
            return ResolvePath(watchPath);
        return Path.Combine(watchPath, ".orchestrator", "context-chats", context!.Encode() + ".jsonl");
    }

    /// <summary>
    /// Persist a chat-composer image under
    /// <c>&lt;watchPath&gt;/.orchestrator/chat-attachments/&lt;id&gt;.&lt;ext&gt;</c>.
    /// Mirrors the per-job <c>attachments/</c> conventions (10 MB cap,
    /// PNG / JPG / GIF / WEBP only) so the user's chat drafts and task
    /// drafts behave the same way.
    /// </summary>
    public (string? FileName, string? RelativePath, string? Error) SaveAttachment(
        string watchPath,
        byte[] content,
        string? originalFileName,
        string? contentType)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return (null, null, "Missing watch path");
        if (content.Length == 0) return (null, null, "Empty file");
        if (content.Length > 10 * 1024 * 1024) return (null, null, "File too large (max 10 MB)");

        var ext = ResolveImageExtension(originalFileName, contentType);
        if (ext == null) return (null, null, "Unsupported file type - only png, jpg, gif, webp allowed");

        var dir = Path.Combine(watchPath, ".orchestrator", "chat-attachments");
        Directory.CreateDirectory(dir);

        string fileName;
        string fullPath;
        do
        {
            fileName = $"{Guid.NewGuid():N}"[..8] + ext;
            fullPath = Path.Combine(dir, fileName);
        } while (File.Exists(fullPath));

        File.WriteAllBytes(fullPath, content);
        return (fileName, $"chat-attachments/{fileName}", null);
    }

    /// <summary>
    /// Resolve a previously-saved chat attachment for serving back to the
    /// frontend. Returns null if the file is gone or escapes the chat
    /// attachments directory.
    /// </summary>
    public (string? Path, string? ContentType) ResolveAttachment(string watchPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(watchPath) || string.IsNullOrWhiteSpace(fileName)) return (null, null);
        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\')) return (null, null);

        var dir = Path.Combine(watchPath, ".orchestrator", "chat-attachments");
        var full = Path.Combine(dir, fileName);
        if (!File.Exists(full)) return (null, null);
        var ct = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
        return (full, ct);
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
}

/// <summary>
/// One turn in the per-project orchestrator chat. <see cref="Role"/> is
/// "user" for the human's messages and "orchestrator" for the model's
/// replies; <see cref="ErrorMessage"/> is set on a failed turn so the
/// frontend can surface what went wrong without losing the user's text.
/// </summary>
public record OrchestratorChatTurn
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime Ts { get; init; } = DateTime.UtcNow;
    public string Role { get; init; } = OrchestratorChatRoles.User;
    public string Text { get; init; } = "";
    public string? Model { get; init; }
    public string? CliType { get; init; }
    public string? ConfiguredModel { get; init; }
    public string? QuotaFallbackReason { get; init; }
    public OrchestratorTokenUsage? TokenUsage { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>
    /// Persisted transparency receipt for the context composed into this
    /// reply's request. Null on user turns and legacy replies.
    /// </summary>
    public OrchestratorContextReceipt? ContextReceipt { get; init; }

    /// <summary>
    /// Raw / technical error detail preserved alongside a friendly
    /// <see cref="ErrorMessage"/>. The chat bubble shows the friendly
    /// message; the detail is what a developer needs to bisect (the
    /// original .NET exception text or CLI stderr). Set whenever
    /// <see cref="ErrorMessage"/> was produced by
    /// <see cref="OrchestratorChatErrorTranslator"/>; null on success
    /// turns and on legacy entries written before the translator existed.
    /// </summary>
    public string? ErrorDetail { get; init; }

    public List<OrchestratorChatAttachment>? Attachments { get; init; }
}

public sealed record OrchestratorContextReceipt(
    string Scope,
    string ContextKey,
    string? TaskKey,
    IReadOnlyList<string> IncludedBlocks,
    DateTime CapturedAt,
    string? ReceiptId = null,
    string? UserTurnId = null,
    OrchestratorContextBudgetReceipt? Budget = null,
    IReadOnlyList<OrchestratorContextSourceReceipt>? Sources = null);

internal sealed record OrchestratorChatPromptComposition(
    string Prompt,
    OrchestratorContextReceipt ContextReceipt);

/// <summary>
/// Reference to a file attachment that was part of the user's message.
/// Today the only carrier is an image stored in the watch path's
/// <c>.orchestrator/attachments/</c> folder; <see cref="RelativePath"/> is
/// the path the frontend resolves through the existing image-serving route.
///
/// <para>
/// <see cref="InlineBase64"/> + <see cref="MimeType"/> are the multimodal
/// fast path: when the frontend ships the raw image bytes, the backend
/// hands them straight to the CLI as a Claude image content block via the
/// <c>--input-format stream-json</c> envelope, so the model sees the image
/// in the same message it sees the text - no Read tool call required.
/// These fields are stripped before the turn is appended to the per-project
/// chat jsonl so the audit log stays text-only and small; the persisted
/// <see cref="RelativePath"/> (when present) remains the long-lived
/// reference.
/// </para>
/// </summary>
public record OrchestratorChatAttachment
{
    public string Alt { get; init; } = "";
    public string RelativePath { get; init; } = "";

    /// <summary>
    /// Base64-encoded image bytes for the multimodal fast path. Set by the
    /// frontend when an image is pasted into the composer. Not persisted.
    /// </summary>
    public string? InlineBase64 { get; init; }

    /// <summary>
    /// MIME type of <see cref="InlineBase64"/> (e.g. <c>image/png</c>). Not
    /// persisted; only meaningful in flight.
    /// </summary>
    public string? MimeType { get; init; }
}

public static class OrchestratorChatRoles
{
    public const string User = "user";
    public const string Orchestrator = "orchestrator";
}

public sealed record SendOrchestratorChatRequest(
    string Text,
    List<OrchestratorChatAttachment>? Attachments,
    ChatNavigationContext? NavigationContext = null,
    string? Model = null,
    string? ThinkingLevel = null,
    string? SelectionSource = null,
    OrchestratorContextEnvelope? ContextEnvelope = null);

/// <summary>
/// Structured navigation context the frontend ships with every project-chat
/// POST. The chat agent reads this to interpret context-dependent questions
/// ("what is the current task?", "explain this") against the page the
/// operator is actually looking at. Every field is optional from the
/// agent's perspective: a missing or null <see cref="CurrentTaskId"/> means
/// the operator is not on a task page and the agent must not invent one.
///
/// <para>
/// Background: before this field existed the agent answered context
/// questions in vacuum and hallucinated freely (see the 2026-05-09
/// "Conversation, Foul Conversation" incident). Carrying the navigation
/// state into the prompt closes that loop deterministically.
/// </para>
/// </summary>
public sealed record ChatNavigationContext(
    string? CurrentPage = null,
    string? CurrentTaskId = null,
    string? CurrentTaskKey = null,
    string? CurrentTaskTitle = null,
    string? CurrentTaskState = null,
    string? CurrentLaneFilter = null,
    string? ViewportTimestamp = null,
    string? ObservedSurface = null,
    string? AffectedComponent = null,
    string? PageRef = null,
    string? PageTitle = null,
    string? PageType = null,
    string? PageExcerpt = null);

/// <summary>
/// Service that turns a user message into an orchestrator reply with the
/// operator-selected Codex model and reasoning level, resolves any
/// quota-driven equivalent-provider route immediately before execution, then
/// persists both turns to the Task Server-owned context transcript.
///
/// <para>
/// The configured chat route remains Codex/GPT. Quota admission may execute
/// the turn on an equivalent Claude tier without rewriting that configuration;
/// both routes and the switch reason remain visible on the persisted turn.
/// </para>
/// </summary>
public class OrchestratorChatService
{
    private readonly OrchestratorChat _chat;
    private readonly OrchestratorRunner _runner;
    private readonly GlobalOrchestratorBootstrap _bootstrap;
    private readonly TaskScannerService _scanner;
    private readonly ILogger<OrchestratorChatService> _logger;
    private readonly ClientIdentityStore? _identityStore;
    private readonly OrchestratorContextDigestService? _contextDigests;
    private readonly ComponentRoutingService? _componentRouting;
    private readonly ProjectSettingsService? _projectSettings;
    private readonly ProjectRegistry? _projects;
    private readonly RemoteChatWorkBroker? _remoteWork;
    private readonly GitService? _git;
    private readonly OrchestratorTaskPromptContextComposer? _taskPromptContext;
    private readonly OrchestratorWorkbenchPromptContextComposer? _workbenchPromptContext;
    private readonly IOrchestratorChatPersistence? _persistence;
    private readonly StartupExecutionAdmission? _executionAdmission;
    private readonly QuotaAdmissionService? _quotaAdmission;
    private readonly QuotaAdmissionRecorder? _quotaAdmissionRecorder;

    /// <summary>
    /// Serializes concurrent <see cref="SendAsync"/> calls so multiple Codex
    /// one-shots do not contend for the same orchestrator working directory
    /// and transcript writes. The user still sees the pending turn while it
    /// waits.
    ///
    /// <para>
    /// This is a pragmatic correctness guard, not a parallelism win:
    /// requests across all projects serialize on this gate. Per-context
    /// concurrency is outside this footer-selection change.
    /// </para>
    /// </summary>
    private static readonly SemaphoreSlim SessionGate = new(1, 1);

    public OrchestratorChatService(
        OrchestratorChat chat,
        OrchestratorRunner runner,
        GlobalOrchestratorSessionStore sessionStore,
        GlobalOrchestratorBootstrap bootstrap,
        TaskScannerService scanner,
        IConfiguration config,
        ILogger<OrchestratorChatService> logger,
        ClientIdentityStore? identityStore = null,
        OrchestratorContextDigestService? contextDigests = null,
        ComponentRoutingService? componentRouting = null,
        ProjectSettingsService? projectSettings = null,
        ProjectRegistry? projects = null,
        RemoteChatWorkBroker? remoteWork = null,
        GitService? git = null,
        OrchestratorTaskPromptContextComposer? taskPromptContext = null,
        IOrchestratorChatPersistence? persistence = null,
        StartupExecutionAdmission? executionAdmission = null,
        QuotaAdmissionService? quotaAdmission = null,
        QuotaAdmissionRecorder? quotaAdmissionRecorder = null,
        OrchestratorWorkbenchPromptContextComposer? workbenchPromptContext = null)
    {
        _chat = chat;
        _runner = runner;
        _bootstrap = bootstrap;
        _scanner = scanner;
        _logger = logger;
        _identityStore = identityStore;
        _contextDigests = contextDigests;
        _componentRouting = componentRouting;
        _projectSettings = projectSettings;
        _projects = projects;
        _remoteWork = remoteWork;
        _git = git;
        _taskPromptContext = taskPromptContext;
        _persistence = persistence;
        _executionAdmission = executionAdmission;
        _quotaAdmission = quotaAdmission;
        _quotaAdmissionRecorder = quotaAdmissionRecorder;
        _workbenchPromptContext = workbenchPromptContext;
    }

    public List<OrchestratorChatTurn> Read(string watchPath) => _chat.Read(watchPath);

    /// <summary>Read the transcript for a specific navigation context (MC-2).</summary>
    public List<OrchestratorChatTurn> Read(string watchPath, OrchestratorContextKey? context)
        => _chat.Read(watchPath, context);

    public Task<IReadOnlyList<OrchestratorChatTurn>> ReadAsync(
        string projectName,
        string watchPath,
        OrchestratorContextKey? context,
        int limit,
        CancellationToken ct)
        => _persistence?.ReadAsync(projectName, watchPath, context, limit, ct)
           ?? Task.FromResult<IReadOnlyList<OrchestratorChatTurn>>(
               _chat.Read(watchPath, context).TakeLast(Math.Clamp(limit, 1, 1000)).ToArray());

    public Task<OrchestratorChatTurn> SendAsync(
        string projectName,
        string watchPath,
        SendOrchestratorChatRequest req,
        CancellationToken ct)
        => SendAsync(projectName, watchPath, req, clientId: null, context: null, ct);

    public Task<OrchestratorChatTurn> SendAsync(
        string projectName,
        string watchPath,
        SendOrchestratorChatRequest req,
        string? clientId,
        CancellationToken ct)
        => SendAsync(projectName, watchPath, req, clientId, context: null, ct);

    /// <summary>
    /// Send a user message and persist both turns through the selected context
    /// store. <paramref name="context"/> selects the transcript and the ORCH-1
    /// read digest injected into this stateless GPT turn. Passing <c>null</c>
    /// resolves the canonical
    /// <c>project:&lt;projectName&gt;</c> context.
    /// </summary>
    public async Task<OrchestratorChatTurn> SendAsync(
        string projectName,
        string watchPath,
        SendOrchestratorChatRequest req,
        string? clientId,
        OrchestratorContextKey? context,
        CancellationToken ct)
    {
        _executionAdmission?.Demand(ExecutionAdmissionPath.Chat);
        var userTurn = new OrchestratorChatTurn
        {
            Role = OrchestratorChatRoles.User,
            Text = req.Text,
            Attachments = req.Attachments
        };
        // Boundary validation runs before any transcript mutation. The full
        // resolver repeats this snapshot under the execution gate and also
        // validates repository paths before the user turn is persisted.
        _ = OrchestratorContextEnvelopePolicy.Snapshot(
            projectName, context, req, userTurn.Ts);

        // Serialize on the singleton-session gate. Two concurrent resumes
        // race on the session id, the on-disk usage record, and Claude's
        // own session memory; the gate is the simplest correctness fix
        // until per-conversation sessions land. The wait counter tells us
        // when chats are actually queueing in the wild.
        var queuedAt = DateTime.UtcNow;
        await SessionGate.WaitAsync(ct);
        var queueWaitMs = (DateTime.UtcNow - queuedAt).TotalMilliseconds;
        if (queueWaitMs > 250)
        {
            _logger.LogInformation(
                "[orchestrator-chat] queued {WaitMs:F0}ms behind another in-flight chat for project {Project}",
                queueWaitMs, projectName);
        }
        try
        {
            var promptComposition = await BuildPromptAsync(
                projectName, watchPath, req, clientId, context, userTurn.Id, ct).ConfigureAwait(false);
            var prompt = promptComposition.Prompt;
            var contextReceipt = promptComposition.ContextReceipt;
            await AppendTurnAsync(projectName, watchPath, context, userTurn, ct).ConfigureAwait(false);
            var requestedModel = string.IsNullOrWhiteSpace(req.Model)
                ? ModelMetadataRegistry.DefaultForCli(CliTypes.Codex) ?? ModelIds.Gpt55
                : req.Model.Trim();
            if (!requestedModel.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Orchestrator composer accepts GPT models only.");
            var thinkingLevel = string.IsNullOrWhiteSpace(req.ThinkingLevel)
                ? ModelMetadataRegistry.DefaultThinkingLevelForCli(CliTypes.Codex, requestedModel)
                : req.ThinkingLevel.Trim();
            var workingDirectory = ResolveWorkingDirectory(projectName, watchPath);
            OrchestratorDecisionResult result;
            try
            {
                var fullPrompt = prompt;
                var remoteRoute = ResolveRemoteRoute(projectName, watchPath);
                if (remoteRoute != null && _remoteWork != null)
                {
                    var quotaPlan = _quotaAdmission?.Plan(new QuotaAdmissionRequest(
                        CliTypes.Codex,
                        requestedModel,
                        thinkingLevel,
                        projectName,
                        OccupiedSlots: 0,
                        ExecutionPath: "remote-project-chat"));
                    if (quotaPlan is not null && !quotaPlan.ShouldLaunch)
                    {
                        _quotaAdmissionRecorder?.EmitProjectAdmissionDecision(
                            watchPath, projectName, quotaPlan, "remote-project-chat");
                        result = new OrchestratorDecisionResult(
                            false,
                            "",
                            requestedModel,
                            null,
                            CapturedSessionId: null,
                            quotaPlan.Reason)
                        {
                            CliType = CliTypes.Codex,
                            ConfiguredModel = requestedModel,
                        };
                    }
                    else
                    {
                        var effectiveCli = quotaPlan?.CliType ?? CliTypes.Codex;
                        var effectiveModel = quotaPlan?.Model ?? requestedModel;
                        var effectiveThinking = quotaPlan?.ThinkingLevel ?? thinkingLevel;
                        if (quotaPlan?.IsFallback == true)
                        {
                            _quotaAdmissionRecorder?.EmitProjectAdmissionDecision(
                                watchPath, projectName, quotaPlan, "remote-project-chat");
                        }

                        var remote = await _remoteWork.EnqueueTurnAsync(
                            remoteRoute,
                            fullPrompt,
                            effectiveModel,
                            effectiveThinking,
                            ct,
                            effectiveCli,
                            CliTypes.Codex,
                            requestedModel,
                            quotaPlan?.IsFallback == true ? quotaPlan.Reason : null).ConfigureAwait(false);
                        result = new OrchestratorDecisionResult(
                            remote.Success,
                            remote.ReplyText,
                            string.IsNullOrWhiteSpace(remote.Model) ? effectiveModel : remote.Model,
                            remote.TokenUsage,
                            CapturedSessionId: null,
                            remote.ErrorMessage)
                        {
                            CliType = remote.CliType ?? effectiveCli,
                            ConfiguredModel = remote.ConfiguredModel ?? requestedModel,
                            QuotaFallback = !string.IsNullOrWhiteSpace(remote.QuotaFallbackReason),
                            QuotaFallbackReason = remote.QuotaFallbackReason,
                        };
                    }
                }
                else
                {
                    result = await _runner.DecideCodexAsync(
                        fullPrompt,
                        requestedModel,
                        thinkingLevel,
                        workingDirectory,
                        ct,
                        projectName,
                        watchPath);
                }
            }
            catch (Exception ex)
            {
                // Full stacktrace at error level so the backend log is
                // self-sufficient for bisecting the 2026-05-24 pipe-closed
                // incident class. The chat bubble itself shows the friendly
                // translation produced below; the raw .NET message lands in
                // ErrorDetail (audit log + future UI expander) but never as
                // the primary bubble text.
                _logger.LogError(
                    ex,
                    "Orchestrator chat send threw for project {Project} ({ExceptionType}): {Raw}",
                    projectName, ex.GetType().Name, ex.Message);
                var translation = OrchestratorChatErrorTranslator.Translate(ex.Message, CliTypes.Codex);
                var failure = new OrchestratorChatTurn
                {
                    Role = OrchestratorChatRoles.Orchestrator,
                    Text = "",
                    ErrorMessage = translation.FriendlyMessage,
                    ErrorDetail = translation.RawDetail,
                    ContextReceipt = contextReceipt
                };
                await AppendTurnAsync(projectName, watchPath, context, failure, ct).ConfigureAwait(false);
                return failure;
            }

            if (!result.Success)
            {
                _logger.LogError(
                    "Orchestrator chat call failed for project {Project} (model={Model}): {Raw}",
                    projectName, result.Model, result.ErrorMessage ?? "(no error message)");
                var translation = OrchestratorChatErrorTranslator.Translate(
                    result.ErrorMessage,
                    result.CliType ?? CliTypes.Codex);
                var failure = new OrchestratorChatTurn
                {
                    Role = OrchestratorChatRoles.Orchestrator,
                    Text = result.ReplyText ?? "",
                    Model = result.Model,
                    CliType = result.CliType,
                    ConfiguredModel = result.ConfiguredModel,
                    QuotaFallbackReason = result.QuotaFallbackReason,
                    TokenUsage = result.TokenUsage,
                    ErrorMessage = translation.FriendlyMessage,
                    ErrorDetail = translation.RawDetail,
                    ContextReceipt = contextReceipt
                };
                await AppendTurnAsync(projectName, watchPath, context, failure, ct).ConfigureAwait(false);
                return failure;
            }

            var reply = new OrchestratorChatTurn
            {
                Role = OrchestratorChatRoles.Orchestrator,
                Text = result.ReplyText,
                Model = result.Model,
                CliType = result.CliType,
                ConfiguredModel = result.ConfiguredModel,
                QuotaFallbackReason = result.QuotaFallbackReason,
                TokenUsage = result.TokenUsage,
                ContextReceipt = contextReceipt
            };
            await AppendTurnAsync(projectName, watchPath, context, reply, ct).ConfigureAwait(false);
            return reply;
        }
        finally
        {
            SessionGate.Release();
        }
    }

    private Task AppendTurnAsync(
        string projectName,
        string watchPath,
        OrchestratorContextKey? context,
        OrchestratorChatTurn turn,
        CancellationToken ct)
    {
        if (_persistence is not null)
            return _persistence.AppendAsync(projectName, watchPath, context, turn, ct);
        if (!_chat.Append(watchPath, turn, context))
            throw new IOException("The orchestrator chat turn could not be persisted.");
        return Task.CompletedTask;
    }

    private async Task<OrchestratorChatPromptComposition> BuildPromptAsync(
        string projectName,
        string watchPath,
        SendOrchestratorChatRequest req,
        string? clientId,
        OrchestratorContextKey? context,
        string userTurnId,
        CancellationToken ct)
    {
        var capturedAt = DateTime.UtcNow;
        var envelope = OrchestratorContextEnvelopePolicy.Snapshot(
            projectName, context, req, capturedAt);
        var automatic = new List<ResolvedContextBlock>();
        var explicitBlocks = new List<ResolvedContextBlock>();

        await ResolveAutomaticContextAsync(
            automatic, projectName, watchPath, req, clientId, context, envelope, ct)
            .ConfigureAwait(false);
        ResolveExplicitContext(explicitBlocks, projectName, watchPath, envelope);
        if (req.Attachments is { Count: > 0 })
        {
            var attachmentText = string.Join('\n', req.Attachments.Select(item =>
                $"- {item.Alt} ({item.RelativePath})"));
            explicitBlocks.Add(ResolvedContextBlock.Included(
                $"attachments:{userTurnId}",
                "image-attachments",
                attachmentText,
                revision: null,
                freshness: "submitted",
                isExplicit: true));
        }

        DeduplicateContext(automatic, explicitBlocks);

        var priorTurns = await ReadAsync(projectName, watchPath, context, 12, ct).ConfigureAwait(false);
        var continuity = RenderContinuity(priorTurns.Where(turn => turn.Id != userTurnId).TakeLast(8));
        if (!string.IsNullOrWhiteSpace(continuity))
        {
            automatic.Add(ResolvedContextBlock.Included(
                $"history:{envelope.Scope.ContextKey}",
                "recent-conversation",
                continuity,
                priorTurns.LastOrDefault(turn => turn.Id != userTurnId)?.Id,
                "current",
                isExplicit: false));
        }

        var allocated = AllocateContext(envelope.Budget, automatic, explicitBlocks);
        var receipts = allocated.Select(item => item.Receipt).ToArray();
        var includedBlocks = receipts
            .Where(item => item.Status is "included" or "excerpted")
            .Select(item => item.SourceId)
            .ToArray();
        var estimatedTokens = receipts.Sum(item => item.EstimatedTokens);
        var receipt = new OrchestratorContextReceipt(
            envelope.Scope.Kind,
            envelope.Scope.ContextKey,
            envelope.Scope.TaskKey,
            includedBlocks,
            envelope.CapturedAt,
            ReceiptId: "rcp_" + Guid.NewGuid().ToString("N"),
            UserTurnId: userTurnId,
            Budget: new OrchestratorContextBudgetReceipt(
                envelope.Budget.AutomaticSoftCapTokens,
                envelope.Budget.AutomaticHardCapTokens,
                envelope.Budget.TotalHardCapTokens,
                estimatedTokens),
            Sources: receipts);

        var sb = new StringBuilder();
        AppendScopedPreamble(sb, projectName, envelope);
        AppendContextLedger(sb, receipt);
        AppendResolvedBlocks(
            sb,
            "AUTOMATIC EVIDENCE",
            allocated.Where(item => !item.IsExplicit && item.Kind != "recent-conversation"));
        AppendResolvedBlocks(sb, "EXPLICIT ATTACHMENTS", allocated.Where(item => item.IsExplicit));

        var history = allocated.FirstOrDefault(item => item.Kind == "recent-conversation");
        if (history is not null && history.IncludedContent.Length > 0)
        {
            sb.AppendLine("=== CONVERSATION CONTINUITY ===");
            sb.AppendLine(history.IncludedContent);
            sb.AppendLine();
        }

        sb.AppendLine("=== USER MESSAGE ===");
        sb.Append(req.Text.TrimEnd());

        _logger.LogInformation(
            "orchestrator_chat_prompt_composed contextKey={ContextKey} scope={Scope} taskKey={TaskKey} sources={Sources} estimatedTokens={EstimatedTokens}",
            receipt.ContextKey,
            receipt.Scope,
            receipt.TaskKey,
            string.Join(',', receipt.IncludedBlocks),
            estimatedTokens);
        return new OrchestratorChatPromptComposition(sb.ToString(), receipt);
    }

    private async Task ResolveAutomaticContextAsync(
        ICollection<ResolvedContextBlock> blocks,
        string projectName,
        string watchPath,
        SendOrchestratorChatRequest request,
        string? clientId,
        OrchestratorContextKey? context,
        OrchestratorContextEnvelope envelope,
        CancellationToken ct)
    {
        // A Dossier (workbench) turn is deliberately isolated: no project
        // digest, board pulse, or task bundle carries into it. The Dossier's
        // own descriptor + entrypoint excerpt (below) is the entire implicit
        // context, so this whole automatic-project-state section is skipped.
        var isWorkbenchScope = envelope.Scope.Kind == "workbench";
        var digestAdded = isWorkbenchScope;
        if (!isWorkbenchScope && _contextDigests is not null)
        {
            try
            {
                var effectiveContext = context;
                if (effectiveContext is null)
                    OrchestratorContextKey.TryParse(envelope.Scope.ContextKey, out effectiveContext);
                if (effectiveContext is not null)
                {
                    var digest = await _contextDigests.BuildAsync(effectiveContext, ct: ct).ConfigureAwait(false);
                    blocks.Add(ResolvedContextBlock.Included(
                        $"digest:{effectiveContext.Value}",
                        "project-base",
                        digest.Digest,
                        digest.CapturedAt.ToString("O"),
                        "current",
                        isExplicit: false));
                    digestAdded = true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                blocks.Add(ResolvedContextBlock.Unavailable(
                    $"digest:{envelope.Scope.ContextKey}",
                    "project-base",
                    "unavailable",
                    "The project context digest could not be resolved."));
                _logger.LogWarning(
                    exception,
                    "orchestrator_context_digest_injection_failed contextKey={ContextKey} project={Project} fallback=project-state-snapshot",
                    envelope.Scope.ContextKey,
                    projectName);
            }
        }
        if (!digestAdded)
        {
            try
            {
                var snapshot = new StringBuilder();
                var tasks = _scanner.ScanAllAutomationJobs()
                    .Where(item => string.Equals(
                        item.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                AppendProjectStateSnapshot(snapshot, projectName, tasks);
                blocks.Add(ResolvedContextBlock.Included(
                    $"project:{projectName}/state",
                    "project-base",
                    snapshot.ToString(),
                    revision: null,
                    freshness: "current",
                    isExplicit: false));
            }
            catch (Exception exception)
            {
                SilentCatch.Note(exception, "OrchestratorChat: project state snapshot unavailable.");
                blocks.Add(ResolvedContextBlock.Unavailable(
                    $"project:{projectName}/state",
                    "project-base",
                    "unavailable",
                    "The project state snapshot could not be resolved."));
            }
        }

        var preferences = new StringBuilder();
        AppendCurrentUserPreferences(preferences, clientId, _identityStore);
        blocks.Add(ResolvedContextBlock.Included(
            $"client:{clientId ?? "anonymous"}/preferences",
            "operator-preferences",
            preferences.ToString(),
            revision: null,
            freshness: "current",
            isExplicit: false));

        var navigation = new StringBuilder();
        AppendNavigationContext(
            navigation,
            request.NavigationContext is null
                ? null
                : request.NavigationContext with { PageExcerpt = null });
        blocks.Add(ResolvedContextBlock.Included(
            $"surface:{envelope.Scope.ContextKey}",
            "active-surface",
            navigation.ToString(),
            envelope.ActiveSurface?.Revision,
            "captured-at-submit",
            isExplicit: false));

        if (envelope.ActiveSurface is { Kind: "page" or "workbench", Reference: not null } surface)
            blocks.Add(ResolveRepositoryText(
                projectName, watchPath, surface.Reference, surface.Kind, isExplicit: false));

        try
        {
            var taskPromptContext = _taskPromptContext?.Compose(
                projectName, watchPath, request.NavigationContext, context);
            if (taskPromptContext is not null)
            {
                blocks.Add(ResolvedContextBlock.Included(
                    $"task:{projectName}/{taskPromptContext.TaskKey}/bundle",
                    "task-bundle",
                    taskPromptContext.PromptBlock,
                    revision: null,
                    freshness: "current",
                    isExplicit: false));
            }
            else if (envelope.Scope.Kind == "task")
            {
                blocks.Add(ResolvedContextBlock.Unavailable(
                    $"task:{projectName}/{envelope.Scope.TaskKey}/bundle",
                    "task-bundle",
                    "unresolved",
                    "The task bundle could not be resolved."));
            }
        }
        catch (Exception exception)
        {
            blocks.Add(ResolvedContextBlock.Unavailable(
                $"task:{projectName}/{envelope.Scope.TaskKey}/bundle",
                "task-bundle",
                "unavailable",
                "The task bundle could not be resolved."));
            _logger.LogWarning(
                exception,
                "orchestrator_task_prompt_context_lookup_failed contextKey={ContextKey} taskKey={TaskKey} project={Project}",
                envelope.Scope.ContextKey,
                envelope.Scope.TaskKey,
                projectName);
        }

        try
        {
            var workbenchPromptContext = isWorkbenchScope
                ? _workbenchPromptContext?.Compose(projectName, envelope.Scope.WorkbenchKey)
                : null;
            if (workbenchPromptContext is not null)
            {
                blocks.Add(ResolvedContextBlock.Included(
                    $"workbench:{projectName}/{workbenchPromptContext.WorkbenchKey}/bundle",
                    "workbench-bundle",
                    workbenchPromptContext.PromptBlock,
                    revision: null,
                    freshness: "current",
                    isExplicit: false));
            }
            else if (isWorkbenchScope)
            {
                blocks.Add(ResolvedContextBlock.Unavailable(
                    $"workbench:{projectName}/{envelope.Scope.WorkbenchKey}/bundle",
                    "workbench-bundle",
                    "unresolved",
                    "The Dossier bundle could not be resolved."));
            }
        }
        catch (Exception exception)
        {
            blocks.Add(ResolvedContextBlock.Unavailable(
                $"workbench:{projectName}/{envelope.Scope.WorkbenchKey}/bundle",
                "workbench-bundle",
                "unavailable",
                "The Dossier bundle could not be resolved."));
            _logger.LogWarning(
                exception,
                "orchestrator_workbench_prompt_context_lookup_failed contextKey={ContextKey} workbenchKey={WorkbenchKey} project={Project}",
                envelope.Scope.ContextKey,
                envelope.Scope.WorkbenchKey,
                projectName);
        }

        if (!isWorkbenchScope && _componentRouting is not null)
        {
            var affectedComponent = request.NavigationContext?.AffectedComponent;
            var routingComponent = string.IsNullOrWhiteSpace(affectedComponent)
                ? request.Text
                : affectedComponent;
            var route = _componentRouting.Resolve(new ComponentRoutingRequest(
                request.NavigationContext?.ObservedSurface ?? request.NavigationContext?.CurrentPage,
                routingComponent,
                projectName));
            blocks.Add(ResolvedContextBlock.Included(
                $"routing:{projectName}/{route.MappingVersion}",
                "component-routing",
                ComponentRoutingService.RenderCompact(route),
                route.MappingVersion?.ToString(),
                "current",
                isExplicit: false));
        }
    }

    private void ResolveExplicitContext(
        ICollection<ResolvedContextBlock> blocks,
        string projectName,
        string watchPath,
        OrchestratorContextEnvelope envelope)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in envelope.ExplicitReferences)
        {
            var canonicalId = $"{reference.Kind}:{reference.Reference}";
            if (!seen.Add(canonicalId)) continue;
            switch (reference.Kind)
            {
                case OrchestratorContextReferenceKinds.Task:
                {
                    var taskKey = NormalizeTaskReference(projectName, reference.Reference);
                    try
                    {
                        OrchestratorContextKey.TryParse(
                            $"task:{projectName}/{taskKey}", out var taskContextKey);
                        var taskContext = _taskPromptContext?.Compose(
                            projectName,
                            watchPath,
                            new ChatNavigationContext(CurrentTaskKey: taskKey),
                            taskContextKey);
                        blocks.Add(taskContext is null
                            ? ResolvedContextBlock.Unavailable(
                                $"task:{projectName}/{taskKey}/bundle",
                                "task-bundle",
                                "unresolved",
                                "The referenced task could not be resolved.",
                                isExplicit: true)
                            : ResolvedContextBlock.Included(
                                $"task:{projectName}/{taskContext.TaskKey}/bundle",
                                "task-bundle",
                                taskContext.PromptBlock,
                                reference.Revision,
                                "current",
                                isExplicit: true));
                    }
                    catch (Exception)
                    {
                        blocks.Add(ResolvedContextBlock.Unavailable(
                            $"task:{projectName}/{taskKey}/bundle",
                            "task-bundle",
                            "unresolved",
                            "The referenced task could not be resolved.",
                            isExplicit: true));
                    }
                    break;
                }
                case OrchestratorContextReferenceKinds.Page:
                    blocks.Add(ResolveRepositoryText(
                        projectName, watchPath, reference.Reference, "page", isExplicit: true));
                    break;
                case OrchestratorContextReferenceKinds.RepositoryFile:
                    blocks.Add(ResolveRepositoryText(
                        projectName, watchPath, reference.Reference, "repository-file", isExplicit: true));
                    break;
                case OrchestratorContextReferenceKinds.Commit:
                    blocks.Add(ResolveCommit(projectName, reference.Reference));
                    break;
            }
        }
    }

    private ResolvedContextBlock ResolveCommit(string projectName, string reference)
    {
        var sha = NormalizeCommitReference(projectName, reference);
        if (_git is null)
            return ResolvedContextBlock.Unavailable(
                $"commit:{projectName}/{sha}",
                "commit",
                "unavailable",
                "Git context is unavailable on this execution host.",
                isExplicit: true);

        var diff = _git.GetProjectCommitDiffResult(projectName, sha, path: null);
        if (!diff.Success)
            return ResolvedContextBlock.Unavailable(
                $"commit:{projectName}/{sha}",
                "commit",
                "unresolved",
                diff.Error ?? "The referenced commit could not be resolved.",
                isExplicit: true);

        var files = _git.GetProjectCommitFiles(projectName, sha);
        var content = new StringBuilder()
            .Append("Commit ").AppendLine(sha)
            .Append("Changed files: ").AppendLine(files.Count.ToString());
        foreach (var file in files.Take(80))
            content.Append("- ").Append(file.Status).Append(' ')
                .Append(file.Path).Append(" (+").Append(file.Added)
                .Append(" / -").Append(file.Removed).AppendLine(")");
        if (files.Count > 80)
            content.Append("- ").Append(files.Count - 80).AppendLine(" more files omitted from the source summary");
        if (!string.IsNullOrWhiteSpace(diff.Diff))
        {
            content.AppendLine().AppendLine("Bounded unified diff:");
            var remaining = Math.Max(0, 64_000 - content.Length);
            content.Append(diff.Diff.AsSpan(0, Math.Min(diff.Diff.Length, remaining)));
        }
        return ResolvedContextBlock.Included(
            $"commit:{projectName}/{sha}",
            "commit",
            content.ToString(),
            sha,
            "current",
            isExplicit: true);
    }

    private ResolvedContextBlock ResolveRepositoryText(
        string projectName,
        string watchPath,
        string reference,
        string kind,
        bool isExplicit)
    {
        var relative = NormalizeRepositoryReference(projectName, reference, kind);
        var root = Path.GetFullPath(ResolveWorkingDirectory(projectName, watchPath));
        if (Path.IsPathRooted(relative)
            || relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
            throw new OrchestratorContextEnvelopeException(
                "context-path-traversal",
                $"Context reference '{reference}' escapes the active project checkout.");
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithinRoot(root, fullPath))
            throw new OrchestratorContextEnvelopeException(
                "context-path-traversal",
                $"Context reference '{reference}' escapes the active project checkout.");
        if (!File.Exists(fullPath))
            return ResolvedContextBlock.Unavailable(
                $"file:{projectName}/{relative.Replace('\\', '/')}",
                kind,
                "unresolved",
                "The referenced repository text does not exist.",
                isExplicit);
        EnsureResolvedPathWithinRoot(root, fullPath, reference);
        var info = new FileInfo(fullPath);
        if (info.Length > 1_000_000)
            return ResolvedContextBlock.Unavailable(
                $"file:{projectName}/{relative.Replace('\\', '/')}",
                kind,
                "oversize",
                "The referenced repository text exceeds the 1 MB resolver limit.",
                isExplicit);
        string content;
        try
        {
            content = File.ReadAllText(fullPath, new UTF8Encoding(false, true));
        }
        catch (DecoderFallbackException)
        {
            return ResolvedContextBlock.Unavailable(
                $"file:{projectName}/{relative.Replace('\\', '/')}",
                kind,
                "blocked",
                "Binary repository files are not eligible for text context.",
                isExplicit);
        }
        return ResolvedContextBlock.Included(
            $"file:{projectName}/{relative.Replace('\\', '/')}",
            kind,
            content,
            info.LastWriteTimeUtc.ToString("O"),
            "current",
            isExplicit);
    }

    private static IReadOnlyList<AllocatedContextBlock> AllocateContext(
        OrchestratorContextBudget budget,
        IReadOnlyList<ResolvedContextBlock> automatic,
        IReadOnlyList<ResolvedContextBlock> explicitBlocks)
    {
        var totalRemaining = budget.TotalHardCapTokens * budget.CharactersPerEstimatedToken;
        var explicitAllocated = new List<AllocatedContextBlock>();
        var unresolvedExplicitContent = explicitBlocks.Count(block => block.Status == "included");
        foreach (var block in explicitBlocks)
        {
            var perSourceRemaining = block.Status == "included" && unresolvedExplicitContent > 0
                ? totalRemaining / unresolvedExplicitContent
                : totalRemaining;
            var allocated = AllocateBlock(block, perSourceRemaining, budget.CharactersPerEstimatedToken);
            if (block.Status == "included")
            {
                unresolvedExplicitContent--;
                if (allocated.IncludedContent.Length == 0)
                    throw new OrchestratorContextEnvelopeException(
                        "context-explicit-budget-insufficient",
                        "Explicit context sources do not fit the submitted budget. Remove or narrow a source, then try again.");
            }
            explicitAllocated.Add(allocated);
            totalRemaining -= allocated.IncludedContent.Length;
        }

        var automaticRemaining = Math.Min(
            totalRemaining,
            budget.AutomaticHardCapTokens * budget.CharactersPerEstimatedToken);
        var automaticAllocated = new List<AllocatedContextBlock>();
        foreach (var block in automatic.Where(item => item.Kind != "recent-conversation"))
        {
            var allocated = AllocateBlock(block, automaticRemaining, budget.CharactersPerEstimatedToken);
            automaticAllocated.Add(allocated);
            automaticRemaining -= allocated.IncludedContent.Length;
            totalRemaining -= allocated.IncludedContent.Length;
        }

        var historyRemaining = Math.Min(
            automaticRemaining,
            Math.Max(0, budget.AutomaticSoftCapTokens * budget.CharactersPerEstimatedToken
                        - automaticAllocated.Sum(item => item.IncludedContent.Length)));
        foreach (var block in automatic.Where(item => item.Kind == "recent-conversation"))
        {
            var allocated = AllocateBlock(block, historyRemaining, budget.CharactersPerEstimatedToken);
            automaticAllocated.Add(allocated);
            historyRemaining -= allocated.IncludedContent.Length;
        }
        return automaticAllocated.Concat(explicitAllocated).ToArray();
    }

    private static void DeduplicateContext(
        IList<ResolvedContextBlock> automatic,
        IList<ResolvedContextBlock> explicitBlocks)
    {
        DeduplicateWithin(explicitBlocks);
        DeduplicateWithin(automatic);
        var explicitIds = explicitBlocks
            .Select(item => item.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = automatic.Count - 1; index >= 0; index--)
        {
            if (explicitIds.Contains(automatic[index].SourceId))
                automatic.RemoveAt(index);
        }
    }

    private static void DeduplicateWithin(IList<ResolvedContextBlock> blocks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = blocks.Count - 1; index >= 0; index--)
        {
            if (!seen.Add(blocks[index].SourceId))
                blocks.RemoveAt(index);
        }
    }

    private static AllocatedContextBlock AllocateBlock(
        ResolvedContextBlock block,
        int availableCharacters,
        int charactersPerToken)
    {
        if (block.Status != "included")
            return new AllocatedContextBlock(
                block.SourceId,
                block.Kind,
                string.Empty,
                block.IsExplicit,
                new OrchestratorContextSourceReceipt(
                    block.SourceId, block.Kind, block.Revision, block.Sha256,
                    block.Freshness, 0, 0, block.Status, block.Reason));
        if (availableCharacters <= 0)
            return new AllocatedContextBlock(
                block.SourceId,
                block.Kind,
                string.Empty,
                block.IsExplicit,
                new OrchestratorContextSourceReceipt(
                    block.SourceId, block.Kind, block.Revision, block.Sha256,
                    block.Freshness, 0, 0, "omitted-budget",
                    "The source did not fit within the context budget."));
        var included = block.Content.Length <= availableCharacters
            ? block.Content
            : block.Content[..availableCharacters];
        var status = included.Length == block.Content.Length ? "included" : "excerpted";
        return new AllocatedContextBlock(
            block.SourceId,
            block.Kind,
            included,
            block.IsExplicit,
            new OrchestratorContextSourceReceipt(
                block.SourceId, block.Kind, block.Revision, block.Sha256,
                block.Freshness, included.Length,
                (int)Math.Ceiling(included.Length / (double)charactersPerToken),
                status,
                status == "excerpted" ? "The source was deterministically excerpted to fit the context budget." : null));
    }

    private static void AppendScopedPreamble(
        StringBuilder builder,
        string projectName,
        OrchestratorContextEnvelope envelope)
    {
        builder.AppendLine("=== SCOPED ORCHESTRATOR CHAT PREAMBLE ===");
        builder.AppendLine("You are the read-only Orchestrator answering an operator question.");
        builder.AppendLine($"Conversation scope: {envelope.Scope.ContextKey}");
        builder.AppendLine($"Project isolation boundary: {projectName}");
        builder.AppendLine("Use only evidence listed in this request. Never infer facts from another project.");
        builder.AppendLine("Do not start, stop, continue, move, or otherwise mutate a task from this chat turn.");
        builder.AppendLine("Reply directly and concretely. Use the word 'tasks', not 'jobs'. Use Markdown when useful.");
        builder.AppendLine();
    }

    private static void AppendContextLedger(StringBuilder builder, OrchestratorContextReceipt receipt)
    {
        builder.AppendLine("=== CONTEXT LEDGER ===");
        builder.AppendLine($"Receipt: {receipt.ReceiptId}");
        builder.AppendLine($"User turn: {receipt.UserTurnId}");
        builder.AppendLine($"Captured at: {receipt.CapturedAt:O}");
        builder.AppendLine($"Scope: {receipt.ContextKey}");
        builder.AppendLine($"Budget: automatic-soft={receipt.Budget?.AutomaticSoftCapTokens}; automatic-hard={receipt.Budget?.AutomaticHardCapTokens}; total-hard={receipt.Budget?.TotalHardCapTokens}; estimated-included={receipt.Budget?.EstimatedIncludedTokens}");
        foreach (var source in receipt.Sources ?? [])
        {
            builder.Append("- ").Append(source.SourceId)
                .Append(" | kind=").Append(source.Kind)
                .Append(" | revision=").Append(source.Revision ?? "none")
                .Append(" | sha256=").Append(source.Sha256 ?? "none")
                .Append(" | freshness=").Append(source.Freshness)
                .Append(" | status=").Append(source.Status)
                .Append(" | chars=").Append(source.IncludedCharacters)
                .Append(" | tokens~=").Append(source.EstimatedTokens);
            if (!string.IsNullOrWhiteSpace(source.Reason))
                builder.Append(" | reason=").Append(source.Reason);
            builder.AppendLine();
        }
        builder.AppendLine();
    }

    private static void AppendResolvedBlocks(
        StringBuilder builder,
        string heading,
        IEnumerable<AllocatedContextBlock> blocks)
    {
        var included = blocks
            .Where(item => item.Kind != "recent-conversation" && item.IncludedContent.Length > 0)
            .ToArray();
        if (included.Length == 0) return;
        builder.Append("=== ").Append(heading).AppendLine(" ===");
        foreach (var block in included)
        {
            builder.Append("--- ").Append(block.SourceId).AppendLine(" ---");
            builder.AppendLine(block.IncludedContent.TrimEnd());
        }
        builder.AppendLine();
    }

    private static string RenderContinuity(IEnumerable<OrchestratorChatTurn> turns)
    {
        var builder = new StringBuilder();
        foreach (var turn in turns)
        {
            if (string.IsNullOrWhiteSpace(turn.Text)) continue;
            builder.Append(turn.Role == OrchestratorChatRoles.User ? "Operator" : "Orchestrator")
                .Append(" [").Append(turn.Id).Append("]: ")
                .AppendLine(turn.Text.Trim());
        }
        return builder.ToString().TrimEnd();
    }

    private static string NormalizeTaskReference(string projectName, string reference)
    {
        var prefix = $"task:{projectName}/";
        return reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? reference[prefix.Length..].Trim()
            : reference.Trim();
    }

    private static string NormalizeRepositoryReference(
        string projectName,
        string reference,
        string kind)
    {
        var normalized = reference.Trim().Replace('\\', '/');
        if (kind is "page" or "workbench")
        {
            var pagePrefix = $"page:{projectName}/";
            if (normalized.StartsWith("page:", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith(pagePrefix, StringComparison.OrdinalIgnoreCase))
                throw new OrchestratorContextEnvelopeException(
                    "context-reference-cross-project",
                    "A page reference cannot cross the active conversation project.");
            if (normalized.StartsWith(pagePrefix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[pagePrefix.Length..];
            if (!normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
                normalized = "docs/" + normalized;
        }
        return normalized;
    }

    private static string NormalizeCommitReference(string projectName, string reference)
    {
        var normalized = reference.Trim();
        var prefix = $"commit:{projectName}/";
        if (normalized.StartsWith("commit:", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new OrchestratorContextEnvelopeException(
                "context-reference-cross-project",
                "A commit reference cannot cross the active conversation project.");
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..].Trim()
            : normalized;
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".."
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static void EnsureResolvedPathWithinRoot(
        string root,
        string fullPath,
        string reference)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (info.LinkTarget is null) continue;
            var resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (resolved is null || !IsWithinRoot(root, Path.GetFullPath(resolved)))
                throw new OrchestratorContextEnvelopeException(
                    "context-path-outside-project",
                    $"Context reference '{reference}' resolves outside the active project checkout.");
        }
    }

    private sealed record ResolvedContextBlock(
        string SourceId,
        string Kind,
        string Content,
        string? Revision,
        string? Sha256,
        string Freshness,
        bool IsExplicit,
        string Status,
        string? Reason)
    {
        public static ResolvedContextBlock Included(
            string sourceId,
            string kind,
            string content,
            string? revision,
            string freshness,
            bool isExplicit)
            => new(
                sourceId, kind, content, revision,
                OrchestratorContextEnvelopePolicy.Sha256(content),
                freshness, isExplicit, "included", null);

        public static ResolvedContextBlock Unavailable(
            string sourceId,
            string kind,
            string status,
            string reason,
            bool isExplicit = false)
            => new(sourceId, kind, string.Empty, null, null, "unknown", isExplicit, status, reason);
    }

    private sealed record AllocatedContextBlock(
        string SourceId,
        string Kind,
        string IncludedContent,
        bool IsExplicit,
        OrchestratorContextSourceReceipt Receipt);

    /// <summary>
    /// Render the AUTHORITATIVE project-state snapshot block. The global
    /// session is shared across projects, so without a per-turn snapshot
    /// the model recalls stale counts from a different project (the
    /// "Runbook has 21 jobs" incident, where the 21 actually belonged to
    /// the Agent Task Processor board the user had been chatting about
    /// earlier). The block names the project explicitly, gives an exact
    /// count, breaks it down by lane, and tells the model in two places
    /// to use "tasks" not "jobs" in the user-facing reply.
    ///
    /// Kept as an internal static helper so unit tests can pin the shape
    /// without spinning up a scanner; <see cref="BuildPrompt"/> calls it
    /// with the already-filtered tasks for the active project.
    /// </summary>
    internal static void AppendProjectStateSnapshot(
        StringBuilder sb,
        string projectName,
        IReadOnlyCollection<TaskInfo> tasksForProject)
    {
        sb.AppendLine($"AUTHORITATIVE current state of \"{projectName}\" ({tasksForProject.Count} tasks total):");
        if (tasksForProject.Count == 0)
        {
            sb.AppendLine("  (no tasks)");
        }
        else
        {
            foreach (var sg in tasksForProject.GroupBy(j => j.State).OrderBy(g => g.Key))
            {
                sb.AppendLine($"  {sg.Key}: {sg.Count()}");
            }
        }
        sb.AppendLine("Use these exact numbers. Any counts you remember from earlier in this session are stale and must be ignored.");
        sb.AppendLine("These items are called \"tasks\" (not \"jobs\") in the user-facing vocabulary.");
        sb.AppendLine();
    }

    /// <summary>
    /// Render the per-turn user-preferences block. Reads the live default
    /// CLI / model for the chatting client from <paramref name="store"/>;
    /// if no client id is available (anonymous read path, legacy callers)
    /// or the record has no defaults set, falls back to the same
    /// bootstrap-identity defaults the boot prompt used.
    ///
    /// Kept static + internal so unit tests can pin the shape without
    /// having to spin up a real chat service.
    /// </summary>
    internal static void AppendCurrentUserPreferences(StringBuilder sb, string? clientId, ClientIdentityStore? store)
    {
        string? cli = null;
        string? model = null;

        if (!string.IsNullOrWhiteSpace(clientId) && store is not null)
        {
            var rec = store.Find(clientId!);
            cli = rec?.DefaultCliType;
            model = rec?.DefaultModel;
        }

        // Always fall back so the block is well-formed even on a fresh
        // install with no recorded defaults.
        var (fallbackCli, fallbackModel) = GlobalOrchestratorBootstrap.ResolveBootDefaults(store);
        var effectiveCli = string.IsNullOrWhiteSpace(cli) ? fallbackCli : cli!;
        var effectiveModel = string.IsNullOrWhiteSpace(model) ? fallbackModel : model!;

        sb.AppendLine("=== CURRENT USER PREFERENCES ===");
        sb.AppendLine($"Default CLI: {effectiveCli}");
        sb.AppendLine($"Default model: {effectiveModel}");
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            sb.AppendLine($"Active client id (forward as X-Client-Id when calling /api/* on the user's behalf): {clientId}");
        }
        sb.AppendLine("These supersede any defaults named in the boot prompt for this turn.");
        sb.AppendLine("If the user asks you to create a task without naming a CLI or model, use these.");
        sb.AppendLine();
    }

    /// <summary>
    /// Render the navigation-context block when the frontend sent one. The
    /// rendered text is what the agent reads, so the wording is part of the
    /// contract: it tells the agent how to interpret context-dependent
    /// questions and explicitly forbids inventing a task when none is in
    /// scope. Kept in one place so unit tests can pin the shape.
    /// </summary>
    internal static void AppendNavigationContext(StringBuilder sb, ChatNavigationContext? nav)
    {
        sb.AppendLine("=== NAVIGATION CONTEXT ===");
        if (nav == null
            || (string.IsNullOrWhiteSpace(nav.CurrentPage)
                && string.IsNullOrWhiteSpace(nav.CurrentTaskId)
                && string.IsNullOrWhiteSpace(nav.CurrentTaskKey)
                && string.IsNullOrWhiteSpace(nav.CurrentTaskTitle)
                && string.IsNullOrWhiteSpace(nav.CurrentTaskState)
                && string.IsNullOrWhiteSpace(nav.CurrentLaneFilter)
                && string.IsNullOrWhiteSpace(nav.ViewportTimestamp)
                && string.IsNullOrWhiteSpace(nav.ObservedSurface)
                && string.IsNullOrWhiteSpace(nav.AffectedComponent)
                && string.IsNullOrWhiteSpace(nav.PageRef)
                && string.IsNullOrWhiteSpace(nav.PageTitle)
                && string.IsNullOrWhiteSpace(nav.PageType)
                && string.IsNullOrWhiteSpace(nav.PageExcerpt)))
        {
            sb.AppendLine("No navigation context was sent with this message.");
            sb.AppendLine("If the user asks a context-dependent question (\"what is the current task?\", \"explain this\"), say no specific task is in scope and ask which task they mean. Do NOT invent a task or hallucinate a context.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("The operator's UI state when they sent this message:");
        if (!string.IsNullOrWhiteSpace(nav.CurrentPage)) sb.AppendLine($"  currentPage: {nav.CurrentPage}");
        if (!string.IsNullOrWhiteSpace(nav.CurrentTaskId)) sb.AppendLine($"  currentTaskId: {nav.CurrentTaskId}");
        if (!string.IsNullOrWhiteSpace(nav.CurrentTaskKey)) sb.AppendLine($"  currentTaskKey: {nav.CurrentTaskKey}");
        if (!string.IsNullOrWhiteSpace(nav.CurrentTaskTitle)) sb.AppendLine($"  currentTaskTitle: {nav.CurrentTaskTitle}");
        if (!string.IsNullOrWhiteSpace(nav.CurrentTaskState)) sb.AppendLine($"  currentTaskState: {nav.CurrentTaskState}");
        if (!string.IsNullOrWhiteSpace(nav.CurrentLaneFilter)) sb.AppendLine($"  currentLaneFilter: {nav.CurrentLaneFilter}");
        if (!string.IsNullOrWhiteSpace(nav.ViewportTimestamp)) sb.AppendLine($"  viewportTimestamp: {nav.ViewportTimestamp}");
        if (!string.IsNullOrWhiteSpace(nav.ObservedSurface)) sb.AppendLine($"  observedSurface: {nav.ObservedSurface}");
        if (!string.IsNullOrWhiteSpace(nav.AffectedComponent)) sb.AppendLine($"  affectedComponent: {nav.AffectedComponent}");
        if (!string.IsNullOrWhiteSpace(nav.PageRef)) sb.AppendLine($"  pageRef: {nav.PageRef}");
        if (!string.IsNullOrWhiteSpace(nav.PageTitle)) sb.AppendLine($"  pageTitle: {nav.PageTitle}");
        if (!string.IsNullOrWhiteSpace(nav.PageType)) sb.AppendLine($"  pageType: {nav.PageType}");
        if (!string.IsNullOrWhiteSpace(nav.PageExcerpt)) sb.AppendLine($"  pageExcerpt: {nav.PageExcerpt}");
        sb.AppendLine();
        sb.AppendLine("Use this when interpreting context-dependent questions. When pageRef is set, the operator is asking from THAT repository page; use its title, type, path, and excerpt. When currentTaskKey or currentTaskId is set, the operator is most likely asking about THAT task; answer with its title/state and refer to it by key. When neither pageRef, currentTaskKey, nor currentTaskId is set, do NOT invent one; say no specific page or task is in scope and ask what they mean. Never produce filler tokens or repeated greetings in place of a real answer.");
        sb.AppendLine();
    }

    internal string ResolveWorkingDirectory(string projectName, string watchPath)
    {
        var entry = _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Path, watchPath, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(entry?.RootPath))
            return entry.RootPath;

        var project = _projects?.FindByStorageLocation(watchPath)
                      ?? _projects?.FindByIdOrDisplayName(projectName);
        if (!string.IsNullOrWhiteSpace(project?.RepositoryPath))
            return project.RepositoryPath;

        var tempPath = Path.GetTempPath();
        _logger.LogWarning(
            "orchestrator_chat_working_directory_temp_fallback project={Project} watchPath={WatchPath} " +
            "reason=missing-watch-root-and-registry-repository-path tempPath={TempPath}",
            projectName, watchPath, tempPath);
        return tempPath;
    }

    /// <summary>
    /// Resolve the checkout identity shown in the chat header. Remote projects
    /// queue a non-mutating host inspection when no exact runner snapshot is
    /// cached yet; local projects read branch and HEAD directly from the same
    /// repository root used by the local Codex one-shot.
    /// </summary>
    public ChatExecutionContext ResolveExecutionContext(string projectName, string watchPath)
    {
        var route = ResolveRemoteRoute(projectName, watchPath);
        if (route != null && _remoteWork != null)
        {
            var observed = _remoteWork.GetContext(route);
            if (observed != null) return observed;
            _remoteWork.RequestInspection(route);
            return new ChatExecutionContext(
                "remote",
                route.RunnerId,
                RepoPath: null,
                route.DefaultBranch,
                HeadSha: null,
                "resolving",
                DateTime.UtcNow);
        }

        var root = ResolveWorkingDirectory(projectName, watchPath);
        return new ChatExecutionContext(
            "local",
            "local",
            root,
            _git?.ReadBranchAt(root),
            _git?.ReadHeadShaAt(root),
            "ready",
            DateTime.UtcNow);
    }

    private RemoteChatWorkRoute? ResolveRemoteRoute(string projectName, string watchPath)
    {
        if (_projectSettings == null || _projects == null) return null;
        var settings = _projectSettings.Get(projectName);
        if (!settings.RemoteExecutionEnabled || string.IsNullOrWhiteSpace(settings.ExecutionRunner))
            return null;

        var project = _projects.FindByStorageLocation(watchPath)
                      ?? _projects.FindByIdOrDisplayName(projectName);
        var repository = RemoteProjectRepositoryResolver.Resolve(project, settings.IntegrationBranch);
        if (repository == null)
            throw new InvalidOperationException(
                $"Remote project chat cannot resolve a repository URL for '{projectName}'.");
        return new RemoteChatWorkRoute(
            settings.ExecutionRunner!,
            repository.ProjectId,
            projectName,
            repository.RepositoryUrl,
            repository.DefaultBranch);
    }

    /// <summary>
    /// Lift the inline-base64 attachments out of the request and into the
    /// shape <see cref="OrchestratorRunner"/> hands to the Claude one-shot
    /// driver. Returns null when no attachment carried inline bytes, so
    /// the caller can pass null through to the existing text-only path.
    /// Strips entries that look malformed (empty base64 / non-image mime)
    /// rather than passing them through and letting the CLI fail later.
    /// </summary>
    internal static IReadOnlyList<CliOneShotImage>? ExtractInlineImages(
        IEnumerable<OrchestratorChatAttachment>? attachments)
    {
        if (attachments == null) return null;
        var result = new List<CliOneShotImage>();
        foreach (var a in attachments)
        {
            if (string.IsNullOrWhiteSpace(a.InlineBase64)) continue;
            var mime = string.IsNullOrWhiteSpace(a.MimeType) ? "image/png" : a.MimeType!;
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new CliOneShotImage(a.InlineBase64!, mime));
        }
        return result.Count == 0 ? null : result;
    }
}
