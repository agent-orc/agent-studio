import type { PickupHoldStatus, TaskInfo, TaskLiveQueue } from './task.model';

/**
 * AGT-3579 - why a Ready card is waiting, in one place.
 *
 * A card waits for exactly one current reason: a card-level pickup hold
 * (AGT-2818), a runner reason the runner recorded for it (mode, slots, quota
 * defer, load throttle, provider limit), or nothing recorded yet ("queued").
 * The Ready lane header split and the card's live status line both read this
 * module, so the two can never name different reasons, and every card falls
 * into one bucket, so the split sums to the lane count (R3).
 */

/** Mechanism keys as the backend `PickupHoldMechanisms` spells them. */
const HOLD_LABELS: Record<string, string> = {
  'dependency-gate': 'Dependency gate',
  'dispatch-rejection': 'Dispatch refused',
  'crash-backoff': 'Crash cooldown',
  'pickup-policy': 'Pickup policy',
  'execution-placement': 'Execution placement',
  'claim-load-gate': 'Host load',
};

interface RunnerReasonText {
  /** Short noun for the lane header split. */
  label: string;
  /** Live status headline on the card. */
  headline: string;
}

/** Keys as the backend `RunnerPickupWaitCodes` spells them. */
const RUNNER_REASONS: Record<string, RunnerReasonText> = {
  'manual-mode': { label: 'manual mode', headline: 'Not picked · auto-pickup is off' },
  'paused': { label: 'runner paused', headline: 'Not picked · runner paused' },
  'breaker-cooldown': { label: 'breaker cooldown', headline: 'Not picked · breaker cooldown' },
  'pending-mode': { label: 'mode change pending', headline: 'Not picked · mode change pending' },
  'test-subject': { label: 'test-subject backend', headline: 'Not picked · test-subject backend' },
  'build-profile': { label: 'build profile', headline: 'Not picked · build profile not validated' },
  'slots-full': { label: 'slots full', headline: 'Waiting for runner slot · slots full' },
  'load-throttle': { label: 'load throttle', headline: 'Skipped · host load throttle' },
  'provider-limit': { label: 'provider limit', headline: 'Skipped · provider limit' },
  'quota-defer': { label: 'quota defer', headline: 'Skipped · quota defer' },
  'parallel-conflict': { label: 'parallel conflict', headline: 'Skipped · conflicts with a running task' },
};

export type ReadyWaitKind = 'hold' | 'runner' | 'queued';

export interface ReadyWaitReason {
  kind: ReadyWaitKind;
  /** Hold mechanism, runner reason code, or `queued`. */
  code: string;
  /** Short noun for the lane header split. */
  label: string;
}

export interface ReadyWaitBucket extends ReadyWaitReason {
  count: number;
  /** Display keys of the cards in this bucket, in lane order. */
  keys: string[];
}

const KIND_ORDER: Record<ReadyWaitKind, number> = { hold: 0, runner: 1, queued: 2 };

/** Display label of a pickup hold mechanism; unknown keys are title-cased. */
export function pickupHoldMechanismLabel(mechanism: string | null | undefined): string {
  return HOLD_LABELS[mechanism ?? ''] ?? titleCaseToken(mechanism ?? '');
}

/** `satisfiable-soon` -> `Satisfiable soon`. */
export function titleCaseToken(value: string): string {
  const words = value.split(/[-_\s]+/).filter(Boolean);
  if (words.length === 0) return 'Held';
  return words.map((word, index) =>
    index === 0 ? word.charAt(0).toUpperCase() + word.slice(1) : word).join(' ');
}

/** The runner reason on a card's live queue line, or null when none is recorded. */
export function runnerWaitCode(queue: TaskLiveQueue | null | undefined): string | null {
  return queue?.kind === 'runner' && queue.reasonCode ? queue.reasonCode : null;
}

/** Live status headline for a runner reason, with the queue position when known. */
export function runnerWaitHeadline(code: string, position?: number | null): string {
  const headline = RUNNER_REASONS[code]?.headline ?? `Not picked · ${titleCaseToken(code).toLowerCase()}`;
  return position != null ? `${headline} · position ${position}` : headline;
}

/**
 * The one current reason a Ready card waits. The order mirrors the live
 * status line: a blocked dependency first, then any other hold, then the
 * runner's reason. `hold` defaults to the card's own projection; a caller
 * that also derives client-side holds (the claim-load gate) passes its result.
 */
export function readyWaitReason(
  task: TaskInfo,
  hold: PickupHoldStatus | null | undefined = task.pickupHold,
): ReadyWaitReason {
  if (task.waitsOn?.blocked) return holdReason('dependency-gate');
  if (hold) return holdReason(hold.mechanism || 'held');
  const code = runnerWaitCode(task.liveStatus?.queue);
  if (code) {
    return { kind: 'runner', code, label: RUNNER_REASONS[code]?.label ?? titleCaseToken(code).toLowerCase() };
  }
  return { kind: 'queued', code: 'queued', label: 'queued' };
}

/**
 * Groups Ready cards by their one current reason. Holds come first, then
 * runner reasons, then plain queued cards; larger buckets lead within a kind.
 * The counts always sum to `tasks.length`.
 */
export function splitReadyWaits(
  tasks: readonly TaskInfo[],
  holdFor: (task: TaskInfo) => PickupHoldStatus | null | undefined = task => task.pickupHold,
): ReadyWaitBucket[] {
  const buckets = new Map<string, ReadyWaitBucket>();
  for (const task of tasks) {
    const reason = readyWaitReason(task, holdFor(task));
    const id = `${reason.kind}:${reason.code}`;
    const bucket = buckets.get(id) ?? { ...reason, count: 0, keys: [] };
    bucket.count++;
    bucket.keys.push(task.displayKey || task.key || task.id);
    buckets.set(id, bucket);
  }
  return [...buckets.values()].sort((left, right) =>
    KIND_ORDER[left.kind] - KIND_ORDER[right.kind]
    || right.count - left.count
    || left.label.localeCompare(right.label));
}

function holdReason(mechanism: string): ReadyWaitReason {
  return { kind: 'hold', code: mechanism, label: pickupHoldMechanismLabel(mechanism).toLowerCase() };
}
