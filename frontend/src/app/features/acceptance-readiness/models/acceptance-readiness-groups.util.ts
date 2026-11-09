import type { TaskInfo } from '../../../models/task.model';
import {
  AcceptanceReadinessClass,
  AcceptanceReadinessFilter,
  AcceptanceReadinessItem,
  READINESS_ORDER,
  READINESS_PRESENTATION,
} from './acceptance-readiness.model';

/** One readiness group of the Human Review lane, shaped like a review sub-section. */
export interface ReadinessGroup {
  readonly kind: AcceptanceReadinessClass | 'unchecked';
  readonly label: string;
  readonly icon: string;
  readonly jobs: TaskInfo[];
}

/** Lookup key of a card in a readiness report: its task key, or its id. */
export function readinessKey(job: Pick<TaskInfo, 'taskKey' | 'id'>): string {
  return job.taskKey || job.id;
}

/**
 * Groups the lane's cards by readiness class in display order, then applies
 * the filter. A card the last check did not cover lands in "Not checked yet".
 * Empty groups are dropped, so the lane count stays the sum of what it shows.
 */
export function groupByReadiness(
  jobs: readonly TaskInfo[],
  items: ReadonlyMap<string, AcceptanceReadinessItem>,
  filter: AcceptanceReadinessFilter,
): ReadinessGroup[] {
  const buckets = new Map<ReadinessGroup['kind'], TaskInfo[]>();
  for (const job of jobs) {
    const kind = items.get(readinessKey(job))?.readiness ?? 'unchecked';
    if (filter === 'ready' && kind !== 'ready') continue;
    if (filter === 'not-ready' && kind === 'ready') continue;
    const bucket = buckets.get(kind) ?? [];
    bucket.push(job);
    buckets.set(kind, bucket);
  }
  const kinds: ReadinessGroup['kind'][] = [...READINESS_ORDER, 'unchecked'];
  return kinds
    .filter(kind => (buckets.get(kind)?.length ?? 0) > 0)
    .map(kind => ({
      kind,
      label: kind === 'unchecked' ? 'Not checked yet' : READINESS_PRESENTATION[kind].label,
      icon: '',
      jobs: buckets.get(kind)!,
    }));
}

/** The cards "Accept all ready" sends: ready cards among those shown. */
export function readyJobs(
  jobs: readonly TaskInfo[],
  items: ReadonlyMap<string, AcceptanceReadinessItem>,
): TaskInfo[] {
  return jobs.filter(job => items.get(readinessKey(job))?.readiness === 'ready');
}
