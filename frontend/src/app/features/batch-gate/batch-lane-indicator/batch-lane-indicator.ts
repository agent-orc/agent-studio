import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import type { TaskInfo } from '../../../models/task.model';
import { BatchGateService } from '../../../services/batch-gate.service';
import { StatusIndicatorComponent } from '../../../components/status-indicator/status-indicator.component';
import { batchAlert, batchShortId, type BatchAlert } from '../../../models/batch-gate.model';
import { BatchPanelComponent } from '../batch-panel/batch-panel';

@Component({
  selector: 'app-batch-lane-indicator',
  standalone: true,
  imports: [DatePipe, BatchPanelComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-lane-indicator.html',
  styleUrl: './batch-lane-indicator.scss',
})
export class BatchLaneIndicatorComponent {
  readonly laneState = input.required<string>();
  readonly projectScope = input<string | null>(null);
  readonly jobs = input<readonly TaskInfo[]>([]);
  readonly panelProject = signal<string | null>(null);
  readonly closePanel = () => this.panelProject.set(null);
  private readonly gate = inject(BatchGateService);
  readonly projects = computed(() => {
    if (!['4-auto-review', '5-human-review', '4-review'].includes(this.laneState())) return [];
    const scoped = this.projectScope();
    return scoped ? [scoped] : [...new Set(this.jobs().map(job => job.projectName))].sort();
  }, { equal: (a, b) => a.length === b.length && a.every((project, index) => project === b[index]) });

  constructor() {
    effect(onCleanup => {
      const stops = this.projects().map(project => this.gate.watch(project));
      onCleanup(() => stops.forEach(stop => stop()));
    });
  }
  stateFor(project: string) { return this.gate.state(project)(); }
  /** AGT-3577: a red or isolating batch of the project, named in the indicator. */
  alertFor(project: string): BatchAlert | null { return batchAlert(this.stateFor(project)); }
  alertLabel(alert: BatchAlert): string {
    return `Batch ${batchShortId(alert.batchId)} ${alert.state === 'red' ? 'red' : 'isolating'}`;
  }
  alertTitle(alert: BatchAlert): string {
    const culprits = alert.culprits.length ? `; proven red: ${alert.culprits.join(', ')}` : '';
    return `Batch ${alert.batchId} ${alert.state === 'red' ? 'is red' : 'is isolating the red member'}${culprits}`;
  }
  runningFor(project: string): number {
    return this.stateFor(project)?.batches.filter(batch => batch.runs.some(run => !!run.startedAtUtc && !run.finishedAtUtc)).length ?? 0;
  }
}
