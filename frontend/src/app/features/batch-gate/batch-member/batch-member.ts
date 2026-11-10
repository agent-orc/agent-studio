import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { BatchGateService } from '../../../services/batch-gate.service';
import { batchEjectionLabel, batchMemberFor } from '../../../models/batch-gate.model';

@Component({
  selector: 'app-batch-member',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
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
  readonly ejection = computed(() => batchEjectionLabel(this.member()?.reason));
  readonly redReason = computed(() => {
    const item = this.member();
    if (!item?.batchId) return null;
    const batch = this.gate.state(this.project())()?.batches.find(candidate => candidate.batchId === item.batchId);
    return batch?.phase === 'red' ? batch.phaseReason ?? item.reasonDetail ?? item.reason ?? 'Batch gate failed' : null;
  });
  constructor() {
    effect(onCleanup => {
      const project = this.project();
      if (project) onCleanup(this.gate.watch(project));
    });
  }
}
