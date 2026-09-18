import type { TaskInfo } from '../../../../models/task.model';

export function isUnpullableDependencyHold(job: TaskInfo): boolean {
  return job.pickupHold?.classification === 'stalled'
    || job.pickupHold?.classification === 'unsatisfiable';
}
