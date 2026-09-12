import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { CopyableTaskKeyComponent } from '../../../../components/copyable-task-key/copyable-task-key.component';
import {
  TaskReferenceMicrocardComponent,
  type TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import { StudioIconComponent, type StudioIconName } from '../../../../components/studio-icon/studio-icon.component';
import type { ArticlePattern, WorkbenchOverviewItem } from '../../../../models/project-docs.model';
import type { ProjectDisplay } from '../../../../services/project-lookup.service';
import { WorkbenchReviewTagComponent } from '../workbench-review-tag/workbench-review-tag.component';
import { WorkbenchViewerComponent } from '../workbench-viewer/workbench-viewer.component';

export type WorkbenchOverviewCardVariant = 'decision' | 'current' | 'invalid' | 'history';

@Component({
  selector: 'app-workbench-overview-card',
  standalone: true,
  imports: [
    CopyableTaskKeyComponent,
    StudioIconComponent,
    TaskReferenceMicrocardComponent,
    WorkbenchReviewTagComponent,
    WorkbenchViewerComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-overview-card.component.html',
  styleUrl: './workbench-overview-card.component.scss',
})
export class WorkbenchOverviewCardComponent {
  readonly item = input.required<WorkbenchOverviewItem>();
  readonly project = input.required<ProjectDisplay>();
  readonly statusLabel = input.required<string>();
  readonly variant = input.required<WorkbenchOverviewCardVariant>();
  readonly referenceStatuses = input<readonly TaskReferenceStatus[]>([]);
  readonly referenceStatusesLoading = input(false);
  readonly inlineDecisionExpanded = input(false);
  readonly openWorkbench = output<WorkbenchOverviewItem>();
  readonly inlineDecisionToggle = output<WorkbenchOverviewItem>();

  readonly excerptExpanded = signal(false);
  readonly excerpt = computed(() => this.item().workbench.error || this.item().workbench.summary);
  readonly excerptCanExpand = computed(() => {
    const summary = this.excerpt();
    return summary.split(/\r?\n/).length > 6 || summary.length > 520;
  });
  readonly itemDomId = computed(() =>
    `${this.item().projectName}:${this.item().workbench.id}`.replace(/[^a-zA-Z0-9_-]+/g, '-'),
  );
  readonly keyLabel = computed(() => this.item().workbench.key ?? this.item().workbench.id);
  readonly documentPattern = computed<ArticlePattern>(() =>
    this.item().workbench.pattern === 'ui' ? 'ui' : 'concept',
  );
  readonly patternIcon = computed<StudioIconName>(() =>
    this.documentPattern() === 'ui' ? 'grid' : 'book',
  );
  readonly showsPattern = computed(() => this.variant() === 'decision' || this.variant() === 'current');
  readonly showsReferences = computed(() => this.showsPattern() && this.referenceStatuses().length > 0);
  readonly itemTestId = computed(() => this.variant() === 'history'
    ? null
    : `workbench-overview-item-${this.item().projectName}-${this.item().workbench.id}`);

  toggleExcerpt(): void {
    this.excerptExpanded.update(expanded => !expanded);
  }

  updatedLabel(value: string): string {
    return new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(value));
  }

  openDecisionCount(): number {
    const workbench = this.item().workbench;
    return workbench.openDecisionCount ?? (workbench.status === 'decision-pending' ? 1 : 0);
  }
}
