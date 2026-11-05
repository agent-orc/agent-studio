import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { BatchGateService } from '../../../services/batch-gate.service';
import { batchEjectionLabel, batchMemberFor, type BatchMemberState, type BatchMemberStatus } from '../../../models/batch-gate.model';
import { StatusIndicatorComponent, type StatusTone } from '../../../components/status-indicator/status-indicator.component';

/**
 * AGT-3403: batch membership tone. Being in a batch is a fact on the card, not
 * work the card itself runs, so it is `info`; unknown states fall back to neutral.
 */
export const BATCH_MEMBER_TONES: Readonly<Record<BatchMemberStatus, StatusTone>> = {
  pending: 'neutral',
  'in-batch': 'info',
  published: 'success',
  fallback: 'warning',
  'pre-check-red': 'danger',
  'review-fix': 'warning',
};

export function batchMemberTone(status: string | null | undefined): StatusTone {
  return BATCH_MEMBER_TONES[status as BatchMemberStatus] ?? 'neutral';
}

/** Visible badge text for a batch member, including the pre-check suffix. */
export function batchMemberLabel(item: BatchMemberState, ejection: string | null): string {
  let label: string;
  switch (item.status) {
    case 'pending': label = 'Batch pending'; break;
    case 'in-batch': label = `Batch ${item.batchId} · ${item.position ?? '?'} of ${item.size ?? '?'}`; break;
    case 'published': label = 'Batch published'; break;
    case 'fallback': label = ejection ? `Per-task fallback · ${ejection}` : 'Per-task fallback'; break;
    case 'pre-check-red': label = 'Pre-check red'; break;
    case 'review-fix': label = item.reason ? `Review fix before batch · ${item.reason}` : 'Review fix before batch'; break;
    default: label = 'Batch';
  }
  if (item.preCheck && item.status !== 'pre-check-red') label += ` · pre-check ${item.preCheck.outcome}`;
  return label;
}

@Component({
  selector: 'app-batch-member',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-member.html',
})
export class BatchMemberComponent {
  readonly project = input.required<string>();
  readonly taskKey = input.required<string>();
  readonly referenceKey = input<string | null | undefined>(null);
  readonly displayKey = input<string | null | undefined>(null);
  readonly taskId = input<string | null | undefined>(null);
  private readonly gate = inject(BatchGateService);
  readonly member = computed(() => batchMemberFor(this.gate.state(this.project())(), this.taskKey(), this.referenceKey(), this.displayKey(), this.taskId()));
  readonly pushWait = computed(() => {
    const state = this.gate.state(this.project())();
    if (!state) return null;
    return state.pushWaits?.find(wait => wait.jobId === this.taskId())
      ?? (state.pushWait?.jobId === this.taskId() ? state.pushWait : null);
  });
  readonly ejection = computed(() => batchEjectionLabel(this.member()?.reason));
  readonly badge = computed(() => {
    const item = this.member();
    if (!item) return null;
    const ejection = this.ejection();
    return {
      status: item.status,
      reason: item.reason ?? null,
      title: item.preCheck?.reason ?? item.reasonDetail ?? ejection,
      tone: batchMemberTone(item.status),
      label: batchMemberLabel(item, ejection),
    };
  });
  constructor() {
    effect(onCleanup => {
      const project = this.project();
      if (project) onCleanup(this.gate.watch(project));
    });
  }
}
