/**
 * One source for how a parked card reads, on the board and in the task detail
 * (AGT-2816).
 *
 * AGT-2736 sat in `5e-escalated` for three days. The card showed
 * `Result: Success`, `Case: feature`, `Open Items: None`; what was actually true
 * is that the run had parked itself on an operator decision it could not take.
 * The park reason was on disk the whole time and no component rendered it.
 *
 * This module is the pure half of the fix: a `TaskInfo` goes in, a
 * presentation-ready view comes out. It is deliberately dependency-light so the
 * board chip and the detail panel cannot drift apart, and so the rules are
 * tested without mounting either.
 */
import type { ParkedBlockerStatus, ParkedDecisionOption, TaskInfo } from './task.model';

/** Sweep verdict tone; matches the shared chip tone vocabulary. */
export type ParkedRecallTone = 'ok' | 'warn' | 'neutral';

/** The latest sweep verdict, said plainly. */
export interface ParkedRecallView {
  status: string;
  label: string;
  tone: ParkedRecallTone;
  /** Why the sweep reached that verdict. */
  detail: string;
  /** ISO instant the current verdict was first observed, or null when nothing
   * has evaluated yet. Not a check interval: an unchanged verdict is never
   * re-persisted, so this is how long the verdict has HELD. */
  at: string | null;
  /** No sweep has ever evaluated this blocker. */
  stale: boolean;
  /** How long the current verdict has held, e.g. `3 days`; null when none. */
  heldFor: string | null;
}

/**
 * Who parked a card, chosen from the recorded lane-change cause (AGT-3586).
 * `unknown` makes no claim: an older marker without a cause, or a cause no
 * entry below describes.
 */
export type ParkOrigin =
  | 'run'
  | 'acceptance-audit'
  | 'review-loop-budget'
  | 'review-gate'
  | 'stall-intervention'
  | 'operator'
  | 'unknown';

/** One suggested step, tied to lane buttons by their triage action ids. */
export interface ParkNextStep {
  /** Buttons that take this step; the first one the lane offers is named. */
  actionIds: readonly string[];
  /** Completes "Use <button> ...", e.g. `to deliver the work again`. */
  purpose: string;
}

/** The park copy chosen from the recorded cause. */
export interface ParkCauseView {
  origin: ParkOrigin;
  cause: string | null;
  causeDetail: string | null;
  /** Badge label; null keeps the blocker-type label. */
  label: string | null;
  /** Sentence subject naming who parked the card; null when unknown. */
  parkedBy: string | null;
  /** Steps in order of preference; the host keeps the ones its lane offers. */
  nextSteps: readonly ParkNextStep[];
}

/** Everything the detail panel and the board chip render for a parked card. */
export interface ParkedBlockerView {
  /** Raw park category, for `data-` attributes and tests. */
  blockerType: string;
  /** Human label for the park category, or for the cause that parked the card. */
  blockerLabel: string;
  /** Who parked the card and what to do next, chosen from the recorded cause. */
  cause: ParkCauseView;
  /** The park reason as one sentence, without a slug prefix. */
  reason: string;
  /** This park waits for a person's choice rather than for a fix. */
  isDecision: boolean;
  /** The question, when the parking run stated one. */
  question: string | null;
  /** The park slug, shown as an id beside the question - never as the question. */
  questionId: string | null;
  /** The parking run supplied only a slug; the surfaces must say so. */
  questionMissing: boolean;
  /** The options the run had already weighed. */
  options: ParkedDecisionOption[];
  /** Repository-relative documents the run named. */
  documents: string[];
  /** Linked decision card, once decision cards exist. */
  decisionCardKey: string | null;
  /** Task-relative artifact holding the full agent message. */
  needsInputFile: string | null;
  parkedAt: string;
  /** e.g. `3 days`. */
  parkedFor: string;
  /** One sentence describing what must become true. */
  condition: string;
  conditionKind: string;
  recall: ParkedRecallView;
  /**
   * The park expressed as open items. Never empty: a card that parked itself
   * has, by definition, at least one open item, so no surface built from this
   * view can report zero.
   */
  openItems: string[];
  /** Short board chip copy, e.g. `Waiting on you · 3 days`. */
  chipLabel: string;
  /** Board chip hover text. */
  chipTooltip: string;
}

const BLOCKER_LABELS: Record<string, string> = {
  'operator-decision': 'Operator decision',
  'agent-needs-input': 'Agent needs input',
  'needs-human-input': 'Needs human input',
  'human-decision-needed': 'Human decision needed',
  'steer-unanswered': 'Steer unanswered',
  'review-subject-unmaterialisierbar': 'Review subject unavailable',
};

const RECALL_LABELS: Record<string, { label: string; tone: ParkedRecallTone }> = {
  recallable: { label: 'Precondition cleared', tone: 'ok' },
  blocked: { label: 'Still blocked', tone: 'warn' },
  undeterminable: { label: 'Nothing can check this', tone: 'neutral' },
};

/** Stated when the parking run left neither a question nor a reason. */
export const UNSTATED_QUESTION = 'the parking run recorded no question';

/** Stated when a park carries no readable reason. */
export const UNSTATED_REASON = 'No reason was recorded for this park.';

const REISSUE = ['reissue-escalated', 'send-back-to-ready'] as const;
const ACCEPT = ['accept-escalated', 'mark-done'] as const;
const ABORT = ['discard-escalated'] as const;

/** Escalation categories a run outcome produces: the run itself parked the card. */
const RUN_DETAILS = new Set([
  'agent-blocked', 'agent-needs-input', 'needs-human-input', 'needs-input-escalate', 'human-decision-needed',
  'steer-unanswered', 'no-completion-signal', 'noop-escalate', 'remote-outcome-unknown',
  'provider-rejected-request', 'external-completion-blocked', 'unverified-delivery', 'watchdog-kill',
  'permission-blocked', 'environment-blocker', 'auto-failure-park', 'failure-intervention', 'pickup-zombie',
  'empty-fast-exit', 'infra-crash', 'context-overflow', 'model-invalid', 'quota-exhausted', 'cli-launch-failed',
  'auth-refresh-failed', 'inconclusive-with-results', 'agent-git-violation',
]);

/** Review gates that rejected a delivery after the run ended. */
const REVIEW_GATE_DETAILS = new Set([
  'completion-gate', 'evidence-gate', 'solution-quality-gate', 'code-review-council', 'concept-review-failed',
  'lint-scss-double-fail', 'post-processing-failure', 'auto-review-escalation', 'ui-iteration-cap',
]);
const REVIEW_GATE_PREFIXES = ['aspect-verdict-', 'build-test-gate-'];

/** Spent automatic round budgets (card round budget, reissue loop breaker). */
const LOOP_BUDGET_DETAILS = new Set(['card-round-budget', 'reissue-budget-exhausted', 'empty-followup-diff']);

/** Watchdogs that parked a card because its queue stalled. */
const STALL_DETAILS = new Set(['gate-capacity']);

/** Lane-change causes a person's move records. */
const OPERATOR_CAUSES = new Set(['operator-decision', 'operator-move', 'operator-requeue', 'escalation-requeue']);

/**
 * The park copy for a recorded lane-change cause. The cause, not the reason
 * text, decides who the copy says parked the card: before AGT-3586 an
 * acceptance-audit park read "The parking run stated no question".
 */
export function parkCauseView(cause: string | null | undefined, causeDetail: string | null | undefined): ParkCauseView {
  const id = cause?.trim().toLowerCase() || null;
  const detail = causeDetail?.trim().toLowerCase() || null;
  const base = { cause: id, causeDetail: detail };
  if (id === 'acceptance-integration-failed') {
    return {
      ...base,
      origin: 'acceptance-audit',
      label: 'Acceptance audit',
      parkedBy: 'The acceptance audit',
      nextSteps: [
        { actionIds: REISSUE, purpose: 'to build and deliver the work again' },
        { actionIds: ABORT, purpose: 'if the work is no longer needed' },
      ],
    };
  }
  if (id === 'escalated' && detail) {
    if (LOOP_BUDGET_DETAILS.has(detail)) {
      return {
        ...base,
        origin: 'review-loop-budget',
        label: 'Review loop budget',
        parkedBy: 'The review loop',
        nextSteps: [
          { actionIds: REISSUE, purpose: 'to allow another round' },
          { actionIds: ACCEPT, purpose: 'to take the current delivery' },
        ],
      };
    }
    if (STALL_DETAILS.has(detail)) {
      // The gate-queue release panel owns this park's action.
      return { ...base, origin: 'stall-intervention', label: 'Gate queue stall', parkedBy: 'The gate queue watchdog', nextSteps: [] };
    }
    if (REVIEW_GATE_DETAILS.has(detail) || REVIEW_GATE_PREFIXES.some(prefix => detail.startsWith(prefix))) {
      return {
        ...base,
        origin: 'review-gate',
        label: null,
        parkedBy: 'A review gate',
        nextSteps: [
          { actionIds: REISSUE, purpose: 'to address the findings in a new round' },
          { actionIds: ACCEPT, purpose: 'to accept the delivery as it is' },
        ],
      };
    }
    if (RUN_DETAILS.has(detail)) return { ...base, origin: 'run', label: null, parkedBy: 'The latest run', nextSteps: [] };
  }
  if (id && OPERATOR_CAUSES.has(id)) return { ...base, origin: 'operator', label: null, parkedBy: 'An operator', nextSteps: [] };
  return { ...base, origin: 'unknown', label: null, parkedBy: null, nextSteps: [] };
}

/** Structural slice of a task timeline row, so this model stays feature-free. */
export interface ParkTimelineEvent {
  ts: string;
  kind: string;
  details?: Record<string, string> | null;
}

/**
 * The cause of a park whose marker predates the recorded cause: the newest
 * lane change into the parked lane at or before the park instant.
 */
export function parkCauseFromTimeline(
  park: Pick<ParkedBlockerStatus, 'parkedAt' | 'lane'>,
  events: readonly ParkTimelineEvent[],
): { cause: string; causeDetail: string | null } | null {
  const parkedMs = Date.parse(park.parkedAt);
  // The marker is written just after the ledger row; allow a little skew.
  const limit = Number.isFinite(parkedMs) ? parkedMs + 60_000 : Number.POSITIVE_INFINITY;
  for (let i = events.length - 1; i >= 0; i--) {
    const event = events[i];
    if (event.kind !== 'lane_changed' || !(Date.parse(event.ts) <= limit)) continue;
    if (park.lane && event.details?.['to'] && event.details['to'] !== park.lane) continue;
    const cause = event.details?.['cause']?.trim();
    return cause ? { cause, causeDetail: event.details?.['causeDetail']?.trim() || null } : null;
  }
  return null;
}

/** The recorded cause, else the one on the lane-change row that parked the card. */
export function resolveParkCause(
  park: ParkedBlockerStatus,
  events: readonly ParkTimelineEvent[] = [],
): ParkCauseView {
  if (park.cause?.trim()) return parkCauseView(park.cause, park.causeDetail);
  const fromTimeline = parkCauseFromTimeline(park, events);
  return parkCauseView(fromTimeline?.cause, fromTimeline?.causeDetail);
}

const SLUG = /^[a-z0-9]+(?:[-_][a-z0-9]+)+$/;

/**
 * The park reason as one sentence. A `[category]` or `category:` prefix is
 * dropped: the cause already names it, and a slug is not a sentence. A reason
 * that is only slugs (the `cause: detail` fallback) reads as unrecorded.
 *
 * The acceptance audit used to write `<category>: <instruction>` with one of
 * two fixed instructions ("rerun the gate", "repeat review") that named no
 * button the lane offers and carried no failure detail. Only those exact
 * legacy shapes are replaced by the fact the category stands for; any other
 * text after the prefix is the recorded failure and is kept.
 */
export function parkReasonSentence(reason: string | null | undefined, cause: ParkCauseView): string {
  const audit = cause.origin === 'acceptance-audit';
  let text = reason?.trim() ?? '';
  const bracket = /^\[([^\]]+)\]\s*/.exec(text);
  if (bracket) {
    text = text.slice(bracket[0].length);
  } else {
    const colon = /^([A-Za-z0-9_-]+):\s+/.exec(text);
    const head = colon?.[1].toLowerCase();
    if (colon && head && (SLUG.test(head) || head === cause.causeDetail || head === cause.cause)) {
      text = text.slice(colon[0].length);
      if (audit && LEGACY_AUDIT_INSTRUCTION.test(text.trim())) return acceptanceAuditFact(head);
    }
  }
  text = text.trim();
  if (!text || SLUG.test(text) || /^[a-z0-9-]+: [a-z0-9-]+$/.test(text)) {
    return audit ? acceptanceAuditFact(cause.causeDetail) : UNSTATED_REASON;
  }
  text = text.charAt(0).toUpperCase() + text.slice(1);
  return /[.!?]$/.test(text) ? text : `${text}.`;
}

/** The instructions the acceptance audit wrote before AGT-3586 (fatal, stale verdict); neither held a failure detail. */
const LEGACY_AUDIT_INSTRUCTION =
  /^(?:Recover the delivery, resolve the integration failure and rerun the gate|Integrate the current delivery and repeat review)\.?$/i;

function acceptanceAuditFact(category: string | null): string {
  if (category === 'missing-delivery') {
    return 'No delivery branch was found for this card, so its work cannot be integrated.';
  }
  if (category === 'delivery-verdict-stale') {
    return 'The review verdict no longer matches the current delivery, so it cannot be accepted.';
  }
  return 'The delivery could not be integrated.';
}

/**
 * "Use Continue (reissue) to deliver the work again, or Abort if the work is
 * no longer needed." Only buttons in `offered` are named, so the suggestion
 * never points at a control the lane lacks. Null when none is offered.
 */
export function parkNextStepSentence(
  steps: readonly ParkNextStep[],
  offered: readonly { id: string; label: string }[],
): string | null {
  const clauses: string[] = [];
  const used = new Set<string>();
  for (const step of steps) {
    const button = step.actionIds
      .map(id => offered.find(action => action.id === id))
      .find(action => !!action && !used.has(action.id));
    if (!button) continue;
    used.add(button.id);
    clauses.push(`${button.label} ${step.purpose}`);
  }
  if (clauses.length === 0) return null;
  return `Use ${clauses.join(', or ')}.`;
}

/** Title-case an unknown slug so a new park category still reads cleanly. */
export function parkedBlockerLabel(blockerType: string): string {
  const key = blockerType.trim().toLowerCase();
  if (BLOCKER_LABELS[key]) return BLOCKER_LABELS[key];
  return key
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(' ');
}

/**
 * Coarse age of a park: the operator's question is "days or hours?", never
 * "how many seconds?". Rounds down so the card never overstates the wait.
 */
export function parkedForLabel(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds));
  const minutes = Math.floor(total / 60);
  if (minutes < 1) return 'less than a minute';
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? '' : 's'}`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} hour${hours === 1 ? '' : 's'}`;
  const days = Math.floor(hours / 24);
  return `${days} day${days === 1 ? '' : 's'}`;
}

function recallView(park: ParkedBlockerStatus): ParkedRecallView {
  const known = RECALL_LABELS[park.recallStatus?.trim().toLowerCase() ?? ''];
  // "Nobody has checked" must never read as "still blocked". Conflating the two
  // is exactly how AGT-2220 stayed parked for four days after its precondition
  // had cleared. The projection reports the absence of a verdict as its own
  // state rather than defaulting it to the blocked one.
  return {
    status: park.recallStatus ?? '',
    label: park.evaluationStale ? 'Never checked' : known?.label ?? park.recallStatus ?? '',
    tone: park.evaluationStale ? 'neutral' : known?.tone ?? 'neutral',
    detail: park.detail?.trim() ?? '',
    at: park.lastEvaluatedAt,
    stale: park.evaluationStale,
    heldFor: park.evaluationAgeSeconds === null || park.evaluationAgeSeconds === undefined
      ? null
      : parkedForLabel(park.evaluationAgeSeconds),
  };
}

/**
 * The park's own open items. The first is always the park itself, so a parked
 * card cannot be summarised as having nothing open. The condition follows when
 * one is recorded.
 */
export function parkedOpenItems(park: ParkedBlockerStatus): string[] {
  const question = park.decision?.question?.trim();
  const reason = park.reason?.trim();
  const statement = question || reason || UNSTATED_QUESTION;
  const items = [
    `This card is parked (${parkedBlockerLabel(park.blockerType)}) and waits for a person: ${statement}`,
  ];
  const condition = park.conditionDescription?.trim();
  if (condition) items.push(`Clears when: ${condition}`);
  return items;
}

/**
 * Seconds the card has been parked at `nowMs`. The board card carries only
 * `parkedAt` (a read-time counter would change the card bytes on every read);
 * a payload that still carries the counter is used when `parkedAt` is absent.
 */
export function parkedSecondsAt(park: ParkedBlockerStatus, nowMs: number): number {
  const parkedAtMs = Date.parse(park.parkedAt);
  if (!Number.isNaN(parkedAtMs)) return Math.max(0, (nowMs - parkedAtMs) / 1000);
  return park.parkedForSeconds ?? 0;
}

/**
 * Build the full view, or null when the card is not parked. `timeline` lets a
 * park whose marker predates the recorded cause take it from the ledger.
 */
const STALE_VERDICT_PARK_DETAIL = 'delivery-verdict-stale';

/**
 * AGT-3621: a stale-verdict park says "integrate the current delivery". Once
 * the card's computed integration field says the delivery is integrated, that
 * precondition is met and the park no longer describes the card. The delivery
 * chain once wrote this park on a plain promotion, so integrated Human Review
 * cards carried a false "Only a person can clear this park" banner.
 */
export function parkPreconditionMet(
  info: Pick<TaskInfo, 'parkedBlocker'> & Partial<Pick<TaskInfo, 'integration'>>,
): boolean {
  const park = info.parkedBlocker;
  return !!park
    && park.cause === 'acceptance-integration-failed'
    && park.causeDetail === STALE_VERDICT_PARK_DETAIL
    && info.integration?.status === 'integrated';
}

export function buildParkedBlockerView(
  info: Pick<TaskInfo, 'parkedBlocker'> & Partial<Pick<TaskInfo, 'integration'>>,
  nowMs = Date.now(),
  timeline: readonly ParkTimelineEvent[] = [],
): ParkedBlockerView | null {
  const park = info.parkedBlocker;
  if (!park || parkPreconditionMet(info)) return null;

  const cause = resolveParkCause(park, timeline);
  const reason = parkReasonSentence(park.reason, cause);
  const blockerLabel = cause.label ?? parkedBlockerLabel(park.blockerType);
  const parkedFor = parkedForLabel(parkedSecondsAt(park, nowMs));
  const question = park.decision?.question?.trim() || null;
  const questionId = park.decision?.questionId?.trim() || null;

  return {
    blockerType: park.blockerType,
    blockerLabel,
    cause,
    reason,
    isDecision: park.requiresDecisionCard,
    question,
    questionId,
    questionMissing: !question,
    options: park.decision?.options ?? [],
    documents: park.decision?.documents ?? [],
    decisionCardKey: park.decision?.decisionCardKey ?? null,
    needsInputFile: park.needsInputFile,
    parkedAt: park.parkedAt,
    parkedFor,
    condition: park.conditionDescription?.trim() ?? '',
    conditionKind: park.conditionKind ?? '',
    recall: recallView(park),
    openItems: parkedOpenItems(park),
    chipLabel: `${park.requiresDecisionCard ? 'Waiting on you' : 'Parked'} · ${parkedFor}`,
    chipTooltip: `${blockerLabel}, parked for ${parkedFor}. ${question || reason}`,
  };
}

/**
 * Board badge for a parked card. Two tones, because the operator's question at
 * board level is "which of these wait for ME?": `decision` is a card parked on a
 * person's choice, `parked` is a card parked by a failure somebody has to fix.
 * Before this the board rendered one "Escalated" chip for both.
 */
export interface ParkedBoardBadge {
  label: string;
  tone: 'decision' | 'parked';
  glyph: string;
  tooltip: string;
}

/** The board badge for a parked card, or null when the card is not parked. */
export function buildParkedBadge(
  info: Pick<TaskInfo, 'parkedBlocker'> & Partial<Pick<TaskInfo, 'integration'>>,
  nowMs = Date.now(),
): ParkedBoardBadge | null {
  const view = buildParkedBlockerView(info, nowMs);
  if (!view) return null;
  return {
    label: view.chipLabel,
    tone: view.isDecision ? 'decision' : 'parked',
    glyph: view.isDecision ? '?' : '\u26a0',
    tooltip: view.chipTooltip,
  };
}
