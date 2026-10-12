import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import type { TaskInfo } from '../../../models/task.model';
import { batchMemberFor, batchPublishedSha } from '../../../models/batch-gate.model';
import { BatchGateService } from '../../../services/batch-gate.service';
import { BatchPanelComponent } from '../batch-panel/batch-panel';

@Component({
  selector: 'app-batch-detail',
  standalone: true,
  imports: [BatchPanelComponent],
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
  readonly unavailable = computed(() => this.gate.unavailable(this.project())());
  readonly member = computed(() => batchMemberFor(this.state(), this.task().taskKey, this.task().key, this.task().displayKey, this.task().id));
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
