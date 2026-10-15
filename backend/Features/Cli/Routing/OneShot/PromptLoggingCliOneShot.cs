namespace AgentStudio.Cli;

/// <summary>
/// Central-dispatch decorator over an <see cref="ICliOneShot"/> that records
/// the final, raw prompt of a step-call into the task's
/// <c>.metadata/prompts.jsonl</c> before delegating the actual CLI run to the
/// wrapped implementation.
///
/// <para>
/// This is the single write-hook that closes the gap where step-call prompts
/// (aspects, code-review-grade, orchestrator-decision, drift, ...) landed in
/// no raw file at the task. Because every step-call routes through
/// <see cref="ICliOneShot.RunAsync"/>, wrapping that one seam captures them
/// all - a call site opts in simply by setting
/// <see cref="CliOneShotRequest.JobFolderPath"/> +
/// <see cref="CliOneShotRequest.StepId"/>. The main run and its follow-ups do
/// NOT go through this path (they use the streaming execution service and are
/// already logged in <c>prompt.md</c> / chat), so there is no double
/// bookkeeping.
/// </para>
///
/// <para>
/// The prompt is written BEFORE the inner call ("beim Absenden"): capturing
/// at dispatch means a prompt is preserved even when the CLI call later
/// times out or fails - exactly the cases the raw-data audit cares about.
/// Logging is best-effort and never alters the result the wrapped runner
/// returns.
/// </para>
/// </summary>
public sealed class PromptLoggingCliOneShot : ICliOneShot
{
    private readonly ICliOneShot _inner;
    private readonly StepPromptLog _log;

    public PromptLoggingCliOneShot(ICliOneShot inner, StepPromptLog log)
    {
        _inner = inner;
        _log = log;
    }

    public string CliType => _inner.CliType;

    public async Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var checkedPrompt = OrchestratorPromptBoundary.Check(request.StepId, request.Prompt);
        if (checkedPrompt.Failure is { } failure)
        {
            var now = DateTime.UtcNow;
            return CliOneShotResult.SpawnFailure("decision-input-incomplete:" + failure, now, now);
        }
        if (checkedPrompt.Prompt != request.Prompt)
            request = request with { Prompt = checkedPrompt.Prompt };

        if (!string.IsNullOrWhiteSpace(request.JobFolderPath)
            && !string.IsNullOrWhiteSpace(request.StepId))
        {
            // StepPromptLog already guards + swallows IO failures; the await is
            // a fast jsonl append so it does not meaningfully delay the call.
            await _log.AppendAsync(request.JobFolderPath!, new StepPromptEntry
            {
                At = DateTime.UtcNow,
                StepId = request.StepId!,
                TemplateRef = request.TemplateRef,
                Model = request.Model,
                Cli = request.CliType,
                Source = request.Source,
                Prompt = request.Prompt,
            }, ct).ConfigureAwait(false);
        }

        return await _inner.RunAsync(request, ct).ConfigureAwait(false);
    }
}
