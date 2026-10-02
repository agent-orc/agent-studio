import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import {
  BOARD_LANE_COUNT_TOOLTIPS,
  boardLaneCountsLabel,
  type ExplorerLaneCounts,
} from '../../studio-shell.project-rows';

export type ExplorerTreeMetricView = 'numbers' | 'dots';

const MAX_DOTS = 15;

@Component({
  selector: 'app-explorer-lane-dashboard',
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './explorer-lane-dashboard.component.html',
  styleUrl: './explorer-lane-dashboard.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExplorerLaneDashboardComponent {
  readonly counts = input.required<ExplorerLaneCounts>();
  readonly view = input<ExplorerTreeMetricView>('numbers');
  readonly projectName = input.required<string>();
  readonly tooltips = BOARD_LANE_COUNT_TOOLTIPS;

  readonly label = (): string => boardLaneCountsLabel({ laneCounts: this.counts() });

  readonly decisions = (): number => this.counts().decisions ?? 0;

  dots(): readonly (keyof ExplorerLaneCounts)[] {
    const counts = this.counts();
    const dots: (keyof ExplorerLaneCounts)[] = [];
    for (const lane of ['decisions', 'ready', 'progress', 'humanReview'] as const) {
      for (let i = 0; i < (counts[lane] ?? 0) && dots.length < MAX_DOTS; i++) dots.push(lane);
    }
    return dots;
  }

  overflow(): number {
    const counts = this.counts();
    return Math.max(0, counts.ready + counts.progress + counts.humanReview + (counts.decisions ?? 0) - MAX_DOTS);
  }
}
