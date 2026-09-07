import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import {
  TaskState,
  type TaskInfo,
  type TaskTestEvidenceSource,
  type TaskTestRunEvidence,
} from '../../../../models/task.model';

type TestEvidenceContext = Pick<TaskInfo, 'id' | 'watchPath' | 'state' | 'commit' | 'commits' | 'integration' | 'testEvidence'>;
export type TestEvidenceStatusVariant = 'card' | 'panel';

const MISSING_EVIDENCE_RELEVANT_STATES = new Set<string>([
  TaskState.AutoReview,
  TaskState.HumanReview,
  TaskState.Completed,
  TaskState.Archive,
]);

function hasRecordedEvidence(evidence: TaskTestRunEvidence): boolean {
  return evidence.evidenceState !== 'unassigned'
    || !!evidence.runId
    || (evidence.sources?.length ?? 0) > 0;
}

function hasAttributedDelivery(task: TestEvidenceContext): boolean {
  return (task.commits?.length ?? 0) > 0
    || !!task.commit
    || !!task.integration?.deliveryRef?.trim();
}

export function visibleTestEvidence(task: TestEvidenceContext): TaskTestRunEvidence | null {
  const evidence = task.testEvidence;
  if (!evidence) return null;
  if (hasRecordedEvidence(evidence)) return evidence;
  return hasAttributedDelivery(task) || MISSING_EVIDENCE_RELEVANT_STATES.has(task.state)
    ? evidence
    : null;
}

@Component({
  selector: 'app-test-evidence-status',
  standalone: true,
  imports: [AppTooltipDirective],
  templateUrl: './test-evidence-status.component.html',
  styleUrl: './test-evidence-status.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TestEvidenceStatusComponent {
  readonly task = input.required<TestEvidenceContext>();
  readonly variant = input<TestEvidenceStatusVariant>('card');
  readonly testId = input('task-card-test-evidence');
  readonly evidence = computed(() => visibleTestEvidence(this.task()));

  tooltipText(): string {
    const evidence = this.evidence();
    if (!evidence) return '';
    const details = evidence.runId
      ? [`Project test run ${evidence.runId} at ${evidence.runCommit || 'unknown commit'}`]
      : [];
    details.push(...(evidence.sources ?? []).map(source => `${source.summary}: ${this.sourceReason(source)}`));
    return details.length > 0 ? details.join(' · ') : evidence.summary;
  }

  sourceDetail(): string {
    const sources = this.evidence()?.sources ?? [];
    if (sources.length > 1) return sources.slice(1).map(source => source.summary).join(' · ');
    if (sources.length === 1) return `${this.sourceLabel(sources[0].kind)} · ${sources[0].id}`;
    return '';
  }

  sourceReason(source: TaskTestEvidenceSource): string {
    return source.reason?.trim() || 'No reason was recorded for this evidence source.';
  }

  sourceAriaLabel(source: TaskTestEvidenceSource): string {
    return `${source.summary}. ${this.sourceReason(source)}`;
  }

  reportHref(source: TaskTestEvidenceSource): string | null {
    const jobId = this.task().id?.trim();
    const reportRef = source.reportRef?.trim().replace(/\\/g, '/');
    if (!jobId || !reportRef || reportRef.startsWith('/') || reportRef.split('/').some(part => !part || part === '.' || part === '..')) {
      return null;
    }
    const path = reportRef.split('/').map(encodeURIComponent).join('/');
    const watchPath = this.task().watchPath?.trim();
    const query = [
      watchPath ? `watchPath=${encodeURIComponent(watchPath)}` : '',
      'scope=workspace',
    ].filter(Boolean).join('&');
    return `/api/tasks/${encodeURIComponent(jobId)}/files/${path}?${query}`;
  }

  private sourceLabel(kind: string): string {
    if (kind === 'review-build-tests') return 'Remote review';
    if (kind === 'review-aspects') return 'Review aspects';
    if (kind === 'pre-develop-build-gate') return 'Pre-develop gate';
    if (kind === 'pre-main-test-gate') return 'Pre-main gate';
    return 'Build/test gate';
  }
}
