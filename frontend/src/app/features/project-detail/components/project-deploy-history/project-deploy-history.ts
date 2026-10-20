import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import {
  ALL_DEPLOY_RUNS,
  DEPLOYMENT_STATES,
  RUN_STATE_LABEL,
  filterDeployRuns,
  formatDuration,
  triggerLabel,
  type DeployPanel,
  type DeployPeriod,
  type DeployRun,
  type DeployRunFilter,
} from '../../state/deploy-area.model';

const PERIODS: readonly { value: DeployPeriod; label: string }[] = [
  { value: '24h', label: 'Last 24 hours' },
  { value: '7d', label: 'Last 7 days' },
  { value: '30d', label: 'Last 30 days' },
  { value: 'all', label: 'All time' },
];

/** AGT-3201 deploy history with environment, outcome, and period filters. */
@Component({
  selector: 'app-project-deploy-history',
  standalone: true,
  imports: [DatePipe, EmptyStateComponent, LoadingSurfaceComponent],
  templateUrl: './project-deploy-history.html',
  styleUrl: './project-deploy-history.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployHistoryComponent {
  readonly panel = input.required<DeployPanel<unknown>>();
  readonly runs = input.required<readonly DeployRun[]>();
  readonly environments = input.required<readonly { id: string; name: string }[]>();
  readonly now = input.required<number>();
  readonly selectedRunId = input<string | null>(null);
  readonly total = input<number>(0);
  readonly offset = input<number>(0);
  readonly openRun = output<string>();
  readonly filterChange = output<DeployRunFilter>();
  readonly pageChange = output<number>();

  readonly filter = signal<DeployRunFilter>(ALL_DEPLOY_RUNS);
  readonly visible = computed(() => filterDeployRuns(this.runs(), this.filter(), this.now()));
  readonly filtered = computed(() => {
    const value = this.filter();
    return value.environmentId !== 'all' || value.state !== 'all' || value.period !== 'all';
  });

  readonly errorMessage = computed(() => {
    const panel = this.panel();
    return panel.status === 'error' ? `${panel.message} Try Refresh.` : '';
  });

  protected readonly states = DEPLOYMENT_STATES;
  protected readonly periods = PERIODS;
  protected readonly stateLabel = RUN_STATE_LABEL;
  protected readonly triggerLabel = triggerLabel;
  protected readonly duration = formatDuration;

  setEnvironment(environmentId: string): void { this.setFilter({ ...this.filter(), environmentId }); }
  setState(state: string): void {
    this.setFilter({ ...this.filter(), state: state as DeployRunFilter['state'] });
  }
  setPeriod(period: string): void { this.setFilter({ ...this.filter(), period: period as DeployPeriod }); }
  reset(): void { this.setFilter(ALL_DEPLOY_RUNS); }
  private setFilter(filter: DeployRunFilter): void { this.filter.set(filter); this.filterChange.emit(filter); }
}
