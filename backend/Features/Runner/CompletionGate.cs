using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Runner;

/// <summary>
/// Deterministic post-processing gate for a run that emitted
/// <c>[[TASK_DONE]]</c>. It reads the generated result/status surface and the
/// final log tail for explicit "not finished" evidence before auto-review may
/// accept the task. This closes the silent-completion gap where an agent says
/// done while its own close-out still lists open items.
/// </summary>
public static class CompletionGate
{
    public const int MaxFindings = 8;

    private const int MaxFindingLength = 220;

    private static readonly Regex ResultLineRegex = new(
        @"^\s*[-*]?\s*Result:\s*(?<result>[A-Za-z]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex UncheckedItemRegex = new(
        @"^\s*[-*+]\s*\[\s*\]\s*(?<text>\S.*)$",
        RegexOptions.Compiled);

    private static readonly Regex BulletRegex = new(
        @"^\s*[-*+]\s+(?<text>\S.*)$",
        RegexOptions.Compiled);

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    // An echoed SOURCE-CODE line, not the agent's own close-out prose: a
    // grep -n / cat -n / file-read line with a "NNN:" (or "NNN<tab>") line-number
    // marker followed by code that carries call/brace/statement punctuation.
    // Optional leading bracketed tokens absorb a CLI log prefix like
    // "[12:00:00.000] [stdout] ". These lines are scanned out of the
    // unfinished-work evidence: an incomplete-work keyword inside one is an
    // identifier or comment in the printed source (e.g. a parameter literally
    // named "pending"), not a status the run is reporting about itself. Without
    // this guard a run that merely greps its own code is reissued forever on a
    // word it never claimed (ASS-794, Epic-776 orchestrator hardening).
    private static readonly Regex ToolSourceEchoRegex = new(
        @"^(?:\[[^\]]*\]\s*)*\s*\d{1,6}[:\t]\s*\S.*[(){};]",
        RegexOptions.Compiled);

    // An echoed line from the durable pipeline history, usually produced by
    // rg/Get-Content while investigating a prior completion-gate verdict. The
    // JSON can contain the gate's own old "Build FAILED" finding; treating that
    // copy as fresh run evidence makes every subsequent success reissue itself.
    // Keep this precise to pipeline-execution.json line-number output so real
    // compiler diagnostics with file paths remain visible (AGT-2148).
    private static readonly Regex PipelineHistoryEchoRegex = new(
        @"^(?:\[[^\]]*\]\s*)*.*(?:^|[\\/])pipeline-execution\.json:\d+:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The build / compile / test-failure vocabulary. Kept as one fragment so the
    // generic incomplete-work scan and the "claims success but build failed"
    // contradiction check (see BuildErrorEvidenceRegex) share exactly one
    // definition and cannot drift. Extended mode (?x): literal spaces are
    // ignored, so spacing is written with \s.
    private const string BuildErrorEvidence = @"
        build\s+fail(?:s|ed|ing)? |
        build\s+(?:is\s+)?broken |
        failed\s+to\s+build |
        compilation\s+failed |
        compile\s+errors? |
        does(?:n'?t|\s+not)\s+compile |
        wo(?:n'?t|uld\s+not)\s+compile |
        error\s+TS\d+ |
        error\s+CS\d+ |
        error\s+NG\d+ |
        typescript\s+errors? |
        npm\s+ERR |
        application\s+bundle\s+generation\s+failed |
        tests?\s+fail(?:s|ed|ing)?";

    /// <summary>
    /// Build / compile / test-failure evidence on its own. Used by the
    /// contradiction rule: a run that claims success while one of these signals
    /// appears in its own close-out is reported as a finding.
    /// </summary>
    private static readonly Regex BuildErrorEvidenceRegex = new(
        @"(?ix)\b(?:" + BuildErrorEvidence + @")\b",
        RegexOptions.Compiled);

    private static readonly Regex IncompleteEvidenceRegex = new(
        @"(?ix)
        \b(?:
            incomplete |
            unfinished |
            not\s+finished |
            not\s+complete |
            (?<![\w-])pending(?![\w-])(?!\.[\w-]) |
            file-state\s+mismatch |
            route[-\s]?wiring\s+pending |
            apply_patch\b.{0,80}\b(?:failed|mismatch|reject(?:ed)?) |
            patch\b.{0,80}\b(?:failed|did\s+not\s+apply|reject(?:ed)?) |"
        + BuildErrorEvidence + @"
        )\b",
        RegexOptions.Compiled);

    private static readonly Regex SuccessResultRegex = new(
        @"^(?:success|succeeded|done|complete|completed|pass|passed)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CliExitRegex = new(
        @"\[taskboard\].*?CLI\s+exited:\s*status=(?<status>\w+).*?exitCode=(?<code>-?\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A no-op open-item declaration: the agent's close-out lists "nothing open"
    // rather than an actual unfinished item. Matched against the FIRST sentence
    // of the extracted item text (everything up to the first '.'), so a phantom
    // checkbox like "- [ ] None. Changes left in working tree per managed-run
    // guidelines." is recognised as a no-op and never reported as a finding.
    // Anchored to the whole first-sentence so a real item that merely starts
    // with one of these words (e.g. "None of the routes are wired") is NOT
    // suppressed. This closes the phantom-"None" reissue/escalation loop
    // (ASS-797): a non-actionable checkbox the agent can never close, that
    // otherwise ping-pongs the task back to 2-ready forever.
    private static readonly Regex NoOpOpenItemRegex = new(
        @"(?ix)^\s*(?:
            none |
            n/?a |
            nothing(?:\s+(?:to\s+do|further|else|needed|remaining|outstanding|open|pending))? |
            no\s+(?:open\s+items? | follow[-\s]?ups? | remaining\s+(?:work|items?) | further\s+(?:work|action|items?))
        )
        (?:\s+(?:required|needed|necessary|outstanding|remaining|pending|at\s+this\s+(?:time|point)))?
        \s*$",
        RegexOptions.Compiled);

    // A non-actionable "the platform owns the commit/push" open item. In a
    // managed run the agent is contractually forbidden from committing or
    // pushing (docs/operations/git/commit-push-doctrine.md - the platform owns that boundary),
    // so an open item that merely defers the commit / push / merge of the
    // leftover working tree to the platform or managed run can NEVER be closed
    // by the agent. Treating it as unfinished work reissues the card forever on
    // a state the run cannot change - the exact false-negative loop this task
    // fixes (ASS-619/766/797). Examples this catches:
    //   "Working tree changes awaiting managed-run commit/push (platform owns merge)."
    //   "Changes left in working tree for the platform to commit."
    //   "Commit/push handled by the managed run."
    // Precise (the delegation must be explicit) so real work like
    // "Push the migration to the shared config repo" is NOT suppressed.
    private static readonly Regex PlatformOwnedCommitItemRegex = new(
        @"(?ix)
        (?:
            (?:platform|managed[-\s]?run)\s+(?:owns?|will\s+\w+|handles?|commits?|pushes|merges?) |
            (?:owned|handled|committed|pushed|merged)\s+by\s+(?:the\s+)?(?:platform|managed[-\s]?run) |
            await(?:s|ing)?\s+(?:the\s+)?(?:managed[-\s]?run|platform) |
            per\s+managed[-\s]?run |
            left\s+in\s+(?:the\s+)?working\s+tree |
            changes?\s+awaiting\s+(?:managed[-\s]?run\s+)?(?:commit|push|merge)
        )",
        RegexOptions.Compiled);

    // An explicitly pre-existing / out-of-scope disclaimer the run attached to a
    // status line, saying the item is NOT work this change is responsible for.
    // The completion gate must not count these as unfinished work: doing so
    // escalated fully-merged runs that had honestly annotated a pre-existing
    // failure as such (AGT-1986; the run marked an item "Pre-existing (not caused
    // by this change)" and the static scan treated it as open work).
    //
    // Defined marker syntax (kept deliberately explicit so a genuine actionable
    // item that merely uses the adjective - e.g. "Fix the pre-existing bug in X"
    // - is NOT suppressed):
    //   - a bracketed / parenthesised tag: "[pre-existing]", "(out of scope)",
    //     "(pre-existing, not caused by this change)";
    //   - an inline disclaimer: "... not caused by this change",
    //     "... not introduced by these changes";
    //   - a labelled prefix: "Pre-existing: ...", "Out-of-scope: ...".
    private static readonly Regex PreExistingOutOfScopeRegex = new(
        @"(?ix)
        (?: [\[(] \s* (?: pre[-\s]?existing | out[-\s]?of[-\s]?scope | not \s+ (?:caused|introduced) ) [^\])]* [\])] )
      | \b not \s+ (?: caused | introduced ) \s+ by \s+ (?: this | these | the ) \s+ change(?:s)? \b
      | ^ \s* (?: pre[-\s]?existing | out[-\s]?of[-\s]?scope ) \b [^:]* :",
        RegexOptions.Compiled);

    // A build/test-failure phrase that the close-out explicitly identifies as a
    // false positive is diagnostic history, not current unfinished work. Keep
    // the relationship on the same line and explicit; broad words such as
    // "resolved", "earlier", or an unrelated false-positive mention must not
    // hide a real failure.
    private static readonly Regex ExplicitFalsePositiveRegex = new(
        @"(?ix)\b(?:" + BuildErrorEvidence + @")\b.{0,80}\b(?:as|was|is|diagnosed\s+as|identified\s+as|confirmed\s+as)\s+(?:a\s+)?false[-\s]?positive\b",
        RegexOptions.Compiled);

    // A successful close-out may describe the defect it just fixed. A quoted
    // failure token explicitly labelled stale/superseded is historical input,
    // not a current build result. Keep the qualifier and failure phrase on the
    // same line so an unqualified current "Build FAILED" still blocks.
    private static readonly Regex SupersededBuildEvidenceRegex = new(
        @"(?ix)\b(?:stale|superseded|already\s+(?:fixed|resolved|cleared))\b.{0,100}\b(?:" + BuildErrorEvidence + @")\b",
        RegexOptions.Compiled);

    // Negative evidence statements such as "no unfinished evidence" describe
    // a clean gate. Matching the bare word "unfinished" reopens a completed
    // task when that sentence is echoed by rg or appears in a close-out.
    private static readonly Regex NegatedIncompleteEvidenceRegex = new(
        @"(?ix)\b(?:no|without)\s+(?:remaining\s+)?(?:unfinished|incomplete|pending)\s+(?:work|items?|evidence)\b",
        RegexOptions.Compiled);

    // The gate's OWN verdict echoed back at it (MKT-8 escalation loop): the
    // reissue follow-up, the supervisor's escalate line, and the agent's
    // narration of / investigation into a prior verdict all contain the gate's
    // vocabulary ("unfinished", "completion gate found unfinished-work
    // evidence", "auto-review flagged ... as \"unfinished\""). Treating that
    // meta-text as fresh unfinished-work evidence makes the verdict
    // self-sustaining: every round re-reads its own previous verdict, finds
    // "unfinished", and reissues/escalates a run that terminated clean - the
    // old evidence can never be cleared. Lines that (a) name the completion
    // gate / unfinished-work evidence, or (b) narrate that something was
    // "flagged ... as unfinished/incomplete" are verdict echoes, never a run's
    // own first-person claim of open work.
    private static readonly Regex GateVerdictEchoRegex = new(
        @"(?ix)
        completion[-\s]?gate\b.{0,160}\b(?:found|flagged|interpret\w*|unresolved|verdict|evidence|clear\w*|reissu\w*|escalat\w*)\b |
        \[completion-gate-unresolved\] |
        \bunfinished-work\s+evidence\b |
        \bauto[-\s]?review\b.{0,100}\bflagged\b |
        \bflagged\b.{0,120}\bas\b\s*[""„“'']?\s*(?:unfinished|incomplete)\b",
        RegexOptions.Compiled);

    // A Codex command-invocation echo ("● Run \"...powershell.exe\" -Command
    // \"rg -n -i \\\"unfinish...\""): the run investigating a prior verdict
    // greps for the gate's vocabulary, and the echoed COMMAND LINE then matches
    // the vocabulary itself. A command echo is tool traffic, never the run's
    // own close-out claim. Anchored to the ● bullet the CLI prints for
    // command items (after optional bracketed log prefixes).
    private static readonly Regex ToolInvocationEchoRegex = new(
        @"^(?:\[[^\]]*\]\s*)*●\s*(?:Run|Ran)\b",
        RegexOptions.Compiled);

    // An open item that defers to explicit operator/user approval ("Release
    // gate: remains locked pending explicit approval of ..."). Like the
    // platform-owned commit boundary, the agent can NEVER close such an item -
    // approval is the operator's move by definition - so counting it as
    // unfinished work reissues/escalates a deliberately approval-gated
    // deliverable forever (MKT-8). Precise: the deferral must say "explicit
    // approval" or name the approver (operator/user/human), so a genuine
    // actionable item like "Get approval from the design team, then wire the
    // header" is NOT suppressed.
    private static readonly Regex OperatorApprovalGateItemRegex = new(
        @"(?ix)
        \b(?:pending|await(?:s|ing)?|blocked\s+on|requires?)\s+explicit\s+(?:\w+\s+)?approval\b |
        \b(?:pending|await(?:s|ing)?|blocked\s+on|requires?)\s+(?:the\s+)?(?:operator|user|human)(?:'s)?\s+(?:approval|sign[-\s]?off|go[-\s]?ahead)\b |
        \bremains?\s+locked\s+pending\b",
        RegexOptions.Compiled);

    // "[taskboard] Started <cli> CLI (PID ...)" - the engine's synthetic
    // run-start marker. Together with CliExitRegex it brackets one attempt's
    // window in the accumulated log.
    private static readonly Regex CliStartedRegex = new(
        @"\[taskboard\]\s+Started\s+\S+\s+CLI\s+\(PID",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public enum CompletionGateAction
    {
        Pass,
        Reissue,
        Escalate,
    }

    public sealed record Decision
    {
        public CompletionGateAction Action { get; init; } = CompletionGateAction.Pass;
        public IReadOnlyList<string> Findings { get; init; } = [];
        public string Reason { get; init; } = "No unfinished-work evidence found.";

        public bool IsIncomplete => Action != CompletionGateAction.Pass;
    }

    /// <summary>
    /// Evaluate the close-out evidence and apply the bounded retry policy. The
    /// caller supplies the shared auto-review reissue count so this gate cannot
    /// spin independently of NEEDS_INPUT / NOOP / aspect-block recovery.
    /// </summary>
    public static Decision Evaluate(string? statusMarkdown, string? recentLog, int priorReissues, int maxReissues)
        => Evaluate(statusMarkdown, recentLog, priorReissues, maxReissues, hasResultsArtifacts: false);

    /// <summary>
    /// Evaluate the gate with run-bound non-commit evidence. A clean DONE/exit-0
    /// closure backed by API delivery, results artefacts, or documented
    /// verification is complete even when it creates no fresh commit. This
    /// decision is made before scanning the accumulated log for historical
    /// failure words, while explicit current open items still block.
    /// </summary>
    public static Decision Evaluate(
        string? statusMarkdown,
        string? recentLog,
        int priorReissues,
        int maxReissues,
        bool hasResultsArtifacts)
    {
        var completionEvidence = CompletionEvidencePolicy.Decide(new CompletionEvidencePolicy.Inputs(
            HasTaskDoneSentinel: recentLog?.Contains("[[TASK_DONE]]", StringComparison.Ordinal) == true,
            ExitCode: ExtractLatestExitCode(recentLog),
            RunStatusCompleted: ExtractLatestRunCompleted(recentLog),
            StatusResultToken: ExtractResultToken(statusMarkdown),
            HasOpenItems: ExtractOpenItemsSection(statusMarkdown ?? string.Empty).Any(),
            HasBuildFailureInStatus: FirstBuildErrorLine(statusMarkdown ?? string.Empty) is not null,
            HasApiDelivery: CompletionEvidencePolicy.DetectApiDelivery(statusMarkdown) || CompletionEvidencePolicy.DetectApiDelivery(recentLog),
            HasResultsArtifacts: hasResultsArtifacts,
            HasDocumentedVerification: CompletionEvidencePolicy.DetectDocumentedVerification(statusMarkdown)));
        if (completionEvidence.AcceptAsCompleted)
        {
            return new Decision { Reason = completionEvidence.Reason };
        }

        var findings = ExtractFindings(statusMarkdown, recentLog);
        if (findings.Count == 0)
        {
            return new Decision();
        }

        if (priorReissues >= maxReissues)
        {
            return new Decision
            {
                Action = CompletionGateAction.Escalate,
                Findings = findings,
                Reason = $"Completion gate found unfinished-work evidence after {priorReissues} prior orchestrator reissue(s); user attention required.",
            };
        }

        return new Decision
        {
            Action = CompletionGateAction.Reissue,
            Findings = findings,
            Reason = $"Completion gate found {findings.Count} unfinished-work item(s); reissuing with the items foregrounded.",
        };
    }

    public static string BuildFollowUp(IReadOnlyList<string> findings, IReadOnlyList<string>? priorCommits = null)
    {
        var sb = new StringBuilder();
        sb.AppendLf(RunOutcomePolicy.DiffOnlySteeringRule);
        sb.AppendLf();
        sb.AppendLf("The Orchestrator Completion-Gate found unfinished work in the previous run's own result/status evidence.");
        sb.AppendLf("Resolve these items before doing anything else, then end with [[TASK_DONE]] only when the task is actually complete.");
        sb.AppendLf();
        foreach (var finding in findings.Take(MaxFindings))
        {
            sb.AppendLf($"- [ ] {finding}");
        }
        var commitsBlock = RunOutcomePolicy.RenderPriorCommitsBlock(priorCommits);
        if (commitsBlock.Length > 0)
        {
            sb.Append(commitsBlock);
            sb.AppendLf();
        }
        sb.AppendLf();
        sb.AppendLf("If any item cannot be completed, stop and end with [[TASK_BLOCKED:missing-dependency-xyz]], replacing the example reason with the actual short reason, instead of claiming done.");
        return sb.ToString();
    }

    /// <summary>
    /// Pull the agent's self-reported <c>Result:</c> token (e.g. "Success",
    /// "Partial", "Failed") out of a status / close-out surface. Returns null
    /// when no <c>Result:</c> line is present. Shares the exact regex the
    /// finding scan uses so the two cannot drift; exposed so the Codex
    /// evidence-based-completion evaluator can read the same token the gate
    /// reads.
    /// </summary>
    public static string? ExtractResultToken(string? statusMarkdown)
    {
        if (string.IsNullOrEmpty(statusMarkdown)) return null;
        var match = ResultLineRegex.Match(statusMarkdown);
        if (!match.Success) return null;
        var token = match.Groups["result"].Value.Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>
    /// True when a <c>Result:</c> token denotes a successful close-out. Same
    /// vocabulary as the contradiction rule's <see cref="SuccessResultRegex"/>.
    /// </summary>
    public static bool IsSuccessResultToken(string? resultToken)
        => resultToken is not null && SuccessResultRegex.IsMatch(resultToken.Trim());

    public static int? ExtractLatestExitCode(string? recentLog)
    {
        if (string.IsNullOrWhiteSpace(recentLog)) return null;
        var matches = CliExitRegex.Matches(recentLog);
        if (matches.Count == 0) return null;
        return int.TryParse(matches[^1].Groups["code"].Value, out var code) ? code : null;
    }

    /// <summary>
    /// Completion ruling based only on durable structured evidence. Narrative
    /// status text is deliberately absent from this API.
    /// </summary>
    public static Decision EvaluateStructured(
        CompletionAcceptanceRecord record,
        int priorReissues,
        int maxReissues)
    {
        if (record.Blockers.Count > 0)
        {
            return new Decision
            {
                Action = CompletionGateAction.Escalate,
                Findings = record.Blockers.Select(b => $"{b.Kind}: {b.Reason} (source: {b.Source})").ToList(),
                Reason = record.DecisionReason,
            };
        }

        if (record.Lifecycle.TurnComplete && record.Lifecycle.ImplementationComplete)
            return new Decision { Reason = record.DecisionReason };

        var evidenceReasons = record.Evidence
            .Where(e => !e.SupportsCompletion && e.Kind != "results-artifacts")
            .Select(e => $"{e.Kind}: {e.Reason} (source: {e.Source})")
            .ToList();
        if (evidenceReasons.Count == 0)
            evidenceReasons.Add("Structured turn or implementation completion evidence is absent.");

        return new Decision
        {
            Action = priorReissues >= maxReissues ? CompletionGateAction.Escalate : CompletionGateAction.Reissue,
            Findings = evidenceReasons,
            Reason = priorReissues >= maxReissues
                ? "Structured completion evidence remained incomplete after the bounded reissue budget."
                : "Structured completion evidence is incomplete; reissuing with explicit evidence reasons.",
        };
    }

    public static bool ExtractLatestRunCompleted(string? recentLog)
    {
        if (string.IsNullOrWhiteSpace(recentLog)) return false;
        var matches = CliExitRegex.Matches(recentLog);
        return matches.Count > 0 && matches[^1].Groups["status"].Value.Equals("completed", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> ExtractFindings(string? statusMarkdown, string? recentLog)
    {
        var findings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? text)
        {
            var normalized = Normalize(text);
            if (normalized.Length == 0) return;
            if (seen.Add(normalized)) findings.Add(normalized);
        }

        var status = statusMarkdown ?? string.Empty;
        // Evidence is judged PER RUN: when the accumulated log's latest CLI
        // exit is a clean terminal (status=completed, exitCode=0), only the
        // latest run's window is scanned. A clean terminal run that came AFTER
        // older failure lines (a recovered apply_patch error, a prior attempt's
        // build break) supersedes them - without this, old-run evidence latches
        // and a task whose final pass verified the fix is reissued/escalated
        // forever (MKT-8).
        var logTail = TailLines(ScopeToLatestRunWindow(recentLog ?? string.Empty), 80);
        var result = ResultLineRegex.Match(status);
        string? resultToken = result.Success ? result.Groups["result"].Value.Trim() : null;
        if (resultToken is not null &&
            (resultToken.Equals("Partial", StringComparison.OrdinalIgnoreCase) ||
             resultToken.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
             resultToken.Equals("Blocked", StringComparison.OrdinalIgnoreCase)))
        {
            Add($"Status result is {resultToken}.");
        }

        foreach (var item in ExtractOpenItemsSection(status))
        {
            Add(item);
        }

        foreach (var line in EvidenceLines(status))
        {
            if (IncompleteEvidenceRegex.IsMatch(line))
                Add(line);
        }

        // The status summary is the preferred result surface. The log tail is a
        // fallback for failures the summarizer omitted, especially CLI/tool
        // errors near the final DONE marker.
        foreach (var line in EvidenceLines(logTail))
        {
            if (IncompleteEvidenceRegex.IsMatch(line))
                Add(line);
        }

        // Contradiction rule: the run claims success yet its own close-out
        // reports a build / compile / test failure. The generic scan above
        // already surfaces the raw failing line, but this makes the conflict
        // explicit so the orchestrator (and the operator) sees that the success
        // claim is contradicted by the evidence rather than just "a build line".
        if (resultToken is not null && SuccessResultRegex.IsMatch(resultToken))
        {
            var contradiction = FirstBuildErrorLine(status) ?? FirstBuildErrorLine(logTail);
            if (contradiction is not null)
                Add($"Status result is {resultToken} but build/test failure evidence was found: {contradiction}");
        }

        return findings.Count > MaxFindings ? findings.Take(MaxFindings).ToList() : findings;
    }

    private static IEnumerable<string> ExtractOpenItemsSection(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) yield break;

        var inSection = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSection = line.Trim().Equals("## Open Items", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection) continue;
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (IsNoneLine(trimmed)) continue;

            var uncheckedMatch = UncheckedItemRegex.Match(trimmed);
            if (uncheckedMatch.Success)
            {
                var text = uncheckedMatch.Groups["text"].Value;
                if (!IsNoOpItemText(text)) yield return text;
                continue;
            }

            var bullet = BulletRegex.Match(trimmed);
            var itemText = bullet.Success ? bullet.Groups["text"].Value : trimmed;
            if (!IsNoOpItemText(itemText)) yield return itemText;
        }
    }

    private static string? FirstBuildErrorLine(string text)
    {
        foreach (var line in EvidenceLines(text))
        {
            if (BuildErrorEvidenceRegex.IsMatch(line))
                return line;
        }
        return null;
    }

    /// <summary>
    /// Scope the accumulated multi-run log to the LATEST run's window when that
    /// run terminated clean (exit marker <c>status=completed, exitCode=0</c>).
    /// The window opens at the run's own <c>[taskboard] Started ... CLI</c>
    /// marker (fallback: after the previous run's exit marker) and extends to
    /// the end of the log, so post-exit orchestrator lines remain visible.
    /// When the latest exit is not clean - or no exit marker exists - the full
    /// log is returned unchanged: historical failure evidence stays in force
    /// until a clean terminal run supersedes it.
    /// </summary>
    internal static string ScopeToLatestRunWindow(string recentLog)
    {
        if (string.IsNullOrWhiteSpace(recentLog)) return recentLog;
        if (ExtractLatestExitCode(recentLog) != 0 || !ExtractLatestRunCompleted(recentLog))
            return recentLog;

        var lines = recentLog.Replace("\r\n", "\n").Split('\n');
        var lastExitIdx = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (CliExitRegex.IsMatch(lines[i])) { lastExitIdx = i; break; }
        }
        if (lastExitIdx < 0) return recentLog;

        // Preferred boundary: the latest run's own Started marker.
        for (var i = lastExitIdx - 1; i >= 0; i--)
        {
            if (CliStartedRegex.IsMatch(lines[i]))
                return string.Join('\n', lines[i..]);
            // Fallback boundary: the previous run's exit marker - everything
            // before it belongs to older runs.
            if (CliExitRegex.IsMatch(lines[i]))
                return string.Join('\n', lines[(i + 1)..]);
        }
        // No earlier boundary: the whole log already IS the latest run.
        return recentLog;
    }

    private static IEnumerable<string> EvidenceLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (IsNoneLine(line)) continue;
            // Drop echoed source code (grep/cat line-number output): its keywords
            // are identifiers/comments in printed source, not the run's own
            // close-out claims. See ToolSourceEchoRegex.
            if (ToolSourceEchoRegex.IsMatch(line)) continue;
            // Drop echoed command invocations ("● Run \"...\" -Command \"rg -n
            // unfinish...\""): a run investigating a prior verdict must not
            // re-trigger it with its own search terms.
            if (ToolInvocationEchoRegex.IsMatch(line)) continue;
            // Drop echoes / narrations of the gate's own prior verdict - the
            // self-sustaining "unfinished-work evidence" loop (MKT-8).
            if (GateVerdictEchoRegex.IsMatch(line)) continue;
            // Drop explicitly approval-gated deliverable notes: waiting for the
            // operator's explicit approval is a designed stop, not open work.
            if (OperatorApprovalGateItemRegex.IsMatch(line)) continue;
            // Drop items the run explicitly disclaimed as pre-existing /
            // out-of-scope: they are not unfinished work this change owns
            // (AGT-1986). See PreExistingOutOfScopeRegex.
            if (PreExistingOutOfScopeRegex.IsMatch(line)) continue;
            // Drop durable pipeline-history output that repeats a prior gate
            // verdict. It is an artifact echo, not this run's build output.
            if (PipelineHistoryEchoRegex.IsMatch(line)) continue;
            // Drop only explicitly disclaimed false-positive failure signals.
            // Genuine current or merely historical failures remain evidence.
            if (ExplicitFalsePositiveRegex.IsMatch(line)) continue;
            // Drop a fixed-problem narrative only when it explicitly calls the
            // build signal stale/superseded. This covers a successful status
            // overview without weakening current failure detection.
            if (SupersededBuildEvidenceRegex.IsMatch(line)) continue;
            // "No unfinished evidence" is evidence of completion, not an
            // unfinished-work finding.
            if (NegatedIncompleteEvidenceRegex.IsMatch(line)) continue;
            yield return line;
        }
    }

    private static bool IsNoneLine(string line)
    {
        var normalized = line.Trim().Trim('-', '*', '+', '.', ':').Trim();
        return normalized.Equals("none", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("n/a", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("no open items", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when an extracted open-item's text is a no-op "nothing open"
    /// declaration rather than a real unfinished item. Tests only the FIRST
    /// sentence (up to the first '.'), so a trailing managed-run boilerplate
    /// clause ("None. Changes left in working tree per managed-run guidelines.")
    /// is still recognised, while a genuine item that merely opens with the word
    /// ("None of the routes are wired") is preserved. See ASS-797.
    /// </summary>
    private static bool IsNoOpItemText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var firstSentence = text.Split('.', 2)[0];
        if (NoOpOpenItemRegex.IsMatch(firstSentence)) return true;
        // Explicitly pre-existing / out-of-scope items are not work this change
        // owns, so they can never be "closed" by the run - treat them as no-op so
        // the gate does not escalate a fully-merged run over them (AGT-1986).
        if (PreExistingOutOfScopeRegex.IsMatch(text)) return true;
        // An item that merely narrates the gate's own prior verdict
        // ("Auto-review flagged one unfinished close-out item; awaiting
        // resolution") is meta-text about the review loop, not work the agent
        // can do - counting it re-feeds the verdict to itself (MKT-8).
        if (GateVerdictEchoRegex.IsMatch(text)) return true;
        // An approval-gated deliverable ("remains locked pending explicit
        // approval") waits on the operator by design; the agent can never
        // close it, so it must not count as unfinished work (MKT-8).
        if (OperatorApprovalGateItemRegex.IsMatch(text)) return true;
        // Non-actionable commit/push delegation: the platform owns the commit
        // boundary, so the agent can never close such an item. Scanned over the
        // full text (the delegation clause may be the second sentence).
        return PlatformOwnedCommitItemRegex.IsMatch(text);
    }

    private static string Normalize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return string.Empty;
        var text = candidate.Replace("\r", " ").Replace("\n", " ").Trim();
        text = Regex.Replace(text, @"^[-*+]\s*(\[\s*[xX ]?\s*\]\s*)?", "");
        text = WhitespaceRun.Replace(text, " ").Trim();
        if (text.Length > MaxFindingLength)
            text = text[..(MaxFindingLength - 3)].TrimEnd() + "...";
        return text;
    }

    private static string TailLines(string text, int n)
    {
        if (string.IsNullOrEmpty(text) || n <= 0) return string.Empty;
        var lines = text.Split('\n');
        if (lines.Length <= n) return text;
        return string.Join('\n', lines[^n..]);
    }
}
