import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import { CountBadgeComponent } from '../../../../components/count-badge/count-badge.component';
import {
  TaskReferenceMicrocardComponent,
  type TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import { StudioIconComponent, type StudioIconName } from '../../../../components/studio-icon/studio-icon.component';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import { TaskService } from '../../../../services/task.service';
import {
  DossierSectionStateService,
  type DossierSectionId,
} from '../../../../services/dossier-section-state.service';
import { WorkbenchOverviewControlsComponent } from '../workbench-overview-controls/workbench-overview-controls.component';
import { WorkbenchViewerComponent } from '../workbench-viewer/workbench-viewer.component';
import { WorkbenchOverviewViewStateService } from './workbench-overview-view-state.service';
import type {
  ArticlePattern,
  WorkbenchOverview,
  WorkbenchOverviewItem,
} from '../../../../models/project-docs.model';
@Component({
  selector: 'app-workbench-overview',
  standalone: true,
  imports: [
    LoadingSurfaceComponent,
    CountBadgeComponent,
    StudioIconComponent,
    TaskReferenceMicrocardComponent,
    WorkbenchOverviewControlsComponent,
    WorkbenchViewerComponent,
  ],
  providers: [WorkbenchOverviewViewStateService],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-overview.component.html',
  styleUrl: './workbench-overview.component.scss',
})
export class WorkbenchOverviewComponent {
  readonly projectName = input<string | null>(null);
  readonly projectId = input<string | null>(null);
  readonly openWorkbench = output<WorkbenchOverviewItem>();

  private readonly docs = inject(ProjectDocsService);
  private readonly hub = inject(JobsHubClient);
  private readonly projects = inject(ProjectLookupService);
  private readonly tasks = inject(TaskService);
  private readonly sectionState = inject(DossierSectionStateService);
  private readonly destroyRef = inject(DestroyRef);
  readonly viewState = inject(WorkbenchOverviewViewStateService);
  private refreshHandle: ReturnType<typeof setTimeout> | null = null;
  private requestGeneration = 0;

  readonly overview = signal<WorkbenchOverview | null>(null);
  readonly loading = signal(false);
  readonly error = signal(false);
  readonly expandedDecisionKey = signal<string | null>(null);
  readonly referenceStatusesByItem = signal<ReadonlyMap<string, readonly TaskReferenceStatus[]>>(new Map());
  readonly referenceStatusesLoading = signal(false);

  readonly filteredItems = computed(() => this.viewState.filter(
    this.overview()?.items ?? [],
    item => this.statusLabel(item),
  ));
  readonly filteredCount = computed(() => this.filteredItems().length);
  readonly decisionPending = computed(() => this.sortedItemsWithStatus('decision-pending'));
  readonly current = computed(() => this.viewState.sort([
    ...this.filteredItemsWithStatus('active'),
    ...this.filteredItemsWithStatus('decided'),
  ], item => this.statusLabel(item)));
  readonly invalid = computed(() => this.sortedItemsWithStatus('invalid'));
  readonly discarded = computed(() => this.sortedItemsWithStatus('archived'));
  readonly documented = computed(() => this.sortedItemsWithStatus('documented'));
  readonly currentCount = computed(() => this.decisionPending().length + this.current().length);
  readonly historyCount = computed(() => this.discarded().length + this.documented().length);
  readonly sectionScope = computed(() => this.projectId()?.trim() || this.projectName()?.trim() || 'all-projects');

  constructor() {
    effect(() => {
      const projectName = this.projectName();
      untracked(() => {
        this.viewState.setScope(projectName);
        this.expandedDecisionKey.set(null);
        this.load(projectName, true);
      });
    });

    effect(() => {
      const scope = this.sectionScope();
      const items = this.overview()?.items;
      if (!items) return;
      untracked(() => {
        this.sectionState.observeItems(scope, 'needs-decision', countStatus(items, 'decision-pending'));
        this.sectionState.observeItems(scope, 'current', countStatus(items, 'active', 'decided'));
        this.sectionState.observeItems(scope, 'needs-attention', countStatus(items, 'invalid'));
        this.sectionState.observeItems(scope, 'history', countStatus(items, 'documented', 'archived'));
        this.sectionState.observeItems(scope, 'documented', countStatus(items, 'documented'));
        this.sectionState.observeItems(scope, 'discarded', countStatus(items, 'archived'));
      });
    });

    effect(() => {
      const event = this.hub.workbenchEvent();
      if (!event) return;
      const scope = this.projectName();
      if (event.projectName && scope && event.projectName !== scope) return;
      untracked(() => this.scheduleRefresh());
    });

    this.destroyRef.onDestroy(() => {
      if (this.refreshHandle) clearTimeout(this.refreshHandle);
    });
  }

  open(item: WorkbenchOverviewItem): void {
    if (item.workbench.valid) this.openWorkbench.emit(item);
  }
  toggleInlineDecision(item: WorkbenchOverviewItem): void {
    const key = this.itemKey(item);
    this.expandedDecisionKey.update(current => current === key ? null : key);
  }
  inlineDecisionExpanded(item: WorkbenchOverviewItem): boolean {
    return this.expandedDecisionKey() === this.itemKey(item);
  }
  sectionExpanded(section: DossierSectionId): boolean {
    return this.sectionState.expanded(this.sectionScope(), section);
  }
  toggleSection(section: DossierSectionId, header: HTMLElement, contentId: string): void {
    const expanded = this.sectionExpanded(section);
    const content = document.getElementById(contentId);
    if (expanded && content?.contains(document.activeElement)) header.focus();
    this.sectionState.setExpanded(this.sectionScope(), section, !expanded);
  }
  openDecisionCount(item: WorkbenchOverviewItem): number {
    return item.workbench.openDecisionCount
      ?? (item.workbench.status === 'decision-pending' ? 1 : 0);
  }
  documentPattern(item: WorkbenchOverviewItem): ArticlePattern {
    return item.workbench.pattern === 'ui' ? 'ui' : 'concept';
  }
  patternIcon(item: WorkbenchOverviewItem): StudioIconName {
    return this.documentPattern(item) === 'ui' ? 'grid' : 'book';
  }
  projectDisplay(item: WorkbenchOverviewItem) {
    return this.projects.getProjectDisplay(item.projectName);
  }
  referenceStatuses(item: WorkbenchOverviewItem): readonly TaskReferenceStatus[] {
    return this.referenceStatusesByItem().get(this.itemKey(item)) ?? [];
  }
  statusLabel(item: WorkbenchOverviewItem): string {
    const workbench = item.workbench;
    if (!workbench.valid) return 'Needs attention';
    if (workbench.status === 'decision-pending') return 'Decision pending';
    if (workbench.status === 'active') return workbench.phase ?? 'Active';
    if (workbench.status === 'decided') return 'Accepted / In progress';
    if (workbench.status === 'archived') return 'Discarded';
    if (workbench.status === 'documented') return 'Documented';
    return workbench.status;
  }
  updatedLabel(value: string): string {
    return new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(value));
  }
  keyLabel(item: WorkbenchOverviewItem): string {
    return item.workbench.key ?? item.workbench.id;
  }
  private filteredItemsWithStatus(status: string): WorkbenchOverviewItem[] {
    return this.filteredItems().filter(item => item.workbench.status === status);
  }
  private sortedItemsWithStatus(status: string): WorkbenchOverviewItem[] {
    return this.viewState.sort(
      this.filteredItemsWithStatus(status),
      item => this.statusLabel(item),
    );
  }
  private itemKey(item: WorkbenchOverviewItem): string {
    return `${item.projectName}:${item.workbench.id}`;
  }
  private scheduleRefresh(): void {
    if (this.refreshHandle) return;
    this.refreshHandle = setTimeout(() => {
      this.refreshHandle = null;
      this.load(this.projectName(), false);
    }, 80);
  }
  private load(projectName: string | null, clear: boolean): void {
    const generation = ++this.requestGeneration;
    this.loading.set(true);
    this.error.set(false);
    if (clear) {
      this.overview.set(null);
      this.referenceStatusesByItem.set(new Map());
      this.referenceStatusesLoading.set(false);
    }
    this.docs.getWorkbenchOverview(projectName).subscribe({
      next: overview => {
        if (generation !== this.requestGeneration) return;
        this.overview.set(overview);
        this.loading.set(false);
        this.hydrateReferenceStatuses(overview, generation);
      },
      error: () => {
        if (generation !== this.requestGeneration) return;
        this.error.set(true);
        this.loading.set(false);
        this.referenceStatusesByItem.set(new Map());
        this.referenceStatusesLoading.set(false);
      },
    });
  }

  private hydrateReferenceStatuses(overview: WorkbenchOverview, generation: number): void {
    const keysByItem = new Map<string, string[]>();
    const allKeys: string[] = [];
    const seen = new Set<string>();
    for (const item of overview.items) {
      if (!['decision-pending', 'active', 'decided'].includes(item.workbench.status)) continue;
      const keys = uniqueKeys([
        ...(item.workbench.documentation?.references.map(reference => reference.key) ?? []),
        ...(item.workbench.relatedTaskKeys ?? []),
      ]);
      keysByItem.set(this.itemKey(item), keys);
      for (const key of keys) {
        const normalized = normalizeKey(key);
        if (seen.has(normalized)) continue;
        seen.add(normalized);
        allKeys.push(key);
      }
    }

    this.referenceStatusesByItem.set(statusesByItem(keysByItem, new Map(), overview.items));
    if (allKeys.length === 0) {
      this.referenceStatusesLoading.set(false);
      return;
    }

    this.referenceStatusesLoading.set(true);
    this.tasks.getReferenceStatuses(allKeys).subscribe({
      next: statuses => {
        if (generation !== this.requestGeneration) return;
        const byKey = new Map(
          statuses.map(status => [normalizeKey(status.key), status] as const),
        );
        this.referenceStatusesByItem.set(statusesByItem(keysByItem, byKey, overview.items));
        this.referenceStatusesLoading.set(false);
      },
      error: () => {
        if (generation !== this.requestGeneration) return;
        this.referenceStatusesLoading.set(false);
      },
    });
  }
}

function countStatus(items: readonly WorkbenchOverviewItem[], ...statuses: readonly string[]): number {
  return items.filter(item => statuses.includes(item.workbench.status)).length;
}

function statusesByItem(
  keysByItem: ReadonlyMap<string, readonly string[]>,
  resolved: ReadonlyMap<string, TaskReferenceStatus>,
  items: readonly WorkbenchOverviewItem[],
): ReadonlyMap<string, readonly TaskReferenceStatus[]> {
  const projectByItem = new Map(items.map(item => [`${item.projectName}:${item.workbench.id}`, item.projectName]));
  return new Map([...keysByItem].map(([itemKey, keys]) => [
    itemKey,
    keys.map(key => resolved.get(normalizeKey(key)) ?? ghostStatus(key, projectByItem.get(itemKey) ?? '')),
  ]));
}

function uniqueKeys(keys: readonly string[]): string[] {
  const result: string[] = [];
  const seen = new Set<string>();
  for (const key of keys) {
    const normalized = normalizeKey(key);
    if (!normalized || seen.has(normalized)) continue;
    seen.add(normalized);
    result.push(key.trim());
  }
  return result;
}

function normalizeKey(value: string | null | undefined): string {
  return (value ?? '').trim().toUpperCase();
}

function ghostStatus(key: string, projectName: string): TaskReferenceStatus {
  return {
    key,
    exists: false,
    taskKey: null,
    title: null,
    lane: null,
    projectId: '',
    projectName,
    projectColor: null,
    merge: null,
    reviewGrade: null,
  };
}
