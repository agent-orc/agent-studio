import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { batchDateTime } from '../../../models/batch-gate.model';
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

  readonly date = batchDateTime;
}
