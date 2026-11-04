import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { LoadingSurfaceComponent, PendingButtonDirective } from '../../../../components/async-feedback';
import { ProjectGitService } from '../../../../services/project-git.service';
import { formatCompactDateTime } from '../../../../services/format.util';
import type { IntegrationQueueState, ProjectIntegrationView } from '../../../git';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** Integration queue outcome on the shared indicator (AGT-3403); unknown states stay neutral. */
export const INTEGRATION_STATUS_TONE: Readonly<Record<IntegrationQueueState, StatusTone>> = {
  merged: 'success',
  waiting: 'info',
  conflict: 'danger',
  skipped: 'neutral',
  'legacy-unverifiable': 'neutral',
  superseded: 'neutral',
};

export function integrationStatusTone(status: string): StatusTone {
  return Object.hasOwn(INTEGRATION_STATUS_TONE, status) ? INTEGRATION_STATUS_TONE[status as IntegrationQueueState] : 'neutral';
}

@Component({
  selector: 'app-project-integration-panel',
  standalone: true,
  imports: [LoadingSurfaceComponent, PendingButtonDirective, StatusIndicatorComponent],
  templateUrl: './project-integration-panel.component.html',
  styleUrl: './project-integration-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectIntegrationPanelComponent {
  private readonly git = inject(ProjectGitService);

  readonly projectName = input.required<string>();
  readonly statusTone = integrationStatusTone;
  readonly view = signal<ProjectIntegrationView | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly selectedStatus = signal<'all' | IntegrationQueueState>('all');

  readonly counts = computed(() => {
    const queue = this.view()?.queue ?? [];
    return {
      merged: queue.filter(item => item.status === 'merged').length,
      waiting: queue.filter(item => item.status === 'waiting').length,
      conflict: queue.filter(item => item.status === 'conflict').length,
      skipped: queue.filter(item => item.status === 'skipped').length,
      legacyUnverifiable: queue.filter(item => item.status === 'legacy-unverifiable').length,
      superseded: queue.filter(item => item.status === 'superseded').length,
      all: queue.length,
    };
  });

  readonly visibleQueue = computed(() => {
    const queue = this.view()?.queue ?? [];
    const selected = this.selectedStatus();
    return selected === 'all' ? queue : queue.filter(item => item.status === selected);
  });

  constructor() {
    effect(() => this.load(this.projectName()));
  }

  refresh(): void {
    this.load(this.projectName());
  }

  when(value: string | null | undefined): string {
    return value ? formatCompactDateTime(value) : '';
  }

  statusLabel(status: IntegrationQueueState): string {
    if (status === 'legacy-unverifiable') return 'Legacy unverifiable';
    return status[0].toUpperCase() + status.slice(1);
  }

  selectStatus(status: 'all' | IntegrationQueueState): void {
    this.selectedStatus.set(status);
  }

  private load(project: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.git.getIntegration(project).subscribe({
      next: view => {
        this.view.set(view);
        this.error.set(view.error);
        this.loading.set(false);
      },
      error: err => {
        this.view.set(null);
        this.error.set(err?.error?.error ?? err?.message ?? 'Could not load integration state.');
        this.loading.set(false);
      },
    });
  }
}
