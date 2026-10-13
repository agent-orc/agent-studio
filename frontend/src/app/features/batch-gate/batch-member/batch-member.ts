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
  readonly ejection = computed(() => batchEjectionLabel(this.member()?.reason));
  constructor() {
    effect(onCleanup => {
      const project = this.project();
      if (project) onCleanup(this.gate.watch(project));
    });
  }
}
