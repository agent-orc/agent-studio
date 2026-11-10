import { Injectable, signal } from '@angular/core';
import type { TaskInfo } from '../../../models/task.model';
import type { LaneCardGroup } from '../../board/components/review-grouping.util';

type LaneGrouper = (jobs: readonly TaskInfo[]) => LaneCardGroup[] | null;

/**
 * AGT-3564 - the only acceptance readiness piece the eager Human Review lane
 * loads. The deferred `AcceptanceReadinessStore` registers its grouping here;
 * until it has and a check has answered, the lane keeps its board order.
 */
@Injectable({ providedIn: 'root' })
export class AcceptanceReadinessLaneGrouping {
  private readonly grouper = signal<LaneGrouper | null>(null);

  register(grouper: LaneGrouper): void {
    this.grouper.set(grouper);
  }

  groupsFor(jobs: readonly TaskInfo[]): LaneCardGroup[] | null {
    return this.grouper()?.(jobs) ?? null;
  }
}
