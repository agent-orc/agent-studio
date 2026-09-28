import { signal } from '@angular/core';
import type { CycleTimeStageKey, TaskCycleTimeRow } from '../../models/project-cycle-time.model';

/** Drill-down columns the operator can sort by; stage keys sort by that stage's seconds. */
export type CycleTimeSortKey =
  | 'key'
  | 'completedAt'
  | 'leadTime'
  | 'cycleTime'
  | 'reviewRun'
  | 'codingRuns'
  | 'bounceRounds'
  | 'backwardTransitions'
  | 'outcome'
  | CycleTimeStageKey;

export type CycleTimeSortDirection = 'asc' | 'desc';

const COLLATOR = new Intl.Collator('en-US', { numeric: true, sensitivity: 'base' });

/** Sort state for the per-task drill-down table. Default: newest completion first. */
export class CycleTimeTableState {
  readonly sortKey = signal<CycleTimeSortKey>('completedAt');
  readonly direction = signal<CycleTimeSortDirection>('desc');

  selectSort(key: CycleTimeSortKey): void {
    if (this.sortKey() === key) {
      this.direction.update(value => (value === 'asc' ? 'desc' : 'asc'));
    } else {
      this.sortKey.set(key);
      this.direction.set(defaultDirection(key));
    }
  }

  /** Stable sort: ties keep the incoming order (newest completion first from the API). */
  sort(rows: readonly TaskCycleTimeRow[]): TaskCycleTimeRow[] {
    const key = this.sortKey();
    const direction = this.direction() === 'asc' ? 1 : -1;
    return rows
      .map((row, index) => ({ row, index }))
      .sort((left, right) => {
        const compared = compareRows(left.row, right.row, key);
        return compared === 0 ? left.index - right.index : compared * direction;
      })
      .map(entry => entry.row);
  }
}

export function sortValue(row: TaskCycleTimeRow, key: CycleTimeSortKey): number | string {
  switch (key) {
    case 'key': return row.taskKey;
    case 'completedAt': return Date.parse(row.completedAt) || 0;
    case 'leadTime': return row.leadTimeSeconds;
    case 'cycleTime': return row.cycleTimeSeconds ?? -1;
    case 'reviewRun': return row.reviewRunSeconds;
    case 'codingRuns': return row.codingRuns;
    case 'bounceRounds': return row.bounceRounds;
    case 'backwardTransitions': return row.backwardTransitions ?? 0;
    case 'outcome': return row.integrationOutcome ?? '';
    default: return row.stages?.[key] ?? 0;
  }
}

function compareRows(left: TaskCycleTimeRow, right: TaskCycleTimeRow, key: CycleTimeSortKey): number {
  const a = sortValue(left, key);
  const b = sortValue(right, key);
  if (typeof a === 'string' || typeof b === 'string') return COLLATOR.compare(String(a), String(b));
  return a - b;
}

function defaultDirection(key: CycleTimeSortKey): CycleTimeSortDirection {
  return key === 'key' || key === 'outcome' ? 'asc' : 'desc';
}
