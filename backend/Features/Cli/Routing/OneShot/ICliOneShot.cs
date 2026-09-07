

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
    /// <summary>Attempt-local route selected by quota admission.</summary>
    public string? EffectiveCliType { get; init; }
    public string? EffectiveModel { get; init; }
    public string? EffectiveThinkingLevel { get; init; }
    public QuotaAdmissionPlan? QuotaAdmission { get; init; }

    /// <summary>
    /// Compatibility envelope for older consumers that still pass one-shot
    /// output through the Claude JSON parser. The payload is built from the
    /// provider-neutral parsed result, so a cross-family fallback never leaks
    /// Codex JSONL into a Claude-only parser.
    /// </summary>
    public string ToLegacyResultEnvelope(string fallbackModel) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "result",
            result = ParsedText,
            usage = Usage is null ? null : new
            {
                input_tokens = Usage.InputTokens,
                output_tokens = Usage.OutputTokens,
                cache_read_input_tokens = Usage.CacheReadTokens,
                cache_creation_input_tokens = Usage.CacheCreationTokens,
            },
            model = Usage?.Model ?? EffectiveModel ?? fallbackModel,
            cliType = EffectiveCliType,
        });

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
    private readonly QuotaService? _quota;
    private readonly CliQuotaCapsService? _caps;
    private readonly CliQuotaFallbackService? _fallback;
    private readonly CliQuotaWaitPolicyService? _waitPolicy;
    private readonly AgentStudio.Tasks.TimelineLog? _timeline;
    private readonly AgentStudio.Runner.OrchestratorLog? _orchestratorLog;
    private readonly ILogger<CliOneShotRegistry>? _logger;

    public CliOneShotRegistry(
        IEnumerable<ICliOneShot> implementations,
        QuotaService? quota = null,
        CliQuotaCapsService? caps = null,
        CliQuotaFallbackService? fallback = null,
        CliQuotaWaitPolicyService? waitPolicy = null,
        AgentStudio.Tasks.TimelineLog? timeline = null,
        AgentStudio.Runner.OrchestratorLog? orchestratorLog = null,
        ILogger<CliOneShotRegistry>? logger = null)
    {
        _byCli = implementations.ToDictionary(i => i.CliType, StringComparer.OrdinalIgnoreCase);
        _quota = quota;
        _caps = caps;
        _fallback = fallback;
        _waitPolicy = waitPolicy;
        _timeline = timeline;
        _orchestratorLog = orchestratorLog;
        _logger = logger;
    }

    /// <summary>Returns the implementation for the given CLI type, or null
    /// when no implementation is registered. Caller decides whether to
    /// throw or fall back.</summary>
    public ICliOneShot? Get(string? cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;
        if (!_byCli.TryGetValue(cliType, out var implementation)) return null;
        if (_quota is null || _caps is null || _fallback is null) return implementation;
        return new QuotaAwareCliOneShot(this, implementation.CliType);
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

    private async Task<CliOneShotResult> RunQuotaAwareAsync(
        string requestedCli,
        CliOneShotRequest request,
        CancellationToken ct)
    {
        var path = request.Source?.Trim().ToLowerInvariant() switch
        {
            "review-decision" or "code-review-step" or "post-abort-review-step" =>
                QuotaExecutionPath.ReviewAspect,
            "orchestrator-chat" or "orchestrator-decision" =>
                QuotaExecutionPath.OrchestratorChat,
            _ when !string.IsNullOrWhiteSpace(request.StepId) =>
                QuotaExecutionPath.PipelineStep,
            _ => QuotaExecutionPath.OrchestratorChat,
        };
        var context = QuotaAdmissionContext.ForTask(path, taskType: null, request.ThinkingLevel);
        var plan = QuotaAdmissionPlanner.Plan(
            requestedCli,
            request.Model,
            request.ThinkingLevel,
            _fallback,
            _caps!,
            cli => string.IsNullOrWhiteSpace(cli) ? null : _quota!.GetCachedFor(cli),
            DateTime.UtcNow,
            occupiedSlots: 0,
            _waitPolicy?.Resolve(project: null),
            context);

        _logger?.Log(
            plan.IsFallback || plan.IsDeferred ? LogLevel.Warning : LogLevel.Information,
            "cli_quota_admission_decision path={Path} project={Project} jobId={JobId} stepId={StepId} outcome={Outcome} requestedCli={RequestedCli} effectiveCli={EffectiveCli} model={Model} reason={Reason}",
            path, request.Project, request.JobId, request.StepId, plan.Outcome,
            requestedCli, plan.CliType, plan.Model, plan.Reason);
        RecordTaskDecision(request, plan);

        if (!plan.ShouldLaunch)
        {
            var at = DateTime.UtcNow;
            return CliOneShotResult.SpawnFailure("Quota admission deferred: " + plan.Reason, at, at) with
            {
                EffectiveCliType = plan.CliType,
                EffectiveModel = plan.Model,
                EffectiveThinkingLevel = plan.ThinkingLevel,
                QuotaAdmission = plan,
            };
        }

        _fallback!.RecordAdmission(
            requestedCli,
            request.Model,
            request.ThinkingLevel,
            plan,
            DateTime.UtcNow);

        if (!_byCli.TryGetValue(plan.CliType, out var effective))
        {
            var at = DateTime.UtcNow;
            return CliOneShotResult.SpawnFailure(
                $"Quota fallback selected CLI '{plan.CliType}', but no one-shot implementation is registered.",
                at,
                at) with
            {
                EffectiveCliType = plan.CliType,
                EffectiveModel = plan.Model,
                EffectiveThinkingLevel = plan.ThinkingLevel,
                QuotaAdmission = plan,
            };
        }

        var effectiveRequest = request with
        {
            CliType = plan.CliType,
            Model = plan.Model ?? request.Model,
            ThinkingLevel = plan.ThinkingLevel ?? request.ThinkingLevel,
            // Session identifiers are provider-owned. A cross-family fallback
            // is always a new invocation and must never reuse another CLI's id.
            ExtraArgs = string.Equals(plan.CliType, requestedCli, StringComparison.OrdinalIgnoreCase)
                ? request.ExtraArgs
                : null,
        };
        var result = await effective.RunAsync(effectiveRequest, ct).ConfigureAwait(false);
        return result with
        {
            EffectiveCliType = plan.CliType,
            EffectiveModel = plan.Model ?? request.Model,
            EffectiveThinkingLevel = plan.ThinkingLevel ?? request.ThinkingLevel,
            QuotaAdmission = plan,
        };
    }

    private void RecordTaskDecision(CliOneShotRequest request, QuotaAdmissionPlan plan)
    {
        if (plan.Outcome == QuotaAdmissionOutcome.LaunchPrimary || string.IsNullOrWhiteSpace(request.JobFolderPath))
            return;

        var prefix = plan.IsFallback ? "[quota-fallback] " : "[quota-admission] ";
        _timeline?.Append(
            request.JobFolderPath!,
            AgentStudio.Shared.TimelineEventKinds.QuotaAdmissionDecision,
            AgentStudio.Shared.TimelineActors.System,
            prefix + plan.Reason,
            details: new Dictionary<string, string>
            {
                ["outcome"] = plan.Outcome.ToString(),
                ["requestedCli"] = request.CliType,
                ["effectiveCli"] = plan.CliType,
                ["model"] = plan.Model ?? string.Empty,
                ["thinkingLevel"] = plan.ThinkingLevel ?? string.Empty,
                ["routeSource"] = plan.RouteSource ?? string.Empty,
                ["equivalentTier"] = plan.EquivalentTier ?? string.Empty,
                ["nextReset"] = plan.NextResetAt?.ToString("o") ?? string.Empty,
            });

        var stateDirectory = Directory.GetParent(request.JobFolderPath!);
        var watchPath = stateDirectory?.Parent?.FullName;
        if (string.IsNullOrWhiteSpace(watchPath)) return;
        _orchestratorLog?.Append(watchPath, new AgentStudio.Runner.OrchestratorLogEntry
        {
            Kind = AgentStudio.Runner.OrchestratorLogKinds.Decision,
            Topic = AgentStudio.Runner.OrchestratorLogTopics.LoadDistribution,
            JobId = request.JobId,
            Summary = plan.Reason,
            Reasoning = QuotaAdmissionPlanner.DescribeLoadNumbers(plan),
        });
    }

    private sealed class QuotaAwareCliOneShot : ICliOneShot
    {
        private readonly CliOneShotRegistry _owner;

        public QuotaAwareCliOneShot(CliOneShotRegistry owner, string cliType)
        {
            _owner = owner;
            CliType = cliType;
        }

        public string CliType { get; }

        public Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
            => _owner.RunQuotaAwareAsync(CliType, request, ct);
    }
}
