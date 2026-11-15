import { TaskState } from '../../../../models/task.model';
import type { BatchIsolationHold } from '../../../../models/batch-isolation-hold';
import type { TriageButton } from '../../state/triage-actions.model';

const REQUEUE_LANES: ReadonlySet<string> = new Set([
  TaskState.Backlog, TaskState.Preparation, TaskState.OrchestratorPrep, TaskState.Ready, TaskState.Progress,
]);

export function isHeldRequeue(button: TriageButton, state: string, hold: BatchIsolationHold | null): boolean {
  return !!hold && button.intent.kind === 'move' && button.intent.targetState !== state
    && REQUEUE_LANES.has(button.intent.targetState);
}
