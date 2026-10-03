import { ChangeDetectionStrategy, Component, computed, effect, inject, untracked } from '@angular/core';
import type { ResourceName } from '../../../../models/task-core.model';
import { LayoutPanesService } from '../../services/layout-panes.service';
import { TaskSelectionService } from '../../state/task-selection.service';
import { resourceReasonLabel } from '../../state/task-resource-states';

const LABELS: Record<ResourceName, { title: string; retry: string }> = {
  documents: { title: 'Documents unavailable', retry: 'Retry documents' },
  git: { title: 'Git information unavailable', retry: 'Retry Git' },
  usage: { title: 'Usage information unavailable', retry: 'Retry usage' },
  review: { title: 'Review information unavailable', retry: 'Retry review' },
  history: { title: 'History unavailable', retry: 'Retry history' },
};
const ORDER: readonly ResourceName[] = ['documents', 'git', 'usage', 'review', 'history'];

/**
 * Independent enrichment state of the selected task inside the rich detail
 * view: one announced line with its own retry per failed, stale or
 * unavailable resource, while the rest of the task stays usable. It also owns
 * the single trigger of the versioned Git resource, which waits for the Git
 * pane (the detail's own `LayoutPanesService`) however that pane opened.
 */
@Component({
  selector: 'app-task-resource-status',
  standalone: true,
  templateUrl: './task-resource-status.component.html',
  styleUrl: './task-resource-status.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskResourceStatusComponent {
  private readonly selection = inject(TaskSelectionService);
  private readonly layout = inject(LayoutPanesService);

  readonly problems = computed(() => {
    const states = this.selection.resourceStates();
    return ORDER.filter(name => ['warming', 'unavailable', 'error', 'stale'].includes(states[name].phase))
      .map(name => ({ name, ...LABELS[name],
        title: states[name].phase === 'warming' ? `${LABELS[name].title.replace(' unavailable', '')} pending` : LABELS[name].title,
        reason: resourceReasonLabel(states[name].reason) }));
  });

  constructor() {
    // Re-requests after a new task or core generation reset Git to idle.
    effect(() => {
      if (!this.layout.panesVisible().git || this.selection.resourceStates().git.phase !== 'idle') return;
      untracked(() => this.selection.loadResource('git'));
    });
  }

  retry(name: ResourceName): void {
    this.selection.retryResource(name);
  }
}
