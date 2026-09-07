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
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BoardFiltersService } from '../../../../features/board';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import { TaskService } from '../../../../services/task.service';
import { WatcherDecisionPanelComponent } from '../../../../features/watcher';
import type { OrchestratorLogEntry } from '../../models/orchestrator.model';
import { GlobalOrchestratorCardComponent } from '../global-orchestrator-card/global-orchestrator-card';
import { LoadDistributionComponent } from '../load-distribution/load-distribution.component';
import { OrchestratorFeedEntryComponent } from '../orchestrator-feed-entry/orchestrator-feed-entry';
import { OrchestratorFeedStore } from '../../state/orchestrator-feed.store';
import { OrchestratorFeedWindow } from './orchestrator-feed-windowing';

import { TooltipDirective } from 'coding-agent-chat/shared';

/** Topic name the Watcher stamps on the Activity entry that carries a review-mode decision (orchestrator-waechter dossier §10.4). */
export const WATCHER_DECISION_TOPIC = 'watcher-decision-required';

/** Workspace feed shared by the embedded main route and quick-access modal. */
@Component({
  selector: 'app-orchestrator-feed',
  standalone: true,
  imports: [FormsModule, GlobalOrchestratorCardComponent, LoadDistributionComponent, OrchestratorFeedEntryComponent, TooltipDirective, WatcherDecisionPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-feed.html',
  styleUrl: './orchestrator-feed.scss'
})
export class OrchestratorFeedComponent {
  readonly projectName = input.required<string>();
  readonly embedded = input(false);
  readonly openTask = output<{ jobId: string; watchPath: string }>();

  private readonly jobService = inject(TaskService);
  private readonly boardFilters = inject(BoardFiltersService);
  private readonly projectLookup = inject(ProjectLookupService);
  readonly feedStore = inject(OrchestratorFeedStore);
  readonly entries = this.feedStore.entries;
  readonly loading = this.feedStore.loading;
  readonly error = this.feedStore.error;
  readonly kindFilter = signal<string>('all');
  readonly projectFilter = computed<ReadonlySet<string>>(() =>
    this.boardFilters.hasExplicitProjectFilter()
      ? this.boardFilters.activeProjects()
      : new Set<string>()
  );
  readonly selectedEntry = signal<OrchestratorLogEntry | null>(null);
  readonly activeView = signal<'activity' | 'load'>('activity');
  readonly overridingTs = signal<string | null>(null);
  readonly submittingOverride = signal(false);
  overrideDraft = '';
  private readonly historyWindow = new OrchestratorFeedWindow();
  private readonly streamRef = viewChild<ElementRef<HTMLElement>>('stream');
  private anchorFrame: number | null = null;

  readonly kindFilters = [
    { id: 'all', label: 'All activity' },
    { id: 'alert', label: 'Alerts' },
    { id: 'decision', label: 'Decisions' },
    { id: 'action', label: 'Actions' },
    { id: 'observation', label: 'Observations' },
    { id: 'intervention', label: 'Interventions' },
    { id: 'signal', label: 'Signal' },
  ];

  readonly projectScopeLabel = computed(() => {
    const projects = [...this.projectFilter()];
    if (projects.length === 0) return 'All projects';
    if (projects.length === 1) return projects[0];
    return 'Selected projects';
  });
  readonly kindScopeLabel = computed(() =>
    this.kindFilters.find(filter => filter.id === this.kindFilter())?.label ?? 'All activity'
  );

  readonly projects = computed(() => [...new Set(this.entries().map(entry => entry.project || this.projectName()).filter(Boolean))].sort());
  readonly reversed = computed(() => [...this.entries()].sort((a, b) => newestFirst(a.ts, b.ts)));
  readonly projectEntries = computed(() => {
    const projects = this.projectFilter();
    return this.reversed().filter(entry => projects.size === 0 || projects.has(entry.project || this.projectName()));
  });
  readonly visibleEntries = computed(() => {
    const filter = this.kindFilter();
    return this.projectEntries().filter(entry =>
      filter === 'all' || (filter === 'signal' ? entry.kind !== 'observation' : entry.kind === filter)
    );
  });
  readonly windowedEntries = computed(() => this.historyWindow.slice(this.visibleEntries()));
  readonly dayGroups = computed(() => {
    const groups: { key: string; day: string; entries: OrchestratorLogEntry[] }[] = [];
    for (const entry of this.windowedEntries()) {
      const key = this.dayKey(entry.ts);
      let group = groups[groups.length - 1];
      if (!group || group.key !== key) {
        group = { key, day: this.formatDay(entry.ts), entries: [] };
        groups.push(group);
      }
      group.entries.push(entry);
    }
    return groups;
  });
  readonly olderEntryCount = computed(() =>
    this.historyWindow.remaining(this.visibleEntries().length, this.windowedEntries().length)
  );

  private readonly selectionEffect = effect(() => {
    const entries = this.visibleEntries();
    const selected = this.selectedEntry();
    if (selected && entries.includes(selected)) return;
    this.selectedEntry.set(entries[0] ?? null);
  });

  private readonly feedGrowthEffect = effect(() => {
    const scope = this.filterScope(this.projectFilter(), this.kindFilter());
    const total = this.visibleEntries().length;
    const stream = this.streamRef()?.nativeElement;
    const followingNewest = !stream || stream.scrollTop <= 8;
    const beforeHeight = stream?.scrollHeight ?? 0;
    const beforeTop = stream?.scrollTop ?? 0;
    const grewBy = this.historyWindow.sync(scope, total, followingNewest);
    if (!stream || followingNewest || grewBy === 0 || typeof requestAnimationFrame === 'undefined') return;
    if (this.anchorFrame !== null) cancelAnimationFrame(this.anchorFrame);
    this.anchorFrame = requestAnimationFrame(() => {
      this.anchorFrame = null;
      stream.scrollTop = beforeTop + Math.max(0, stream.scrollHeight - beforeHeight);
    });
  });

  refresh(silent = false): void {
    this.feedStore.refresh(silent);
  }

  isWatcherDecision(entry: OrchestratorLogEntry): boolean {
    return entry.kind === 'decision' && entry.topic === WATCHER_DECISION_TOPIC;
  }

  kindLabel(kind: string): string {
    switch (kind) {
      case 'alert': return 'Alert';
      case 'decision': return 'Decision';
      case 'action': return 'Action';
      case 'observation': return 'Observation';
      case 'intervention': return 'Intervention';
      default: return kind;
    }
  }

  filterCount(kind: string): number {
    const entries = this.projectEntries();
    if (kind === 'all') return entries.length;
    if (kind === 'signal') return entries.filter(entry => entry.kind !== 'observation').length;
    return entries.filter(entry => entry.kind === kind).length;
  }

  selectFilter(kind: string): void {
    this.historyWindow.reset(this.filterScope(this.projectFilter(), kind));
    this.kindFilter.set(kind);
    const first = this.visibleEntries()[0] ?? null;
    this.selectedEntry.set(first);
  }

  selectEntry(entry: OrchestratorLogEntry): void {
    this.selectedEntry.set(entry);
  }

  selectProject(project: string): void {
    const next = project === 'all' ? new Set<string>() : new Set([project]);
    this.historyWindow.reset(this.filterScope(next, this.kindFilter()));
    if (project === 'all') this.boardFilters.clearProjectScope();
    else this.boardFilters.setExplicitSoleProject(project);
    this.selectedEntry.set(this.visibleEntries()[0] ?? null);
  }

  isProjectSelected(project: string): boolean {
    return this.projectFilter().has(project);
  }

  navigateToTask(entry: OrchestratorLogEntry): void {
    if (!entry.jobId || !entry.watchPath) return;
    this.openTask.emit({ jobId: entry.jobId, watchPath: entry.watchPath });
  }

  loadOlder(): void {
    this.historyWindow.loadOlder(this.olderEntryCount());
  }

  isSelected(entry: OrchestratorLogEntry): boolean {
    const selected = this.selectedEntry();
    return !!selected && selected.ts === entry.ts && selected.summary === entry.summary;
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString();
    } catch {
      return iso;
    }
  }

  formatDay(iso: string): string {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return iso;
    const today = new Date();
    if (date.toDateString() === today.toDateString()) return 'Today';
    return date.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
  }

  projectColor(project: string): string {
    return this.projectLookup.getProjectDisplay(project).color;
  }

  entryKey(entry: OrchestratorLogEntry): string {
    return [entry.project, entry.ts, entry.kind, entry.topic, entry.summary].join('\u0000');
  }

  startOverride(entry: OrchestratorLogEntry): void {
    this.overridingTs.set(entry.ts);
    this.overrideDraft = '';
  }

  cancelOverride(): void {
    this.overridingTs.set(null);
    this.overrideDraft = '';
  }

  submitOverride(entry: OrchestratorLogEntry): void {
    const direction = (this.overrideDraft ?? '').trim();
    if (!direction || !entry.jobId) return;
    this.submittingOverride.set(true);
    this.jobService.overrideOrchestratorEntry(entry.project || this.projectName(), {
      originalTs: entry.ts,
      jobId: entry.jobId,
      newDirection: direction
    }).subscribe({
      next: () => {
        this.submittingOverride.set(false);
        this.overridingTs.set(null);
        this.overrideDraft = '';
        // Refresh so the new intervention entry shows up.
        this.refresh(true);
      },
      error: (err) => {
        this.submittingOverride.set(false);
        const message = err?.error?.error || err?.message || 'Override failed';
        this.feedStore.reportError(message);
      }
    });
  }

  private dayKey(iso: string): string {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return iso;
    return `${date.getFullYear()}-${date.getMonth() + 1}-${date.getDate()}`;
  }

  private filterScope(projects: ReadonlySet<string>, kind: string): string {
    return `${[...projects].sort().join(',')}\u0000${kind}`;
  }
}

function newestFirst(left: string, right: string): number {
  const leftMs = Date.parse(left);
  const rightMs = Date.parse(right);
  if (Number.isFinite(leftMs) && Number.isFinite(rightMs) && leftMs !== rightMs) {
    return rightMs - leftMs;
  }
  return right.localeCompare(left);
}
