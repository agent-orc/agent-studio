import { TaskState, type TaskInfo } from '../../../models/task.model';
import { laneName } from '../../../models/lane-presentation';
import { decisionMoveRefusal } from '../../../models/decision-card-presentation';
import { laneActionsFor, type TriageButton } from '../../task-detail/state/triage-actions.model';

/**
 * AGT-3585: lane actions a group may run on all of its cards at once. Only
 * plain moves that the per-card flow runs without further questions qualify:
 * Accept as-is checks each card's delivery evidence and Abort archives, so
 * both stay per card. Leaving the cards where they are is the confirmation's
 * cancel choice.
 */
const BULK_ACTION_IDS: Readonly<Record<string, readonly string[]>> = {
  [TaskState.Escalated]: ['reissue-escalated', 'manual-resolve-escalated'],
};

export interface BulkLaneOffer {
  readonly actions: readonly TriageButton[];
  /** Why no action is offered; null when at least one is. */
  readonly reason: string | null;
}

/**
 * The bulk actions valid for every card in `tasks`. `missing` counts group
 * members that are no longer on the board: an action cannot be valid for a
 * card nobody can see, so any missing member withholds every action.
 */
export function bulkLaneOffer(tasks: readonly TaskInfo[], missing = 0): BulkLaneOffer {
  if (missing > 0) {
    return none(`${missing} of these cards ${missing === 1 ? 'is' : 'are'} no longer on the board; open the rest one by one.`);
  }
  if (tasks.length === 0) return none('No card of this group is on the board.');
  const lanes = new Set(tasks.map(task => task.state));
  if (lanes.size > 1) return none('These cards sit in different lanes now; open them one by one.');
  const lane = tasks[0].state;
  const ids = BULK_ACTION_IDS[lane] ?? [];
  if (ids.length === 0) return none(`No bulk action applies in ${laneName(lane)}.`);
  const actions = laneActionsFor(lane).filter(button =>
    ids.includes(button.id)
    && button.intent.kind === 'move'
    && tasks.every(task => decisionMoveRefusal(task, (button.intent as { targetState: string }).targetState) === null));
  return actions.length > 0
    ? { actions, reason: null }
    : none('No action is valid for every card in this group.');
}

/** Target lane of a bulk action; every offered action is a move. */
export function bulkTargetState(button: TriageButton): string {
  return button.intent.kind === 'move' ? button.intent.targetState : '';
}

function none(reason: string): BulkLaneOffer {
  return { actions: [], reason };
}
