/**
 * Board lane layout: which lanes the kanban renders, in which order, and under
 * which container.
 *
 * AGT-2819: this used to live as two large `computed()` bodies inside the `App`
 * shell (`focusGroups` and `laneGroups`). They are pure functions of the grouped
 * feed, so the shell had no reason to own them and no test could reach them
 * without mounting the whole application. They are one unit here, with the lane
 * ordering rules documented where they are decided.
 */
import type { GroupedJobs, TaskInfo } from '../../../models/task.model';
import { TaskState } from '../../../models/task.model';
import { lanePresentation } from '../../../models/lane-presentation';
import { splitReadyByPhase } from './ready-lane-split.util';

/** One rendered board lane: its state key, display chrome, and its cards. */
export interface BoardLane {
  state: string;
  title: string;
  icon: string;
  jobs: TaskInfo[];
}

/** One contiguous board container holding an ordered run of lanes. */
export interface BoardLaneGroup {
  id: string;
  label: string;
  lanes: BoardLane[];
}

/**
 * Board chrome for one lane: its state key, display name, and glyph, all read
 * from the lane presentation catalogue.
 *
 * AGT-2715: the board used to spell out `title:` and `icon:` inline at each of
 * the ~18 lane definitions across the focus list and the lane groups. That is
 * how the board column ended up calling `5-human-review` "Review" while the
 * Result tab called the same lane "Human review lane".
 */
export function laneChrome(state: string): { state: string; title: string; icon: string } {
  const lane = lanePresentation(state);
  return { state, title: lane.name, icon: lane.glyph };
}

/**
 * The flat focus lane list.
 *
 * ADR-0025: seven lanes. The robot icon is the orchestrator's machine pass; the
 * eye icon is the user's "needs me" lane. Orchestrator prep is no longer a
 * backlog lane: it runs in-place on 1-preparation as the optional pipeline step
 * `pre-orchestrator-prep` (see PipelineCatalogue), so the retired 1a lane is not
 * rendered. Backlog-lane spec: 0-backlog leads the focus list when populated.
 *
 * `3b-code-not-complete` is a hide-when-empty park lane.
 */
export function buildFocusLanes(grouped: GroupedJobs): BoardLane[] {
  const lanes: BoardLane[] = [
    { ...laneChrome(TaskState.Backlog), jobs: grouped.backlog ?? [] },
    { ...laneChrome(TaskState.Preparation), jobs: grouped.preparation },
    { ...laneChrome(TaskState.Ready), jobs: grouped.ready },
    { ...laneChrome(TaskState.Progress), jobs: grouped.progress },
  ];
  if ((grouped.codeNotComplete ?? []).length > 0) {
    lanes.push({ ...laneChrome(TaskState.CodeNotComplete), jobs: grouped.codeNotComplete });
  }
  lanes.push(
    { ...laneChrome(TaskState.AutoReview), jobs: grouped.autoReview },
    { ...laneChrome(TaskState.Escalated), jobs: grouped.escalated ?? [] },
    { ...laneChrome(TaskState.HumanReview), jobs: grouped.humanReview },
    { ...laneChrome(TaskState.Completed), jobs: grouped.completed },
    { ...laneChrome(TaskState.Archive), jobs: grouped.archive ?? [] },
  );
  return lanes;
}

/**
 * Board lane groups. Three contiguous containers map the workflow:
 *
 *  - backlog: 0-backlog, 1-preparation, 2-ready
 *  - active:  3-progress, 4-auto-review
 *  - decide:  5e-escalated, 5-human-review, 6-completed, 7-archive ("Done &
 *             Decide" - intervention precedes acceptance in the user-owned
 *             tail.)
 *
 * The previous human/agent axis suffix was misleading (Backlog mixes agent prep
 * with human triage) and is removed.
 *
 * @param dragActive keeps the otherwise hide-when-empty Escalated lane rendered
 *   while a card is being dragged, so it stays a drop target.
 */
export function buildLaneGroups(grouped: GroupedJobs, dragActive: boolean): BoardLaneGroup[] {
  // Backlog lanes put the most actionable work first; the old order buried
  // Ready under large backlogs.
  //   1. 2-ready      "Ready"              - pick-up candidates
  //   2. 1-preparation                     - in human preparation
  //   3. 0-backlog                         - fresh inbox / triage
  const readySplit = splitReadyByPhase(grouped.ready);
  const backlogLanes: BoardLane[] = [
    { ...laneChrome(TaskState.Ready), jobs: readySplit.humanReady },
  ];
  if (readySplit.intake.length > 0) {
    // Own "Preparation" lane: only pushed (so only rendered) while the
    // orchestrator-prep/intake loop is actually working a card, so the lane
    // is hidden whenever nothing is mid-preparation.
    backlogLanes.push({ ...laneChrome('2-ready-intake'), jobs: readySplit.intake });
  }
  backlogLanes.push({ ...laneChrome(TaskState.Preparation), jobs: grouped.preparation });
  backlogLanes.push({ ...laneChrome(TaskState.Backlog), jobs: grouped.backlog ?? [] });

  const activeLanes: BoardLane[] = [
    { ...laneChrome(TaskState.Progress), jobs: grouped.progress },
  ];
  // 3b-code-not-complete is a hide-when-empty park lane: the runner moves a
  // task here when it exhausts its auto-pickup retry budget without reaching
  // review, and keeps auto-mode running. It sits at 3-progress / before review
  // so the operator sees stuck work next to what is actively running.
  if ((grouped.codeNotComplete ?? []).length > 0) {
    activeLanes.push({ ...laneChrome(TaskState.CodeNotComplete), jobs: grouped.codeNotComplete });
  }
  activeLanes.push({ ...laneChrome(TaskState.AutoReview), jobs: grouped.autoReview });

  const escalatedJobs = grouped.escalated ?? [];
  // Future option: metadata could apply this empty-lane policy to exception
  // lanes such as 1-preparation. For now it is intentionally Escalated-only.
  const showEscalated = escalatedJobs.length > 0 || dragActive;
  return [
    { id: 'backlog', label: 'Backlog', lanes: backlogLanes },
    { id: 'active', label: 'Active', lanes: activeLanes },
    {
      id: 'decide',
      label: 'Done & Decide',
      lanes: [
        // Intervention comes before acceptance in the visible workflow.
        ...(showEscalated ? [{ ...laneChrome(TaskState.Escalated), jobs: escalatedJobs }] : []),
        { ...laneChrome(TaskState.HumanReview), jobs: grouped.humanReview },
        { ...laneChrome(TaskState.Completed), jobs: grouped.completed },
        { ...laneChrome(TaskState.Archive), jobs: grouped.archive ?? [] },
      ],
    },
  ];
}
