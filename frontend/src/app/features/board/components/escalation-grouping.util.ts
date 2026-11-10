import type { TaskInfo } from '../../../models/task.model';
import { groupCauseBursts } from '../../../models/cause-burst';
import { parkedBlockerLabel } from '../../../models/parked-blocker-presentation';
import type { LaneCardGroup } from './review-grouping.util';

/**
 * AGT-3585: splits the Escalated lane into bursts of cards that escalated for
 * the same recorded cause close together (`parkedBlocker.blockerType` and
 * `parkedAt`), then one group for every other card. Returns null while no two
 * cards share a burst, so a calm lane keeps its flat list. Every card lands in
 * exactly one group, so the group counts add up to the lane count (R3).
 */
export function groupEscalatedJobs(jobs: readonly TaskInfo[]): LaneCardGroup[] | null {
  const bursts = groupCauseBursts(
    jobs,
    job => job.parkedBlocker?.blockerType?.trim().toLowerCase() || null,
    job => Date.parse(job.parkedBlocker?.parkedAt ?? ''),
  );
  const groups: LaneCardGroup[] = [];
  const rest: TaskInfo[] = [];
  for (const burst of bursts) {
    if (burst.cause === null || burst.members.length < 2) {
      rest.push(...burst.members);
      continue;
    }
    groups.push({
      key: burst.key,
      kind: 'burst',
      label: parkedBlockerLabel(burst.cause),
      icon: '',
      testid: 'escalation-burst',
      jobs: burst.members,
      burst: { cause: burst.cause, since: new Date(burst.firstMs).toISOString() },
    });
  }
  if (groups.length === 0) return null;
  if (rest.length > 0) {
    groups.push({ key: 'rest', kind: 'rest', label: 'Other escalations', icon: '', testid: 'escalation-rest', jobs: rest });
  }
  return groups;
}
