using System.Diagnostics;

namespace AgentStudio.AdHoc;

/// <summary>
/// Static helper that bundles the two pieces every ad-hoc Haiku call
/// site needs after its subprocess returns:
///
/// <list type="number">
///   <item>Parse the CLI's <c>--output-format json</c> wrapper into
///         <c>(text, usage)</c>, falling back to the raw stdout when
///         the wrapper is missing (the path test fakes take, since
///         their <c>InvokeAsync</c> stub returns plain strings).</item>
///   <item>Hand the parsed usage to <see cref="AdHocUsageRecorder"/>
///         tagged with a stable <see cref="AdHocUsageSources"/> string
///         and the wall-clock duration.</item>
/// </list>
///
/// <para>
/// The seven ad-hoc Haiku call sites all keep their own
/// <c>protected virtual InvokeAsync</c> seam for testability; this
/// helper sits between that seam and the caller's parsing /
/// sanitisation logic. Recording is best-effort and never throws.
/// </para>
/// </summary>
public static class AdHocClaudeInvoker
{
    /// <summary>
    /// Argv tail for one-shot Haiku calls that follow the convention
    /// "feed the prompt via stdin, ask for the JSON wrapper". Callers
    /// build the <c>ProcessStartInfo</c> themselves (working directory,
    /// claude path, stdin encoding) and append these arguments.
    /// </summary>
    public static IReadOnlyList<string> BuildArgs(string model) => new[]
    {
        "-p",
        "--output-format", "json",
        "--model", model,
        "--dangerously-skip-permissions"
    };

    /// <summary>
    /// Parse the raw stdout the subprocess returned. When it is the
    /// JSON wrapper claude-code emits with <c>--output-format json</c>,
    /// extract <c>result</c> and <c>usage</c>; otherwise treat the input
    /// as plain text with no usage data (the path tests take). Tolerant
    /// to empty / whitespace input.
    /// </summary>
    public static (string Text, OrchestratorTokenUsage? Usage) ParseOrFallback(string? raw, string fallbackModel)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", null);
        var trimmed = raw.TrimStart();
        // Plain-text fast path (test fakes return strings without a JSON wrapper).
        if (!trimmed.StartsWith('{')) return (raw, null);

        // The CLI's --output-format json wrapper has a top-level "type":"result"
        // marker. Domain JSON the test fakes return ({"candidates":[...]} for the
        // splitter, etc.) does not. Only treat the wrapper-shaped doc as the CLI
        // output; everything else is plain text.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return (raw, null);
            var hasWrapperShape =
                (root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == System.Text.Json.JsonValueKind.String && typeEl.GetString() == "result")
                || root.TryGetProperty("usage", out _);
            if (!hasWrapperShape) return (raw, null);

            var result = OrchestratorRunner.ParseResult(raw, fallbackModel);
            return (result.ReplyText ?? "", result.TokenUsage);
        }
        catch
        {
            return (raw, null);
        }
    }

    /// <summary>
    /// Record one call, tolerant to a null recorder (some test paths
    /// don't wire one up) and to a null usage block (the plain-text
    /// fallback case, which we still log as a zero-token entry so the
    /// per-source call counts are correct).
    /// </summary>
    public static void Record(
        AdHocUsageRecorder? recorder,
        string source,
        string model,
        OrchestratorTokenUsage? usage,
        long durationMs,
        bool ok,
        string? project = null,
        string? jobId = null,
        string? stepId = null,
        string? jobFolderPath = null,
        string? thinkingLevel = null,
        string? modelSource = null)
    {
        if (recorder == null) return;
        recorder.Record(new AdHocUsageRecord
        {
            Ts = DateTime.UtcNow,
            Source = source,
            Model = string.IsNullOrWhiteSpace(usage?.Model) ? model : usage!.Model!,
            InputTokens = usage?.InputTokens ?? 0,
            OutputTokens = usage?.OutputTokens ?? 0,
            CacheReadTokens = usage?.CacheReadTokens ?? 0,
            CacheCreationTokens = usage?.CacheCreationTokens ?? 0,
            DurationMs = durationMs,
            Ok = ok,
            Project = string.IsNullOrWhiteSpace(project) ? null : project,
            JobId = string.IsNullOrWhiteSpace(jobId) ? null : jobId,
            StepId = stepId,
            JobFolderPath = jobFolderPath,
            ThinkingLevel = thinkingLevel,
            ModelSource = modelSource,
        });
    }

    /// <summary>
    /// Convenience: start a Stopwatch ready for a one-shot call. Sugar
    /// only, but keeps the call sites uniform.
    /// </summary>
    public static Stopwatch StartTiming() => Stopwatch.StartNew();
}
