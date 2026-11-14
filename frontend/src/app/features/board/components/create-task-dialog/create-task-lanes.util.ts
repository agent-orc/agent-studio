import { TaskState } from '../../../../models/task.model';
import { lanePresentation } from '../../../../models/lane-presentation';

export interface LaneOption {
  state: string;
  label: string;
  icon: string;
}
/** Manual create targets stop before orchestrator-owned Progress. */
export const CREATE_LANE_OPTIONS: readonly LaneOption[] = [
  TaskState.Backlog,
  TaskState.Preparation,
  TaskState.Ready,
].map((state) => {
  const lane = lanePresentation(state);
  return { state, label: lane.name, icon: lane.glyph };
});

/** AGT-3668: the lane picker's options; a Product session files into Backlog or Ready only, as the server enforces. */
export function createLaneSegments(productSession: boolean) {
  return CREATE_LANE_OPTIONS
    .filter(lane => !productSession || lane.state === TaskState.Backlog || lane.state === TaskState.Ready)
    .map(lane => ({ value: lane.state, label: lane.label, testid: 'create-lane-' + lane.state }));
}
