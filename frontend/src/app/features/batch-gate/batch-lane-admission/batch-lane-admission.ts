import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { BatchGateState } from '../../../models/batch-gate.model';

/**
 * AGT-3856: full suites held while a batch gate and one other full suite run,
 * and per-card suites not started because a batch gates the card anyway.
 */
@Component({
  selector: 'app-batch-lane-admission',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-lane-admission.html',
  styleUrl: './batch-lane-admission.scss',
})
export class BatchLaneAdmissionComponent {
  readonly lane = input<BatchGateState['laneAdmission']>();
  readonly refusals = input<BatchGateState['suiteRefusals']>();

  date(value?: string | null): string {
    const stamp = value ? Date.parse(value) : NaN;
    if (!Number.isFinite(stamp)) return 'Unknown';
    return new Date(stamp).toLocaleString('en-US', { dateStyle: 'medium', timeStyle: 'short' });
  }
}
