import { ChangeDetectionStrategy, Component, OnDestroy, effect, inject, input, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { laneShortName } from '../../../../models/lane-presentation';
import type { PipelineHealthSnapshot, PipelineLaneDrainHealth } from '../../../task-pipeline';

@Component({
  selector: 'app-pipeline-health-block',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-health-block.html',
  styleUrl: './pipeline-health-block.scss',
})
export class PipelineHealthBlockComponent implements OnDestroy {
  readonly projectName = input.required<string>();
  readonly health = signal<PipelineHealthSnapshot | null>(null);
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

  /** Lane display name; wording lives in `models/lane-presentation.ts`. */
  laneLabel(lane: string): string {
    return laneShortName(lane);
  }

  drainRate(lane: PipelineLaneDrainHealth): string {
    return `${lane.completedPerHour.toLocaleString(undefined, { maximumFractionDigits: 1 })}/h`;
  }

  private refresh(): void {
    const project = this.projectName();
    if (!project) return;
    this.tasks.getProjectPipelineHealth(project).subscribe({
      next: snapshot => this.health.set(snapshot),
      error: () => { /* additive signal: keep the last known snapshot */ },
    });
  }
}
