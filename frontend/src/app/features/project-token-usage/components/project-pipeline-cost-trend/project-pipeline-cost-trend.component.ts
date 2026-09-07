import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type {
  PipelinePricingGap,
  PipelineStepKindKey,
  ProjectPipelineCostTimeline,
} from '../../models/project-token-usage.model';
import {
  buildTokenCostTooltip,
  formatTokenCostDisplay,
  formatUsageTokens,
  incompleteTokenCostLabel,
} from '../../../tokens';
import { TooltipDirective } from 'coding-agent-chat/shared';

interface PipelineKindLegendRow {
  kind: PipelineStepKindKey;
  label: string;
  tokens: number;
  cost: number;
  unpricedRuns: number;
  pricingGaps: PipelinePricingGap[];
}

interface PipelineStackSegment extends PipelineKindLegendRow {
  pctOfColumn: number;
}

interface PipelineStackColumn {
  day: string;
  shortDay: string;
  total: number;
  cost: number;
  unpricedRuns: number;
  pricingGaps: PipelinePricingGap[];
  heightPct: number;
  segments: PipelineStackSegment[];
}

const KIND_LABELS: Record<PipelineStepKindKey, string> = {
  core: 'Core run',
  aspect: 'Aspects',
  tool: 'Tool steps',
  analysis: 'Analysis',
  orchestrator: 'Orchestrator',
  drift: 'Drift',
  module: 'Modules',
};

/** Project-level pipeline price summary and day trend. */
@Component({
  selector: 'app-project-pipeline-cost-trend',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-pipeline-cost-trend.component.html',
  styleUrl: './project-pipeline-cost-trend.component.scss',
})
export class ProjectPipelineCostTrendComponent {
  readonly timeline = input<ProjectPipelineCostTimeline | null>(null);

  readonly kindLegend = computed<PipelineKindLegendRow[]>(() =>
    (this.timeline()?.kinds ?? []).map(kind => ({
      kind: kind.kind,
      label: this.kindLabel(kind.kind),
      tokens: kind.totalTokens,
      cost: kind.totalCostUsd,
      unpricedRuns: kind.unpricedRuns ?? (kind.anyModelUnknown ? 1 : 0),
      pricingGaps: kind.pricingGaps ?? [],
    })),
  );

  readonly stackColumns = computed<PipelineStackColumn[]>(() => {
    const timeline = this.timeline();
    if (!timeline?.days.length) return [];
    const dayTotals = timeline.days.map(() => 0);
    for (const kind of timeline.kinds) {
      kind.cells.forEach((cell, index) => { dayTotals[index] += cell.totalTokens; });
    }
    const maxDay = dayTotals.reduce((max, value) => Math.max(max, value), 0);
    return timeline.days.map((day, index) => {
      const total = dayTotals[index];
      const dayCost = timeline.dayCosts?.[index];
      const segments = total > 0
        ? timeline.kinds
            .filter(kind => (kind.cells[index]?.totalTokens ?? 0) > 0)
            .map(kind => {
              const cell = kind.cells[index]!;
              return {
                kind: kind.kind,
                label: this.kindLabel(kind.kind),
                tokens: cell.totalTokens,
                cost: cell.costUsd,
                unpricedRuns: cell.unpricedRuns ?? 0,
                pricingGaps: cell.pricingGaps ?? [],
                pctOfColumn: Math.round((cell.totalTokens / total) * 100),
              };
            })
        : [];
      return {
        day,
        shortDay: day.length >= 10 ? day.slice(5) : day,
        total,
        cost: dayCost?.costUsd
          ?? timeline.kinds.reduce((sum, kind) => sum + (kind.cells[index]?.costUsd ?? 0), 0),
        unpricedRuns: dayCost?.unpricedRuns ?? 0,
        pricingGaps: dayCost?.pricingGaps ?? [],
        heightPct: maxDay > 0 ? Math.max(2, Math.round((total / maxDay) * 100)) : 0,
        segments,
      };
    });
  });

  costDisplay(costUsd: number, totalTokens: number, unpricedRuns: number): string {
    return formatTokenCostDisplay({ costUsd, totalTokens, unpricedRuns });
  }

  incompleteCostLabel(unpricedRuns: number): string {
    return incompleteTokenCostLabel(unpricedRuns);
  }

  isPartialCost(costUsd: number, unpricedRuns: number): boolean {
    return costUsd > 0 && unpricedRuns > 0;
  }

  costTooltip(
    costUsd: number,
    totalTokens: number,
    unpricedRuns: number,
    pricingGaps: readonly PipelinePricingGap[],
    context: string,
  ): string {
    return buildTokenCostTooltip({
      costUsd,
      priceKnown: unpricedRuns === 0,
      totalTokens,
      context: `${context}: ${this.formatTokens(totalTokens)} tokens.`,
      unpricedRuns,
      pricingGaps,
    });
  }

  columnTooltip(column: PipelineStackColumn): string {
    if (column.total <= 0) return `${column.day}: no pipeline activity`;
    const parts = column.segments.map(segment =>
      `${segment.label} ${this.formatTokens(segment.tokens)} (${this.costDisplay(segment.cost, segment.tokens, segment.unpricedRuns)})`);
    return buildTokenCostTooltip({
      costUsd: column.cost,
      priceKnown: column.unpricedRuns === 0,
      totalTokens: column.total,
      context: `${column.day}: ${this.formatTokens(column.total)} tokens\n${parts.join('\n')}`,
      unpricedRuns: column.unpricedRuns,
      pricingGaps: column.pricingGaps,
    });
  }

  readonly formatTokens = formatUsageTokens;

  private kindLabel(kind: PipelineStepKindKey): string {
    return KIND_LABELS[kind] ?? kind;
  }
}
