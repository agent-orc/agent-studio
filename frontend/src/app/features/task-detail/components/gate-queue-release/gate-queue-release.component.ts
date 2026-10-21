import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';

import type { TaskInfo } from '../../../../models/task.model';
import { TaskState } from '../../../../models/task.model';
import { isGateQueueOverrunReason } from '../../../../models/batch-gate.model';
import { BatchGateService } from '../../../../services/batch-gate.service';
import { PendingButtonDirective } from '../../../../components/async-feedback';

/**
 * AGT-3219: an escalated card whose per-task gate spent its budget waiting for
 * a machine gate slot was never tested. This panel names how many cards of the
 * project are parked for that reason and returns all of them to the batch
 * queue with one operator action.
 */
@Component({
  selector: 'app-gate-queue-release',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PendingButtonDirective],
  templateUrl: './gate-queue-release.component.html',
  styleUrl: './gate-queue-release.component.scss',
})
export class GateQueueReleaseComponent {
  readonly info = input.required<TaskInfo>();

  private readonly batchGate = inject(BatchGateService);

  readonly applies = computed(() => {
    const info = this.info();
    return info.state === TaskState.Escalated && isGateQueueOverrunReason(info.parkedBlocker?.reason);
  });
  readonly releasable = signal<string[]>([]);
  readonly releasing = signal(false);
  readonly outcome = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  private loadedFor: string | null = null;

  constructor() {
    effect(() => {
      const info = this.info();
      if (!this.applies() || this.loadedFor === info.id) return;
      this.loadedFor = info.id;
      this.load(info.projectName);
    });
  }

  release(): void {
    const project = this.info().projectName;
    this.releasing.set(true);
    this.error.set(null);
    this.batchGate.releaseQueueStalled(project).subscribe({
      next: (result) => {
        this.releasing.set(false);
        const refused = result.refused.length;
        this.outcome.set(
          `Released ${result.released.length} card${result.released.length === 1 ? '' : 's'} to the gate queue`
            + (refused > 0 ? `; ${refused} stayed escalated: ${result.refused.map((item) => `${item.taskKey} (${item.reason})`).join(', ')}` : '.'),
        );
        this.load(project);
      },
      error: () => {
        this.releasing.set(false);
        this.error.set('The release failed. The cards stay escalated; try again.');
      },
    });
  }

  private load(project: string): void {
    this.batchGate.queueStalls(project).subscribe({
      next: (report) => this.releasable.set(report.releasable),
      error: () => this.releasable.set([]),
    });
  }
}
