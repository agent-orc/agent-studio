import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { CopyableTaskKeyComponent } from '../../../../components/copyable-task-key/copyable-task-key.component';
import {
  TaskReferenceMicrocardComponent,
  type TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import { StudioIconComponent, type StudioIconName } from '../../../../components/studio-icon/studio-icon.component';
import type { ArticlePattern, WorkbenchOverviewItem } from '../../../../models/project-docs.model';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import { WorkbenchViewerComponent } from '../workbench-viewer/workbench-viewer.component';

export type WorkbenchOverviewListItemKind = 'decision' | 'current' | 'invalid' | 'history';
const LONG_EXCERPT_CHARACTER_THRESHOLD = 560;

@Component({
  selector: 'app-workbench-overview-list-item',
  standalone: true,
  imports: [
    CopyableTaskKeyComponent,
    StudioIconComponent,
    TaskReferenceMicrocardComponent,
    WorkbenchViewerComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-overview-list-item.component.html',
  styleUrl: './workbench-overview-list-item.component.scss',
})
export class WorkbenchOverviewListItemComponent {
  readonly item = input.required<WorkbenchOverviewItem>();
  readonly kind = input.required<WorkbenchOverviewListItemKind>();
  readonly statusLabel = input.required<string>();
  readonly referenceStatuses = input<readonly TaskReferenceStatus[]>([]);
  readonly referenceStatusesLoading = input(false);
  readonly inlineDecisionExpanded = input(false);
  readonly openWorkbench = output<WorkbenchOverviewItem>();
  readonly toggleInlineDecision = output<WorkbenchOverviewItem>();

  private readonly projects = inject(ProjectLookupService);
  readonly excerptExpanded = signal(false);
  readonly project = computed(() => this.projects.getProjectDisplay(this.item().projectName));
  readonly excerptText = computed(() => this.item().workbench.error || this.item().workbench.summary);
  readonly shouldClampExcerpt = computed(() => this.excerptText().length > LONG_EXCERPT_CHARACTER_THRESHOLD);
  readonly excerptId = computed(() =>
    `workbench-overview-excerpt-${this.itemKey().replace(/[^a-zA-Z0-9_-]+/g, '-')}`,
  );

  open(): void {
    if (this.item().workbench.valid) this.openWorkbench.emit(this.item());
  }

  keyLabel(): string {
    return this.item().workbench.key ?? this.item().workbench.id;
  }

  openDecisionCount(): number {
    return this.item().workbench.openDecisionCount
      ?? (this.item().workbench.status === 'decision-pending' ? 1 : 0);
  }

  documentPattern(): ArticlePattern {
    return this.item().workbench.pattern === 'ui' ? 'ui' : 'concept';
  }

  patternIcon(): StudioIconName {
    return this.documentPattern() === 'ui' ? 'grid' : 'book';
  }

  updatedLabel(): string {
    return new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(this.item().workbench.updatedAtUtc));
  }

  private itemKey(): string {
    return `${this.item().projectName}:${this.item().workbench.id}`;
  }
}
