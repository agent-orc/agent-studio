import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import type { HttpErrorResponse } from '@angular/common/http';
import { ProjectCycleTimeService } from '../../services/project-cycle-time.service';
import type { FlowSegment, TaskLaneTransition } from '../../models/project-cycle-time.model';
import { formatDuration, formatTimestamp, formatTokens, formatUsd, laneLabel } from '../project-cycle-time-panel/cycle-time-format';

/**
 * Inline transition history of one task (drill-down row of the Cycle Time
 * panel). Fetches the per-task endpoint on mount so the list payload stays
 * bounded; every lane change is one row with dwell, actor, cause, and detail.
 */
@Component({
  selector: 'app-cycle-time-task-transitions',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './cycle-time-task-transitions.component.html',
  styleUrl: './cycle-time-task-transitions.component.scss',
})
export class CycleTimeTaskTransitionsComponent {
  private readonly api = inject(ProjectCycleTimeService);

  readonly projectName = input.required<string>();
  readonly taskKey = input.required<string>();

  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);
  readonly transitions = signal<TaskLaneTransition[]>([]);
  readonly segments = signal<FlowSegment[]>([]);

  constructor() {
    effect(() => {
      const project = this.projectName();
      const key = this.taskKey();
      if (project && key) this.load(project, key);
    });
  }

  laneLabel(lane: string): string {
    return laneLabel(lane);
  }

  time(iso: string): string {
    return formatTimestamp(iso);
  }

  duration(seconds: number | null | undefined): string {
    return formatDuration(seconds) ?? '–';
  }

  causeLabel(cause: string): string {
    return cause.replace(/-/g, ' ');
  }

  tokens(value: number): string { return formatTokens(value); }
  usd(value: number): string { return formatUsd(value); }

  /** Sequence of the latest request; a response from an older request (inputs changed meanwhile) is dropped. */
  private requestSeq = 0;

  private load(project: string, key: string): void {
    const seq = ++this.requestSeq;
    this.loading.set(true);
    this.error.set(null);
    this.api.loadTask(project, key).subscribe({
      next: response => {
        if (seq !== this.requestSeq) return;
        this.transitions.set(response.task.transitions ?? []);
        this.segments.set(response.task.segments ?? []);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        if (seq !== this.requestSeq) return;
        this.transitions.set([]);
        this.segments.set([]);
        this.error.set(err?.error?.error ?? err?.message ?? 'Could not load transitions.');
        this.loading.set(false);
      },
    });
  }
}
