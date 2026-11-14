import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { BatchGateState } from '../../../models/batch-gate.model';

@Component({
  selector: 'app-batch-phase-metrics',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-phase-metrics.html',
  styleUrl: './batch-phase-metrics.scss',
})
export class BatchPhaseMetricsComponent {
  private static nextId = 0;
  readonly headingId = `batch-phase-${++BatchPhaseMetricsComponent.nextId}`;
  readonly summary = input<BatchGateState['dailySummary']>();
  readonly rows = [
    { key: 'reviewToPreCheck', label: 'Review pass to pre-check end' },
    { key: 'formationWait', label: 'Pre-check end to batch start' },
    { key: 'batchRun', label: 'Batch run' },
    { key: 'publish', label: 'Publish after gate' },
  ];
  minutes(value: number | null | undefined): string { return value == null ? 'No data' : `${value.toFixed(1)} min`; }
  share(value: number | null | undefined): string { return value == null ? 'No data' : `${(value * 100).toFixed(1)}%`; }
}
