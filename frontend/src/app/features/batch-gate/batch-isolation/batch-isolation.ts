import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatusIndicatorComponent, type StatusTone } from '../../../components/status-indicator/status-indicator.component';
import type { BatchIsolationRow, BatchIsolationRowStatus } from '../../../models/batch-gate.model';
import { BatchProvenRedComponent } from '../batch-proven-red/batch-proven-red';

const TONES: Record<BatchIsolationRowStatus, StatusTone> = {
  'proven-red': 'danger', 'proven-green': 'success', superseded: 'neutral', 'not-isolated': 'neutral',
};
const LABELS: Record<BatchIsolationRowStatus, string> = {
  'proven-red': 'Proven red', 'proven-green': 'Proven green', superseded: 'Superseded', 'not-isolated': 'Not yet isolated',
};

/** AGT-3577: what the bisection of a red batch proved for each member. */
@Component({
  selector: 'app-batch-isolation',
  standalone: true,
  imports: [StatusIndicatorComponent, BatchProvenRedComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-isolation.html',
  styleUrl: './batch-isolation.scss',
})
export class BatchIsolationComponent {
  readonly project = input.required<string>();
  readonly batchId = input.required<string>();
  readonly rows = input.required<BatchIsolationRow[]>();
  tone(status: BatchIsolationRowStatus): StatusTone { return TONES[status]; }
  label(status: BatchIsolationRowStatus): string { return LABELS[status]; }
}
