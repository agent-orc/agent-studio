using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

/// <summary>
/// Pure parsing for the review-decision orchestrator's outputs.
///
/// Two grammars live here, both kept static so the rules are
/// unit-testable without spinning up a CLI:
///
/// <list type="bullet">
///   <item>The agent-side <c>[[TASK_NEEDS_INPUT:&lt;reason&gt;]]</c>
///         sentinel that originally landed the job in 4-review.</item>
///   <item>The orchestrator-side
///         <c>[[ORCHESTRATOR_DECISION: action=&lt;reissue|escalate|accept-as-done&gt;; reason=&lt;short&gt;]]</c>
///         response sentinel produced by the fast-model session.</item>
/// </list>
/// </summary>
public static class ReviewDecisionParsing
{
    private static readonly Regex NeedsInputRegex = new(
        @"\[\[\s*TASK[\s_-]*NEEDS[\s_-]*INPUT\s*(?::\s*(?<reason>[^\]]*?))?\s*\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NoOpRegex = new(
        @"\[\[\s*TASK[\s_-]*NOOP\s*(?::\s*(?<reason>[^\]]*?))?\s*\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BlockedRegex = new(
        @"\[\[\s*TASK[\s_-]*BLOCKED\s*(?::\s*(?<reason>[^\]]*?))?\s*\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DoneRegex = new(
        @"\[\[\s*TASK[\s_-]*DONE\s*\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DecisionRegex = new(
        @"\[\[\s*ORCHESTRATOR_DECISION\s*:\s*(?<body>[^\]]+?)\s*\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Inspect a job's <c>cli-output.log</c> contents and return the
    /// 1-based line index of the latest unresolved <c>[[TASK_NEEDS_INPUT]]</c>
    /// sentinel, plus its reason. "Unresolved" means no orchestrator,
    /// supervisor, or user line appears after that sentinel; once any of
    /// those streams writes after a NEEDS_INPUT, the decision chain has
    /// been answered (by orchestrator follow-up or by the user) and the
    /// task should not be re-processed.
    /// </summary>
    public static NeedsInputState? FindUnresolvedNeedsInput(string log)
    {
        var hit = FindLatestUnresolvedSentinel(log, NeedsInputRegex);
        return hit == null ? null : new NeedsInputState(hit.Value.LineNumber, hit.Value.Reason);
    }

    /// <summary>
    /// Counterpart to <see cref="FindUnresolvedNeedsInput"/> for the
    /// <c>[[TASK_NOOP]]</c> sentinel. Returns the latest unresolved
    /// occurrence (no <c>[orchestrator]</c>, <c>[supervisor]</c>, or
    /// <c>[user]</c> line written after it) so the review-decision tick
    /// can treat NOOP as a recoverable signal rather than a terminal one.
    /// </summary>
    public static NoOpState? FindUnresolvedNoOp(string log)
    {
        var hit = FindLatestUnresolvedSentinel(log, NoOpRegex);
        return hit == null ? null : new NoOpState(hit.Value.LineNumber, hit.Value.Reason);
    }

    /// <summary>
    /// Counterpart to <see cref="FindUnresolvedNeedsInput"/> for the
    /// <c>[[TASK_BLOCKED]]</c> sentinel. The orchestrator picks these up
    /// from 4-review (StaleProgressArchiver hands BLOCKED jobs from
    /// 3-progress over there once the runner has gone idle); the
    /// review-decision tick then escalates them so the user sees one
    /// "this job needs your attention" intake rather than a quiet card.
    /// </summary>
    public static BlockedState? FindUnresolvedBlocked(string log)
    {
        var hit = FindLatestUnresolvedSentinel(log, BlockedRegex);
        if (hit == null) return null;

        var reason = IsMissingBlockedReason(hit.Value.Reason)
            ? FindLastAgentParagraph(log, hit.Value.LineNumber - 1, BlockedRegex)
            : hit.Value.Reason;
        return new BlockedState(hit.Value.LineNumber, reason);
    }

    /// <summary>
    /// Counterpart to <see cref="FindUnresolvedNeedsInput"/> for the
    /// <c>[[TASK_DONE]]</c> terminal sentinel. The multi-aspect
    /// auto-review pipeline runs only when DONE is the latest unresolved
    /// signal: that is, the agent declared the work complete and no
    /// orchestrator/supervisor/user line has been written after it. Once
    /// the orchestrator records its multi-aspect decision the log gets a
    /// follow-up line and this helper stops returning a hit, preventing
    /// duplicate aspect runs across ticks.
    /// </summary>
    public static DoneState? FindUnresolvedDone(string log)
    {
        var hit = FindLatestUnresolvedSentinel(log, DoneRegex);
        return hit == null ? null : new DoneState(hit.Value.LineNumber);
    }

    /// <summary>
    /// True when the job's most recent run produced no terminal task
    /// sentinel at all. The "most recent run" is the slice of the log after
    /// the last orchestrator/supervisor/user follow-up line (the technical
    /// "Runner active state cleared" marker is bookkeeping, not a
    /// follow-up). Returns <c>false</c> when that slice is empty (nothing
    /// new since the last follow-up, so the orchestrator has already acted)
    /// or when it carries any of
    /// <c>[[TASK_DONE]] / [[TASK_NOOP]] / [[TASK_BLOCKED]] / [[TASK_NEEDS_INPUT]]</c>.
    ///
    /// <para>
    /// This is the deterministic-completion contract's "no signal arrived"
    /// detector. A run can land in 4-auto-review with no terminal sentinel -
    /// e.g. it exited 0 with only heuristic "done"-ish prose, or the
    /// terminal classifier force-routed an Unknown / committed-partial
    /// outcome there. Such a run must never be silently accepted as
    /// completed: the review-decision loop reissues it (demanding a
    /// sentinel) until the shared reissue budget is spent, then escalates to
    /// human review. Distinguishing "no sentinel ever" from "a sentinel that
    /// was already resolved on a prior tick" is exactly what the
    /// last-follow-up slice gives us, so an already-handled card is left
    /// untouched.
    /// </para>
    /// </summary>
    public static bool LacksTerminalSentinelInLatestRun(string log)
    {
        if (string.IsNullOrWhiteSpace(log)) return false;
        var lines = log.Split('\n');

        var lastFollowUp = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (LineHasFollowUpStream(lines[i])) lastFollowUp = i;
        }

        var sawContent = false;
        for (var i = lastFollowUp + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (LineIsTechnicalOrchestratorMarker(line)) continue;
            if (NeedsInputRegex.IsMatch(line) || NoOpRegex.IsMatch(line)
                || BlockedRegex.IsMatch(line) || DoneRegex.IsMatch(line))
            {
                // A terminal sentinel lives in the latest run; the typed
                // FindUnresolved* helpers own this case.
                return false;
            }
            sawContent = true;
        }

        return sawContent;
    }

    /// <summary>
    /// Scans a large CLI log one line at a time. Only the current signal and a
    /// bounded BLOCKED explanation are retained; no log content is passed to
    /// the decision prompt. Line numbers remain relative to the whole log.
    /// </summary>
    public static (string? Kind, int LineNumber, string? Reason) ScanUnresolvedLines(
        IEnumerable<string> lines, int? activeEpochLineBoundary = null)
    {
        var all = new SignalScan();
        var active = new SignalScan();
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            all.Accept(line, lineNumber);
            if (activeEpochLineBoundary is >= 0 && lineNumber > activeEpochLineBoundary.Value)
                active.Accept(line, lineNumber);
        }

        return activeEpochLineBoundary is >= 0 && activeEpochLineBoundary <= lineNumber
            ? active.Result : all.Result;
    }

    private sealed class SignalScan
    {
        private string? _kind;
        private int _lineNumber = -1;
        private string? _reason;
        private string _agentParagraph = string.Empty;
        private bool _sawContent;
        private bool _sawSentinel;

        public (string? Kind, int LineNumber, string? Reason) Result => _kind is not null
            ? (_kind, _lineNumber, _reason)
            : _sawContent && !_sawSentinel
                ? ("no-completion-signal", -1, null)
                : (null, -1, null);

        public void Accept(string line, int lineNumber)
        {
            if (LineHasFollowUpStream(line))
            {
                _kind = null;
                _lineNumber = -1;
                _reason = null;
                _agentParagraph = string.Empty;
                _sawContent = false;
                _sawSentinel = false;
                return;
            }

            var (isAgent, agentText) = ReadAgentLogLine(line);
            var blockedParagraph = _agentParagraph;
            var withoutBlocked = BlockedRegex.Replace(agentText, string.Empty).Trim();
            if (isAgent && withoutBlocked.Length > 0)
                blockedParagraph = (blockedParagraph + " " + withoutBlocked).Trim();

            var hits = new (string Kind, Match Match)[]
            {
                ("done", DoneRegex.Match(line)),
                ("blocked", BlockedRegex.Match(line)),
                ("noop", NoOpRegex.Match(line)),
                ("needs-input", NeedsInputRegex.Match(line)),
            };
            foreach (var (kind, match) in hits)
            {
                if (!match.Success) continue;
                _kind = kind;
                _lineNumber = lineNumber;
                var raw = match.Groups["reason"].Success ? match.Groups["reason"].Value.Trim() : null;
                _reason = kind == "blocked" && IsMissingBlockedReason(raw)
                    ? blockedParagraph : string.IsNullOrWhiteSpace(raw) ? null : raw;
                _sawSentinel = true;
            }

            if (!string.IsNullOrWhiteSpace(line) && !LineIsTechnicalOrchestratorMarker(line))
                _sawContent = true;
            _agentParagraph = isAgent && agentText.Length > 0
                ? blockedParagraph.Length <= 512 ? blockedParagraph : blockedParagraph[^512..]
                : string.Empty;
        }
    }

    private static (int LineNumber, string? Reason)? FindLatestUnresolvedSentinel(string log, Regex regex)
    {
        if (string.IsNullOrEmpty(log)) return null;
        var lines = log.Split('\n');
        int? lastAt = null;
        string? reason = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var match = regex.Match(lines[i]);
            if (match.Success)
            {
                lastAt = i;
                var raw = match.Groups["reason"].Success ? match.Groups["reason"].Value.Trim() : null;
                reason = string.IsNullOrWhiteSpace(raw) ? null : raw;
            }
        }
        if (lastAt == null) return null;

        for (int j = lastAt.Value + 1; j < lines.Length; j++)
        {
            if (LineHasFollowUpStream(lines[j])) return null;
        }
        return (lastAt.Value + 1, reason);
    }

    private static bool IsMissingBlockedReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
        || string.Equals(reason.Trim(), "<short reason>", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Recovers a useful BLOCKED reason from the final agent paragraph when
    /// the sentinel contains no reason or the prompt's literal placeholder.
    /// Log prefixes are removed so escalation text contains only agent prose.
    /// </summary>
    private static string? FindLastAgentParagraph(string log, int sentinelIndex, Regex sentinelRegex)
    {
        var lines = log.Split('\n');
        var paragraph = new List<string>();

        for (var i = Math.Min(sentinelIndex, lines.Length - 1); i >= 0; i--)
        {
            var (isAgent, text) = ReadAgentLogLine(lines[i]);
            if (!isAgent)
            {
                if (paragraph.Count > 0) break;
                continue;
            }

            text = sentinelRegex.Replace(text, string.Empty).Trim();
            if (text.Length == 0)
            {
                if (paragraph.Count > 0) break;
                continue;
            }

            paragraph.Add(text);
        }

        if (paragraph.Count == 0) return null;
        paragraph.Reverse();
        return string.Join(" ", paragraph);
    }

    private static (bool IsAgent, string Text) ReadAgentLogLine(string line)
    {
        var streamStart = line.IndexOf("] [", StringComparison.Ordinal);
        if (streamStart < 0) return (true, line.Trim());

        var streamNameStart = streamStart + 3;
        var streamEnd = line.IndexOf(']', streamNameStart);
        if (streamEnd < 0) return (true, line.Trim());

        var stream = line[streamNameStart..streamEnd];
        var isAgent = !stream.Equals("system", StringComparison.OrdinalIgnoreCase)
            && !stream.Equals("user", StringComparison.OrdinalIgnoreCase)
            && !stream.Equals("orchestrator", StringComparison.OrdinalIgnoreCase)
            && !stream.Equals("supervisor", StringComparison.OrdinalIgnoreCase);
        var text = streamEnd + 1 < line.Length ? line[(streamEnd + 1)..].Trim() : string.Empty;
        return (isAgent, text);
    }

    /// <summary>
    /// Returns the last <c>[[ORCHESTRATOR_DECISION]]</c> sentinel parsed
    /// from a model response, or <c>null</c> when none is present or the
    /// action keyword is unknown. Tolerant of field order, whitespace,
    /// and case so a fast-model reply with the sentinel on its own line
    /// or buried in narrative still resolves. Multiple sentinels are
    /// intentional "last wins"; duplicate fields inside the winning
    /// sentinel are malformed and fail closed.
    /// </summary>
    public static OrchestratorDecisionVerdict? ParseDecision(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var matches = DecisionRegex.Matches(output);
        if (matches.Count == 0) return null;

        var last = matches[^1];
        var body = last.Groups["body"].Value;
        var fields = ParseFields(body);
        if (fields == null) return null;

        var actionRaw = fields.GetValueOrDefault("action")?.Trim().ToLowerInvariant();
        var reason = fields.GetValueOrDefault("reason")?.Trim();
        if (string.IsNullOrWhiteSpace(actionRaw) || string.IsNullOrWhiteSpace(reason)) return null;
        var action = actionRaw switch
        {
            "reissue" => OrchestratorDecisionAction.Reissue,
            "escalate" => OrchestratorDecisionAction.Escalate,
            "accept-as-done" or "accept_as_done" or "accept" => OrchestratorDecisionAction.AcceptAsDone,
            _ => (OrchestratorDecisionAction?)null
        };
        if (action == null) return null;

        return new OrchestratorDecisionVerdict(action.Value, reason);
    }

    private static bool LineHasFollowUpStream(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (LineIsTechnicalOrchestratorMarker(line)) return false;
        // Lines persisted by OrchestratorChatLog look like
        //   [HH:mm:ss.fff] [orchestrator] ...
        // and the runner's own user-input lines use the [user] stream tag.
        return line.Contains("[orchestrator]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[supervisor]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[user]", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LineIsTechnicalOrchestratorMarker(string line)
    {
        // This is written after the job has already moved from
        // 3-progress to 4-auto-review. It is bookkeeping, not an answer
        // to the agent's terminal sentinel. Treating it as a resolution
        // makes every freshly completed job invisible to auto-review.
        return line.Contains("[orchestrator]", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Runner active state cleared:", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string>? ParseFields(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();
            if (key.Length == 0) continue;
            if (dict.ContainsKey(key)) return null;
            dict[key] = value;
        }
        return dict.Count == 0 ? null : dict;
    }
}

public sealed record NeedsInputState(int LineNumber, string? Reason);

public sealed record NoOpState(int LineNumber, string? Reason);

public sealed record BlockedState(int LineNumber, string? Reason);

public sealed record DoneState(int LineNumber);

public enum OrchestratorDecisionAction
{
    Reissue,
    Escalate,
    AcceptAsDone
}

public sealed record OrchestratorDecisionVerdict(OrchestratorDecisionAction Action, string Reason);
