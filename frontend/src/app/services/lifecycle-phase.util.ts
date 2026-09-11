/**
 * Single source of truth for lifecycle-phase display text (Run-Liveness Slice
 * C). Both surfaces that show the `phase` substate - the board task-card pill
 * (`buildPhaseBadge`) and the task-detail title chip (`lifecyclePhaseLabel`) -
 * read their labels and elapsed formatting from here so the two can never
 * drift.
 *
 * Before this was shared, the card and the detail carried independent copies:
 * the elapsed formatter was duplicated and the detail's label map was
 * incomplete, so `intake-blocked`, `intake-passed`, `post-processing-blocked`
 * and `awaiting-review` fell through to the raw kebab-case phase id in the
 * task detail ("intake-blocked" instead of "Intake blocked").
 */

import { laneName } from '../models/lane-presentation';
import { TaskState } from '../models/task.model';

/**
 * Format an elapsed wait as a compact "m:ss" (sub-hour) / "h:mm h" label.
 * Clamps negatives to zero so clock skew never renders a "-1:59" wait. Refreshes
 * on the caller's shared time tick, so it reads as a live "waiting since …"
 * without a dedicated per-second timer.
 */
export function formatPhaseElapsed(elapsedMs: number): string {
  const total = Math.max(0, Math.floor(elapsedMs / 1000));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, '0')}h`
    : `${minutes}:${String(seconds).padStart(2, '0')}`;
}

/**
 * Canonical label per lifecycle phase (see `LifecyclePhases` in
 * `backend/Shared/Models/TaskStates.cs`). `loop-waiting` / `steer-pending` carry
 * only their base here; callers append the elapsed suffix from
 * {@link formatPhaseElapsed}. Every phase in the backend contract has an entry
 * so no surface ever renders a raw kebab-case id.
 */
export const PHASE_LABELS: Readonly<Record<string, string>> = {
  // The task is sitting in the Ready lane, so it is named by that lane
  // (AGT-2715); the remaining phases are their own vocabulary.
  'human-ready': laneName(TaskState.Ready),
  'intake-running': 'Intake running',
  'intake-blocked': 'Intake blocked',
  'intake-passed': 'Intake passed',
  'execution-running': 'Execution running',
  'execution-stalled': 'Execution stalled',
  'quota-waiting': 'Waiting for quota reset',
  'loop-waiting': 'Waiting for loop continuation',
  'steer-pending': 'Waiting for answer',
  'post-processing-running': 'Post processing',
  'post-processing-blocked': 'Post processing blocked',
  'awaiting-review': 'Awaiting review',
  'integrating': 'Integrating',
};

/** The two intentional-wait phases whose label carries a live elapsed timer. */
const TIMED_WAIT_PHASES = new Set(['loop-waiting', 'steer-pending']);

/**
 * Static (no-timer) label for a phase. Returns null for no phase, and falls back
 * to the raw id only for an unknown phase the backend has not taught us yet
 * (defensive - every current phase is mapped above).
 */
export function phaseStaticLabel(phase: string | null | undefined): string | null {
  if (!phase) return null;
  return PHASE_LABELS[phase] ?? phase;
}

/**
 * Full label for the task-detail title chip: the static label, plus a live
 * "since m:ss" suffix for the two intentional-wait phases. `steer-pending` reads
 * its start from the durable `steerPendingSince` marker; every other timed phase
 * (`loop-waiting`) reads `phaseEnteredAt`. Falls back to the bare label when no
 * start timestamp is parseable.
 */
export function lifecyclePhaseLabel(
  phase: string | null | undefined,
  phaseEnteredAt: string | null | undefined,
  steerPendingSince: string | null | undefined,
  nowMs: number,
): string | null {
  if (!phase) return null;
  const base = phaseStaticLabel(phase) ?? phase;
  if (!TIMED_WAIT_PHASES.has(phase)) return base;
  const startedAt = (phase === 'steer-pending' ? steerPendingSince : phaseEnteredAt) ?? phaseEnteredAt;
  const since = startedAt ? Date.parse(startedAt) : NaN;
  return Number.isFinite(since) ? `${base} ${formatPhaseElapsed(nowMs - since)}` : base;
}
