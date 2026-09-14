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

/** Everything the detail panel and the board chip render for a parked card. */
export interface ParkedBlockerView {
  /** Raw park category, for `data-` attributes and tests. */
  blockerType: string;
  /** Human label for the park category. */
  blockerLabel: string;
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
    status: park.recallStatus,
    label: park.evaluationStale ? 'Never checked' : known?.label ?? park.recallStatus,
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

/** Build the full view, or null when the card is not parked. */
export function buildParkedBlockerView(info: Pick<TaskInfo, 'parkedBlocker'>): ParkedBlockerView | null {
  const park = info.parkedBlocker;
  if (!park) return null;

  const blockerLabel = parkedBlockerLabel(park.blockerType);
  const parkedFor = parkedForLabel(park.parkedForSeconds);
  const question = park.decision?.question?.trim() || null;
  const questionId = park.decision?.questionId?.trim() || null;

  return {
    blockerType: park.blockerType,
    blockerLabel,
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
    conditionKind: park.conditionKind,
    recall: recallView(park),
    openItems: parkedOpenItems(park),
    chipLabel: `${park.requiresDecisionCard ? 'Waiting on you' : 'Parked'} · ${parkedFor}`,
    chipTooltip: `${blockerLabel}, parked for ${parkedFor}. ${question || park.reason?.trim() || UNSTATED_QUESTION}`,
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
export function buildParkedBadge(info: Pick<TaskInfo, 'parkedBlocker'>): ParkedBoardBadge | null {
  const view = buildParkedBlockerView(info);
  if (!view) return null;
  return {
    label: view.chipLabel,
    tone: view.isDecision ? 'decision' : 'parked',
    glyph: view.isDecision ? '?' : '\u26a0',
    tooltip: view.chipTooltip,
  };
}
