import { TaskInfo } from '../../../models/task.model';
import { laneName } from '../../../models/lane-presentation';
import { TaskState } from '../../../models/task.model';

/** One in-column subsection of a grouped lane: a review split or an escalation burst. */
export interface LaneCardGroup {
  readonly key: string;
  readonly kind: string;
  readonly label: string;
  readonly icon: string;
  readonly testid: string;
  readonly jobs: TaskInfo[];
  /** AGT-3585: set on an escalation burst; the shared cause and when the burst began. */
  readonly burst?: { readonly cause: string; readonly since: string } | null;
}

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
export interface ReviewSubSection extends LaneCardGroup {
  readonly kind: 'orchestrator' | 'human';
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
    { key: 'orchestrator', kind: 'orchestrator', label: 'Orchestrator review', icon: '🤖', testid: 'review-subsection-orchestrator', jobs: orchestrator },
    { key: 'human', kind: 'human', label: laneName(TaskState.HumanReview), icon: '👤', testid: 'review-subsection-human', jobs: human }
  ];
}
