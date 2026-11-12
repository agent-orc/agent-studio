import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatusIndicatorComponent } from '../../../components/status-indicator/status-indicator.component';
import type { BatchQuarantinedRecord } from '../../../models/batch-gate.model';

/**
 * AGT-3681: batch records the store could not read. The pilot skips those
 * batches and keeps every other batch running; each row says what to do.
 */
@Component({
  selector: 'app-batch-quarantine',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-quarantine.html',
  styleUrl: './batch-quarantine.scss',
})
export class BatchQuarantineComponent {
  readonly records = input.required<BatchQuarantinedRecord[]>();
  kindLabel(kind: string): string {
    return kind === 'manifest' ? 'Manifest' : kind === 'state' ? 'State record' : kind === 'run' ? 'Run record' : kind;
  }
}
