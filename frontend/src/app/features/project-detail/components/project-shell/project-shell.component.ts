import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { QualityStudioPanelComponent } from '../quality-studio-panel/quality-studio-panel.component';
import { QualityStudioStatusService } from '../quality-studio-panel/quality-studio-status.service';
import { AuthSessionState } from '../../../../services/auth.service';
import { QualityStudioService } from '../../../../services/quality-studio.service';
import { SectionHeaderComponent } from '../../../../components/section-header/section-header.component';
import { StudioSidebarHeaderComponent } from '../../../../components/studio-sidebar-header/studio-sidebar-header.component';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TreeRowComponent } from '../../../../components/tree-row/tree-row.component';
import {
  PROJECT_RAIL_ITEMS,
  PROJECT_RAIL_PARENT_KEYS,
  ProjectRailGroup,
  ProjectRailItem,
  ProjectRailKey,
} from './project-shell.config';

/** A top-level rail entry plus any tree-expandable children beneath it. */
export interface ProjectRailNode {
  item: ProjectRailItem;
  children: readonly ProjectRailItem[];
}

/** A collapsible main segment of the rail with its top-level nodes. */
export interface ProjectRailGroupView {
  id: ProjectRailGroup;
  label: string;
  nodes: readonly ProjectRailNode[];
}

type ProjectRailCompactItem = ProjectRailItem & { railIcon: NonNullable<ProjectRailItem['railIcon']> };

function isCompactRailItem(item: ProjectRailItem): item is ProjectRailCompactItem {
  return item.navigable !== false && item.railIcon != null;
}

interface RailResizeState {
  pointerId: number;
  startX: number;
  startWidth: number;
  moved: boolean;
}

interface ProjectShellPersistedState {
  railCollapsed?: boolean;
  railWidth?: number;
  collapsedGroups?: ProjectRailGroup[];
  expandedParents?: ProjectRailKey[];
}

const PROJECT_SHELL_STATE_STORAGE_PREFIX = 'atp.projectShell.v1.';

/**
 * Project page shell. Slice 2 of the quality-system mockup: introduces the
 * left-rail navigation skeleton plus a placeholder body per rail item.
 *
 * The shell is action-driven by design (see docs/mockups/quality-system/
 * README.md): mounting a panel must not run any analysis. Real content for
 * each rail item lands in a separate follow-up slice.
 */
@Component({
  selector: 'app-project-shell',
  standalone: true,
  imports: [SectionHeaderComponent, StudioSidebarHeaderComponent, StudioIconComponent, TooltipDirective, TreeRowComponent, QualityStudioPanelComponent],
  providers: [QualityStudioStatusService],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-shell.component.html',
  styleUrl: './project-shell.component.scss',
})
export class ProjectShellComponent {
  private readonly qualityStudio = inject(QualityStudioStatusService);
  private readonly quality = inject(QualityStudioService);
  private readonly auth = inject(AuthSessionState);
  readonly collapsedRailWidth = 44;
  readonly minRailWidth = 176;
  readonly maxRailWidth = 360;
  private readonly collapseThreshold = 128;
  private readonly keyboardResizeStep = 16;

  readonly projectName = input.required<string>();
  readonly activeRail = input.required<ProjectRailKey>();
  /**
   * When true, the shell hides its built-in panel header + empty state and
   * surfaces an `<ng-content>` slot in the panel body instead. The host
   * (typically `app.ts`) projects a real panel component (Security panel,
   * etc.) and owns the panel header so the slice can paint its own
   * baseline badge / cards / actions per the mockup. When false, the
   * generic placeholder + description still renders for rails that have
   * not landed their content slice yet.
   */
  readonly hasCustomPanel = input<boolean>(false);
  readonly railChange = output<ProjectRailKey>();
  readonly openFeed = output<void>();
  readonly closeShell = output<void>();

  readonly railCollapsed = signal(false);
  readonly railWidth = signal(240);
  readonly railWidthStyle = computed(() => `${this.railWidth()}px`);
  readonly splitterValue = computed(() => this.railCollapsed() ? this.collapsedRailWidth : this.railWidth());

  private railResizeState: RailResizeState | null = null;
  private suppressSplitterClick = false;

  /** Main segments the user has collapsed (hidden contents). */
  private readonly collapsedGroups = signal<ReadonlySet<ProjectRailGroup>>(new Set());
  /**
   * Tree parents whose children are currently shown. Seeded with every parent
   * so the tree opens fully expanded — keeps existing deep-links to nested
   * rails (currently Settings sub-pages) reachable on first paint.
   */
  private readonly expandedParents = signal<ReadonlySet<ProjectRailKey>>(
    new Set(PROJECT_RAIL_PARENT_KEYS),
  );

  constructor() {
    effect(() => this.qualityStudio.load(this.projectName()));
    effect(() => this.quality.ensure(this.projectName()));
    effect(() => {
      this.restorePersistedState(this.projectName());
    });

    effect(() => {
      this.ensureActiveRailVisible(this.activeRail());
    });
  }

  /**
   * Rail items as a collapsible-segment → tree-node structure. Top-level nodes
   * are items without a `parent`; each carries the children that point back at
   * its key. Group order follows the canonical Deck taxonomy:
   * Insight → Quality → Context → Config.
   */
  readonly railGroups = computed<readonly ProjectRailGroupView[]>(() => {
    const order: { id: ProjectRailGroup; label: string }[] = [
      { id: 'insight',    label: 'Insight' },
      { id: 'quality',    label: 'Quality' },
      { id: 'context',    label: 'Context' },
      { id: 'config',     label: 'Config' },
    ];
    return order.map(({ id, label }) => {
      const inGroup = PROJECT_RAIL_ITEMS.filter(i => i.group === id && this.showsItem(i));
      const nodes = inGroup
        .filter(i => !i.parent)
        .map<ProjectRailNode>(item => ({
          item,
          children: inGroup.filter(c => c.parent === item.key),
        }));
      return { id, label, nodes };
    });
  });

  /** Panel descriptor for the currently selected rail key. */
  readonly activeItem = computed<ProjectRailItem>(() => {
    const key = this.activeRail();
    return PROJECT_RAIL_ITEMS.find(i => i.key === key) ?? PROJECT_RAIL_ITEMS[0];
  });

  readonly compactRailItems = computed<readonly ProjectRailCompactItem[]>(() =>
    PROJECT_RAIL_ITEMS.filter(item => this.showsItem(item)).filter(isCompactRailItem),
  );

  private showsItem(item: ProjectRailItem): boolean {
    return item.key !== 'quality-studio' || this.qualityStudio.status()?.connected === true
      || this.auth.status()?.user?.role === 'owner'
      || this.quality.status(this.projectName())?.configured === true;
  }

  setRailCollapsed(collapsed: boolean): void {
    this.railCollapsed.set(collapsed);
    this.persistState();
  }

  toggleRailCollapsed(): void {
    this.railCollapsed.update(v => !v);
    this.persistState();
  }

  startRailResize(event: PointerEvent): void {
    event.preventDefault();
    const target = event.currentTarget as HTMLElement | null;
    target?.setPointerCapture?.(event.pointerId);
    this.railResizeState = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startWidth: this.railCollapsed() ? this.collapsedRailWidth : this.railWidth(),
      moved: false,
    };
  }

  resizeRail(event: PointerEvent): void {
    const state = this.railResizeState;
    if (!state || event.pointerId !== state.pointerId) return;

    const delta = event.clientX - state.startX;
    if (Math.abs(delta) <= 2) return;

    state.moved = true;
    this.applyRailWidth(state.startWidth + delta);
  }

  finishRailResize(event: PointerEvent): void {
    const state = this.railResizeState;
    if (!state || event.pointerId !== state.pointerId) return;

    const target = event.currentTarget as HTMLElement | null;
    target?.releasePointerCapture?.(event.pointerId);
    this.suppressSplitterClick = state.moved;
    this.railResizeState = null;

    if (this.suppressSplitterClick) {
      window.setTimeout(() => {
        this.suppressSplitterClick = false;
      }, 0);
    }
  }

  onSplitterClick(): void {
    if (this.suppressSplitterClick) return;
    this.toggleRailCollapsed();
  }

  onSplitterKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.toggleRailCollapsed();
      return;
    }

    if (event.key === 'ArrowLeft') {
      event.preventDefault();
      if (this.railCollapsed()) return;
      const nextWidth = this.railWidth() - this.keyboardResizeStep;
      if (nextWidth < this.minRailWidth) this.setRailCollapsed(true);
      else {
        this.railWidth.set(nextWidth);
        this.persistState();
      }
      return;
    }

    if (event.key === 'ArrowRight') {
      event.preventDefault();
      if (this.railCollapsed()) {
        this.setRailCollapsed(false);
        this.railWidth.update(width => Math.max(width, this.minRailWidth));
      } else {
        this.railWidth.set(this.clampRailWidth(this.railWidth() + this.keyboardResizeStep));
      }
      this.persistState();
    }
  }

  isGroupCollapsed(id: ProjectRailGroup): boolean {
    return this.collapsedGroups().has(id);
  }

  /** Adapter for the shared section-header, whose `collapsedChange` emits the next state. */
  setGroupCollapsed(id: ProjectRailGroup, collapsed: boolean): void {
    const next = new Set(this.collapsedGroups());
    if (collapsed) next.add(id);
    else next.delete(id);
    this.collapsedGroups.set(next);
    this.persistState();
  }

  isExpanded(key: ProjectRailKey): boolean {
    return this.expandedParents().has(key);
  }

  toggleParent(key: ProjectRailKey): void {
    const next = new Set(this.expandedParents());
    if (next.has(key)) next.delete(key);
    else next.add(key);
    this.expandedParents.set(next);
    this.persistState();
  }

  /**
   * Row click. A pure container (navigable === false) only toggles its
   * children; a navigable row routes to its panel (no-op when already active).
   * The disclosure twisty is a separate control, so a navigable parent can be
   * selected without forcing its children open or shut.
   */
  onRailClick(item: ProjectRailItem): void {
    if (item.navigable === false) {
      this.toggleParent(item.key);
      return;
    }
    if (item.key === this.activeRail()) return;
    this.railChange.emit(item.key);
  }

  private applyRailWidth(width: number): void {
    if (width <= this.collapseThreshold) {
      this.setRailCollapsed(true);
      return;
    }

    this.setRailCollapsed(false);
    this.railWidth.set(this.clampRailWidth(width));
    this.persistState();
  }

  private clampRailWidth(width: number): number {
    return Math.min(this.maxRailWidth, Math.max(this.minRailWidth, Math.round(width)));
  }

  private restorePersistedState(projectName: string): void {
    const state = this.readPersistedState(projectName);
    this.railCollapsed.set(state?.railCollapsed === true);
    this.railWidth.set(this.clampRailWidth(state?.railWidth ?? 240));
    this.collapsedGroups.set(new Set(state?.collapsedGroups ?? []));
    this.expandedParents.set(new Set(state?.expandedParents ?? PROJECT_RAIL_PARENT_KEYS));
  }

  private persistState(): void {
    const projectName = this.projectName();
    if (!projectName) return;
    const state: ProjectShellPersistedState = {
      railCollapsed: this.railCollapsed(),
      railWidth: this.railWidth(),
      collapsedGroups: [...this.collapsedGroups()],
      expandedParents: [...this.expandedParents()],
    };
    try {
      globalThis.localStorage?.setItem(this.storageKey(projectName), JSON.stringify(state));
    } catch {
      /* Persistence is a convenience; navigation keeps working without storage. */
    }
  }

  private readPersistedState(projectName: string): ProjectShellPersistedState | null {
    try {
      const raw = globalThis.localStorage?.getItem(this.storageKey(projectName));
      if (!raw) return null;
      const parsed = JSON.parse(raw) as Partial<ProjectShellPersistedState>;
      return {
        railCollapsed: parsed.railCollapsed === true,
        railWidth: this.readStoredRailWidth(parsed.railWidth),
        collapsedGroups: this.readStoredGroups(parsed.collapsedGroups),
        expandedParents: this.readStoredParents(parsed.expandedParents),
      };
    } catch {
      return null;
    }
  }

  private readStoredRailWidth(value: unknown): number | undefined {
    return typeof value === 'number' && Number.isFinite(value)
      ? this.clampRailWidth(value)
      : undefined;
  }

  private readStoredGroups(value: unknown): ProjectRailGroup[] | undefined {
    if (!Array.isArray(value)) return undefined;
    const valid = new Set<ProjectRailGroup>(['insight', 'quality', 'context', 'config']);
    return value.filter((item): item is ProjectRailGroup => valid.has(item));
  }

  private readStoredParents(value: unknown): ProjectRailKey[] | undefined {
    if (!Array.isArray(value)) return undefined;
    const valid = new Set<ProjectRailKey>(PROJECT_RAIL_PARENT_KEYS);
    return value.filter((item): item is ProjectRailKey => valid.has(item));
  }

  private ensureActiveRailVisible(activeKey: ProjectRailKey): void {
    const item = PROJECT_RAIL_ITEMS.find(i => i.key === activeKey);
    if (!item) return;

    let changed = false;
    if (this.collapsedGroups().has(item.group)) {
      this.collapsedGroups.update(current => {
        const next = new Set(current);
        next.delete(item.group);
        return next;
      });
      changed = true;
    }

    const parent = item.parent;
    if (parent && !this.expandedParents().has(parent)) {
      this.expandedParents.update(current => new Set([...current, parent]));
      changed = true;
    }

    if (changed) this.persistState();
  }

  private storageKey(projectName: string): string {
    return `${PROJECT_SHELL_STATE_STORAGE_PREFIX}${encodeURIComponent(projectName)}`;
  }
}
