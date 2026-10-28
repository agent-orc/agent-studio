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
import {
  INTEGRATED_DELIVERY_COLUMN_ID,
  type DeliveryColumnApproval,
  type DeliveryColumnCard,
  type DeliveryColumnRun,
  type DeliveryColumnsResponse,
} from '../../../models/delivery-column.model';
import { DELIVERY_COPY, deliveryColumnTitle } from '../../../models/delivery-presentation';
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

const EMPTY_GROUPED: GroupedJobs = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
  codeNotComplete: [], autoReview: [], humanReview: [], escalated: [], review: [], completed: [], archive: [],
};

/**
 * AGT-3038: lane states the board renders under the current container focus,
 * in board order. The card context menu steps only between lanes the operator
 * can see. Only hide-when-empty lanes depend on the feed, and none of them is a
 * manual move target, so an empty feed resolves the same targets as the live
 * board without threading the shell's lane groups down to every card.
 */
export function focusVisibleLaneStates(focusedContainer: string | null): string[] {
  return buildLaneGroups(EMPTY_GROUPED, false)
    .filter((group) => focusedContainer === null || group.id === focusedContainer)
    .flatMap((group) => group.lanes.map((lane) => lane.state));
}

/** Container id of the Delivery group; it follows "Done & Decide". */
export const DELIVERY_GROUP_ID = 'delivery';

/** One tile in a Delivery column: the board's own task and the release that placed it. */
export interface BoardDeliveryCard {
  task: TaskInfo;
  releaseVersion: string | null;
  rolledBack: boolean;
}

/** One project's release line in a column header. */
export interface BoardDeliveryHeader {
  projectId: string;
  releaseVersion: string | null;
  run: DeliveryColumnRun | null;
  health: string | null;
  approval: DeliveryColumnApproval | null;
}

export interface BoardDeliveryColumn {
  id: string;
  title: string;
  integrated: boolean;
  approvalPending: boolean;
  headers: BoardDeliveryHeader[];
  landed: BoardDeliveryCard[];
  waiting: BoardDeliveryCard[];
}

/** The Delivery group; `count` is the sum of the tiles every column shows. */
export interface BoardDeliveryGroup {
  id: string;
  label: string;
  columns: BoardDeliveryColumn[];
  count: number;
}

interface PlacedCard extends BoardDeliveryCard { position: number }

/**
 * The Delivery group (AGT-3276 D5 L-B): an Integrated column, then one column
 * per environment in lifecycle order, built from the `deploy/lanes` read model.
 *
 * Columns of several projects merge by column id in read-model order. A column
 * lists its landed cards first, then its waiting cards; a rolled-back card
 * waits again with its flag. Only cards the board feed carries render, so the
 * active filters apply and every count is the sum of visible tiles (R3). Lane
 * states are untouched: a card can show in every environment it reached.
 *
 * @param furthestOnly display option: each card shows once, landed in the
 *   furthest column it reached, or waiting in its first column when it has
 *   landed nowhere.
 */
export function buildDeliveryGroup(
  responses: readonly DeliveryColumnsResponse[],
  tasks: readonly TaskInfo[],
  furthestOnly: boolean,
): BoardDeliveryGroup {
  const byKey = new Map<string, TaskInfo>();
  for (const task of tasks) {
    for (const key of [task.taskKey, task.key, task.displayKey]) {
      if (key) byKey.set(deliveryRef(task.projectName, key), task);
    }
  }
  // Columns of several projects merge by id; a column new to the merge sits
  // right after its predecessor in its own project, so every project's
  // lifecycle order survives.
  const ordered: BoardDeliveryColumn[] = [];
  const landed: PlacedCard[] = [];
  const waiting: PlacedCard[] = [];
  const resolve = (project: string, card: DeliveryColumnCard, position: number, into: PlacedCard[]) => {
    const task = byKey.get(deliveryRef(project, card.key));
    if (task) into.push({ task, releaseVersion: card.releaseVersion, rolledBack: card.rolledBack, position });
  };
  for (const response of responses) {
    let previous = -1;
    for (const column of response.columns ?? []) {
      const integrated = column.id === INTEGRATED_DELIVERY_COLUMN_ID;
      let index = ordered.findIndex((item) => item.id === column.id);
      if (index < 0) {
        index = previous + 1;
        ordered.splice(index, 0, {
          id: column.id, title: deliveryColumnTitle(column), integrated, approvalPending: false,
          headers: [], landed: [], waiting: [],
        });
      }
      previous = index;
      const merged = ordered[index];
      merged.approvalPending ||= column.pendingApproval !== null;
      if (!integrated) {
        merged.headers.push({
          projectId: response.projectId, releaseVersion: column.currentRelease?.version ?? null,
          run: column.lastRun, health: column.health, approval: column.pendingApproval,
        });
      }
    }
  }
  const positionOf = new Map(ordered.map((column, index) => [column.id, index]));
  for (const response of responses) {
    for (const column of response.columns ?? []) {
      const position = positionOf.get(column.id)!;
      column.landed.forEach((card) => resolve(response.projectId, card, position, landed));
      const shown = new Set([...column.landed, ...column.waiting].map((card) => card.key.toLowerCase()));
      [...column.waiting, ...column.rolledBack.filter((card) => !shown.has(card.key.toLowerCase()))]
        .forEach((card) => resolve(response.projectId, card, position, waiting));
    }
  }
  const furthest = new Map<string, number>();
  const firstWait = new Map<string, number>();
  for (const card of landed) furthest.set(card.task.taskKey, Math.max(furthest.get(card.task.taskKey) ?? -1, card.position));
  for (const card of waiting) firstWait.set(card.task.taskKey, Math.min(firstWait.get(card.task.taskKey) ?? Infinity, card.position));
  const keepLanded = (card: PlacedCard) => !furthestOnly || furthest.get(card.task.taskKey) === card.position;
  const keepWaiting = (card: PlacedCard) => !furthestOnly
    || (!furthest.has(card.task.taskKey) && firstWait.get(card.task.taskKey) === card.position);
  ordered.forEach((column, index) => {
    column.landed = uniqueTiles(landed.filter((card) => card.position === index && keepLanded(card)));
    column.waiting = uniqueTiles(waiting.filter((card) => card.position === index && keepWaiting(card)));
  });
  return {
    id: DELIVERY_GROUP_ID,
    label: DELIVERY_COPY.groupLabel,
    columns: ordered,
    count: ordered.reduce((sum, column) => sum + column.landed.length + column.waiting.length, 0),
  };
}

function deliveryRef(project: string, key: string): string {
  return `${project.toLowerCase()}::${key.toLowerCase()}`;
}

function uniqueTiles(cards: PlacedCard[]): BoardDeliveryCard[] {
  const seen = new Set<string>();
  return cards.filter((card) => !seen.has(card.task.taskKey) && seen.add(card.task.taskKey))
    .map(({ task, releaseVersion, rolledBack }) => ({ task, releaseVersion, rolledBack }));
}
