import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import type { TaskInfo } from '../../../models/task.model';
import { batchMemberFor, batchProvenRedFor, batchPublishedSha } from '../../../models/batch-gate.model';
import { BatchGateService } from '../../../services/batch-gate.service';
import { BatchPanelComponent } from '../batch-panel/batch-panel';
import { BatchProvenRedComponent } from '../batch-proven-red/batch-proven-red';
import { StatusIndicatorComponent } from '../../../components/status-indicator/status-indicator.component';

@Component({
  selector: 'app-batch-detail',
  standalone: true,
  imports: [BatchPanelComponent, BatchProvenRedComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-detail.html',
  styleUrl: './batch-detail.scss',
})
export class BatchDetailComponent {
  readonly task = input.required<TaskInfo>();
  readonly panelOpen = signal(false);
  readonly publishedSha = batchPublishedSha;
  readonly closePanel = () => this.panelOpen.set(false);
  private readonly gate = inject(BatchGateService);
  readonly project = computed(() => this.task().projectName);
  readonly state = computed(() => this.gate.state(this.project())());
  readonly pushWait = computed(() => {
    const state = this.state();
    if (!state) return null;
    return state.pushWaits?.find(wait => wait.jobId === this.task().id)
      ?? (state.pushWait?.jobId === this.task().id ? state.pushWait : null);
  });
  /** AGT-3463: drives the whole section; null while loading, when disabled, or for a non-member. */
  readonly member = computed(() => {
    const state = this.state();
    if (!state || state.plan.decision === 'disabled') return null;
    return batchMemberFor(state, this.task().taskKey, this.task().key, this.task().displayKey, this.task().id);
  });
  /** AGT-3577: the current finding when the bisection proved this card red. */
  readonly provenRed = computed(() => batchProvenRedFor(this.state(), this.member(),
    this.task().taskKey, this.task().key, this.task().displayKey, this.task().id));
  readonly record = computed(() => {
    const batches = this.state()?.batches;
    const batchId = this.member()?.batchId;
    if (batchId) return batches?.find(batch => batch.batchId === batchId) ?? null;
    return batches?.find(batch => batch.members.some(member =>
      [this.task().taskKey, this.task().key, this.task().displayKey, this.task().id].includes(member.taskKey))) ?? null;
  });

  constructor() {
    effect(onCleanup => onCleanup(this.gate.watch(this.project())));
  }
}
