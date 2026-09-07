import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
  viewChildren,
} from '@angular/core';
import { TreeRowComponent } from '../../../../components/tree-row/tree-row.component';
import type { StudioIconName } from '../../../../components/studio-icon/studio-icon.component';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import {
  DossierSectionStateService,
  type DossierSectionId,
} from '../../../../services/dossier-section-state.service';
import type { ArticlePattern, WorkbenchCatalogue, WorkbenchListItem } from '../../../../models/project-docs.model';
import {
  ExplorerWorkbenchStateService,
  type ExplorerWorkbenchGroupId,
} from '../../services/explorer-workbench-state.service';
import { ExplorerWorkbenchHistoryComponent } from '../explorer-workbench-history/explorer-workbench-history.component';

const STYLE_GUIDE_PATH = 'docs/operations/admin-design-guideline/index.html';

interface WorkbenchNavigationGroup {
  id: Exclude<ExplorerWorkbenchGroupId, 'history'>;
  label: string;
  empty: string;
  items: WorkbenchListItem[];
}

@Component({
  selector: 'app-explorer-workbench-list',
  standalone: true,
  imports: [TreeRowComponent, ExplorerWorkbenchHistoryComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './explorer-workbench-list.component.html',
  styleUrl: './explorer-workbench-list.component.scss',
})
export class ExplorerWorkbenchListComponent {
  readonly projectName = input.required<string>();
  readonly projectId = input<string | null>(null);
  readonly activeWorkbenchId = input<string | null>(null);
  readonly overviewActive = input(false);
  readonly openWorkbench = output<WorkbenchListItem>();
  readonly openOverview = output<void>();
  readonly openWiki = output<void>();

  private readonly docs = inject(ProjectDocsService);
  private readonly hub = inject(JobsHubClient);
  private readonly navigationState = inject(ExplorerWorkbenchStateService);
  private readonly sectionState = inject(DossierSectionStateService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly loading = signal(false);
  readonly catalogue = signal<WorkbenchCatalogue | null>(null);
  readonly expanded = computed(() =>
    this.navigationState.stateFor(this.projectName()).dossiersExpanded);
  readonly styleGuide = computed(() =>
    this.catalogue()?.items.find(item => normalizePath(item.entryPath) === STYLE_GUIDE_PATH) ?? null);
  readonly currentGroups = computed<WorkbenchNavigationGroup[]>(() => {
    const items = this.dossierItems();
    return [
      {
        id: 'needs-decision',
        label: 'Needs a decision',
        empty: 'No decisions waiting',
        items: items.filter(item => item.status === 'decision-pending' || item.status === 'invalid'),
      },
      {
        id: 'in-implementation',
        label: 'In implementation',
        empty: 'No Dossiers in implementation',
        items: items.filter(item => item.status === 'active' || item.status === 'decided'),
      },
    ];
  });
  readonly historyItems = computed(() =>
    this.dossierItems().filter(item => item.status === 'documented' || item.status === 'archived'));
  readonly pendingDecisionCount = computed(() =>
    (this.catalogue()?.items ?? [])
      .filter(item => item.status === 'decision-pending')
      .reduce((count, item) => count + this.openCount(item), 0));
  readonly sectionScope = computed(() => this.projectId()?.trim() || this.projectName());

  private readonly topics = viewChildren<ElementRef<HTMLElement>>('workbenchTopic');
  private readonly styleGuideRow = viewChild<ElementRef<HTMLElement>>('styleGuideRow');
  private lastProjectName: string | null = null;
  private lastRevealedWorkbench: string | null = null;

  constructor() {
    effect(() => {
      const projectName = this.projectName();
      const activeWorkbenchId = this.activeWorkbenchId();
      const catalogue = this.catalogue();
      if (projectName !== this.lastProjectName) {
        this.lastProjectName = projectName;
        this.lastRevealedWorkbench = null;
        untracked(() => {
          this.catalogue.set(null);
          this.loadCatalogue();
        });
        return;
      }
      if (activeWorkbenchId && catalogue?.projectName === projectName) {
        untracked(() => this.revealActiveWorkbench(activeWorkbenchId));
      } else if (!activeWorkbenchId) {
        this.lastRevealedWorkbench = null;
      }
    });

    effect(() => {
      const scope = this.sectionScope();
      const catalogue = this.catalogue();
      if (!catalogue) return;
      const groups = this.currentGroups();
      untracked(() => {
        for (const group of groups) {
          this.sectionState.observeItems(scope, sectionId(group.id), group.items.length);
        }
        this.sectionState.observeItems(scope, 'history', this.historyItems().length);
      });
    });

    effect(() => {
      const event = this.hub.workbenchEvent();
      if (!event || event.projectName && event.projectName !== this.projectName()) return;
      untracked(() => this.refreshCatalogue());
    });

    effect(() => {
      const activeWorkbenchId = this.activeWorkbenchId();
      const activeTopic = this.topics()
        .map(topic => topic.nativeElement.querySelector<HTMLButtonElement>('[aria-current="page"]'))
        .find(topic => topic !== null);
      const activeStyleGuide = this.styleGuideRow()?.nativeElement
        .querySelector<HTMLButtonElement>('[aria-current="page"]');
      const activeRow = activeTopic ?? activeStyleGuide;
      if (!activeWorkbenchId || !activeRow || typeof activeRow.scrollIntoView !== 'function') return;
      queueMicrotask(() => activeRow.scrollIntoView({ block: 'nearest', inline: 'nearest' }));
    });
  }

  toggle(): void {
    this.setExpanded(!this.expanded());
  }

  openOverviewPage(): void {
    this.setExpanded(true);
    this.openOverview.emit();
  }

  groupExpanded(group: ExplorerWorkbenchGroupId): boolean {
    return this.sectionState.expanded(this.sectionScope(), sectionId(group));
  }

  toggleGroup(group: ExplorerWorkbenchGroupId): void {
    const expanded = this.groupExpanded(group);
    if (expanded) this.focusGroupHeaderWhenNeeded(group);
    this.sectionState.setExpanded(this.sectionScope(), sectionId(group), !expanded);
  }

  groupContentId(group: ExplorerWorkbenchGroupId): string {
    return `studio-explorer-dossier-${encodeURIComponent(this.projectName())}-${group}`;
  }

  isActive(item: WorkbenchListItem): boolean {
    return item.id === this.activeWorkbenchId();
  }

  documentPattern(item: WorkbenchListItem): ArticlePattern {
    return item.pattern === 'ui' ? 'ui' : 'concept';
  }

  patternIcon(item: WorkbenchListItem): StudioIconName {
    return this.documentPattern(item) === 'ui' ? 'grid' : 'book';
  }

  secondaryMeta(item: WorkbenchListItem): string {
    if (!item.valid) return item.error || 'Descriptor needs repair';
    if (item.documentation?.eligible) return 'Ready to document';
    if (item.status === 'decision-pending') return 'Decision pending';
    if (item.status === 'active') return item.phase ?? 'Active';
    if (item.status === 'decided') return 'In implementation';
    if (item.status === 'documented') return 'Documented';
    if (item.status === 'archived') return 'Discarded';
    return item.status;
  }

  openCount(item: WorkbenchListItem): number {
    return item.openDecisionCount ?? (item.status === 'decision-pending' ? 1 : 0);
  }

  accessibleMeta(item: WorkbenchListItem): string {
    const days = Math.max(0, Math.floor((Date.now() - new Date(item.updatedAtUtc).getTime()) / 86_400_000));
    const updated = days === 0 ? 'updated today' : `updated ${days} days ago`;
    return `${this.documentPattern(item)} pattern, ${this.secondaryMeta(item)}, ${updated}`;
  }

  navTooltip(item: WorkbenchListItem): string {
    return [item.title, item.key, this.accessibleMeta(item)].filter(Boolean).join('\n');
  }

  dossierAriaLabel(): string {
    const count = this.pendingDecisionCount();
    return count > 0 ? `Dossiers, ${count} decisions waiting` : 'Dossiers';
  }

  private dossierItems(): WorkbenchListItem[] {
    const styleGuideId = this.styleGuide()?.id;
    return (this.catalogue()?.items ?? []).filter(item => item.id !== styleGuideId);
  }

  private setExpanded(expanded: boolean): void {
    this.navigationState.setDossiersExpanded(this.projectName(), expanded);
    if (expanded) this.loadCatalogue();
  }

  private loadCatalogue(): void {
    if (this.catalogue() || this.loading()) return;
    this.loading.set(true);
    this.docs.getWorkbenches(this.projectName(), true).subscribe({
      next: value => {
        this.catalogue.set(value);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  private refreshCatalogue(): void {
    this.docs.getWorkbenches(this.projectName(), true).subscribe({
      next: value => this.catalogue.set(value),
      error: () => undefined,
    });
  }

  private revealActiveWorkbench(activeWorkbenchId: string): void {
    const item = this.catalogue()?.items.find(candidate => candidate.id === activeWorkbenchId);
    if (!item) return;
    const revealKey = `${this.projectName()}:${activeWorkbenchId}`;
    if (this.lastRevealedWorkbench === revealKey) return;
    this.lastRevealedWorkbench = revealKey;
    if (item.id === this.styleGuide()?.id) return;
    this.setExpanded(true);
    if (item.status === 'documented' || item.status === 'archived') {
      this.sectionState.setExpanded(this.sectionScope(), 'history', true);
    } else {
      const group = item.status === 'decision-pending' || item.status === 'invalid'
        ? 'needs-decision'
        : 'in-implementation';
      this.sectionState.setExpanded(this.sectionScope(), sectionId(group), true);
    }
  }

  private focusGroupHeaderWhenNeeded(group: ExplorerWorkbenchGroupId): void {
    const suffix = group === 'history' ? 'history-items' : `group-items-${group}`;
    const content = this.host.nativeElement.querySelector<HTMLElement>(
      `[data-dossier-section-content="${suffix}"]`,
    );
    if (!content?.contains(document.activeElement)) return;
    this.host.nativeElement.querySelector<HTMLButtonElement>(
      `[data-testid="${group === 'history'
        ? `studio-explorer-workbench-history-${this.projectName()}`
        : `studio-explorer-workbench-group-${this.projectName()}-${group}`}"]`,
    )?.focus();
  }
}

function sectionId(group: ExplorerWorkbenchGroupId): DossierSectionId {
  return group === 'in-implementation' ? 'current' : group;
}

function normalizePath(value: string): string {
  return value.replace(/\\/g, '/').replace(/^\/+/, '');
}
