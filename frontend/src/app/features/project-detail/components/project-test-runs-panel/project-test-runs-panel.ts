import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import type { ProjectTestRunItem, ProjectTestRunsResponse } from '../../../../models/project-overview.model';
import { TaskService } from '../../../../services/task.service';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/**
 * Test run result (or state while it has no result) on the shared indicator
 * (AGT-3403). Completed runs are history, so a failure reads as warning and a
 * cancellation as neutral, never as an acute danger (hard rule R4).
 */
const TEST_RUN_RESULT_TONES: Readonly<Record<string, StatusTone>> = {
  passed: 'success',
  failed: 'warning',
  canceled: 'neutral',
  running: 'running',
  planned: 'neutral',
  completed: 'neutral',
};

export function testRunResultTone(result: string | null | undefined): StatusTone {
  return result && Object.hasOwn(TEST_RUN_RESULT_TONES, result) ? TEST_RUN_RESULT_TONES[result] : 'neutral';
}

@Component({
  selector: 'app-project-test-runs-panel',
  standalone: true,
  imports: [DatePipe, DecimalPipe, PendingButtonDirective, StatusIndicatorComponent],
  templateUrl: './project-test-runs-panel.html',
  styleUrl: './project-test-runs-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectTestRunsPanelComponent {
  private readonly tasks = inject(TaskService);

  readonly projectName = input.required<string>();
  readonly resultTone = testRunResultTone;
  readonly response = signal<ProjectTestRunsResponse | null>(null);
  readonly loading = signal(true);
  readonly failed = signal(false);
  readonly planned = computed(() => this.byState('planned'));
  readonly running = computed(() => this.byState('running'));
  readonly completed = computed(() => this.byState('completed'));

  constructor() {
    effect(() => this.load(this.projectName()));
  }

  refresh(): void {
    this.load(this.projectName());
  }

  shortSha(sha: string): string {
    return sha.slice(0, 8);
  }

  private byState(state: ProjectTestRunItem['run']['state']): ProjectTestRunItem[] {
    return this.response()?.runs.filter(item => item.run.state === state) ?? [];
  }

  private load(project: string): void {
    this.loading.set(true);
    this.failed.set(false);
    this.tasks.getProjectTestRuns(project).subscribe({
      next: response => {
        this.response.set(response);
        this.loading.set(false);
      },
      error: () => {
        this.response.set(null);
        this.failed.set(true);
        this.loading.set(false);
      },
    });
  }
}
