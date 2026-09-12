import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { CopyableTaskKeyComponent } from '../../../../components/copyable-task-key/copyable-task-key.component';
import {
  TaskReferenceMicrocardComponent,
  type TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import { StudioIconComponent, type StudioIconName } from '../../../../components/studio-icon/studio-icon.component';
import type { ArticlePattern, WorkbenchOverviewItem } from '../../../../models/project-docs.model';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import { WorkbenchReviewTagComponent } from '../workbench-review-tag/workbench-review-tag.component';

type OverviewCardMode = 'decision' | 'current' | 'invalid' | 'history';

@Component({
  selector: 'app-workbench-overview-card',
  standalone: true,
  imports: [
    CopyableTaskKeyComponent,
    StudioIconComponent,
    TaskReferenceMicrocardComponent,
    WorkbenchReviewTagComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-overview-card.component.html',
  styleUrl: './workbench-overview-card.component.scss',
  host: {
    'class': 'workbench-overview__row',
    'role': 'article',
    '[class.workbench-overview__row--expanded]': 'reviewExpanded()',
    '[class.workbench-overview__row--invalid]': "mode() === 'invalid'",
    '[class.workbench-overview__row--history]': "mode() === 'history'",
    '[attr.data-testid]': 'itemTestId()',
  },
})
export class WorkbenchOverviewCardComponent {
  readonly item = input.required<WorkbenchOverviewItem>();
  readonly mode = input.required<OverviewCardMode>();
  readonly references = input<readonly TaskReferenceStatus[]>([]);
  readonly referencesLoading = input(false);
  readonly excerptExpanded = input(false);
  readonly excerptCanExpand = input(false);
  readonly reviewExpanded = input(false);
  readonly openRequested = output<void>();
  readonly reviewToggleRequested = output<void>();
  readonly excerptToggleRequested = output<void>();

  private readonly projects = inject(ProjectLookupService);
  readonly project = computed(() => this.projects.getProjectDisplay(this.item().projectName));
  readonly itemTestId = computed(() => `workbench-overview-item-${this.item().projectName}-${this.item().workbench.id}`);
  readonly keyTestId = computed(() => `workbench-overview-key-${this.item().projectName}-${this.item().workbench.id}`);
  readonly excerptId = computed(() => `workbench-overview-excerpt-${this.itemKey().replace(/[^a-zA-Z0-9_-]+/g, '-')}`);
  readonly excerptToggleTestId = computed(() => `workbench-overview-excerpt-toggle-${this.item().projectName}-${this.item().workbench.id}`);
  readonly actionsTestId = computed(() => `workbench-overview-actions-${this.item().projectName}-${this.item().workbench.id}`);

  statusLabel(): string {
    const workbench = this.item().workbench;
    if (!workbench.valid) return 'Needs attention';
    if (workbench.status === 'decision-pending') return 'Decision pending';
    if (workbench.status === 'active') return workbench.phase ?? 'Active';
    if (workbench.status === 'decided') return 'Accepted / In progress';
    if (workbench.status === 'archived') return 'Discarded';
    if (workbench.status === 'documented') return 'Documented';
    return workbench.status;
  }

  updatedLabel(): string {
    return new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(this.item().workbench.updatedAtUtc));
  }

  keyLabel(): string {
    return this.item().workbench.key ?? this.item().workbench.id;
  }

  openDecisionCount(): number {
    const workbench = this.item().workbench;
    return workbench.openDecisionCount ?? (workbench.status === 'decision-pending' ? 1 : 0);
  }

  documentPattern(): ArticlePattern {
    return this.item().workbench.pattern === 'ui' ? 'ui' : 'concept';
  }

  patternIcon(): StudioIconName {
    return this.documentPattern() === 'ui' ? 'grid' : 'book';
  }

  private itemKey(): string {
    return `${this.item().projectName}:${this.item().workbench.id}`;
  }
}
