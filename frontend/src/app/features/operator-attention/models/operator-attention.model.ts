import { laneName } from '../../../models/lane-presentation';
import { TaskState } from '../../../models/task.model';

/** One open fact of the operator decision stream (`GET /api/operator/attention`). */
export interface OperatorAttentionItem {
  /** Occurrence key: stable until the fact clears. */
  id: string;
  type: string;
  project: string | null;
  cardKey: string | null;
  cards: string[];
  reason: string;
  observedAt: string;
  suggestedOperations: string[];
}

export interface OperatorAttentionResponse {
  items: OperatorAttentionItem[];
  count: number;
}

/** The decision-event classes of the operator stream, in the order the list shows them. */
export type AttentionClass =
  | 'decision' | 'escalation' | 'human-review' | 'batch' | 'batch-record' | 'push' | 'quota' | 'host' | 'other';

/** Where a row's primary action leads. */
export type AttentionTarget = 'card' | 'batch-gate' | 'cli-usage' | 'hosts' | 'activity';

export interface AttentionClassSpec {
  readonly key: AttentionClass;
  readonly label: string;
  readonly action: string;
  readonly target: AttentionTarget;
}

const CLASSES: readonly AttentionClassSpec[] = [
  { key: 'decision', label: 'Decision cards', action: 'Decide', target: 'card' },
  { key: 'escalation', label: laneName(TaskState.Escalated), action: 'Open card', target: 'card' },
  { key: 'human-review', label: laneName(TaskState.HumanReview), action: 'Review', target: 'card' },
  { key: 'batch', label: 'Batch red or isolating', action: 'Open batch gate', target: 'batch-gate' },
  { key: 'batch-record', label: 'Batch record unreadable', action: 'Open batch gate', target: 'batch-gate' },
  { key: 'push', label: 'Overdue pending push', action: 'Open batch gate', target: 'batch-gate' },
  { key: 'quota', label: 'Quota exhausted or fallback active', action: 'Open CLI usage', target: 'cli-usage' },
  { key: 'host', label: 'Host not claiming', action: 'Open hosts', target: 'hosts' },
  { key: 'other', label: 'Other', action: 'Open Activity', target: 'activity' },
];

const CLASS_OF_TYPE: Readonly<Record<string, AttentionClass>> = {
  'decision-card.opened': 'decision',
  'escalation.opened': 'escalation',
  'human-review.ready': 'human-review',
  'batch.red': 'batch',
  'batch.isolating': 'batch',
  'batch.record-unreadable': 'batch-record',
  'push.pending-overdue': 'push',
  'quota.exhausted': 'quota',
  'quota.fallback-active': 'quota',
  'backend.stalled': 'host',
  'progress.idle-with-ready': 'host',
};

/** The class of an event type; a type this build does not know yet lands in `other`. */
export function attentionClassOf(type: string): AttentionClass {
  return CLASS_OF_TYPE[type] ?? 'other';
}

export interface AttentionGroup {
  readonly spec: AttentionClassSpec;
  readonly items: readonly OperatorAttentionItem[];
}

/**
 * Groups the rows by class in the fixed class order. Every item lands in
 * exactly one group, so the group counts add up to the header count (R3).
 */
export function groupAttention(items: readonly OperatorAttentionItem[]): AttentionGroup[] {
  const byClass = new Map<AttentionClass, OperatorAttentionItem[]>();
  for (const item of items) {
    const key = attentionClassOf(item.type);
    const bucket = byClass.get(key);
    if (bucket) bucket.push(item);
    else byClass.set(key, [item]);
  }
  return CLASSES.filter(spec => byClass.has(spec.key)).map(spec => ({ spec, items: byClass.get(spec.key)! }));
}

/** The card a card action opens, when the row names one. */
export function attentionCard(item: OperatorAttentionItem): string | null {
  return item.cardKey ?? (item.cards.length === 1 ? item.cards[0] : null);
}

/**
 * A card row without a card falls back to the Activity feed, and a batch row
 * without a project cannot open a batch gate; both still offer one action.
 */
export function attentionTarget(item: OperatorAttentionItem, spec: AttentionClassSpec): AttentionTarget {
  if (spec.target === 'card' && !attentionCard(item)) return 'activity';
  if (spec.target === 'batch-gate' && !item.project) return 'activity';
  return spec.target;
}

export function attentionActionLabel(item: OperatorAttentionItem, spec: AttentionClassSpec): string {
  return attentionTarget(item, spec) === spec.target ? spec.action : 'Open Activity';
}

/** Compact age of a fact: "just now", "12 min", "3 h", "2 d". */
export function attentionAge(observedAt: string, nowMs: number): string {
  const at = Date.parse(observedAt);
  if (!Number.isFinite(at)) return '';
  const minutes = Math.max(0, Math.floor((nowMs - at) / 60_000));
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours} h`;
  return `${Math.floor(hours / 24)} d`;
}
