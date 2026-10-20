import { GroupedJobs, TaskInfo } from '../../../models/task.model';

/**
 * Flatten the lane-keyed grouped feed into a single de-duplicated task list.
 * `GroupedJobs.review` is a legacy alias for `autoReview`, so a naive concat
 * would double-count every auto-review card; we dedupe by `taskKey` to collapse
 * those (and any other accidental cross-lane repeat).
 */
export function flattenGrouped(grouped: GroupedJobs): TaskInfo[] {
  const lanes: TaskInfo[][] = [
    grouped.backlog,
    grouped.preparation,
    grouped.orchestratorPrep,
    grouped.ready,
    grouped.progress,
    grouped.failedPickup,
    grouped.autoReview,
    grouped.humanReview,
    grouped.escalated,
    grouped.review,
    grouped.completed,
    grouped.archive,
  ];
  const seen = new Set<string>();
  const out: TaskInfo[] = [];
  for (const lane of lanes) {
    if (!lane) continue;
    for (const t of lane) {
      if (seen.has(t.taskKey)) continue;
      seen.add(t.taskKey);
      out.push(t);
    }
  }
  return out;
}
