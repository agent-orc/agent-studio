import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import type { TaskInfo } from '../../../models/task.model';
import { BatchGateService } from '../../../services/batch-gate.service';
import { BatchPanelComponent } from '../batch-panel/batch-panel';

@Component({
  selector: 'app-batch-lane-indicator',
  standalone: true,
  imports: [DatePipe, BatchPanelComponent],
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
  runningFor(project: string): number {
    return this.stateFor(project)?.batches.filter(batch => batch.runs.some(run => !!run.startedAtUtc && !run.finishedAtUtc)).length ?? 0;
  }
}
