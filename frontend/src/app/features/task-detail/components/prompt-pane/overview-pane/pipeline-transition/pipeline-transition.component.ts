import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { PipelineRowVm } from '../pipeline-row.vm';
import { formatStepDuration, stepStatusLabel } from '../overview-pane-formatters';
import { formatTokenCostDisplay } from '../../../../../tokens';

@Component({
  selector: 'app-pipeline-transition',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-transition.component.html',
  styleUrl: './pipeline-transition.component.scss',
})
export class PipelineTransitionComponent {
  readonly row = input.required<PipelineRowVm>();
  readonly artefact = input<string | null>(null);
  readonly documentRequested = output<string>();
  readonly duration = formatStepDuration;
  readonly statusLabel = stepStatusLabel;

  price(row: PipelineRowVm): string {
    if (row.costStatus === 'deterministic-zero') return '$0.00';
    if (row.costStatus === 'unmeasured' || row.costStatus === 'missing-model') return 'unmeasured';
    return formatTokenCostDisplay({ costUsd: row.costUsd, totalTokens: row.totalTokens, unpricedRuns: row.unpricedRuns });
  }
}
