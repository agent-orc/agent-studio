import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { CycleTimeAggregate, ProjectCycleTimeResponse } from '../../models/project-cycle-time.model';
import { formatDuration, formatTokens, formatUsd } from '../project-cycle-time-panel/cycle-time-format';

/** Machine-time headline and the daily, step, and resource breakdowns. */
@Component({
  selector: 'app-flow-overview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './flow-overview.component.html',
  styleUrl: './flow-overview.component.scss',
})
export class FlowOverviewComponent {
  readonly data = input.required<ProjectCycleTimeResponse>();
  readonly metrics = computed(() => new Map((this.data().flow ?? []).map(a => [a.stage, a])));
  readonly topResource = computed(() => this.data().resources?.[0]);

  metric(id: string): CycleTimeAggregate | undefined { return this.metrics().get(id); }
  duration(value: number | null | undefined): string { return formatDuration(value) ?? '–'; }
  tokens(value: number | null | undefined): string { return formatTokens(value); }
  usd(value: number | null | undefined): string { return formatUsd(value); }
  share(value: number | null | undefined): string {
    return value === null || value === undefined ? '–' : `${Math.round(value * 100)}%`;
  }
}
