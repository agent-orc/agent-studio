import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { SectionHeaderComponent } from '../../../../components/section-header/section-header.component';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { TreeRowComponent } from '../../../../components/tree-row/tree-row.component';
import {
  PromptCatalogItem,
  PromptProjectOverride,
} from '../../../../services/prompt-admin.service';
import { TooltipDirective } from 'coding-agent-chat/shared';

interface PromptGroup {
  name: string;
  items: PromptCatalogItem[];
}

/**
 * Canonical prompt catalogue navigation shared by Workspace Settings and the
 * project Prompts page. It owns grouping, inheritance filtering, telemetry,
 * override origins, and stale indicators so host padding cannot fork it.
 */
@Component({
  selector: 'app-prompt-catalogue-nav',
  standalone: true,
  imports: [
    DatePipe,
    SectionHeaderComponent,
    StatusIndicatorComponent,
    TreeRowComponent,
    TooltipDirective,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './prompt-catalogue-nav.component.html',
  styleUrl: './prompt-catalogue-nav.component.scss',
})
export class PromptCatalogueNavComponent {
  readonly items = input<readonly PromptCatalogItem[]>([]);
  readonly selectedName = input<string | null>(null);
  /** Null means workspace/global scope; a name limits project origins to it. */
  readonly projectName = input<string | null>(null);
  readonly homeRequest = output<void>();
  readonly selectPrompt = output<string>();

  readonly collapsedGroups = signal<ReadonlySet<string>>(new Set());
  readonly onlyOverrides = signal(false);

  readonly scopedItems = computed(() => this.items().map(item => ({
    ...item,
    projectOverrides: this.projectOverridesFor(item),
  })));

  readonly visibleItems = computed(() => this.onlyOverrides()
    ? this.scopedItems().filter(item => this.hasVisibleOverride(item))
    : this.scopedItems());

  readonly groups = computed<PromptGroup[]>(() => {
    const buckets = new Map<string, PromptCatalogItem[]>();
    const order: string[] = [];
    for (const item of this.visibleItems()) {
      if (!buckets.has(item.group)) {
        buckets.set(item.group, []);
        order.push(item.group);
      }
      buckets.get(item.group)!.push(item);
    }
    return order.map(name => ({ name, items: buckets.get(name)! }));
  });

  readonly overrideCount = computed(() =>
    this.scopedItems().filter(item => this.hasVisibleOverride(item)).length);

  readonly inheritedCount = computed(() => this.scopedItems().length - this.overrideCount());

  readonly hasTelemetry = computed(() => this.items().some(item =>
    item.lastChangedAt !== undefined || item.lastReviewedAt !== undefined));

  isGroupCollapsed(name: string): boolean {
    return this.collapsedGroups().has(name);
  }

  setGroupCollapsed(name: string, collapsed: boolean): void {
    const next = new Set(this.collapsedGroups());
    if (collapsed) next.add(name);
    else next.delete(name);
    this.collapsedGroups.set(next);
  }

  projectOverridesFor(item: PromptCatalogItem): PromptProjectOverride[] {
    const all = Array.isArray(item.projectOverrides) ? item.projectOverrides : [];
    const project = this.projectName();
    return project
      ? all.filter(origin => origin.projectName.localeCompare(project, undefined, { sensitivity: 'accent' }) === 0)
      : all;
  }

  hasGlobalOverride(item: PromptCatalogItem): boolean {
    if (item.hasGlobalOverride !== undefined) return item.hasGlobalOverride;
    return item.hasOverride && (item.projectOverrides?.length ?? 0) === 0;
  }

  hasVisibleOverride(item: PromptCatalogItem): boolean {
    return this.hasGlobalOverride(item) || this.projectOverridesFor(item).length > 0;
  }

  overrideOrigins(item: PromptCatalogItem): string[] {
    const origins: string[] = [];
    if (this.hasGlobalOverride(item)) origins.push('global');
    for (const project of this.projectOverridesFor(item).map(origin => origin.projectName)) {
      if (!origins.includes(project)) origins.push(project);
    }
    return origins;
  }

  overrideLabel(item: PromptCatalogItem): string {
    return `overridden - ${this.overrideOrigins(item).join(', ')}`;
  }

  overrideTooltip(item: PromptCatalogItem): string {
    const origins = this.overrideOrigins(item).join(', ');
    return `Override active in ${origins}.`;
  }

  isStale(item: PromptCatalogItem): boolean {
    const globalStale = this.hasGlobalOverride(item)
      && (item.globalDefaultChangedSinceOverride ?? (
        (item.projectOverrides?.length ?? 0) === 0 && item.defaultChangedSinceOverride
      ));
    return globalStale || this.projectOverridesFor(item)
      .some(origin => origin.defaultChangedSinceOverride === true);
  }
}
