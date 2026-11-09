/**
 * F35: per-lane sort-strategy metadata shared by the project-settings
 * dropdown and the kanban lane-header indicator. The strategy ids mirror
 * the backend `LaneSortStrategies` constants exactly; keep them in sync.
 */

import { TaskState, type TaskInfo } from '../models/task.model';
import { lanePresentation } from '../models/lane-presentation';

export interface LaneSortStrategyMeta {
  /** Strategy id sent to the backend (empty string clears the override). */
  value: string;
  /** Human label for the settings dropdown. */
  label: string;
  /** Compact glyph for the lane-header indicator. */
  icon: string;
  /** One-line explanation used in tooltips. */
  hint: string;
}

const META: Record<string, LaneSortStrategyMeta> = {
  'lane-entry': {
    value: 'lane-entry',
    label: 'Lane entry',
    icon: '⤓',
    hint: 'Cards placed by hand first, then the most recently entered on top. Drag a card to place it.',
  },
  manual: {
    value: 'manual',
    label: 'Manual',
    icon: '≡',
    hint: 'Manual order. Drag cards to reorder.',
  },
  'newest-first': {
    value: 'newest-first',
    label: 'Newest first',
    icon: '↓',
    hint: 'Newest task on top.',
  },
  'oldest-first': {
    value: 'oldest-first',
    label: 'Oldest first',
    icon: '↑',
    hint: 'Oldest task on top (FIFO).',
  },
  'last-activity': {
    value: 'last-activity',
    label: 'Last activity',
    icon: '◷',
    hint: 'Most recently active task on top.',
  },
  // Internal runner-only strategy; never offered in the settings dropdown,
  // but the board may still resolve to it for the auto-pickup lane.
  'pickup-priority': {
    value: 'pickup-priority',
    label: 'Pickup priority',
    icon: '⚡',
    hint: 'Auto-pickup priority order.',
  },
};

/** Strategies offered in the project-settings dropdown, in display order. */
export const USER_VISIBLE_LANE_SORT_STRATEGIES: readonly LaneSortStrategyMeta[] = [
  META['lane-entry'],
  META['manual'],
  META['newest-first'],
  META['oldest-first'],
  META['last-activity'],
];

export function laneSortStrategyMeta(strategy: string | null | undefined): LaneSortStrategyMeta {
  if (strategy && META[strategy]) return META[strategy];
  return META['lane-entry'];
}

export function isManualStrategy(strategy: string | null | undefined): boolean {
  return strategy === 'manual';
}

/**
 * Strategies under which drag-reorder is allowed. `manual` reorders the whole
 * lane; `lane-entry` lets a drag pin individual cards while the rest flow by
 * entry time. Mirrors the backend `laneReorderDisabled` gate.
 */
export function allowsDragReorder(strategy: string | null | undefined): boolean {
  return strategy === 'manual' || strategy === 'lane-entry';
}

export interface SortableLaneMeta {
  state: string;
  label: string;
  icon: string;
}

/**
 * Lanes surfaced in the project-settings sort-strategy section, in board
 * order. The internal `3a-failed-pickup` lane is intentionally omitted — it
 * is a short-lived bounce lane the operator does not curate by hand. The
 * retired `1a-orchestrator-prep` lane is likewise omitted — prep now runs
 * in-place on 1-preparation as the optional `pre-orchestrator-prep`
 * pipeline step (see PipelineCatalogue), so there is no lane to sort.
 */
const SORTABLE_LANE_STATES: readonly string[] = [
  TaskState.Backlog,
  TaskState.Preparation,
  TaskState.Ready,
  TaskState.Progress,
  TaskState.AutoReview,
  TaskState.Escalated,
  TaskState.HumanReview,
  TaskState.Completed,
  TaskState.Archive,
];

export const SORTABLE_LANES: readonly SortableLaneMeta[] = SORTABLE_LANE_STATES.map((state) => {
  const lane = lanePresentation(state);
  return { state, label: lane.name, icon: lane.glyph };
});

/**
 * Map a board display-state to the backend lane key its sort strategy is
 * stored under. Most are 1:1; the Ready lane is split into a human-ready
 * and an orchestrator-intake sub-lane on the board, but both share the
 * single `2-ready` strategy.
 */
export function displayStateToLaneKey(state: string): string {
  if (state === '2-ready-intake') return TaskState.Ready;
  return state;
}

/**
 * AGT-3587: one sentence for how the runners read Ready. Shared by the lane
 * sort tooltip and mirrored by `docs/app/help/lane-guides/lane-2-ready.md`, so
 * the tooltip and the lane help describe the same rule.
 */
export const READY_PICKUP_RULE =
  'Each project runs its own Ready queue from the top: the card marked Next runs when that project has a free slot.';

/**
 * F35 / AGT-3587: lane-header sort indicator. Returns the glyph and tooltip
 * for the resolved strategy, or null when no strategy data is loaded. `mixed`
 * means the visible projects disagree. The Ready lane appends the pickup rule.
 */
export function laneSortIndicator(
  strategy: string,
  state: string,
): { icon: string; label: string; tooltip: string } | null {
  if (!strategy) return null;
  const pickup = displayStateToLaneKey(state) === TaskState.Ready ? ` ${READY_PICKUP_RULE}` : '';
  if (strategy === 'mixed') {
    return {
      icon: '⇅',
      label: 'mixed',
      tooltip: 'Sorted per project: the projects shown in this lane use different sort orders. '
        + `Filter to a single project, or set that lane to Manual, to reorder by hand.${pickup}`,
    };
  }
  const meta = laneSortStrategyMeta(strategy);
  const order = allowsDragReorder(strategy)
    ? meta.hint
    : `Auto-sorted by ${meta.label.toLowerCase()}; switch this lane to Manual to reorder.`;
  return { icon: meta.icon, label: meta.label, tooltip: `${order}${pickup}` };
}

/**
 * AGT-3587: quiet note above a Ready lane that mixes projects. The lane is a
 * run of per-project queues (alphabetical by project), not one global queue,
 * so the board says so instead of implying a cross-project order.
 */
export function perProjectOrderNote(state: string, jobs: readonly TaskInfo[]): string | null {
  if (displayStateToLaneKey(state) !== TaskState.Ready) return null;
  const projects = new Set(jobs.map((job) => job.projectName));
  return projects.size > 1 ? 'Grouped by project. Each project keeps its own order.' : null;
}

const UNPINNED_ORDER = 999;

function time(value: string | null | undefined): number {
  const parsed = value ? Date.parse(value) : NaN;
  return Number.isFinite(parsed) ? parsed : 0;
}

/** Mirrors backend `LaneSortStrategies.KeyComparer`: prefix, then numeric suffix. */
function compareKeys(a: string, b: string): number {
  const dashA = a.lastIndexOf('-');
  const dashB = b.lastIndexOf('-');
  if (dashA > 0 && dashB > 0) {
    const byPrefix = compareIgnoreCase(a.slice(0, dashA), b.slice(0, dashB));
    if (byPrefix !== 0) return byPrefix;
    const nA = Number(a.slice(dashA + 1));
    const nB = Number(b.slice(dashB + 1));
    if (Number.isInteger(nA) && Number.isInteger(nB)) return Math.sign(nA - nB);
  }
  return compareIgnoreCase(a, b);
}

function compareIgnoreCase(a: string, b: string): number {
  const ua = a.toUpperCase();
  const ub = b.toUpperCase();
  return ua < ub ? -1 : ua > ub ? 1 : 0;
}

/** Null keys sort last in both directions, as on the backend. */
function compareKey(a: TaskInfo, b: TaskInfo, direction: 1 | -1): number {
  if (!a.key && !b.key) return 0;
  if (!a.key) return 1;
  if (!b.key) return -1;
  return direction * compareKeys(a.key, b.key);
}

const COMPARERS: Record<string, (a: TaskInfo, b: TaskInfo) => number> = {
  manual: (a, b) => (a.order - b.order) || compareKey(a, b, -1),
  'lane-entry': (a, b) => {
    const aPinned = a.order !== UNPINNED_ORDER;
    const bPinned = b.order !== UNPINNED_ORDER;
    if (aPinned !== bPinned) return aPinned ? -1 : 1;
    if (aPinned) return (a.order - b.order) || compareKey(a, b, -1);
    return Math.sign(time(b.enteredLaneAt) - time(a.enteredLaneAt)) || compareKey(a, b, -1);
  },
  'newest-first': (a, b) => compareKey(a, b, -1) || Math.sign(time(b.createdAt) - time(a.createdAt)),
  'oldest-first': (a, b) => compareKey(a, b, 1) || Math.sign(time(a.createdAt) - time(b.createdAt)),
  'last-activity': (a, b) => Math.sign(time(b.lastActivity) - time(a.lastActivity)) || (a.order - b.order),
  'pickup-priority': (a, b) => (a.order - b.order) || Math.sign(time(a.lastActivity) - time(b.lastActivity)),
};

/**
 * AGT-3587: client mirror of backend `LaneSortApplier.Sort`. Groups a lane by
 * project in alphabetical order and sorts each group with that project's lane
 * strategy (default `lane-entry`). A pushed card is re-inserted with this, so
 * the board keeps the order `/api/tasks/grouped` returns instead of sorting
 * the whole lane by `order` across projects.
 */
export function sortLaneLikeBoard(
  jobs: readonly TaskInfo[],
  state: string,
  strategies: Record<string, Record<string, string>>,
): TaskInfo[] {
  const laneKey = displayStateToLaneKey(state);
  const byProject = new Map<string, TaskInfo[]>();
  for (const job of jobs) {
    const project = (job.projectName ?? '').toUpperCase();
    const group = byProject.get(project);
    if (group) group.push(job);
    else byProject.set(project, [job]);
  }
  return [...byProject.keys()].sort(compareIgnoreCase).flatMap((project) => {
    const group = byProject.get(project)!;
    const strategy = strategies[group[0].projectName]?.[laneKey];
    const comparer = COMPARERS[strategy ?? ''] ?? COMPARERS['lane-entry'];
    return [...group].sort(comparer);
  });
}
