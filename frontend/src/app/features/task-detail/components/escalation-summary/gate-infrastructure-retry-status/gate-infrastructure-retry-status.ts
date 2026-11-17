import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import type { InfrastructureRetryRequest } from '../../../../../models/batch-gate.model';
import type { TaskInfo } from '../../../../../models/task.model';
import { BatchGateService } from '../../../../../services/batch-gate.service';

/** The durable batch request follows a parked card through refreshes. */
@Component({
  selector: 'app-gate-infrastructure-retry-status',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe],
  templateUrl: './gate-infrastructure-retry-status.html',
})
export class GateInfrastructureRetryStatusComponent {
  readonly info = input.required<TaskInfo>();
  readonly requested = input<InfrastructureRetryRequest | null>(null);
  private readonly gate = inject(BatchGateService);
  readonly retry = computed(() => {
    const info = this.info();
    const key = info.key ?? info.id;
    const parkedAt = info.parkedBlocker?.parkedAt;
    const current = (request: InfrastructureRetryRequest) => request.taskKey === key
      && (!parkedAt || request.requestedAtUtc >= parkedAt);
    return this.gate.state(info.projectName)()?.infrastructureRetries?.find(current)
      ?? (this.requested() && current(this.requested()!) ? this.requested() : null);
  });

  constructor() {
    effect(onCleanup => {
      const info = this.info();
      onCleanup(this.gate.watch(info.projectName));
    });
  }
}
