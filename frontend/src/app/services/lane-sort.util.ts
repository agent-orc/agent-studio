/**
 * F35: per-lane sort-strategy metadata shared by the project-settings
 * dropdown and the kanban lane-header indicator. The strategy ids mirror
 * the backend `LaneSortStrategies` constants exactly; keep them in sync.
 */

import { TaskState } from '../models/task.model';
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
    hint: 'Most recently entered on top; drag a card to pin it.',
  },
  manual: {
    value: 'manual',
    label: 'Manual',
    icon: '≡',
    hint: 'Manual order — drag cards to reorder.',
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
