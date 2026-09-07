import { TaskInfo, TaskState } from '../../../models/task.model';
import { laneName } from '../../../models/lane-presentation';

/**
 * Lane phases for the 2-ready filesystem state. Mirrors backend
 * `LifecyclePhases` (only the Ready-group values; 3-progress phases live
 * on the same field but are not surfaced here). Centralised so the lane
 * split, the card phase chip, and any future test helpers agree on the
 * legal set.
 */
export const READY_PHASES = {
  humanReady: 'human-ready',
  intakeRunning: 'intake-running',
  intakeBlocked: 'intake-blocked',
  intakePassed: 'intake-passed',
} as const;

export type ReadyPhase = (typeof READY_PHASES)[keyof typeof READY_PHASES];

export interface ReadyLaneSplit {
  /** Human Ready: cards the user has marked ready, no intake verdict yet. */
  humanReady: TaskInfo[];
  /**
   * Preparation: cards the orchestrator preparation/intake loop is processing
   * (`intake-running`), has flagged for human attention (`intake-blocked`),
   * or has approved for pickup (`intake-passed`). Bundling the three lets
   * the UI render Preparation as one column with phase-aware chips on cards;
   * the lane stays empty (and so is hidden) when no card is mid-preparation.
   */
  intake: TaskInfo[];
}

/**
 * Splits the existing 2-ready bucket into the two lanes we render under
 * the Backlog group. Compatibility rule (see
 * docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md section 10):
 * a job with no `phase` defaults to Human Ready, so existing job folders
 * that predate the field continue to render correctly.
 *
 * Pure so the lane projection stays unit-testable without TestBed.
 */
export function splitReadyByPhase(jobs: readonly TaskInfo[]): ReadyLaneSplit {
  const humanReady: TaskInfo[] = [];
  const intake: TaskInfo[] = [];
  for (const j of jobs) {
    const phase = j.phase ?? READY_PHASES.humanReady;
    if (
      phase === READY_PHASES.intakeRunning ||
      phase === READY_PHASES.intakeBlocked ||
      phase === READY_PHASES.intakePassed
    ) {
      intake.push(j);
    } else {
      humanReady.push(j);
    }
  }
  return { humanReady, intake };
}

/**
 * Short, user-visible label for a Ready-group phase. Used by the card
 * phase chip and by the activity log entry the orchestrator-intake loop
 * writes. `null` returns null so callers can suppress rendering when no
 * phase is set; for Ready cards that null falls back to "Human Ready"
 * via the lane split above.
 */
export function readyPhaseLabel(phase: string | null | undefined): string | null {
  switch (phase) {
    case READY_PHASES.humanReady: return laneName(TaskState.Ready);
    case READY_PHASES.intakeRunning: return 'Preparing';
    case READY_PHASES.intakeBlocked: return 'Prep blocked';
    case READY_PHASES.intakePassed: return 'Prep passed';
    default: return null;
  }
}
