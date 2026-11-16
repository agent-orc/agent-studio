import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import {
  TaskState,
  type TaskInfo,
  type TaskTestEvidenceSource,
  type TaskTestRunEvidence,
  type TestEvidenceState,
} from '../../../../models/task.model';

type TestEvidenceContext = Pick<TaskInfo, 'id' | 'watchPath' | 'state' | 'commit' | 'commits' | 'commitCount' | 'integration' | 'testEvidence'> & Pick<Partial<TaskInfo>, 'mode'>;
export type TestEvidenceStatusVariant = 'card' | 'panel';

const MISSING_EVIDENCE_RELEVANT_STATES = new Set<string>([
  TaskState.AutoReview,
  TaskState.Completed,
  TaskState.Archive,
]);

function hasRecordedEvidence(evidence: TaskTestRunEvidence): boolean {
  return evidence.evidenceState !== 'unassigned'
    || !!evidence.runId
    || (evidence.sources?.length ?? evidence.sourceCount ?? 0) > 0;
}

function hasAttributedDelivery(task: TestEvidenceContext): boolean {
  return (task.commitCount ?? task.commits?.length ?? 0) > 0
    || !!task.commit
    || !!task.integration?.deliveryRef?.trim();
}

export function visibleTestEvidence(task: TestEvidenceContext): TaskTestRunEvidence | null {
  const evidence = task.testEvidence;
  if (!evidence || task.mode === 'concept') return null;
  if (hasRecordedEvidence(evidence)) return evidence;
  return hasAttributedDelivery(task) || MISSING_EVIDENCE_RELEVANT_STATES.has(task.state)
    ? evidence
    : null;
}

/**
 * AGT-3403: evidence state to shared status tone. Proven is the wanted state,
 * a pending verdict needs attention, a failed one is danger, and evidence that
 * is not proven, not applicable or not assigned has nothing acute to report.
 */
export const TEST_EVIDENCE_STATE_TONES: Readonly<Record<TestEvidenceState, StatusTone>> = {
  proven: 'success',
  pending: 'warning',
  failed: 'danger',
  'not-proven': 'neutral',
  'not-applicable': 'neutral',
  unassigned: 'neutral',
};

/** Per-source result to shared status tone (panel variant). */
export const TEST_EVIDENCE_SOURCE_TONES: Readonly<Record<string, StatusTone>> = {
  passed: 'success',
  blocked: 'warning',
  'integration-red': 'warning',
  failed: 'danger',
  'not-proven': 'neutral',
  'not-applicable': 'neutral',
};

export function testEvidenceStateTone(state: string | null | undefined): StatusTone {
  return TEST_EVIDENCE_STATE_TONES[state as TestEvidenceState] ?? 'neutral';
}

export function testEvidenceSourceTone(result: string | null | undefined): StatusTone {
  return (result && Object.hasOwn(TEST_EVIDENCE_SOURCE_TONES, result) ? TEST_EVIDENCE_SOURCE_TONES[result] : null) ?? 'neutral';
}

/**
 * Link to a file in the task folder, or null for a ref that is absent or
 * leaves the folder.
 */
export function taskFileHref(task: Pick<TaskInfo, 'id' | 'watchPath'>, ref: string | null | undefined): string | null {
  const jobId = task.id?.trim();
  const fileRef = ref?.trim().replace(/\\/g, '/');
  if (!jobId || !fileRef || fileRef.startsWith('/') || fileRef.split('/').some(part => !part || part === '.' || part === '..')) {
    return null;
  }
  const path = fileRef.split('/').map(encodeURIComponent).join('/');
  const watchPath = task.watchPath?.trim();
  const query = [
    watchPath ? `watchPath=${encodeURIComponent(watchPath)}` : '',
    'scope=workspace',
  ].filter(Boolean).join('&');
  return `/api/tasks/${encodeURIComponent(jobId)}/files/${path}?${query}`;
}

/**
 * AGT-3812: the newest review build-tests verdict when it does not prove the
 * change green, so acceptance surfaces can say why: the integration branch is
 * red, the verdict is missing, or the build failed.
 */
export function reviewBuildTestsGap(evidence: TaskTestRunEvidence | null | undefined): TaskTestEvidenceSource | null {
  const source = (evidence?.sources ?? []).find(item => item.kind === 'review-build-tests');
  return source && source.result !== 'passed' && source.result !== 'not-applicable' ? source : null;
}

@Component({
  selector: 'app-test-evidence-status',
  standalone: true,
  imports: [AppTooltipDirective, StatusIndicatorComponent],
  templateUrl: './test-evidence-status.component.html',
  styleUrl: './test-evidence-status.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TestEvidenceStatusComponent {
  readonly task = input.required<TestEvidenceContext>();
  readonly variant = input<TestEvidenceStatusVariant>('card');
  readonly testId = input('task-card-test-evidence');
  readonly evidence = computed(() => visibleTestEvidence(this.task()));
  readonly evidenceTone = testEvidenceStateTone;
  readonly sourceTone = testEvidenceSourceTone;

  tooltipText(): string {
    const evidence = this.evidence();
    if (!evidence) return '';
    const details = evidence.runId
      ? [`Project test run ${evidence.runId} at ${evidence.runCommit || 'unknown commit'}`]
      : [];
    details.push(...(evidence.sources ?? []).map(source => `${source.summary}: ${this.sourceReason(source)}`));
    if (evidence.sourceLabel && !evidence.sources?.length) details.push(evidence.sourceLabel);
    return details.length > 0 ? details.join(' · ') : evidence.summary;
  }

  sourceDetail(): string {
    const sources = this.evidence()?.sources ?? [];
    // A board card omits the label when it repeats the summary.
    if (sources.length === 0) return this.evidence()?.sourceLabel ?? this.evidence()?.summary ?? '';
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
    return taskFileHref(this.task(), source.reportRef);
  }

  outputHref(source: TaskTestEvidenceSource): string | null {
    return taskFileHref(this.task(), source.outputRef);
  }

  private sourceLabel(kind: string): string {
    if (kind === 'review-build-tests') return 'Remote review';
    if (kind === 'review-aspects') return 'Review aspects';
    if (kind === 'pre-develop-build-gate') return 'Pre-develop gate';
    if (kind === 'pre-main-test-gate') return 'Pre-main gate';
    return 'Build/test gate';
  }
}
