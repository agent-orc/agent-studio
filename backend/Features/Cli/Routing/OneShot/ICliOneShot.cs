

namespace AgentStudio.Cli;

/// <summary>
/// Single point of code for one-shot CLI calls (Claude/Codex/Gemini in
/// <c>-p</c> / <c>exec</c> / one-prompt-one-response mode).
/// </summary>
/// <remarks>
/// <para>
/// Replaces eight independent <c>DefaultRunCliAsync</c> helpers scattered
/// across the runner / supervisor / ad-hoc services. Three of those eight
/// historically passed the prompt as a positional argv argument
/// (<c>-p &lt;multi-KB prompt&gt;</c>), which fails silently on Windows
/// shims for prompts over ~8 KB and was the root cause of the 2026-05-11
/// "every aspect verdict comes back Concerns" incident. The OneShot
/// service exposes one contract that enforces stdin-piped prompts,
/// captures latency, parses token + context-window data via
/// <see cref="ICliUsageParser"/>, and surfaces stderr + non-zero exit
/// codes that the legacy helpers ignored.
/// </para>
/// <para>
/// Stateless and thread-safe; safe to share across the process. Per-CLI
/// implementations live alongside this file and are dispatched via
/// <see cref="CliOneShotRegistry"/>.
/// </para>
/// </remarks>
public interface ICliOneShot
{
    /// <summary>CLI identifier this implementation handles (lowercase).</summary>
    string CliType { get; }

    /// <summary>
    /// Run one prompt and return the full result envelope. Never throws on
    /// CLI-level failures - those land as <c>Ok=false</c> with a populated
    /// <see cref="CliOneShotResult.Error"/>. Process-spawn failures or
    /// cancellation surface the same way so call sites can treat all
    /// failure modes uniformly.
    /// </summary>
    Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default);
}

/// <summary>One CLI invocation request. All optional fields fall back to
/// project / configuration defaults; the only required fields are the
/// CLI type, model, and prompt.</summary>
public sealed record CliOneShotRequest(
    string CliType,
    string Model,
    string Prompt)
{
    /// <summary>Optional thinking/reasoning level for CLIs and models that
    /// support one. Unsupported combinations are normalized away by the
    /// concrete runner.</summary>
    public string? ThinkingLevel { get; init; }

    /// <summary>Working directory the child process runs in. Defaults to
    /// the backend's CWD when null - usually the workspace root.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Wall-clock cap. The child is killed when this elapses.
    /// Defaults to two minutes when null.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Tag for <see cref="AdHoc.AdHocUsageRecorder"/> so the
    /// per-source usage chart can attribute spend.</summary>
    public string? Source { get; init; }

    /// <summary>Project slug for usage attribution. Optional.</summary>
    public string? Project { get; init; }

    /// <summary>Job slug for usage attribution. Optional.</summary>
    public string? JobId { get; init; }

    /// <summary>When false, the OneShot does not record this call into
    /// <see cref="AdHoc.AdHocUsageRecorder"/>. Used by call sites that
    /// own their own bookkeeping (e.g. ProjectRunner).</summary>
    public bool RecordUsage { get; init; } = true;

    /// <summary>Extra argv tokens appended after the standard
    /// <c>-p / --output-format / --model / --dangerously-skip-permissions</c>
    /// args. Use for resume flags like <c>-r &lt;sessionId&gt;</c>. Tokens are
    /// passed through ProcessStartInfo.ArgumentList so no manual quoting
    /// is required.</summary>
    public IReadOnlyList<string>? ExtraArgs { get; init; }

    /// <summary>
    /// Multimodal fast path. When non-empty, the one-shot driver switches
    /// to <c>--input-format stream-json</c> and writes a single user
    /// message envelope to stdin containing <see cref="Prompt"/> as a text
    /// content block followed by one image content block per entry. The
    /// model sees the images in the same turn as the text - no Read tool
    /// call required. Claude uses stream JSON; Codex materializes bounded
    /// temporary files and supplies repeated <c>--image</c> arguments.
    /// </summary>
    public IReadOnlyList<CliOneShotImage>? InlineImages { get; init; }

    /// <summary>
    /// Absolute path to the task's job folder. When set together with
    /// <see cref="StepId"/>, the central dispatch decorator
    /// (<see cref="PromptLoggingCliOneShot"/>) records the final
    /// <see cref="Prompt"/> raw into <c>.metadata/prompts.jsonl</c> in this
    /// folder before the call runs - the "Rohdaten" capture for step-call
    /// prompts that otherwise land in no raw file at the task. Leave null for
    /// the main run and follow-ups (already logged in the task's
    /// <c>prompt.md</c> / chat) so the prompt is not double-booked.
    /// </summary>
    public string? JobFolderPath { get; init; }

    /// <summary>
    /// Pipeline step id this call belongs to, e.g.
    /// <c>aspect-requirement-fit</c> or <c>post-code-review-grade</c>. Keys
    /// the recorded prompt to the matching step so the UI can show it next to
    /// the step / timeline entry. Required (with <see cref="JobFolderPath"/>)
    /// for prompt logging to fire.
    /// </summary>
    public string? StepId { get; init; }

    /// <summary>
    /// Runtime prompt template the final prompt was rendered from, e.g.
    /// <c>review-aspect-requirement-fit.md</c>. Recorded as provenance
    /// alongside the prompt. Null when the prompt is built inline.
    /// </summary>
    public string? TemplateRef { get; init; }
}

/// <summary>
/// One image attached to a one-shot CLI call. <see cref="Base64"/> is the
/// raw image bytes encoded without any data-URL prefix; <see cref="MediaType"/>
/// is the MIME type the Claude SDK expects in the
/// <c>{"type":"image","source":{...}}</c> block (e.g. <c>image/png</c>,
/// <c>image/jpeg</c>, <c>image/gif</c>, <c>image/webp</c>).
/// </summary>
public sealed record CliOneShotImage(string Base64, string MediaType);

/// <summary>Full result envelope. All fields are populated even on
/// failure so call sites can log a consistent shape.</summary>
public sealed record CliOneShotResult(
    bool Ok,
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    string ParsedText,
    OrchestratorTokenUsage? Usage,
    ParsedTurnUsage? RichUsage,
    AgentMessageLatency Latency,
    string? Error)
{
    /// <summary>Quota decision that produced the effective one-shot route.</summary>
    public QuotaAdmissionPlan? QuotaAdmission { get; init; }
    public string? EffectiveCliType { get; init; }
    public string? EffectiveModel { get; init; }
    public string? EffectiveThinkingLevel { get; init; }
    public bool QuotaDeferred { get; init; }

    /// <summary>Convenience: zero-token empty failure used when a child
    /// could not be started at all.</summary>
    public static CliOneShotResult SpawnFailure(string error, DateTime requestedAt, DateTime completedAt) => new(
        Ok: false,
        ExitCode: -1,
        Stdout: string.Empty,
        Stderr: string.Empty,
        Duration: completedAt - requestedAt,
        ParsedText: string.Empty,
        Usage: null,
        RichUsage: null,
        Latency: new AgentMessageLatency(
            RequestedAt: requestedAt,
            CompletedAt: completedAt,
            TotalMs: (long)(completedAt - requestedAt).TotalMilliseconds),
        Error: error);
}

/// <summary>Dispatcher that picks the right one-shot implementation by
/// CLI type. Singleton; the underlying implementations are stateless.</summary>
public sealed class CliOneShotRegistry
{
    private readonly Dictionary<string, ICliOneShot> _byCli;
    private readonly Dictionary<string, ICliOneShot> _quotaAware = new(StringComparer.OrdinalIgnoreCase);
    private readonly QuotaAdmissionService? _quotaAdmission;
    private readonly QuotaAdmissionRecorder? _quotaRecorder;
    private readonly object _dispatcherLock = new();

    public CliOneShotRegistry(
        IEnumerable<ICliOneShot> implementations,
        QuotaAdmissionService? quotaAdmission = null,
        QuotaAdmissionRecorder? quotaRecorder = null)
    {
        _byCli = implementations.ToDictionary(i => i.CliType, StringComparer.OrdinalIgnoreCase);
        _quotaAdmission = quotaAdmission;
        _quotaRecorder = quotaRecorder;
    }

    /// <summary>Returns the implementation for the given CLI type, or null
    /// when no implementation is registered. Caller decides whether to
    /// throw or fall back.</summary>
    public ICliOneShot? Get(string? cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;
        if (!_byCli.ContainsKey(cliType)) return null;
        if (_quotaAdmission is null) return _byCli[cliType];
        lock (_dispatcherLock)
        {
            if (!_quotaAware.TryGetValue(cliType, out var dispatcher))
            {
                dispatcher = new QuotaAwareOneShot(this, cliType);
                _quotaAware[cliType] = dispatcher;
            }
            return dispatcher;
        }
    }

    /// <summary>Convenience: get the implementation or throw a clear
    /// error. Use at call sites that cannot meaningfully recover from a
    /// missing implementation.</summary>
    public ICliOneShot Require(string cliType)
    {
        var impl = Get(cliType);
        if (impl == null) throw new InvalidOperationException($"No ICliOneShot registered for CLI '{cliType}'");
        return impl;
    }

    private async Task<CliOneShotResult> DispatchAsync(
        CliOneShotRequest request,
        CancellationToken ct)
    {
        if (_quotaAdmission is null)
            return await _byCli[request.CliType].RunAsync(request, ct).ConfigureAwait(false);

        var executionPath = request.StepId
                            ?? request.Source
                            ?? "one-shot";
        var plan = _quotaAdmission.Plan(new QuotaAdmissionRequest(
            request.CliType,
            request.Model,
            request.ThinkingLevel,
            request.Project,
            OccupiedSlots: 0,
            QuotaExpectedCostClassifier.For(
                taskType: null,
                taskMode: null,
                model: request.Model,
                thinkingLevel: request.ThinkingLevel,
                executionPath: executionPath),
            executionPath));

        var task = FindTask(request);
        if (!plan.ShouldLaunch)
        {
            if (task is not null) _quotaRecorder?.Record(task, plan);
            else RecordProjectDecision(request, executionPath, plan);
            var now = DateTime.UtcNow;
            return CliOneShotResult.SpawnFailure(
                    $"[quota-deferred] {plan.Reason}", now, now)
                with
                {
                    QuotaAdmission = plan,
                    EffectiveCliType = plan.CliType,
                    EffectiveModel = plan.Model,
                    EffectiveThinkingLevel = plan.ThinkingLevel,
                    QuotaDeferred = true,
                };
        }

        if (!_byCli.TryGetValue(plan.CliType, out var implementation))
        {
            var now = DateTime.UtcNow;
            return CliOneShotResult.SpawnFailure(
                    $"Quota selected CLI '{plan.CliType}', but no one-shot implementation is registered.", now, now)
                with
                {
                    QuotaAdmission = plan,
                    EffectiveCliType = plan.CliType,
                    EffectiveModel = plan.Model,
                    EffectiveThinkingLevel = plan.ThinkingLevel,
                };
        }

        if (task is not null)
            _quotaRecorder?.Record(task, plan, committedLaunch: plan.IsFallback);
        else if (plan.IsFallback)
            RecordProjectDecision(request, executionPath, plan);
        var effectiveRequest = request with
        {
            CliType = plan.CliType,
            Model = plan.Model ?? request.Model,
            ThinkingLevel = plan.ThinkingLevel,
            // CLI-native session ids cannot cross provider families.
            ExtraArgs = string.Equals(plan.CliType, request.CliType, StringComparison.OrdinalIgnoreCase)
                ? request.ExtraArgs
                : null,
        };
        var result = await implementation.RunAsync(effectiveRequest, ct).ConfigureAwait(false);
        return result with
        {
            QuotaAdmission = plan,
            EffectiveCliType = effectiveRequest.CliType,
            EffectiveModel = effectiveRequest.Model,
            EffectiveThinkingLevel = effectiveRequest.ThinkingLevel,
        };
    }

    private TaskInfo? FindTask(CliOneShotRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobFolderPath)) return null;
        var lane = Directory.GetParent(request.JobFolderPath);
        var watch = lane?.Parent?.FullName;
        if (string.IsNullOrWhiteSpace(watch)) return null;
        return new TaskInfo
        {
            Id = string.IsNullOrWhiteSpace(request.JobId)
                ? Path.GetFileName(request.JobFolderPath)
                : request.JobId,
            ProjectName = request.Project ?? Path.GetFileName(watch),
            FolderPath = request.JobFolderPath,
            WatchPath = watch,
        };
    }

    private void RecordProjectDecision(
        CliOneShotRequest request,
        string executionPath,
        QuotaAdmissionPlan plan)
    {
        if (string.IsNullOrWhiteSpace(request.WorkingDirectory)) return;
        _quotaRecorder?.RecordProject(
            request.WorkingDirectory,
            request.Project,
            executionPath,
            plan);
    }

    private sealed class QuotaAwareOneShot : ICliOneShot
    {
        private readonly CliOneShotRegistry _owner;

        public QuotaAwareOneShot(CliOneShotRegistry owner, string cliType)
        {
            _owner = owner;
            CliType = cliType;
        }

        public string CliType { get; }

        public Task<CliOneShotResult> RunAsync(
            CliOneShotRequest request,
            CancellationToken ct = default)
            => _owner.DispatchAsync(request, ct);
    }
}

/// <summary>Compatibility projection for legacy callers that parse Claude result JSON.</summary>
public static class CliOneShotCompatibility
{
    public static string ToClaudeResultEnvelope(CliOneShotResult result, string? configuredModel)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "result",
            result = result.ParsedText,
            usage = result.Usage is null ? null : new
            {
                input_tokens = result.Usage.InputTokens,
                output_tokens = result.Usage.OutputTokens,
                cache_read_input_tokens = result.Usage.CacheReadTokens,
                cache_creation_input_tokens = result.Usage.CacheCreationTokens,
            },
            model = result.Usage?.Model ?? result.EffectiveModel ?? configuredModel,
        });
}
