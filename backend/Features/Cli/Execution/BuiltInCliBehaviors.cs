using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.CliHosting;

namespace AgentStudio.Cli;

/// <summary>
/// Live rate-limit snapshot derived from Anthropic's <c>rate_limit_event</c>
/// stream-json frames. Captured per-turn while the CLI is running and
/// surfaced via <c>GET /api/tasks/{id}/claude/session-info</c> so the
/// frontend's protocol-pane pill can show "5h reset in 12 min".
/// </summary>
public record ClaudeRateLimitSnapshot(
    string? Window,
    string? Status,
    long ResetsAt,
    string? OverageStatus,
    bool IsUsingOverage,
    DateTime CapturedAt);

/// <summary>
/// Last <c>command_execution</c> <c>item.completed</c> frame a Codex run
/// emitted. Carried as a value type because the runner reads it from a
/// different thread than the read loop that wrote it; the snapshot is
/// immutable so no copy-coupling exists between producer and consumer.
/// </summary>
public readonly record struct CodexLastCommandSnapshot(
    int? ExitCode,
    string? Command,
    string? OutputTail,
    DateTime ObservedAt);

/// <summary>
/// Built-in <see cref="CliBehavior"/> catalog: the per-CLI data + delegates
/// that customize the single concrete <see cref="GenericCliExecutionService"/>
/// engine for Claude Code, Codex, and Antigravity/Gemini. This is the host
/// analogue of the library's per-CLI descriptor catalog — each factory returns
/// a fully-wired behavior; all CLI-specific parsing and rendering helpers
/// live here as private (or test-visible <c>internal</c>) statics rather than on
/// the engine. The previous thin per-CLI shim classes (<c>ClaudeCliService</c> /
/// <c>CodexCliService</c> / <c>AntigravityCliService</c>) were deleted in favour
/// of this catalog plus <see cref="GenericCliExecutionService"/> factory helpers.
/// </summary>
internal static class BuiltInCliBehaviors
{
    // ════════════════════════════════════════════════════════════════════
    // Claude
    // ════════════════════════════════════════════════════════════════════

    internal static CliBehavior Claude(
        CliUsageParserRegistry? usageParsers,
        ICliModelRegistry modelRegistry,
        ClaudeModelDiscovery? modelDiscovery) => new CliBehavior
    {
        CliType = CliTypes.Claude,
        EmitsSessionId = true,
        NeedsPostHocUsageReconstruction = true,
        SupportsCleanContext = true,
        GetCliPath = ctx => ctx.CliPathOverride
                            ?? ctx.Configuration["ClaudeCli:Path"]
                            ?? "claude",
        IsCompatibleSessionName = (ctx, sessionName)
            => !string.IsNullOrWhiteSpace(sessionName) && ClaudeUuidRegex.IsMatch(sessionName),
        NormalizeModelForInvocation = (_, model) => NormalizeModelId(model),
        CaptureRawLine = (ctx, jobKey, line) => ClaudeCaptureRawLine(ctx, usageParsers, modelRegistry, jobKey, line),
        MapLineToRunEvents = (ctx, jobKey, line) => ClaudeMapLineToRunEvents(ctx, usageParsers, modelRegistry, jobKey, line),
        StartSessionLiveness = (ctx, info, resumeSession, sessionName) =>
        {
            if (resumeSession && ctx.IsCompatibleSessionName(sessionName))
                ClaudeEnsureSessionLiveness(ctx, info, sessionName!);
        },
        DescribeContextSources = (ctx, jobKey) => ClaudeDescribeContextSources(ctx, jobKey),
        PrepareCleanContext = (ctx, jobKey, workingDirectory)
            => CleanContextPreparer.PrepareClaude(
                GenericCliExecutionService.ResolveUserHome(),
                jobKey,
                ctx.Logger,
                CleanContextRetentionHostedService.ResolveRootOverride(ctx.Configuration)),
        TransformReadLine = (ctx, raw) => _claudeRenderer.Render(raw),
        OnOutputLine = (ctx, info, line) => ClaudeOnOutputLine(ctx, info, line),
        GetModelCatalog = (ctx, force, ct) => ClaudeGetModelCatalog(ctx, modelDiscovery, force, ct),
    };

    /// <summary>
    /// Bridge to <see cref="ClaudeEventAdapter"/>. Each raw stdout line is
    /// passed through and emitted on <see cref="GenericCliExecutionService.OnRunEvent"/>
    /// alongside the legacy marker stream. Stderr passes through unchanged
    /// (we do not parse provider stderr today).
    /// </summary>
    private static IEnumerable<CliRunEvent> ClaudeMapLineToRunEvents(
        GenericCliExecutionService ctx,
        CliUsageParserRegistry? usageParsers,
        ICliModelRegistry modelRegistry,
        string jobKey,
        CliOutputLine line)
    {
        if (line.Stream != "stdout") return Array.Empty<CliRunEvent>();

        // Keep the rate-limit path local and forgiving. The packaged adapter
        // pins the original camelCase schema; this shim also accepts the
        // snake_case/string variants observed on adjacent Claude surfaces and
        // prevents an optional field type from breaking the read loop.
        if (ClaudeRateLimitEventParser.TryMap(line.Text, jobKey, out var rateLimit)
            && rateLimit != null)
            return [rateLimit];

        return ClaudeEventAdapter.Map(line.Text, jobKey);
    }

    private static void ClaudeCaptureRawLine(
        GenericCliExecutionService ctx,
        CliUsageParserRegistry? usageParsers,
        ICliModelRegistry modelRegistry,
        string jobKey,
        CliOutputLine line)
    {
        if (line.Stream != "stdout" || !ctx.TryGetProc(jobKey, out var info)) return;
        ClaudeTryCaptureTurnUsage(ctx, usageParsers, modelRegistry, info, line);
        ClaudeTryCaptureInitContext(ctx, info, line);
    }

    /// <summary>
    /// Idempotently arm the per-session JSONL mtime watcher for a run. The
    /// watcher raises a <see cref="CliRunEvent.Heartbeat"/> on every file
    /// change, which the runner treats as an activity signal and uses to
    /// reset the watchdog silence clock. Safe to call from both the spawn
    /// thread (resume) and the read-loop thread (fresh-session UUID capture):
    /// the first caller wins, later calls see a non-null watcher and return.
    /// </summary>
    private static void ClaudeEnsureSessionLiveness(GenericCliExecutionService ctx, GenericCliExecutionService.ProcInfo info, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        lock (info)
        {
            if (info.SessionLiveness != null) return;
            var jobKey = info.Execution.TaskKey;
            // On a clean-context run (the DEFAULT), claude redirects its session
            // transcript to CLAUDE_CONFIG_DIR (info.CleanContext.TempHome). The
            // heartbeat MUST watch that dir, not the default ~/.claude, or it sees
            // permanent silence and the watchdog kills the live run mid-work
            // (exit=-1) - the "runs never complete / backlog never drains" bug.
            var heartbeat = new ClaudeSessionHeartbeat(
                sessionId,
                info.WorkingDirectory,
                onActivity: () => ctx.RaiseRunEvent(jobKey, new CliRunEvent.Heartbeat { RunId = jobKey }),
                logger: ctx.Logger,
                configDir: info.CleanContext?.TempHome);
            info.SessionLiveness = heartbeat;
            ctx.Logger.LogInformation(
                "Claude session-liveness watcher armed for {JobKey} (session {Session}, watching {Path})",
                jobKey, sessionId, heartbeat.WatchedPath ?? "<unresolved>");
        }
    }

    /// <summary>
    /// Parse the cumulative <c>usage</c> block on the stream-json
    /// <c>result</c> frame via the shared <see cref="ClaudeUsageParser"/> and
    /// stash it on <see cref="GenericCliExecutionService.ProcInfo.LastParsedUsage"/>.
    /// The runner consumes the stash when the matching <c>TurnCompleted</c>
    /// event arrives and mirrors it onto the agent message bus as
    /// <c>kind:token-usage</c>. Without this the CORE coding-agent run's own
    /// per-run spend is invisible to <c>BusAggregationCache</c>, the per-job
    /// token summary, and the Overview - the exact "no token activity recorded"
    /// symptom. Only the top-level <c>usage</c> object (which the parser
    /// requires) appears on the <c>result</c> frame; assistant frames nest
    /// usage under <c>message</c>, so they are correctly ignored. Best-effort:
    /// a malformed frame or parser miss leaves the previous snapshot untouched.
    /// </summary>
    private static void ClaudeTryCaptureTurnUsage(
        GenericCliExecutionService ctx,
        CliUsageParserRegistry? usageParsers,
        ICliModelRegistry modelRegistry,
        GenericCliExecutionService.ProcInfo info,
        CliOutputLine line)
    {
        var text = line.Text?.TrimStart();
        if (string.IsNullOrEmpty(text) || text![0] != '{') return;
        // Fast prefilter: cumulative usage rides the `result` frame; skip JSON
        // parsing for everything else. The parser is the authority - it only
        // returns true for a frame with a top-level `usage` object.
        if (!text.Contains("result", StringComparison.Ordinal)) return;

        var parser = usageParsers?.Get(CliTypes.Claude);
        if (parser == null) return;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var modelHint = info.Execution.Model;
            var usages = parser.ParseAll(doc.RootElement, modelHint, modelRegistry);
            if (usages.Count == 0) return;

            info.LastParsedUsages = usages;
            info.LastParsedUsage = usages.Count == 1 ? usages[0] : AggregateUsage(usages, modelHint, modelRegistry);
            info.LastParsedUsageAt = line.Timestamp == default ? DateTime.UtcNow : line.Timestamp;
        }
        catch (JsonException __ex) { SilentCatch.Note(__ex, "BuiltInCliBehaviors.Claude: malformed frame; nothing to capture"); /* malformed frame; nothing to capture */ }
        catch (Exception ex) { ctx.Logger.LogDebug(ex, "Claude turn-usage capture skipped"); }
    }

    /// <summary>
    /// Stash the parsed init frame onto <see cref="GenericCliExecutionService.ProcInfo"/>
    /// the first time we see it. The frame Claude already emits carries the
    /// model, effective permission mode, cwd, and wired-in MCP servers - all of
    /// it discarded today except the session id. Capturing it here (next to
    /// <see cref="ClaudeTryCaptureTurnUsage"/>) lets <c>DescribeContextSources</c>
    /// report what the CLI itself said it loaded (ASS-1739 / T1a). Read-only:
    /// parsing the frame never changes what the run loads. Best-effort - a
    /// missing or malformed frame leaves the snapshot null and the surface falls
    /// back to convention.
    /// </summary>
    private static void ClaudeTryCaptureInitContext(GenericCliExecutionService ctx, GenericCliExecutionService.ProcInfo info, CliOutputLine line)
    {
        if (info.ClaudeInit != null) return; // first init frame wins
        var text = line.Text?.TrimStart();
        if (string.IsNullOrEmpty(text) || text![0] != '{') return;
        if (!text.Contains("\"init\"", StringComparison.Ordinal)) return;
        try
        {
            if (ClaudeInitContextParser.TryParse(text, out var context) && context != null)
                info.ClaudeInit = context;
        }
        catch (Exception ex) { ctx.Logger.LogDebug(ex, "Claude init-context capture skipped"); }
    }

    /// <summary>
    /// Claude execution context (ASS-1739 / T1a): prefer the CLI's own init
    /// frame for the scalar header (model / permission mode / cwd) and the MCP
    /// server list, then layer the convention sources (memory chain, session
    /// store, global config) underneath. Falls back to the engine
    /// convention-only context when no init frame was captured (e.g. the run
    /// died before the frame, or a non-stream-json invocation).
    /// </summary>
    private static AgentStudio.Shared.CliExecutionContext? ClaudeDescribeContextSources(GenericCliExecutionService ctx, string jobKey)
    {
        if (!ctx.TryGetProc(jobKey, out var info)) return null;
        var convention = ctx.BuildConventionContext(info);
        var init = info.ClaudeInit;
        if (init == null) return convention;

        var sources = new List<AgentStudio.Shared.CliContextSource>();
        foreach (var mcp in init.McpServers)
            sources.Add(new AgentStudio.Shared.CliContextSource
            {
                Kind = AgentStudio.Shared.CliContextSourceKinds.Mcp,
                Label = mcp.Name,
                Detail = mcp.Status,
            });
        sources.AddRange(convention.Sources);

        return convention with
        {
            Model = string.IsNullOrWhiteSpace(init.Model) ? convention.Model : init.Model,
            PermissionMode = string.IsNullOrWhiteSpace(init.PermissionMode) ? convention.PermissionMode : init.PermissionMode,
            Cwd = string.IsNullOrWhiteSpace(init.Cwd) ? convention.Cwd : init.Cwd,
            Source = "init-frame",
            Sources = sources,
        };
    }

    /// <summary>
    /// Walk the npm-shim convention to find the underlying claude.exe when
    /// <see cref="CliBehavior.GetCliPath"/> resolved to the <c>claude.CMD</c> dispatcher.
    ///
    /// <para>
    /// <b>Why this exists.</b> npm-installed Node CLIs ship as a tiny <c>.CMD</c>
    /// batch shim that calls <c>node.exe path\to\bin\<cli>.exe %*</c>. When the
    /// .NET runner spawns the <c>.CMD</c>, Windows wraps it as
    /// <c>cmd.exe /c "claude.CMD ..."</c> — and that wrapper interferes with
    /// stdin pipe inheritance: claude reads its first <c>system/init</c> frame
    /// out, then never sees the prompt bytes (cmd.exe consumes / mistakes the
    /// pipe), so the agent goes silent and the watchdog kills it. Calling the
    /// real <c>claude.exe</c> directly bypasses cmd.exe entirely. The
    /// regression test
    /// <c>CliSpawnIntegrationTests.DirectExe_PipeStdin_StreamJson_ProducesMultipleFrames</c>
    /// pins this behaviour.
    /// </para>
    /// <para>
    /// We probe the canonical npm-installed location first; if it is missing
    /// (e.g. a portable install or a non-standard layout) we fall back to the
    /// original path and accept that the user may need to set
    /// <c>ClaudeCli:Path</c> explicitly.
    /// </para>
    /// </summary>
    internal static string ResolveCmdShimToExe(string cmdOrExePath)
    {
        if (string.IsNullOrWhiteSpace(cmdOrExePath)) return cmdOrExePath;
        if (!cmdOrExePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            && !cmdOrExePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            return cmdOrExePath;
        var dir = Path.GetDirectoryName(cmdOrExePath) ?? string.Empty;
        var candidate = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        return File.Exists(candidate) ? candidate : cmdOrExePath;
    }

    /// <summary>
    /// Resolves the actual file to execute for the Claude CLI.
    ///
    /// <para>
    /// <b>Default (shell-PATH wins):</b> trust the user's PATH the same way
    /// their `claude` invocation in PowerShell / cmd does. ResolveExecutable
    /// walks PATH + PATHEXT and returns the first hit (typically a native
    /// `claude.exe` from the Anthropic standalone installer, or the
    /// `claude.cmd` shim from an npm install). Both are safe to spawn
    /// directly now that ADR-0014 routes the prompt as a positional argv
    /// instead of through stdin — the original cmd.exe pipe-inheritance
    /// bug no longer applies.
    /// </para>
    ///
    /// <para>
    /// <b>Legacy npm-shim probe</b> (opt-in via
    /// <c>ClaudeCli:UseNpmShimProbe=true</c>): if PATH resolves to a `.cmd`,
    /// look for a sibling `node_modules/@anthropic-ai/claude-code/bin/claude.exe`
    /// and prefer it. Kept as an escape hatch in case argv quoting through
    /// cmd.exe regresses on a specific Windows build; the user can flip the
    /// switch in appsettings without redeploying.
    /// </para>
    ///
    /// <para>
    /// <b>Why this changed:</b> the previous implementation hard-coded the
    /// npm-shim probe and silently picked the node_modules-bundled
    /// `claude.exe` over the user's PATH binary. When the bundled exe was
    /// missing, outdated, or pointed at a different Anthropic release than
    /// the user's shell, project-level chat broke while shell `claude`
    /// kept working — exactly the symptom the user reported.
    /// </para>
    /// </summary>
    internal static string ResolveClaudeBinary(GenericCliExecutionService ctx, string nameOrPath)
    {
        // 1. Shell PATH resolution (uses PATHEXT on Windows).
        var resolved = GenericCliExecutionService.ResolveExecutable(nameOrPath);

        // 2. Prefer the real claude.exe over a .cmd/.bat shim — DEFAULT ON.
        //    ROOT CAUSE (2026-06-23 pipeline stall): when PATH resolves `claude`
        //    to the npm `claude.CMD` shim (no claude.exe on PATH), spawning the
        //    .CMD routes through `cmd.exe /c claude.CMD <args>`. cmd.exe treats
        //    the newline inside the multi-line `-p <prompt>` argument as a
        //    command separator, so the agent receives ONLY the first line
        //    ("## Worktree containment") and never sees the task brief — every
        //    run then flails / emits NEEDS_INPUT / escalates. ResolveCmdShimToExe
        //    rewrites the shim to the bundled claude.exe the shim itself calls
        //    (identical binary, minus the cmd.exe layer), which CreateProcess
        //    parses via CommandLineToArgvW so the multi-line prompt survives
        //    verbatim. This was wrongly gated behind an opt-IN flag; spawning a
        //    .cmd with a multi-line argv is never safe on Windows, so the
        //    conversion is now the default. Opt OUT with UseNpmShimProbe=false
        //    only for unusual layouts where the bundled exe must not be used.
        var optOut = string.Equals(
            ctx.Configuration["ClaudeCli:UseNpmShimProbe"], "false",
            StringComparison.OrdinalIgnoreCase);
        if (!optOut)
        {
            var probed = ResolveCmdShimToExe(resolved);
            if (!string.Equals(probed, resolved, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Logger.LogInformation(
                    "[claude-bin] Rewrote .cmd shim {Shell} -> bundled exe {Probed} (cmd.exe truncates multi-line -p prompts at the first newline)",
                    resolved, probed);
                return probed;
            }
        }

        ctx.Logger.LogInformation(
            "[claude-bin] Using shell-resolved binary {Path} (input: {Input})",
            resolved, nameOrPath);
        return resolved;
    }

    /// <summary>
    /// Resolves <c>AgentRules:CorePath</c> to an absolute existing file path.
    /// Honours absolute paths verbatim; for relative paths, searches CWD,
    /// then walks up from <c>AppContext.BaseDirectory</c> looking for the file.
    /// Returns <c>null</c> if no candidate exists or the file is empty / oversized.
    /// </summary>
    internal static string? ResolveAgentRulesPath(GenericCliExecutionService ctx)
    {
        var configured = ctx.Configuration["AgentRules:CorePath"];
        if (string.IsNullOrWhiteSpace(configured)) return null;

        var candidates = new List<string>();
        if (Path.IsPathRooted(configured))
        {
            candidates.Add(configured);
        }
        else
        {
            candidates.Add(Path.GetFullPath(configured));
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                candidates.Add(Path.Combine(dir.FullName, configured));
                dir = dir.Parent;
            }
        }

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            var size = new FileInfo(candidate).Length;
            if (size == 0) return null;
            if (size > 8 * 1024)
            {
                ctx.Logger.LogWarning("Agent rules file {Path} is {Size} bytes (>8 KB), skipping injection", candidate, size);
                return null;
            }
            return candidate;
        }
        return null;
    }

    private static readonly ClaudeOutputRenderer _claudeRenderer = new();

    // Claude's `-r` flag expects a session UUID written by the CLI itself.
    // Slug-style names from another CLI (e.g. a legacy "taskboard-...") cause
    // the process to hang instead of erroring out, so reject anything that
    // isn't a 36-char canonical UUID.
    private static readonly Regex ClaudeUuidRegex =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

    // The first `system` frame is rendered by TransformReadLine into
    //   "● Session init <uuid>"  (or another subtype + uuid)
    // and we read the UUID back from the marker line so the same plumbing as
    // Gemini/Codex applies. Without this, Continue always starts a fresh
    // session because info.SessionName never advances past the placeholder
    // slug TaskRunnerService pre-generates.
    private static readonly Regex ClaudeSessionMarkerRegex = new(
        @"●\s*Session\s+\S+\s+(?<uuid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled);

    // Defensive fallback: any canonical UUID anywhere on an early stdout
    // line is treated as the session id. The marker regex above is the
    // intended path, but in production we have observed runs where the
    // marker did not get captured (Claude Code's stream-json frame format
    // varies across versions and platforms). Once we have ANY UUID for
    // this run we stop, so this never overrides the marker if the marker
    // already fired.
    private static readonly Regex ClaudeAnyUuidRegex = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled);

    private static readonly Regex ClaudeRateLimitMarkerRegex = new(
        @"●\s*Rate limit\b.*\[" +
        @"window=(?<win>[^\s\]]+)\s+" +
        @"status=(?<st>[^\s\]]+)\s+" +
        @"resetsAt=(?<reset>\d+)\s+" +
        @"overage=(?<ov>[^\s\]]+)\s+" +
        @"usingOverage=(?<using>true|false)\]",
        RegexOptions.Compiled);

    private static void ClaudeOnOutputLine(GenericCliExecutionService ctx, GenericCliExecutionService.ProcInfo info, CliOutputLine line)
    {
        if (line.Text == null) return;

        // Capture session UUID. Marker line is the intended path, but we
        // also accept ANY canonical UUID on any stdout line as a defensive
        // fallback, because the marker has been observed to be missing on
        // some Claude Code stream-json versions / platforms. The first
        // captured UUID wins; later UUIDs in the same run (e.g. tool
        // result ids) are ignored so we do not overwrite the session id
        // with an unrelated identifier.
        if (info.CapturedSessionId == null && line.Stream == "stdout")
        {
            var sessionMatch = ClaudeSessionMarkerRegex.Match(line.Text);
            string? uuid = sessionMatch.Success ? sessionMatch.Groups["uuid"].Value : null;
            if (uuid == null)
            {
                var anyUuidMatch = ClaudeAnyUuidRegex.Match(line.Text);
                if (anyUuidMatch.Success) uuid = anyUuidMatch.Value;
            }
            if (!string.IsNullOrWhiteSpace(uuid))
            {
                info.CapturedSessionId = uuid;
                info.SessionName = uuid;
                ctx.Logger.LogInformation("Captured Claude session id {Id} (marker={Marker})",
                    uuid, sessionMatch.Success);
                // Fresh-run path: now that we know the CLI-assigned UUID, arm
                // the side-channel liveness watcher so any later stdout
                // buffering does not read as silence. No-op on resume - the
                // watcher was already armed at spawn from the resume UUID.
                ClaudeEnsureSessionLiveness(ctx, info, uuid);
            }
        }

        // Capture the latest rate-limit telemetry from the bracketed kv tail
        // of the `● Rate limit ... [window=... status=... resetsAt=...]` marker.
        var rateMatch = ClaudeRateLimitMarkerRegex.Match(line.Text);
        if (rateMatch.Success)
        {
            long.TryParse(rateMatch.Groups["reset"].Value, out var resetsAt);
            info.LastRateLimit = new ClaudeRateLimitSnapshot(
                Window:         NullIfPlaceholder(rateMatch.Groups["win"].Value),
                Status:         NullIfPlaceholder(rateMatch.Groups["st"].Value),
                ResetsAt:       resetsAt,
                OverageStatus:  NullIfPlaceholder(rateMatch.Groups["ov"].Value),
                IsUsingOverage: rateMatch.Groups["using"].Value == "true",
                CapturedAt:     DateTime.UtcNow);
        }
    }

    private static string? NullIfPlaceholder(string v) =>
        string.IsNullOrEmpty(v) || v == "?" || v == "-" ? null : v;

    private static Task<CliModelCatalog> ClaudeGetModelCatalog(
        GenericCliExecutionService ctx,
        ClaudeModelDiscovery? modelDiscovery,
        bool forceRefresh,
        CancellationToken ct)
        => modelDiscovery != null
            ? modelDiscovery.GetAsync(ctx.GetCliPath(), forceRefresh, ct)
            : Task.FromResult(ClaudeModelDiscovery.FallbackCatalog());

    /// <summary>
    /// Coerces the dotted model-version forms users tend to type or paste
    /// (<c>claude-opus-4.7</c>, <c>claude-sonnet-4.6</c>) into the dashed form
    /// the Anthropic CLI requires (<c>claude-opus-4-7</c>). Any other model
    /// string is returned unchanged so non-standard ids still flow through.
    /// </summary>
    internal static string? NormalizeModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return model;
        var trimmed = model.Trim();
        if (!trimmed.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) return trimmed;
        // Replace dots between digits ("4.7" → "4-7") without touching dots in
        // unrelated positions (none exist in real Claude ids today, but be safe).
        return ModelMetadataRegistry.NormalizeId(
            System.Text.RegularExpressions.Regex.Replace(trimmed, @"(?<=\d)\.(?=\d)", "-"));
    }

    // ════════════════════════════════════════════════════════════════════
    // Codex
    // ════════════════════════════════════════════════════════════════════

    // Fallback Codex model when neither the task nor config specifies one.
    // Must be an account-valid model: codex-cli 0.143 on a ChatGPT account
    // rejects gpt-5-codex with a 400 invalid_request, so the default is
    // gpt-5.5 (AGT-1941). Discovery of the live catalog does NOT self-correct
    // an explicitly-requested stale model - ResolveInvocationModel only swaps
    // out cross-vendor ids (claude-*/gemini-*), not a stale OpenAI id - so this
    // fallback baseline is the single lever that keeps fresh Codex spawns on a
    // valid model.
    internal const string CodexFallbackModel = ModelIds.Gpt55;

    internal static CliBehavior Codex(
        CodexModelDiscovery modelDiscovery,
        CliUsageParserRegistry usageParsers,
        ICliModelRegistry modelRegistry) => new CliBehavior
    {
        CliType = CliTypes.Codex,
        IsCompatibleSessionName = (ctx, sessionName)
            => !string.IsNullOrWhiteSpace(sessionName) && CodexUuidRegex.IsMatch(sessionName),
        GetCliPath = ctx => ctx.CliPathOverride
                            ?? ctx.Configuration["CodexCli:Path"]
                            ?? "codex",
        SupportsCleanContext = true,
        PrepareCleanContext = (ctx, jobKey, workingDirectory)
            => CleanContextPreparer.PrepareCodex(
                GenericCliExecutionService.ResolveUserHome(),
                jobKey,
                ctx.Logger,
                CleanContextRetentionHostedService.ResolveRootOverride(ctx.Configuration)),
        NormalizeModelForInvocation = (ctx, model) => ResolveInvocationModel(model, ctx.Configuration),
        CaptureRawLine = (ctx, jobKey, line) => CodexCaptureRawLine(ctx, usageParsers, modelRegistry, jobKey, line),
        MapLineToRunEvents = (ctx, jobKey, line) => CodexMapLineToRunEvents(ctx, usageParsers, modelRegistry, jobKey, line),
        TransformReadLine = (ctx, raw) => _codexRenderer.Render(raw),
        GetModelCatalog = (ctx, force, ct) => modelDiscovery.GetAsync(ctx.GetCliPath(), force, ct),
    };

    // Codex resumes by UUID captured from thread.started (or legacy session_meta).
    // A slug from any other CLI is invalid and would make
    // `codex exec resume` error out.
    private static readonly Regex CodexUuidRegex =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

    internal static string ResolveInvocationModel(string? model, IConfiguration configuration)
    {
        var requested = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (!IsForeignModelId(requested)) return requested ?? DefaultCodexModel(configuration);

        return DefaultCodexModel(configuration);
    }

    private static string DefaultCodexModel(IConfiguration configuration)
    {
        var configured = configuration["CodexCli:Model"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        configured = configuration["CodexCli:DefaultModel"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        // Follow the installed CLI: when discovery has detected a newer top
        // model (gpt-5.6-*) it is the invocation default too, so a codex task
        // that reaches spawn with no/foreign model runs on the current model
        // rather than the gpt-5.5 floor (AGT-2025). Null => the account-valid
        // gpt-5.5 baseline (AGT-1941).
        var detected = ModelMetadataRegistry.DetectedCodexDefault;
        return string.IsNullOrWhiteSpace(detected) ? CodexFallbackModel : detected;
    }

    private static bool IsForeignModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        return model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
               || model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Codex has no <c>--append-system-prompt</c> flag (Claude's mechanism),
    /// so per-CLI orchestrator guidance must be prepended to the positional
    /// prompt argument. This builds a short prefix with two prophylactic
    /// hints that complement the reactive
    /// <see cref="AgentStudio.Cli.AgentEnvironmentDetector"/>
    /// pipeline:
    /// <list type="number">
    ///   <item>Sentinel reminder. Codex's pass-through frame model means the
    ///         fresh-start template's terminal-sentinel rule can drift out of
    ///         view on a resume turn, where the user follow-up is the entire
    ///         prompt. The "missing-terminal-sentinel" auto-review case noted
    ///         in <c>AgentEnvironmentDetector</c>'s "why this exists" section
    ///         was caused by exactly this gap.</item>
    ///   <item>No-shell hint on Windows. Codex's Windows sandbox wrapper
    ///         (<c>windows-sandbox-rs</c>) refuses <c>CreateProcessAsUserW</c>
    ///         under common service / RDP logon-session configurations; the
    ///         agent retries the same command 3-10 times and burns the silence
    ///         budget without producing useful output. Telling Codex up front
    ///         to prefer file reads and to report a single failure via
    ///         <c>[[TASK_BLOCKED:windows-sandbox]]</c> short-circuits that
    ///         retry loop.</item>
    /// </list>
    /// Kept deliberately short (~5 lines): every Codex invocation, including
    /// resumes whose user prompt is one sentence, pays this prefix in tokens.
    /// </summary>
    internal static string BuildSystemPromptPrefix(bool isWindows)
    {
        const string sentinelLine =
            "Orchestrator note: your reply MUST end with exactly one of `[[TASK_DONE]]`, " +
            "`[[TASK_BLOCKED:missing-dependency-xyz]]`, `[[TASK_NEEDS_INPUT:choose-primary-column]]`, or " +
            "`[[TASK_NOOP]]` as the final line. Replace the example reason with the actual short reason; " +
            "never emit the example text unchanged. This is required, not optional. The " +
            "orchestrator parses this token; without it the run lands in auto-review as " +
            "missing-terminal-sentinel.";

        const string investigationLine =
            "Time-box investigation: do not spend the whole turn searching or reading - " +
            "form a plan early and start making the change, then verify. A turn spent only " +
            "exploring will be killed by the watchdog with the work unfinished.";

        if (!isWindows)
        {
            return sentinelLine + "\n" + investigationLine + "\n\n";
        }

        const string windowsShellLine =
            "Windows note: if a shell command returns `windows sandbox: runner error` " +
            "or `CreateProcessAsUserW failed`, do NOT retry; the host sandbox is " +
            "refusing execution. Read files directly instead, and if you cannot make " +
            "progress without shell access, stop and reply with " +
            "`[[TASK_BLOCKED:windows-sandbox]]`.";

        return sentinelLine + "\n" + investigationLine + "\n" + windowsShellLine + "\n\n";
    }

    /// <summary>
    /// Bridge to <see cref="CodexEventAdapter"/>. Each raw stdout line is
    /// passed through and emitted on <see cref="GenericCliExecutionService.OnRunEvent"/>.
    /// <para>
    /// We also opportunistically parse <c>turn.completed</c> frames here so
    /// the captured <see cref="ParsedTurnUsage"/> lands on <c>ProcInfo</c>
    /// <b>before</b> the typed <c>TurnCompleted</c> event is raised. Order
    /// matters: <see cref="GenericCliExecutionService"/> runs
    /// <c>MapLineToRunEvents</c> first, raises the typed events, and
    /// only then fires <c>OnOutputLine</c>. Doing the usage capture
    /// downstream of the event raise races the runner's subscriber, which
    /// immediately calls back into <see cref="GenericCliExecutionService.GetLastParsedTurnUsage"/> to
    /// mirror the spend onto the bus.
    /// </para>
    /// </summary>
    private static IEnumerable<CliRunEvent> CodexMapLineToRunEvents(
        GenericCliExecutionService ctx,
        CliUsageParserRegistry usageParsers,
        ICliModelRegistry modelRegistry,
        string jobKey,
        CliOutputLine line)
    {
        if (line.Stream != "stdout") return Array.Empty<CliRunEvent>();

        return MapCodexFrame(line.Text, jobKey);
    }

    private static void CodexCaptureRawLine(
        GenericCliExecutionService ctx,
        CliUsageParserRegistry usageParsers,
        ICliModelRegistry modelRegistry,
        string jobKey,
        CliOutputLine line)
    {
        if (line.Stream != "stdout" || !ctx.TryGetProc(jobKey, out var info)) return;
        CodexTryCaptureTurnUsage(ctx, usageParsers, modelRegistry, info, line);
        CodexTryCaptureSessionId(ctx, info, line);
        CodexTryCaptureCommandExecution(info, line);
    }

    /// <summary>
    /// Map one raw Codex frame and preserve the command outcome carried by
    /// <c>item.completed.command_execution.exit_code</c>. CodingAgentRunner
    /// 0.5 classifies the item as a completed tool but does not project that
    /// field onto <see cref="CliRunEvent.ToolCompleted.IsError"/>. Normalizing
    /// it here keeps the typed event contract aligned with the renderer and
    /// prevents a real exit 1 from looking like a successful or missing result.
    /// </summary>
    internal static IEnumerable<CliRunEvent> MapCodexFrame(string text, string jobKey)
    {
        var events = CodexEventAdapter.Map(text, jobKey).ToList();
        var todoList = TryExtractCodexTodoList(text);
        if (todoList is not null && events.All(evt => evt is not CliRunEvent.PlanUpdated))
        {
            // CAR 0.7 predates Codex's item.updated/todo_list family. Keep the
            // package adapter authoritative for every frame it knows, but
            // bridge this observed additive family until the exact package pin
            // advances. Unknown is diagnostic fallback, not a second semantic
            // event once we have parsed the frame deliberately.
            events.RemoveAll(evt => evt is CliRunEvent.Unknown);
            events.Add(new CliRunEvent.PlanUpdated("codex/todo_list", todoList));
        }
        var command = TryExtractCommandExecution(text);
        if (command?.ExitCode is not int exitCode || exitCode == 0) return events;

        return events.Select(evt => evt is CliRunEvent.ToolCompleted completed
            ? new CliRunEvent.ToolCompleted(completed.ToolName, IsError: true, completed.FirstLine)
            : evt);
    }

    /// <summary>
    /// Parse Codex's live <c>todo_list</c> item family. The wire only carries a
    /// completed boolean, so the first incomplete item is active while the item
    /// is started or updated; an <c>item.completed</c> frame leaves incomplete
    /// entries pending because the turn itself is no longer working that step.
    /// </summary>
    internal static IReadOnlyList<PlanFrameItem>? TryExtractCodexTodoList(string? line)
    {
        var text = line?.TrimStart();
        if (string.IsNullOrEmpty(text) || text![0] != '{') return null;
        if (!text.Contains("todo_list", StringComparison.Ordinal)) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var frameType = root.TryGetProperty("type", out var type) ? type.GetString() : null;
            if (frameType is not ("item.started" or "item.updated" or "item.completed")) return null;
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
            if (!item.TryGetProperty("type", out var itemType)
                || !string.Equals(itemType.GetString(), "todo_list", StringComparison.Ordinal)) return null;
            if (!item.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;

            var projected = new List<PlanFrameItem>();
            var activeAssigned = string.Equals(frameType, "item.completed", StringComparison.Ordinal);
            foreach (var entry in items.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var title = entry.TryGetProperty("text", out var titleValue)
                    && titleValue.ValueKind == JsonValueKind.String
                    ? titleValue.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(title)) continue;
                var completed = entry.TryGetProperty("completed", out var completedValue)
                    && completedValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && completedValue.GetBoolean();
                var status = completed ? "done" : activeAssigned ? "pending" : "active";
                if (!completed) activeAssigned = true;
                projected.Add(new PlanFrameItem(PlanItemId.From(title), title, status));
            }
            return projected.Count == 0 ? null : projected;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pre-parse <c>item.completed</c> frames whose nested item is a
    /// <c>command_execution</c> and stash the trigger data on
    /// <see cref="GenericCliExecutionService.ProcInfo"/>. The runner reads this via
    /// <see cref="GenericCliExecutionService.GetLastCommandExecution"/> to feed
    /// <see cref="CodexSilentCompletionDetector"/>.
    /// <para>
    /// Best-effort: a malformed frame leaves the previous snapshot
    /// untouched. Fast prefilter keeps the hot path cheap - most stdout
    /// lines never reach <see cref="JsonDocument.Parse"/>.
    /// </para>
    /// </summary>
    private static void CodexTryCaptureCommandExecution(GenericCliExecutionService.ProcInfo info, CliOutputLine line)
    {
        var parsed = TryExtractCommandExecution(line.Text);
        if (parsed is not { } cap) return;

        info.LastCommandExitCode = cap.ExitCode;
        info.LastCommandLine = cap.Command;
        info.LastCommandOutputTail = cap.OutputTail;
        info.LastCommandObservedAt = line.Timestamp == default ? DateTime.UtcNow : line.Timestamp;
    }

    /// <summary>
    /// Pure JSON parser for the silent-completion capture path. Exposed
    /// <c>internal</c> so the regression test for the Codex 0.128
    /// <c>command_execution</c> frame shape can drive it without spinning
    /// up a live CLI. Returns <c>null</c> for any non-matching line shape
    /// (other frame type, missing <c>item</c>, malformed JSON, non-JSON
    /// text) so the caller's hot path stays cheap and a malformed frame
    /// never throws.
    /// </summary>
    internal static (int? ExitCode, string? Command, string? OutputTail)? TryExtractCommandExecution(string? line)
    {
        var text = line?.TrimStart();
        if (string.IsNullOrEmpty(text) || text![0] != '{') return null;
        if (!text.Contains("item.completed", StringComparison.Ordinal)) return null;
        if (!text.Contains("command_execution", StringComparison.Ordinal)) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "item.completed", StringComparison.Ordinal)) return null;
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
            var itemType = item.TryGetProperty("type", out var ity) ? ity.GetString() : null;
            if (!string.Equals(itemType, "command_execution", StringComparison.Ordinal)) return null;

            int? exitCode = null;
            if (item.TryGetProperty("exit_code", out var ec) && ec.TryGetInt32(out var ecValue))
                exitCode = ecValue;

            string? command = null;
            if (item.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
                command = cmd.GetString();

            string? outputTail = null;
            if (item.TryGetProperty("aggregated_output", out var agg) && agg.ValueKind == JsonValueKind.String)
            {
                var raw = agg.GetString() ?? string.Empty;
                outputTail = raw.Length <= 400 ? raw : raw[^400..];
            }

            return (exitCode, command, outputTail);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Codex emits the session UUID on the first <c>{"type":"thread.started",
    /// "thread_id":"&lt;uuid&gt;"}</c> line of <c>--json</c> output (codex-cli
    /// &gt;= 0.128). Older builds used <c>{"type":"session_meta","payload":{"id":"&lt;uuid&gt;"}}</c>
    /// which we still accept. Without this capture the per-job session store
    /// stays empty and every follow-up rebuilds context from disk via Recovery
    /// instead of <c>codex exec resume &lt;uuid&gt;</c>, throwing away Codex's
    /// own prompt-cache.
    /// <para>
    /// This runs in <c>MapLineToRunEvents</c> on the RAW stdout line, not
    /// in <c>OnOutputLine</c>. <c>OnOutputLine</c> now receives the rendered
    /// <c>● Session &lt;id&gt;</c> marker (see <see cref="CodexOutputRenderer"/>),
    /// from which the original <c>thread_id</c> payload is no longer recoverable;
    /// capturing here keeps <see cref="TryExtractSessionId"/> reading the real
    /// JSON frame.
    /// </para>
    /// </summary>
    private static void CodexTryCaptureSessionId(GenericCliExecutionService ctx, GenericCliExecutionService.ProcInfo info, CliOutputLine line)
    {
        if (info.CapturedSessionId != null) return;

        var id = TryExtractSessionId(line.Text);
        if (id == null) return;

        info.CapturedSessionId = id;
        info.SessionName ??= id;
        ctx.Logger.LogInformation("Captured Codex session id {Id}", id);
    }

    private static readonly CodexOutputRenderer _codexRenderer = new();

    /// <summary>
    /// Parse a <c>turn.completed</c> frame's <c>usage</c> block via the
    /// shared <see cref="CodexUsageParser"/> and stash the parsed snapshot on
    /// <see cref="GenericCliExecutionService.ProcInfo.LastParsedUsage"/>. The runner consumes the stash
    /// when the matching <c>TurnCompleted</c> typed event arrives and mirrors
    /// it onto the agent message bus as <c>kind:token-usage</c>. Without this,
    /// the Codex coding-agent's own per-turn spend is invisible to
    /// <c>BusAggregationCache</c>, the project token summary, and the workspace
    /// quota strip. Best-effort: a malformed frame or parser miss leaves the
    /// previous snapshot untouched.
    /// </summary>
    private static void CodexTryCaptureTurnUsage(
        GenericCliExecutionService ctx,
        CliUsageParserRegistry usageParsers,
        ICliModelRegistry modelRegistry,
        GenericCliExecutionService.ProcInfo info,
        CliOutputLine line)
    {
        var text = line.Text?.TrimStart();
        if (string.IsNullOrEmpty(text) || text![0] != '{') return;
        // Fast prefilter: only attempt JSON parsing for frames we care about.
        if (!text.Contains("turn.completed", StringComparison.Ordinal)) return;

        var parser = usageParsers.Get(CliTypes.Codex);
        if (parser == null) return;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var modelHint = info.Execution.Model;
            if (!parser.TryParse(doc.RootElement, modelHint, modelRegistry, out var usage)) return;

            info.LastParsedUsage = usage;
            info.LastParsedUsages = [usage];
            info.LastParsedUsageAt = line.Timestamp == default ? DateTime.UtcNow : line.Timestamp;
        }
        catch (JsonException __ex) { SilentCatch.Note(__ex, "BuiltInCliBehaviors.Codex: malformed frame; nothing to capture"); /* malformed frame; nothing to capture */ }
        catch (Exception ex) { ctx.Logger.LogDebug(ex, "Codex turn-usage capture skipped"); }
    }

    private static ParsedTurnUsage AggregateUsage(
        IReadOnlyList<ParsedTurnUsage> usages,
        string? modelHint,
        ICliModelRegistry modelRegistry)
    {
        var models = usages.Select(usage => usage.Model)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var model = models.Length == 1 ? models[0] : null;
        var input = usages.Sum(usage => usage.Input);
        var cacheRead = usages.Sum(usage => usage.CacheRead);
        var totalContext = modelRegistry.TotalContextSize(model);
        return new ParsedTurnUsage(
            model,
            input,
            usages.Sum(usage => usage.Output),
            cacheRead,
            usages.Sum(usage => usage.CacheWrite),
            usages.Sum(usage => usage.ReasoningOutput ?? 0),
            new AgentMessageContextWindow(
                totalContext,
                input + cacheRead,
                totalContext is { } total ? Math.Max(0, total - input - cacheRead) : null),
            usages.Any(usage => usage.InputIncludesCached),
            modelHint,
            usages.Any(usage => usage.ModelMismatch));
    }

    /// <summary>
    /// Parses a single <c>codex exec --experimental-json</c> stdout line and returns the
    /// session UUID iff the line is a <c>thread.started</c> (preferred) or
    /// legacy <c>session_meta</c> frame carrying a canonical UUID. Returns
    /// <c>null</c> for every other line shape (other frame types, malformed
    /// JSON, non-JSON text, non-UUID ids). Exposed <c>internal</c> so the
    /// regression test for the codex-cli 0.128 capture path can drive it
    /// without spinning up a real CLI process.
    /// </summary>
    internal static string? TryExtractSessionId(string? line)
    {
        var text = line?.TrimStart();
        if (string.IsNullOrEmpty(text) || text[0] != '{') return null;

        // Fast prefilter: only attempt JSON parsing for frame types we care about.
        var hasThreadStarted = text.Contains("thread.started", StringComparison.Ordinal);
        var hasSessionMeta = text.Contains("session_meta", StringComparison.Ordinal);
        if (!hasThreadStarted && !hasSessionMeta) return null;

        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (string.Equals(type, "thread.started", StringComparison.Ordinal)
                && root.TryGetProperty("thread_id", out var tid)
                && tid.ValueKind == JsonValueKind.String)
            {
                id = tid.GetString();
            }
            else if (string.Equals(type, "session_meta", StringComparison.Ordinal))
            {
                // Legacy: id may live at payload.id or at session_id on root.
                if (root.TryGetProperty("payload", out var payload)
                    && payload.TryGetProperty("id", out var pid)
                    && pid.ValueKind == JsonValueKind.String)
                {
                    id = pid.GetString();
                }
                else if (root.TryGetProperty("session_id", out var sid)
                    && sid.ValueKind == JsonValueKind.String)
                {
                    id = sid.GetString();
                }
            }
        }
        catch { return null; }

        return !string.IsNullOrWhiteSpace(id) && CodexUuidRegex.IsMatch(id) ? id : null;
    }

    // ════════════════════════════════════════════════════════════════════
    // Antigravity / Gemini
    // ════════════════════════════════════════════════════════════════════

    internal static CliBehavior Antigravity() => new CliBehavior
    {
        CliType = CliTypes.Gemini,
        GetCliPath = ctx => ctx.CliPathOverride
                            ?? ctx.Configuration["AntigravityCli:Path"]
                            ?? ctx.Configuration["GeminiCli:Path"]
                            ?? "agentapi",
        IsCompatibleSessionName = (ctx, sessionName)
            => !string.IsNullOrWhiteSpace(sessionName) && GeminiUuidRegex.IsMatch(sessionName),
        MapLineToRunEvents = (ctx, jobKey, line) =>
        {
            if (line.Stream != "stdout") return Array.Empty<CliRunEvent>();
            return GeminiEventAdapter.Map(line.Text, jobKey);
        },
        OnOutputLine = (ctx, info, line) => GeminiCaptureSessionId(ctx, info, line),
        TransformReadLine = (ctx, raw) => GeminiRenderLine(raw),
        TestCliPath = (ctx, path) => GeminiProbeCliPath(ctx, path),
        GetModelCatalog = (ctx, force, ct) => GeminiGetModelCatalog(),
    };

    private static readonly Regex GeminiUuidRegex =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

    private static readonly Regex GeminiSessionInitRegex = new(
        @"●\s*Session init\s+(?<uuid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled);

    private static void GeminiCaptureSessionId(GenericCliExecutionService ctx, GenericCliExecutionService.ProcInfo info, CliOutputLine line)
    {
        if (info.CapturedSessionId != null) return;
        if (line.Text == null) return;
        var m = GeminiSessionInitRegex.Match(line.Text);
        if (!m.Success) return;

        info.CapturedSessionId = m.Groups["uuid"].Value;
        info.SessionName ??= info.CapturedSessionId;
        ctx.Logger.LogInformation("Captured Antigravity session id {Id}", info.CapturedSessionId);
    }

    private static IEnumerable<CliOutputLine> GeminiRenderLine(CliOutputLine raw)
    {
        if (raw.Stream != "stdout" || string.IsNullOrWhiteSpace(raw.Text) || raw.Text[0] != '{')
        {
            yield return raw;
            yield break;
        }

        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(raw.Text); } catch (Exception __ex) { SilentCatch.Note(__ex, "BuiltInCliBehaviors.Antigravity:render"); }
        if (doc == null) { yield return raw; yield break; }

        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { yield return raw; yield break; }

        var cid = GeminiTryFindConversationId(root);
        if (!string.IsNullOrEmpty(cid))
        {
            yield return raw with { Text = $"● Session init {cid} (gemini-3)".TrimEnd() };
        }

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type != null)
        {
            switch (type)
            {
                case "message":
                    var role = root.TryGetProperty("role", out var r) ? r.GetString() : null;
                    if (role != "user")
                    {
                        var content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(content))
                        {
                            foreach (var line in GeminiSplitLines(content))
                                yield return raw with { Text = line };
                        }
                    }
                    yield break;
                case "tool_call":
                case "tool_use":
                    var name = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "Tool"
                             : root.TryGetProperty("name",      out var n)  ? n.GetString()  ?? "Tool"
                             : "Tool";
                    var args = root.TryGetProperty("parameters", out var p) ? p
                             : root.TryGetProperty("input",      out var i) ? i
                             : root.TryGetProperty("args",       out var a) ? a : default;
                    yield return raw with { Text = GeminiFormatToolUse(name, args) };
                    yield break;
                case "tool_result":
                    var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
                    if (status != null && status != "success")
                        yield return raw with { Text = $"  tool_result: {status}" };
                    yield break;
                case "result":
                    var resStatus = root.TryGetProperty("status", out var rest) ? rest.GetString() : "result";
                    yield return raw with { Text = $"● Result {resStatus}" };
                    yield break;
            }
        }

        if (root.TryGetProperty("response", out var resp))
        {
            var text = resp.TryGetProperty("text", out var tx) ? tx.GetString() : null;
            if (text == null && resp.TryGetProperty("content", out var cn)) text = cn.GetString();
            if (text != null)
            {
                foreach (var line in GeminiSplitLines(text))
                    yield return raw with { Text = line };
                yield break;
            }
        }

        yield return raw;
    }

    private static string? GeminiTryFindConversationId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    if (prop.Name == "conversationId" || prop.Name == "conversation_id")
                    {
                        return prop.Value.GetString();
                    }
                    if (prop.Name == "id" && (element.TryGetProperty("conversationMetadata", out _) || element.TryGetProperty("metadata", out _)))
                    {
                        return prop.Value.GetString();
                    }
                }
                var sub = GeminiTryFindConversationId(prop.Value);
                if (sub != null) return sub;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var subEl in element.EnumerateArray())
            {
                var sub = GeminiTryFindConversationId(subEl);
                if (sub != null) return sub;
            }
        }
        return null;
    }

    private static string GeminiFormatToolUse(string name, JsonElement input)
    {
        string Get(string key) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v) ? v.ToString() : "";

        return name switch
        {
            "read_file"     or "ReadFile"     => $"● Read {Get("absolute_path")}{Get("path")}".TrimEnd(),
            "write_file"    or "WriteFile"    => $"● Write {Get("absolute_path")}{Get("path")}".TrimEnd(),
            "edit"          or "Edit"
                            or "replace"      => $"● Edit {Get("file_path")}{Get("path")}".TrimEnd(),
            "glob"          or "Glob"         => $"● Search glob {Get("pattern")}".TrimEnd(),
            "search_file_content"
                            or "Grep"         => $"● Search {Get("pattern")}".TrimEnd(),
            "run_shell_command"
                            or "Shell"
                            or "Bash"         => $"● Run {GeminiTrimSingleLine(Get("command"))}".TrimEnd(),
            "web_fetch"     or "WebFetch"     => $"● Fetch {Get("url")}".TrimEnd(),
            "google_web_search"
                            or "WebSearch"    => $"● Search web {Get("query")}".TrimEnd(),
            _                                  => $"● {name}"
        };
    }

    private static IEnumerable<string> GeminiSplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            yield return line;
    }

    private static string GeminiTrimSingleLine(string s) =>
        s.Replace('\n', ' ').Replace('\r', ' ').Trim() is { } t && t.Length > 200 ? t[..200] + "…" : s.Trim();

    private static (bool Available, string? Version, string Path) GeminiProbeCliPath(GenericCliExecutionService ctx, string? path)
    {
        var testPath = GenericCliExecutionService.ResolveExecutable(path?.Trim() ?? ctx.GetCliPath());
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = testPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            var rawOutput = proc.StandardOutput.ReadToEnd().Trim();
            var rawError = proc.StandardError.ReadToEnd().Trim();
            proc.WaitForExit(5000);

            var isAvailable = proc.ExitCode == 0
                || rawOutput.Contains("unknown command: --version")
                || rawError.Contains("unknown command: --version")
                || rawOutput.Contains("Usage: agentapi");
            return (isAvailable, "1.0.0", testPath);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Antigravity CLI not available at path '{Path}'", testPath);
            return (false, null, testPath);
        }
    }

    private static Task<CliModelCatalog> GeminiGetModelCatalog()
    {
        var models = new List<CliModelInfo>
        {
            new() { Id = "flash",      Label = "Gemini Flash (Default)", Vendor = "google", IsDefault = true },
            new() { Id = "pro",        Label = "Gemini Pro",             Vendor = "google" },
            new() { Id = "flash_lite", Label = "Gemini Flash-Lite",      Vendor = "google" }
        };
        return Task.FromResult(new CliModelCatalog
        {
            Models = models,
            Source = "hardcoded",
            FetchedAt = DateTime.UtcNow
        });
    }
}
