import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import {
  TaskReferenceMicrocardComponent,
  type TaskReferenceStatus,
} from '../../../../../components/task-reference-microcard/task-reference-microcard';
import { StudioIconComponent, type StudioIconName } from '../../../../../components/studio-icon/studio-icon.component';
import type { ArticlePattern, WorkbenchOverviewItem } from '../../../../../models/project-docs.model';
import { ProjectLookupService } from '../../../../../services/project-lookup.service';
import { WorkbenchViewerComponent } from '../../workbench-viewer/workbench-viewer.component';

const SUMMARY_DISCLOSURE_FALLBACK_CHARACTERS = 800;

export type WorkbenchOverviewItemVariant = 'decision' | 'current' | 'invalid' | 'history';

@Component({
  selector: 'app-workbench-overview-item',
  standalone: true,
  imports: [StudioIconComponent, TaskReferenceMicrocardComponent, WorkbenchViewerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-overview-item.component.html',
  styleUrl: './workbench-overview-item.component.scss',
  host: {
    class: 'workbench-overview__row',
    role: 'article',
    '[class.workbench-overview__row--expanded]': 'inlineDecisionExpanded()',
    '[class.workbench-overview__row--invalid]': "variant() === 'invalid'",
    '[class.workbench-overview__row--history]': "variant() === 'history'",
    '[attr.data-testid]': "'workbench-overview-item-' + item().projectName + '-' + item().workbench.id",
  },
})
export class WorkbenchOverviewItemComponent {
  readonly item = input.required<WorkbenchOverviewItem>();
  readonly variant = input.required<WorkbenchOverviewItemVariant>();
  readonly referenceStatuses = input<readonly TaskReferenceStatus[]>([]);
  readonly referenceStatusesLoading = input(false);
  readonly inlineDecisionExpanded = input(false);
  readonly openWorkbench = output<void>();
  readonly toggleInlineDecision = output<void>();

  private readonly projects = inject(ProjectLookupService);
  private readonly excerpt = viewChild<ElementRef<HTMLElement>>('excerpt');
  private readonly measuredOverflow = signal<boolean | null>(null);
  readonly excerptExpanded = signal(false);
  readonly project = computed(() => this.projects.getProjectDisplay(this.item().projectName));
  readonly excerptNeedsToggle = computed(() =>
    this.measuredOverflow() ?? this.excerptText().length >= SUMMARY_DISCLOSURE_FALLBACK_CHARACTERS);

  constructor() {
    effect(() => {
      this.item();
      untracked(() => {
        this.excerptExpanded.set(false);
        this.measuredOverflow.set(null);
      });
    });
    afterRenderEffect(onCleanup => {
      const element = this.excerpt()?.nativeElement;
      if (!element || typeof ResizeObserver === 'undefined') return;
      const measure = () => {
        if (!this.excerptExpanded() && element.clientHeight > 0) {
          this.measuredOverflow.set(element.scrollHeight > element.clientHeight + 1);
        }
      };
      const observer = new ResizeObserver(measure);
      observer.observe(element);
      measure();
      onCleanup(() => observer.disconnect());
    });
  }

  toggleExcerpt(): void {
    this.excerptExpanded.update(expanded => !expanded);
  }

  excerptText(): string {
    const workbench = this.item().workbench;
    return workbench.error || workbench.summary;
  }

  excerptId(): string {
    const item = this.item();
    return `workbench-overview-summary-${item.projectName}-${item.workbench.id}`
      .replace(/[^a-zA-Z0-9_-]+/g, '-');
  }

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
}
