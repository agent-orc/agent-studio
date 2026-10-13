import type { TaskReferences } from '../../../../models/task.model';

/**
 * True only for a full reference set. The slim board summary carries
 * `dependsOnCount`/`dependsOnKeys` and at most three `dependsOn` edges and no
 * other kinds; seeding or saving from it would drop edges.
 */
export function isCompleteReferences(r: TaskReferences | undefined | null): r is TaskReferences {
  if (!r) return false;
  const summary = r as unknown as Record<string, unknown>;
  if ('dependsOnCount' in summary || 'dependsOnKeys' in summary) return false;
  return Array.isArray(r.dependsOn) && Array.isArray(r.relatedTo)
    && Array.isArray(r.blockedBy) && Array.isArray(r.supersedes);
}
