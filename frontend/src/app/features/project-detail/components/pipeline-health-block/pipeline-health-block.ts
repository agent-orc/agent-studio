import { ChangeDetectionStrategy, Component, OnDestroy, computed, effect, inject, input, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { laneName } from '../../../../models/lane-presentation';
import type { PipelineHealthAlert, PipelineHealthSnapshot, PipelineLaneDrainHealth } from '../../../task-pipeline';
import { OperatorSweepsBlockComponent } from '../operator-sweeps-block/operator-sweeps-block';
import { GateEconomicsBlockComponent } from '../gate-economics-block/gate-economics-block';
import type { GateEconomicsResponse } from '../../../task-pipeline/models/gate-economics.model';

@Component({
  selector: 'app-pipeline-health-block',
  standalone: true,
  imports: [OperatorSweepsBlockComponent, GateEconomicsBlockComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-health-block.html',
  styleUrl: './pipeline-health-block.scss',
})
export class PipelineHealthBlockComponent implements OnDestroy {
  readonly projectName = input.required<string>();
  readonly health = signal<PipelineHealthSnapshot | null>(null);
  private readonly economicsResponse = signal<GateEconomicsResponse | null>(null);
  readonly economics = computed(() => this.economicsResponse()?.rows
    .find(row => row.project === this.projectName()) ?? null);
  readonly economicsWindow = signal('7d');
  /** Workspace repositories whose evidence flush keeps failing (AGT-3000). */
  readonly evidenceFlushStalls = computed<PipelineHealthAlert[]>(() =>
    (this.health()?.alerts ?? []).filter(alert => alert.kind === 'evidence-flush-stalled'));
  private readonly tasks = inject(TaskService);
  private readonly poll = setInterval(() => this.refresh(), 60_000);

  constructor() {
    effect(() => {
      if (this.projectName()) this.refresh();
    });
  }

  ngOnDestroy(): void {
    clearInterval(this.poll);
  }

  /** Lane display name from the one catalogue (AGT-2715). */
  laneLabel(lane: string): string {
    return laneName(lane);
  }

  drainRate(lane: PipelineLaneDrainHealth): string {
    return `${lane.completedPerHour.toLocaleString('en-US', { maximumFractionDigits: 1 })}/h`;
  }

  private refresh(): void {
    const project = this.projectName();
    if (!project) return;
    this.tasks.getProjectPipelineHealth(project).subscribe({
      next: snapshot => this.health.set(snapshot),
      error: () => { /* additive signal: keep the last known snapshot */ },
    });
  }

  /** The project row comes from the gate-economics block's one fetch. */
  onEconomics(response: GateEconomicsResponse): void {
    this.economicsWindow.set(response.window);
    this.economicsResponse.set(response);
  }

  metric(value: number | null, unit = ''): string {
    return value == null ? 'Unknown' : `${value.toLocaleString('en-US', { maximumFractionDigits: 1 })}${unit}`;
  }

  minutes(ms: number | null): string {
    return this.metric(ms == null ? null : ms / 60_000, ' min');
  }
}
