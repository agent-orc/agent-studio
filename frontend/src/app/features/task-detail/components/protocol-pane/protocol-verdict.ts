import { TaskState } from '../../../../models/task.model';
import type { CliExecution, TaskOutcomeIssue, TaskSummaryStatus } from '../../../../models/task.model';
import { lanePresentation } from '../../../../models/lane-presentation';
import type { PipelineExecutionRecord } from '../../../task-pipeline';
import type { OutcomeAssessment } from '../agent-outcome.util';

/** Legacy visual tone retained for selectors and color-token compatibility. */
export type ProtocolVerdictKind = 'ok' | 'problem' | 'unclear';
export type AuthoritativeRunOutcomeStatus = 'failed' | 'needs-decision' | 'unclear' | 'succeeded';

export interface RunOutcomeSignal {
  source: 'runner' | 'execution' | 'pipeline' | 'status' | 'activity' | 'review' | 'lane' | 'summary';
  status: AuthoritativeRunOutcomeStatus;
  label: string;
  detail: string;
  /**
   * Lane key this signal speaks for, set only on `source: 'lane'`. When such a
   * signal leads the verdict, the Result header wears that lane's tone instead
   * of the generic status tone, so the lane reads in one colour everywhere
   * (AGT-2715).
   */
  lane?: string;
}

export interface ProtocolVerdict {
  kind: ProtocolVerdictKind;
  /** Canonical four-state status shared by banner, Result, and Pipeline. */
  status: AuthoritativeRunOutcomeStatus;
  /** Raw evidence exposed only inside the "Why this status?" disclosure. */
  signals: RunOutcomeSignal[];
  emoji: string;
  label: string;
  detail: string;
  /**
   * Lane key when the leading signal is the lane itself, else null. Lets the
   * Result header tint from the lane tone rather than the generic status tone
   * (AGT-2715).
   */
  lane: string | null;
  /**
   * Run duration as written by the runner into the `# Status` header of
   * status.md (e.g. `4 min`). Surfaced as a compact inline stat beside the
   * outcome instead of leaving the Status section in the rendered body.
   * Null when status.md has no Duration line yet.
   */
  duration: string | null;
}

export interface ProtocolVerdictInputs {
  isRunning: boolean;
  summaryStatus: TaskSummaryStatus;
  /** Raw markdown of status.md (may be null/empty when no run yet). */
  statusMarkdown: string | null | undefined;
  outcomeIssue: TaskOutcomeIssue | null | undefined;
  /** Has any cli-output / log activity been observed for this job at all? */
  hasActivity: boolean;
  /** Canonical lane key, used as one raw signal. */
  laneState?: string | null;
  /** Latest orchestrator-review verdict, used as one raw signal. */
  orchestratorVerdict?: 'pending' | 'reissue' | 'escalate' | 'accept' | null;
  /**
   * True when status.md provenance points at an older pipeline attempt than
   * the current execution. Its outcome is history and cannot lead the banner.
   */
  statusSuperseded?: boolean;
  execution?: CliExecution | null;
  pipelineExecution?: PipelineExecutionRecord | null;
  activityOutcome?: OutcomeAssessment | null;
}

/**
 * Collapse current-run signals with one strict precedence:
 * failed > needs-decision > unclear > succeeded. While a run is active, stale
 * terminal records are excluded and the current run remains Running.
 */
export function deriveProtocolVerdict(input: ProtocolVerdictInputs): ProtocolVerdict {
  if (input.isRunning) {
    return presentation('unclear', 'Running', 'Agent is still working. No terminal outcome exists for this run yet.', [
      signal('execution', 'unclear', 'Run is active', 'The current CLI process is still running.'),
    ], input.statusMarkdown);
  }

  const signals = collectSignals(input);
  if (signals.length === 0) {
    signals.push(signal(
      input.hasActivity ? 'activity' : 'status',
      'unclear',
      input.hasActivity ? 'Unclear' : 'No run yet',
      input.hasActivity
        ? 'The last run produced output but no classifiable terminal signal.'
        : 'No run outcome has been recorded yet.',
    ));
  }
  const leading = resolveAuthoritativeRunOutcome(signals)!;
  return presentation(
    leading.status, leading.label, leading.detail, signals, input.statusMarkdown, leading.lane ?? null,
  );
}

const OUTCOME_RANK: Record<AuthoritativeRunOutcomeStatus, number> = {
  failed: 4,
  'needs-decision': 3,
  unclear: 2,
  succeeded: 1,
};

/**
 * Select the one primary signal for a run. Severity always wins over source
 * order; equal-status ties keep the first signal so the caller can define a
 * stable source priority while collecting evidence.
 */
export function resolveAuthoritativeRunOutcome(
  signals: readonly RunOutcomeSignal[],
): RunOutcomeSignal | null {
  if (signals.length === 0) return null;
  return signals.reduce((winner, candidate) =>
    OUTCOME_RANK[candidate.status] > OUTCOME_RANK[winner.status] ? candidate : winner,
  );
}

function collectSignals(input: ProtocolVerdictInputs): RunOutcomeSignal[] {
  const signals: RunOutcomeSignal[] = [];
  const issue = input.outcomeIssue;
  if (issue) {
    const severity = (issue.severity ?? '').toLowerCase();
    const status: AuthoritativeRunOutcomeStatus = severity === 'high'
      ? 'failed'
      : severity === 'warn' ? 'needs-decision' : 'unclear';
    signals.push(signal('runner', status, issue.label || issue.kind, issue.summary || issue.kind));
  }

  const executionStatus = executionOutcome(input.execution);
  if (executionStatus) signals.push(executionStatus);

  const pipeline = input.pipelineExecution;
  if (pipeline) {
    const failed = (pipeline.steps ?? []).filter(step => step.status === 'failed');
    if (failed.length > 0) {
      signals.push(signal(
        'pipeline',
        'failed',
        'Pipeline failure',
        `${failed.length} pipeline step${failed.length === 1 ? '' : 's'} failed: ${failed.map(step => step.stepId).join(', ')}.`,
      ));
    } else if (pipeline.completedAt) {
      signals.push(signal('pipeline', 'succeeded', 'Pipeline completed', 'All recorded pipeline steps completed without failure.'));
    } else if ((pipeline.steps ?? []).some(step => step.status === 'running' || step.status === 'pending')) {
      signals.push(signal('pipeline', 'unclear', 'Pipeline incomplete', 'The current pipeline has not reached a terminal result.'));
    }
  }

  if (input.statusSuperseded) {
    signals.push(signal(
      'status',
      'unclear',
      'Current attempt',
      'A newer attempt is active. The previous status document is retained as historical evidence only.',
    ));
  }
  const statusSignal = input.statusSuperseded ? null : markdownOutcome(input.statusMarkdown);
  if (statusSignal) signals.push(statusSignal);

  const activity = input.activityOutcome;
  if (activity) signals.push(activitySignal(activity));

  switch (input.orchestratorVerdict) {
    case 'accept':
      signals.push(signal('review', 'succeeded', 'Review accepted', 'The orchestrator accepted the reviewed stand.'));
      break;
    case 'escalate':
    case 'reissue':
    case 'pending':
      signals.push(signal('review', 'needs-decision', 'Review decision', `The orchestrator verdict is ${input.orchestratorVerdict}.`));
      break;
  }

  // AGT-2715: a lane signal is named by the lane presentation, never by a
  // string of its own — "Human review lane" here versus "Review" on the header
  // chip is exactly the drift the operator reported. `lane` is carried through
  // so the Result header can wear the lane's tone when this signal leads.
  if (input.laneState === TaskState.HumanReview || input.laneState === TaskState.Escalated) {
    const lane = lanePresentation(input.laneState);
    signals.push(signal('lane', 'needs-decision', lane.name, lane.sentence, lane.state));
  } else if (input.laneState === TaskState.Completed || input.laneState === TaskState.Archive) {
    const lane = lanePresentation(input.laneState);
    signals.push(signal('lane', 'succeeded', lane.name, lane.sentence, lane.state));
  }

  if (input.summaryStatus === 'failed') {
    signals.push(signal('summary', 'unclear', 'Result summary failed', 'The result summary could not be generated.'));
  } else if (input.summaryStatus === 'degraded') {
    signals.push(signal(
      'summary',
      'unclear',
      'Result degraded',
      'The summary retry budget was exhausted. The completed core run remains reviewable.',
    ));
  }
  return signals;
}

function executionOutcome(execution: CliExecution | null | undefined): RunOutcomeSignal | null {
  if (!execution) return null;
  switch ((execution.runOutcome ?? '').toLowerCase()) {
    case 'failed':
    case 'interrupted':
      return signal('execution', 'failed', 'Execution failed', `The terminal run outcome is ${execution.runOutcome}.`);
    case 'blocked':
    case 'needs-input':
      return signal('execution', 'needs-decision', 'Execution needs a decision', `The terminal run outcome is ${execution.runOutcome}.`);
    case 'success':
    case 'noop':
      return signal('execution', 'succeeded', 'Execution succeeded', `The terminal run outcome is ${execution.runOutcome}.`);
    case 'committed-partial':
      return signal(
        'execution',
        'needs-decision',
        'Partial result',
        'The run committed work but did not produce a conclusive terminal verdict.',
      );
    case 'unknown':
      return signal('execution', 'unclear', 'Execution unclear', 'The runner could not classify the terminal outcome.');
    default:
      if (execution.status === 'failed') return signal('execution', 'failed', 'Execution failed', 'The process ended with failed status.');
      if (execution.status === 'completed') return signal('execution', 'succeeded', 'Execution completed', 'The process ended with completed status.');
      return null;
  }
}

function markdownOutcome(markdown: string | null | undefined): RunOutcomeSignal | null {
  const sentinel = parseSentinel(markdown);
  if (sentinel) {
    const detail = sentinel.reason || `Agent emitted TASK_${sentinel.kind.toUpperCase()}.`;
    if (sentinel.kind === 'done') return signal('status', 'succeeded', 'Done', detail);
    if (sentinel.kind === 'noop') return signal('status', 'succeeded', 'No action needed', detail);
    if (sentinel.kind === 'blocked') return signal('status', 'needs-decision', 'Blocked', detail);
    return signal('status', 'needs-decision', 'Needs input', detail);
  }
  const result = parseResultLine(markdown);
  if (!result) return null;
  if (result === 'failed') return signal('status', 'failed', 'Failed', 'status.md records Result: Failed.');
  if (result === 'blocked') return signal('status', 'needs-decision', 'Blocked', 'status.md records Result: Blocked.');
  if (result === 'partial') return signal('status', 'needs-decision', 'Partial', 'status.md records Result: Partial.');
  if (result === 'needsinput') return signal('status', 'needs-decision', 'Needs input', 'status.md records Result: NeedsInput.');
  if (result === 'success') {
    const blocker = scanForBlockers(markdown);
    if (blocker) return signal(
      'status',
      'needs-decision',
      'Blocked',
      blocker.sentence ? `${blocker.section}: ${blocker.sentence}` : `${blocker.section} contains "${blocker.phrase}".`,
    );
  }
  return signal('status', 'succeeded', result === 'noop' ? 'No action needed' : 'Success', `status.md records Result: ${result}.`);
}

function activitySignal(activity: OutcomeAssessment): RunOutcomeSignal {
  if (activity.kind === 'failed') return signal('activity', 'failed', 'Activity error', activity.summary);
  if (activity.kind === 'blocked' || activity.kind === 'question' || activity.kind === 'needs_input') {
    return signal('activity', 'needs-decision', 'Agent reply needs a decision', activity.summary);
  }
  if (activity.kind === 'done') return signal('activity', 'succeeded', 'Agent reply completed', activity.summary);
  return signal('activity', 'unclear', 'Agent reply unclear', activity.summary || 'The agent reply has no clear terminal verdict.');
}

function signal(
  source: RunOutcomeSignal['source'],
  status: AuthoritativeRunOutcomeStatus,
  label: string,
  detail: string,
  lane?: string,
): RunOutcomeSignal {
  return lane ? { source, status, label, detail, lane } : { source, status, label, detail };
}

function presentation(
  status: AuthoritativeRunOutcomeStatus,
  label: string,
  detail: string,
  signals: RunOutcomeSignal[],
  markdown: string | null | undefined,
  lane: string | null = null,
): ProtocolVerdict {
  const kind: ProtocolVerdictKind = status === 'failed' ? 'problem' : status === 'succeeded' ? 'ok' : 'unclear';
  const emoji = status === 'failed' ? '🔴' : status === 'succeeded' ? '🟢' : status === 'needs-decision' ? '🟠' : '🟡';
  return { kind, status, signals, emoji, label, detail, lane, duration: parseDuration(markdown) };
}

const DURATION_RE = /^\s*-\s*Duration:\s*(.+?)\s*$/im;

/**
 * Pull the `- Duration: <text>` line out of a status.md so it can render
 * as a chip on the verdict pill instead of staying buried in the Status
 * section. Returns the raw text (e.g. "4 min", "12s"), trimmed; null when
 * absent.
 */
export function parseDuration(markdown: string | null | undefined): string | null {
  if (!markdown) return null;
  const m = DURATION_RE.exec(markdown);
  return m ? m[1].trim() : null;
}

const STATUS_HEADING_RE = /^#{1,2}\s+Status\s*$/i;
const ANY_HEADING_RE = /^#{1,6}\s+/;

/**
 * Drop the `# Status` / `## Status` header and the immediately-following
 * Result / Duration list from status.md before handing it to the
 * markdown renderer. The verdict pill carries those two facts; leaving
 * them in the body would duplicate the read.
 *
 * Everything from the Status heading up to (but not including) the next
 * heading is removed; the rest of the document is returned verbatim with
 * leading blank lines trimmed.
 */
export function stripStatusHeader(markdown: string | null | undefined): string {
  if (!markdown) return '';
  const lines = markdown.replace(/\r\n/g, '\n').split('\n');
  const out: string[] = [];
  let i = 0;
  while (i < lines.length) {
    if (STATUS_HEADING_RE.test(lines[i])) {
      i++;
      while (i < lines.length && !ANY_HEADING_RE.test(lines[i])) {
        i++;
      }
      continue;
    }
    out.push(lines[i]);
    i++;
  }
  let start = 0;
  while (start < out.length && out[start].trim() === '') start++;
  return out.slice(start).join('\n');
}

type SentinelKind = 'done' | 'blocked' | 'needs_input' | 'noop';
// Reason capture is lazy up to the closing `]]` (`[\s\S]*?`) rather than
// "any char that is not `]`" so a reason that itself contains a single `]`,
// a quote, or other special characters is preserved whole instead of being
// truncated at the first bracket (BEFUND 1: robust against Sonderzeichen).
const SENTINEL_RE = /\[\[TASK_(DONE|BLOCKED|NEEDS_INPUT|NOOP)(?::([\s\S]*?))?\]\]/gi;

function parseSentinel(markdown: string | null | undefined): { kind: SentinelKind; reason: string | null } | null {
  if (!markdown) return null;
  let last: { kind: SentinelKind; reason: string | null } | null = null;
  SENTINEL_RE.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = SENTINEL_RE.exec(markdown)) !== null) {
    last = { kind: m[1].toLowerCase() as SentinelKind, reason: (m[2] ?? '').trim() || null };
  }
  return last;
}

type ResultKind = 'success' | 'noop' | 'failed' | 'blocked' | 'partial' | 'needsinput';
const RESULT_RE = /^\s*-\s*Result:\s*([A-Za-z][A-Za-z _-]*)/im;

function parseResultLine(markdown: string | null | undefined): ResultKind | null {
  if (!markdown) return null;
  const m = RESULT_RE.exec(markdown);
  if (!m) return null;
  const normalised = m[1].trim().toLowerCase().replace(/[ _-]+/g, '');
  switch (normalised) {
    case 'success':    return 'success';
    case 'noop':       return 'noop';
    case 'failed':
    case 'fail':       return 'failed';
    case 'blocked':    return 'blocked';
    case 'partial':    return 'partial';
    case 'needsinput': return 'needsinput';
    default:           return null;
  }
}

// Phrases that flip a Haiku "Success" verdict into a Blocked verdict when they
// appear in the Notes / Open Items / What Was Done body of status.md. Keep one
// phrase per line so the list reads as a lint surface. EN + DE because the agent
// log can be either.
const BLOCKER_PHRASES = [
  'blocked',
  'blocker',
  'could not',
  'was prevented',
  'prevented from',
  'sandbox denied',
  'access denied',
  'forbidden',
  'requires manual',
  'requires external',
  'geblockt',
  'konnte nicht',
  'verhindert'
] as const;

const BLOCKER_SECTION_HEADINGS = new Set(['notes', 'open items', 'what was done']);

interface BlockerHit {
  phrase: string;
  /** The full sentence (or list-bullet line) containing the phrase, trimmed. */
  sentence: string;
  /** Human-readable section heading (e.g. "Notes"). */
  section: string;
}

export function scanForBlockers(markdown: string | null | undefined): BlockerHit | null {
  if (!markdown) return null;
  for (const section of splitSections(markdown)) {
    if (!BLOCKER_SECTION_HEADINGS.has(section.heading.trim().toLowerCase())) continue;
    const hit = findBlockerPhrase(section.body);
    if (hit) return { ...hit, section: section.heading.trim() };
  }
  return null;
}

function splitSections(markdown: string): { heading: string; body: string }[] {
  const lines = markdown.replace(/\r\n/g, '\n').split('\n');
  const out: { heading: string; body: string }[] = [];
  let current: { heading: string; body: string[] } | null = null;
  for (const line of lines) {
    const m = /^##\s+(.*)$/.exec(line);
    if (m) {
      if (current) out.push({ heading: current.heading, body: current.body.join('\n') });
      current = { heading: m[1], body: [] };
    } else if (current) {
      current.body.push(line);
    }
  }
  if (current) out.push({ heading: current.heading, body: current.body.join('\n') });
  return out;
}

function findBlockerPhrase(body: string): { phrase: string; sentence: string } | null {
  const lower = body.toLowerCase();
  let best: { phrase: string; index: number } | null = null;
  for (const phrase of BLOCKER_PHRASES) {
    const idx = lower.indexOf(phrase);
    if (idx >= 0 && (best === null || idx < best.index)) {
      best = { phrase, index: idx };
    }
  }
  if (!best) return null;
  return { phrase: best.phrase, sentence: extractSentence(body, best.index) };
}

/**
 * True when the `.`/`!`/`?` at `punctIndex` is a real sentence terminator:
 * followed by whitespace or end-of-text. A dot glued to the next character is
 * not a boundary, so file extensions (`protocol-verdict.ts`), decimals (`5.1`),
 * and abbreviations (`e.g.`) no longer split a sentence mid-word — the exact
 * cause of the "…ts' rendering 5 canonical states…" truncation (BEFUND 1).
 */
function isSentenceBoundary(text: string, punctIndex: number): boolean {
  const ch = text[punctIndex];
  if (ch !== '.' && ch !== '!' && ch !== '?') return false;
  const next = text[punctIndex + 1];
  return next === undefined || /\s/.test(next);
}

function extractSentence(text: string, atIndex: number): string {
  let start = atIndex;
  while (start > 0) {
    const ch = text[start - 1];
    if (ch === '\n') break;
    if (isSentenceBoundary(text, start - 1)) break;
    start--;
  }
  let end = atIndex;
  while (end < text.length) {
    if (text[end] === '\n') break;
    if (isSentenceBoundary(text, end)) { end++; break; }
    end++;
  }
  // Strip leading markdown bullet / block-quote / wrapping-quote noise and any
  // trailing wrapping quote so the surfaced reason reads as a clean sentence.
  return text
    .slice(start, end)
    .trim()
    .replace(/^[-*>\s"'`]+/, '')
    .replace(/["'`]+$/, '')
    .trim();
}
