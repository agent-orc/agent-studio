import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import type { HttpErrorResponse } from '@angular/common/http';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { ProjectCycleTimeService } from '../../services/project-cycle-time.service';
import {
  CYCLE_TIME_WINDOWS,
  type CycleTimeAggregate,
  type CycleTimeStageKey,
  type CycleTimeWindow,
  type ProjectCycleTimeResponse,
  type TaskCycleTimeRow,
} from '../../models/project-cycle-time.model';
import {
  compositionSegments,
  formatCount,
  formatDuration,
  formatTimestamp,
  formatTokens,
  formatUsd,
  windowLabel,
  type CompositionSegment,
} from './cycle-time-format';
import { CycleTimeTableState, type CycleTimeSortKey } from './cycle-time-table-state';
import { CycleTimeTransitionsComponent } from '../cycle-time-transitions/cycle-time-transitions.component';
import { CycleTimeTaskTransitionsComponent } from '../cycle-time-task-transitions/cycle-time-task-transitions.component';
import { FlowOverviewComponent } from '../flow-overview/flow-overview.component';

/** One sortable drill-down column. Stage columns carry the stage key so the cell reads `row.stages[key]`. */
interface DrillDownColumn {
  key: CycleTimeSortKey;
  label: string;
  stage?: CycleTimeStageKey;
  numeric: boolean;
  highlighted?: boolean;
}

const DRILL_DOWN_COLUMNS: readonly DrillDownColumn[] = [
  { key: 'key', label: 'Task', numeric: false },
  { key: 'completedAt', label: 'Completed', numeric: false },
  { key: 'leadTime', label: 'Lead', numeric: true },
  { key: 'cycleTime', label: 'Cycle', numeric: true },
  { key: 'queueWait', label: 'Queue', stage: 'queueWait', numeric: true },
  { key: 'coding', label: 'Coding', stage: 'coding', numeric: true },
  { key: 'reviewWait', label: 'PP wait', stage: 'reviewWait', numeric: true },
  { key: 'testGate', label: 'Gate', stage: 'testGate', numeric: true, highlighted: true },
  { key: 'reviewOther', label: 'Review', stage: 'reviewOther', numeric: true },
  { key: 'integration', label: 'Integration', stage: 'integration', numeric: true, highlighted: true },
  { key: 'humanReview', label: 'Human', stage: 'humanReview', numeric: true },
  { key: 'codingRuns', label: 'Runs', numeric: true },
  { key: 'bounceRounds', label: 'Bounces', numeric: true },
  { key: 'backwardTransitions', label: 'Back', numeric: true },
  { key: 'outcome', label: 'Integration outcome', numeric: false },
];

const FLOW_COLUMNS: readonly DrillDownColumn[] = [
  { key: 'key', label: 'Task', numeric: false },
  { key: 'completedAt', label: 'Completed', numeric: false },
  { key: 'machineCycle', label: 'Machine', numeric: true },
  { key: 'active', label: 'Active', numeric: true },
  { key: 'resourceWait', label: 'Resource wait', numeric: true },
  { key: 'systemHold', label: 'Hold', numeric: true },
  { key: 'humanWait', label: 'Human (excluded)', numeric: true },
  { key: 'dependencyWait', label: 'Dependency (excluded)', numeric: true },
  { key: 'tokens', label: 'Tokens', numeric: true },
  { key: 'cost', label: 'Cost', numeric: true },
  { key: 'codingRuns', label: 'Runs', numeric: true },
  { key: 'outcome', label: 'Outcome', numeric: false },
];

/**
 * Project rail panel "Cycle time": per-stage duration aggregates for the
 * completed tasks of one project (count, median, p90, max), a stacked median
 * composition bar, the rollups and round counts, and a sortable per-task
 * drill-down. Read-only; the backend owns every number.
 *
 * Flat admin grammar (admin-design-guideline ADM-01..ADM-09): no panels inside
 * the panel, one type hierarchy, tables with hairlines only, semantic tokens.
 */
@Component({
  selector: 'app-project-cycle-time-panel',
  standalone: true,
  imports: [TooltipDirective, CycleTimeTransitionsComponent, CycleTimeTaskTransitionsComponent, FlowOverviewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-cycle-time-panel.component.html',
  styleUrl: './project-cycle-time-panel.component.scss',
})
export class ProjectCycleTimePanelComponent {
  private readonly api = inject(ProjectCycleTimeService);

  readonly projectName = input.required<string>();
  /** Bubbles a drill-down row click so the host can open the task. */
  readonly openTask = output<{ jobId: string; watchPath: string }>();

  readonly windows = CYCLE_TIME_WINDOWS;
  readonly laneStages = signal(false);
  readonly columns = computed(() => this.laneStages() ? DRILL_DOWN_COLUMNS : FLOW_COLUMNS);
  readonly window = signal<CycleTimeWindow>('7d');
  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);
  readonly data = signal<ProjectCycleTimeResponse | null>(null);
  readonly table = new CycleTimeTableState();

  /** Additive lane stages that occurred at least once, plus the highlighted ones even at zero. */
  readonly stageRows = computed<CycleTimeAggregate[]>(() =>
    (this.data()?.aggregates ?? []).filter(a => a.kind === 'stage' && (a.count > 0 || a.highlighted)));
  readonly rollupRows = computed<CycleTimeAggregate[]>(() =>
    (this.data()?.aggregates ?? []).filter(a => a.kind === 'rollup'));
  readonly countRows = computed<CycleTimeAggregate[]>(() =>
    (this.data()?.aggregates ?? []).filter(a => a.kind === 'count'));
  readonly segments = computed<CompositionSegment[]>(() =>
    compositionSegments(this.data()?.aggregates ?? []));
  readonly sortedTasks = computed<TaskCycleTimeRow[]>(() => {
    // Read the sort signals so the memo re-evaluates on header clicks.
    this.table.sortKey();
    this.table.direction();
    return this.table.sort(this.data()?.tasks ?? []);
  });
  readonly outcomesLine = computed<string>(() =>
    (this.data()?.integrationOutcomes ?? [])
      .map(o => `${o.outcome} ${o.count}`)
      .join(' · '));
  readonly hasRows = computed<boolean>(() => (this.data()?.coverage.tasksInWindow ?? 0) > 0);
  /** Task keys whose transition history is expanded in the drill-down. */
  readonly expandedKeys = signal<readonly string[]>([]);
  /** Drill-down column count including the leading disclosure column. */
  readonly columnCount = computed(() => this.columns().length + 1);

  constructor() {
    effect(() => {
      const name = this.projectName();
      const window = this.window();
      if (name) this.refresh(name, window);
    });
  }

  selectWindow(window: CycleTimeWindow): void {
    if (this.window() !== window) this.window.set(window);
  }

  reload(): void {
    const name = this.projectName();
    if (name) this.refresh(name, this.window());
  }

  sort(key: CycleTimeSortKey): void {
    this.table.selectSort(key);
  }

  ariaSort(key: CycleTimeSortKey): 'ascending' | 'descending' | 'none' {
    if (this.table.sortKey() !== key) return 'none';
    return this.table.direction() === 'asc' ? 'ascending' : 'descending';
  }

  sortIndicator(key: CycleTimeSortKey): string {
    if (this.table.sortKey() !== key) return '';
    return this.table.direction() === 'asc' ? '↑' : '↓';
  }

  open(row: TaskCycleTimeRow): void {
    if (!row.watchPath) return;
    this.openTask.emit({ jobId: row.taskId, watchPath: row.watchPath });
  }

  isExpanded(row: TaskCycleTimeRow): boolean {
    return this.expandedKeys().includes(row.taskKey);
  }

  toggleExpanded(row: TaskCycleTimeRow): void {
    this.expandedKeys.update(keys => keys.includes(row.taskKey)
      ? keys.filter(k => k !== row.taskKey)
      : [...keys, row.taskKey]);
  }

  windowLabel(window: CycleTimeWindow): string {
    return windowLabel(window);
  }

  /** Duration or count cell text; an en dash marks "did not occur" / "unknown". */
  value(aggregate: CycleTimeAggregate, field: 'p50' | 'p90' | 'max' | 'mean'): string {
    const raw = aggregate[field];
    const text = aggregate.unit === 'count' ? formatCount(raw) : formatDuration(raw);
    return text ?? '–';
  }

  duration(seconds: number | null | undefined): string {
    return formatDuration(seconds) ?? '–';
  }

  cell(row: TaskCycleTimeRow, column: DrillDownColumn): string {
    switch (column.key) {
      case 'key': return row.taskKey;
      case 'completedAt': return formatTimestamp(row.completedAt);
      case 'leadTime': return this.duration(row.leadTimeSeconds);
      case 'cycleTime': return this.duration(row.cycleTimeSeconds);
      case 'codingRuns': return String(row.codingRuns);
      case 'bounceRounds': return String(row.bounceRounds);
      case 'backwardTransitions': return String(row.backwardTransitions ?? 0);
      case 'outcome': return row.integrationOutcome ?? '–';
      case 'machineCycle': return this.duration(row.machineCycleSeconds);
      case 'active': return this.duration(row.activeSeconds);
      case 'resourceWait': return `${this.duration(row.resourceWaitSeconds)}${row.largestResource ? ` (${row.largestResource})` : ''}`;
      case 'systemHold': return this.duration(row.systemHoldSeconds);
      case 'humanWait': return this.duration(row.humanWaitSeconds);
      case 'dependencyWait': return this.duration(row.dependencyWaitSeconds);
      case 'tokens': return formatTokens(row.episodeTokens);
      case 'cost': return formatUsd(row.episodeCostUsd);
      default: return column.stage ? this.duration(row.stages?.[column.stage]) : '';
    }
  }

  segmentTooltip(segment: CompositionSegment): string {
    const p90 = formatDuration(segment.p90);
    return `${segment.label}: median ${formatDuration(segment.seconds)}`
      + (p90 ? `, p90 ${p90}` : '')
      + ` (${segment.count} ${segment.count === 1 ? 'task' : 'tasks'})`;
  }

  rowTooltip(row: TaskCycleTimeRow): string {
    const parts = [row.title];
    if (row.integrationStage) parts.push(`integration at ${row.integrationStage}`);
    if (row.reviewRounds) parts.push(`${row.reviewRounds} review ${row.reviewRounds === 1 ? 'round' : 'rounds'}`);
    if (row.dataGaps.length) parts.push(`gaps: ${row.dataGaps.join(', ')}`);
    return parts.join(' · ');
  }

  /** Sequence of the latest request; a response from an older request (window switched meanwhile) is dropped. */
  private requestSeq = 0;

  private refresh(name: string, window: CycleTimeWindow): void {
    const seq = ++this.requestSeq;
    this.loading.set(true);
    this.error.set(null);
    this.api.load(name, window).subscribe({
      next: response => {
        if (seq !== this.requestSeq) return;
        this.data.set(response);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        if (seq !== this.requestSeq) return;
        this.data.set(null);
        this.error.set(err?.error?.error ?? err?.message ?? 'Could not load cycle time.');
        this.loading.set(false);
      },
    });
  }
}
