import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { DialogComponent } from '../../../../../../components/dialog/dialog.component';
import {
  formatTokenCostDisplay,
  incompleteTokenCostLabel,
} from '../../../../../tokens';
import type { PipelineRowVm, TokenBreakdownRowVm } from '../pipeline-row.vm';
import {
  formatAbsoluteTime,
  formatStepDuration,
  formatTokens,
  liveStepDurationMs,
  stepKindLabel,
} from '../overview-pane-formatters';

/**
 * Token and cost breakdown for one pipeline step, opened from the step's cost
 * cell. Split out of `overview-pane.component.ts` in AGT-2819.
 *
 * Everything it renders is derived from the row it is given plus three scalars
 * the pane already owns, so it holds no state of its own and the pane keeps
 * deciding which row (if any) is open.
 */
@Component({
  selector: 'app-overview-step-token-modal',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DialogComponent, TooltipDirective],
  templateUrl: './overview-step-token-modal.component.html',
  styleUrl: './overview-step-token-modal.component.scss',
})
export class OverviewStepTokenModalComponent {
  readonly formatTokens = formatTokens;
  readonly stepKindLabel = stepKindLabel;

  readonly row = input.required<PipelineRowVm>();
  /** Recorded agent runs, used for the CORE row's "N agent runs" call count. */
  readonly agentRunCount = input<number>(0);
  /** False while an archived run is selected in the run switcher. */
  readonly currentRun = input<boolean>(true);
  /** The pane's live tick, so a running step's duration line ticks up here too. */
  readonly nowMs = input<number>(Date.now());

  readonly closeRequest = output<void>();

  tokenBreakdownRows(row: PipelineRowVm): TokenBreakdownRowVm[] {
    return [
      { label: 'Input', tokens: row.inputTokens, costUsd: row.inputCostUsd },
      { label: 'Output', tokens: row.outputTokens, costUsd: row.outputCostUsd },
      { label: 'Cache read', tokens: row.cacheReadTokens, costUsd: row.cacheReadCostUsd },
      { label: 'Cache write', tokens: row.cacheCreationTokens, costUsd: row.cacheCreationCostUsd },
    ];
  }

  tokenComponentTotal(row: PipelineRowVm): number {
    return row.inputTokens + row.outputTokens + row.cacheReadTokens + row.cacheCreationTokens;
  }

  tokenComponentMatchesTotal(row: PipelineRowVm): boolean {
    return this.tokenComponentTotal(row) === row.totalTokens;
  }

  tokenStepCallsLabel(row: PipelineRowVm): string {
    if (row.kind === 'core' && this.currentRun()) {
      const n = this.agentRunCount();
      if (n > 0) return n === 1 ? '1 agent run' : `${n} agent runs`;
    }
    if (row.status === 'passed' || row.status === 'failed' || row.status === 'skipped' || row.status === 'notApplicable') {
      return '1 step execution';
    }
    return 'Not reported';
  }

  tokenStepSourceLabel(row: PipelineRowVm): string {
    const source = row.tokenUsageSource?.trim();
    if (source) return source;
    if (row.kind === 'core') return 'CORE agent run';
    return 'Pipeline step usage';
  }

  costDisplay(costUsd: number, totalTokens: number, unpricedRuns: number): string {
    return formatTokenCostDisplay({ costUsd, totalTokens, unpricedRuns });
  }

  incompleteCostLabel(unpricedRuns: number): string {
    return incompleteTokenCostLabel(unpricedRuns);
  }

  isPartialCost(costUsd: number, unpricedRuns: number): boolean {
    return costUsd > 0 && unpricedRuns > 0;
  }

  tokenStepTimeLabel(row: PipelineRowVm): string {
    const parts: string[] = [];
    if (row.startedAt) parts.push(`Started ${formatAbsoluteTime(row.startedAt)}`);
    if (row.completedAt) parts.push(`Ended ${formatAbsoluteTime(row.completedAt)}`);
    const duration = liveStepDurationMs(row, this.nowMs());
    if (duration > 0) parts.push(`Duration ${formatStepDuration(duration)}`);
    return parts.length > 0 ? parts.join(' · ') : 'No step time recorded';
  }
}
