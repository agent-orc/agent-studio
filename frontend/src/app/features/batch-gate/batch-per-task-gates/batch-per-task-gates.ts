import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { BatchGateState } from '../../../models/batch-gate.model';

/** Per-task gates that run while the batch lane is paused, one row per member. */
@Component({
  selector: 'app-batch-per-task-gates',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-per-task-gates.html',
  styleUrl: './batch-per-task-gates.scss',
})
export class BatchPerTaskGatesComponent {
  readonly gates = input<BatchGateState['perTaskGates']>();

  date(value?: string | null): string {
    const stamp = value ? Date.parse(value) : NaN;
    if (!Number.isFinite(stamp)) return 'Unknown';
    return new Date(stamp).toLocaleString('en-US', { dateStyle: 'medium', timeStyle: 'short' });
  }
}
