import { TaskInfo } from '../../../models/task.model';
import { laneName } from '../../../models/lane-presentation';
import { TaskState } from '../../../models/task.model';

/**
 * Splits 4-review jobs into the two swim-lane sub-sections rendered by
 * <app-job-column>. Pure so it stays testable without Angular's TestBed.
 *
 * Routing rule: any job with a non-null `orchestratorVerdict` belongs in
 * the "Orchestrator review" sub-section; everything else falls through to
 * "Human review". The verdict values map 1:1 to the per-project decision
 * journal kinds (`reissue` / `escalate` / `accept`); the literal `pending`
 * value is reserved for forward compatibility.
 */
export interface ReviewSubSection {
  readonly kind: 'orchestrator' | 'human';
  readonly label: string;
  readonly icon: string;
  readonly jobs: TaskInfo[];
}

export function groupReviewJobs(jobs: readonly TaskInfo[]): readonly ReviewSubSection[] {
  const orchestrator: TaskInfo[] = [];
  const human: TaskInfo[] = [];
  for (const j of jobs) {
    if (j.orchestratorVerdict) orchestrator.push(j);
    else human.push(j);
  }
  return [
    // The human half is named by the lane it represents (AGT-2715); the
    // orchestrator half is a sub-section of the legacy combined lane, not a
    // lane of its own, so it keeps its descriptive heading.
    { kind: 'orchestrator', label: 'Orchestrator review',        icon: '🤖', jobs: orchestrator },
    { kind: 'human',        label: laneName(TaskState.HumanReview), icon: '👤', jobs: human }
  ];
}
