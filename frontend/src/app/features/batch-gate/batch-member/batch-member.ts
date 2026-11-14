import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { BatchGateService } from '../../../services/batch-gate.service';
import { batchEjectionLabel, batchFallbackWaitLabel, batchMemberFor, type BatchMemberState, type BatchMemberStatus } from '../../../models/batch-gate.model';
import { StatusIndicatorComponent, type StatusTone } from '../../../components/status-indicator/status-indicator.component';

@Component({
  selector: 'app-batch-member',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusIndicatorComponent],
  templateUrl: './batch-member.html',
  styleUrl: './batch-member.scss',
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
  readonly ejection = computed(() => batchEjectionLabel(this.member()?.fallbackRule ?? this.member()?.reason));
  readonly redReason = computed(() => {
    const item = this.member();
    if (!item?.batchId) return null;
    const batch = this.gate.state(this.project())()?.batches.find(candidate => candidate.batchId === item.batchId);
    return batch?.phase === 'red' ? batch.phaseReason ?? item.reasonDetail ?? item.reason ?? 'Batch gate failed' : null;
  });
  readonly fallbackWait = computed(() => batchFallbackWaitLabel(this.member()?.waitKind));
  readonly memberTone = (status: BatchMemberStatus): StatusTone => ({
    pending: 'neutral', 'in-batch': 'info', published: 'success', fallback: 'warning',
    'pre-check-red': 'danger', 'review-fix': 'warning',
  } satisfies Record<BatchMemberStatus, StatusTone>)[status];
  readonly memberLabel = (item: BatchMemberState): string => {
    let label: string;
    switch (item.status) {
      case 'pending': label = 'Batch pending'; break;
      case 'in-batch': label = `Batch ${item.batchId} · ${item.position ?? '?'} of ${item.size ?? '?'}`; break;
      case 'published': label = 'Batch published'; break;
      case 'fallback': label = `Per-task fallback${this.ejection() ? ` · ${this.ejection()}` : ''}${this.fallbackWait() ? ` · ${this.fallbackWait()}` : ''}`; break;
      case 'pre-check-red': label = 'Pre-check red'; break;
      case 'review-fix': label = `Review fix before batch${item.reason ? ` · ${item.reason}` : ''}`; break;
    }
    if (this.redReason()) label += ` · in a red batch: ${this.redReason()}`;
    if (item.preCheck && item.status !== 'pre-check-red') label += ` · pre-check ${item.preCheck.outcome}`;
    return label;
  };
  constructor() {
    effect(onCleanup => {
      const project = this.project();
      if (project) onCleanup(this.gate.watch(project));
    });
  }
}
