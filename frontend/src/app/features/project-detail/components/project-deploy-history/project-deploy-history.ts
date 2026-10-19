import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import {
  ALL_DEPLOY_RUNS,
  OUTCOME_LABEL,
  filterDeployRuns,
  formatDuration,
  shortArtifact,
  type DeployPanel,
  type DeployPeriod,
  type DeployRun,
  type DeployRunFilter,
  type DeploymentState,
} from '../../state/deploy-area.model';

const OUTCOMES: readonly DeploymentState[] = ['succeeded', 'failed', 'running', 'queued'];
const PERIODS: readonly { value: DeployPeriod; label: string }[] = [
  { value: '24h', label: 'Last 24 hours' },
  { value: '7d', label: 'Last 7 days' },
  { value: '30d', label: 'Last 30 days' },
  { value: 'all', label: 'All time' },
];

/** Deploy history table with environment, outcome, and period filters. */
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
  readonly openRun = output<string>();

  readonly filter = signal<DeployRunFilter>(ALL_DEPLOY_RUNS);
  readonly visible = computed(() => filterDeployRuns(this.runs(), this.filter(), this.now()));
  readonly filtered = computed(() => this.filter() !== ALL_DEPLOY_RUNS);

  protected readonly outcomes = OUTCOMES;
  protected readonly periods = PERIODS;
  protected readonly outcomeLabel = OUTCOME_LABEL;
  protected readonly shortArtifact = shortArtifact;
  protected readonly duration = formatDuration;

  setEnvironment(environmentId: string): void { this.filter.update(filter => ({ ...filter, environmentId })); }
  setOutcome(outcome: string): void {
    this.filter.update(filter => ({ ...filter, outcome: outcome as DeployRunFilter['outcome'] }));
  }
  setPeriod(period: string): void { this.filter.update(filter => ({ ...filter, period: period as DeployPeriod })); }
  reset(): void { this.filter.set(ALL_DEPLOY_RUNS); }
}
