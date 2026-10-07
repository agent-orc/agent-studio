
namespace AgentStudio.Runner;

/// <summary>
/// Kind of decision sentinel observed mid-run. Mirrors the interruptive
/// subset of <see cref="AgentOutcomeKind"/>; <c>DONE</c> and <c>NOOP</c>
/// are post-run signals handled by <see cref="AgentOutcomeAnalyzer"/> and
/// are intentionally not surfaced through the continuous-decision channel.
/// </summary>
public enum PendingDecisionKind
{
    /// <summary>Agent emitted <c>[[TASK_NEEDS_INPUT:...]]</c>.</summary>
    NeedsInput,
    /// <summary>Agent emitted <c>[[TASK_BLOCKED:...]]</c>.</summary>
    Blocked
}

/// <summary>
/// One mid-run decision sentinel that has not yet been resolved by an
/// <c>[orchestrator]</c>, <c>[supervisor]</c>, or <c>[user]</c> follow-up.
/// </summary>
public sealed record PendingDecision(
    PendingDecisionKind Kind,
    string? Reason,
    DateTime DetectedAt,
    int LineIndex);

/// <summary>
/// Pure helper that scans a CLI's live output buffer for unresolved
/// decision sentinels (<c>[[TASK_NEEDS_INPUT:...]]</c> and
/// <c>[[TASK_BLOCKED:...]]</c>). The orchestrator's continuous-review tick
/// uses this to surface decision moments while the run is still alive,
/// before the post-run path in <see cref="AgentOutcomeAnalyzer"/> fires.
///
/// <para>
/// <b>Why this exists.</b> ADR-0002 pins a single sentinel grammar
/// (<see cref="AgentOutcomeAnalyzer.SentinelRegex"/>) as authoritative.
/// Reusing it for mid-run detection keeps the agent contract one-grammar:
/// the same bracketed token that ends a run also stands out *during* a run.
/// See <c>docs/research/orchestrator-decision-protocol-2026-05.md</c> for
/// the rationale (scan over typed channel).
/// </para>
///
/// <para>
/// "Unresolved" mirrors <see cref="ReviewDecisionParsing.LineHasFollowUpStream"/>:
/// any <c>[orchestrator]</c>, <c>[supervisor]</c>, or <c>[user]</c> line
/// after the sentinel cancels it. Once the user replies through the banner
/// (or the orchestrator answers via the auto-decide path), the next tick
/// picks up the follow-up line and the banner clears on its own.
/// </para>
/// </summary>
public static class PendingDecisionScanner
{
    /// <summary>Default tail window. Plenty of headroom for any reasonable agent emission while keeping the regex pass O(K) per tick.</summary>
    public const int DefaultTailLines = 200;

    /// <summary>
    /// Scan <paramref name="lines"/> for the latest unresolved interruptive
    /// sentinel. Returns null when none is present, or when one was present
    /// but a follow-up stream line resolved it. Looks at the last
    /// <paramref name="tailLines"/> entries only so the cost stays bounded
    /// for long-running jobs.
    /// </summary>
    public static PendingDecision? Scan(IReadOnlyList<CliOutputLine>? lines, int tailLines = DefaultTailLines)
    {
        if (lines == null || lines.Count == 0) return null;
        var start = Math.Max(0, lines.Count - tailLines);

        // Walk forward through the tail so the *latest* sentinel wins. We
        // skip the system / user / orchestrator / supervisor streams when
        // matching the sentinel itself (those streams never produce agent
        // output) but we still need to *see* them when checking for resolution.
        int? latestIdx = null;
        PendingDecisionKind? latestKind = null;
        string? latestReason = null;
        DateTime latestTs = default;

        for (int i = start; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line == null) continue;
            if (IsAgentStream(line.Stream))
            {
                var match = AgentOutcomeAnalyzer.SentinelRegex.Match(line.Text ?? string.Empty);
                if (!match.Success) continue;

                var keyword = match.Groups["keyword"].Value.ToUpperInvariant();
                PendingDecisionKind? kind = keyword switch
                {
                    "NEEDS_INPUT" => PendingDecisionKind.NeedsInput,
                    "BLOCKED"     => PendingDecisionKind.Blocked,
                    _             => null
                };
                if (kind == null) continue; // DONE / NOOP are post-run, not interruptive.

                latestIdx = i;
                latestKind = kind;
                var raw = match.Groups["reason"].Success ? match.Groups["reason"].Value.Trim() : null;
                latestReason = string.IsNullOrWhiteSpace(raw) ? null : raw;
                latestTs = line.Timestamp == default ? DateTime.UtcNow : line.Timestamp;
            }
        }

        if (latestIdx == null || latestKind == null) return null;

        // Resolution check: any follow-up stream line after the sentinel
        // means the orchestrator or user already addressed it.
        for (int j = latestIdx.Value + 1; j < lines.Count; j++)
        {
            var line = lines[j];
            if (line == null) continue;
            if (IsFollowUpLine(line)) return null;
        }

        return new PendingDecision(
            Kind: latestKind.Value,
            Reason: latestReason,
            DetectedAt: latestTs,
            LineIndex: latestIdx.Value);
    }

    /// <summary>
    /// A follow-up line: the orchestrator, supervisor, or user stream, or a
    /// legacy line that embeds the follow-up tag in its text rather than the
    /// stream column (the same shape ReviewDecisionParsing uses).
    /// </summary>
    public static bool IsFollowUpLine(CliOutputLine line)
        => IsFollowUpStream(line.Stream)
           || line.Text is { } text
              && (text.Contains("[orchestrator]", StringComparison.OrdinalIgnoreCase)
                  || text.Contains("[supervisor]", StringComparison.OrdinalIgnoreCase)
                  || text.Contains("[user]", StringComparison.OrdinalIgnoreCase));

    private static bool IsAgentStream(string? stream)
    {
        if (string.IsNullOrEmpty(stream)) return true; // legacy lines default to stdout
        return !string.Equals(stream, "system",       StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stream, "user",         StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stream, "orchestrator", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stream, "supervisor",   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFollowUpStream(string? stream)
    {
        if (string.IsNullOrEmpty(stream)) return false;
        return string.Equals(stream, "user",         StringComparison.OrdinalIgnoreCase)
            || string.Equals(stream, "orchestrator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stream, "supervisor",   StringComparison.OrdinalIgnoreCase);
    }
}
