import { ChangeDetectionStrategy, Component, OnDestroy, computed, effect, inject, input, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { laneName } from '../../../../models/lane-presentation';
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
  readonly sweepBusy = signal<string | null>(null);
  readonly sweepError = signal<string | null>(null);
  readonly waitingCards = computed(() => this.health()?.operatorSweeps?.cards.filter(card => card.waitingForPerson) ?? []);
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

  sweepLabel(sweep: string): string {
    return ({ 'auto-fix': 'Product fix', 'gate-triage': 'Gate triage', salvage: 'Salvage' } as Record<string, string>)[sweep] ?? sweep;
  }

  lastRun(value?: string | null): string {
    return value ? new Date(value).toLocaleString() : 'Waiting for first tick';
  }

  toggleSweep(sweep: string, paused: boolean): void {
    this.sweepBusy.set(sweep);
    this.sweepError.set(null);
    this.tasks.setOperatorSweepPaused(this.projectName(), sweep, paused).subscribe({
      next: operatorSweeps => {
        this.health.update(health => health ? { ...health, operatorSweeps } : health);
        this.sweepBusy.set(null);
      },
      error: () => {
        this.sweepError.set(`Could not update ${this.sweepLabel(sweep)}.`);
        this.sweepBusy.set(null);
      },
    });
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
