import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, HostListener, computed, effect, inject, input, model, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { TaskInfo } from '../../../../models/task.model';
import { BoardFiltersService } from '../../../board';
import { StudioTabStateService } from '../../services/studio-tab-state.service';
import { GlobalSearchItem, GlobalSearchService, SEARCH_DOMAINS, SearchDomain } from './global-search.service';

/** Debounce before a keystroke turns into a request. */
const DEBOUNCE_MS = 250;
/** After this long the palette says out loud that repository search takes a moment. */
const SLOW_NOTICE_MS = 2_000;
const TICK_MS = 100;

export type SearchDomainStatus = 'idle' | 'searching' | 'done' | 'failed';

/** What one domain row in the palette reports. */
export interface SearchDomainState {
  status: SearchDomainStatus;
  /** Repositories answered / total. Zero for the task domain, which has no fan-out. */
  completed: number;
  total: number;
  error: string | null;
}

const IDLE: SearchDomainState = { status: 'idle', completed: 0, total: 0, error: null };
const DOMAIN_LABELS: Readonly<Record<SearchDomain, string>> = {
  tasks: 'Tasks', dossiers: 'Dossiers', wiki: 'Wiki', commits: 'Commits', files: 'Files',
};

@Component({
  selector: 'app-global-search',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './global-search.component.html',
  styleUrl: './global-search.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GlobalSearchComponent {
  private readonly api = inject(GlobalSearchService);
  private readonly tabs = inject(StudioTabStateService);
  private readonly boardFilters = inject(BoardFiltersService);
  readonly tasks = input<readonly TaskInfo[]>([]);
  readonly open = model(false);
  readonly query = signal('');
  readonly remote = signal<
    Record<SearchDomain, GlobalSearchItem[]>
  >({ tasks: [], dossiers: [], wiki: [], commits: [], files: [] });
  readonly domains = signal<Record<SearchDomain, SearchDomainState>>(
    idleDomains());
  readonly domainOptions = SEARCH_DOMAINS.map(domain => ({ domain, label: DOMAIN_LABELS[domain] }));
  readonly enabledDomains = signal<Record<SearchDomain, boolean>>(
    { tasks: true, dossiers: true, wiki: true, commits: true, files: true });
  readonly elapsedMs = signal(0);
  readonly activeIndex = signal(0);
  readonly inputRef = viewChild<ElementRef<HTMLInputElement>>('searchInput');
  private readonly focusWhenOpened = effect(() => {
    if (this.open()) queueMicrotask(() => this.inputRef()?.nativeElement.focus());
  });
  private timer: ReturnType<typeof setTimeout> | null = null;
  private ticker: ReturnType<typeof setInterval> | null = null;
  private controller: AbortController | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.cancel());
  }

  readonly searching = computed(() => Object.values(this.domains()).some(state => state.status === 'searching'));
  /** A calm note, only once the wait is long enough that silence would read as a hang. */
  readonly showSlowNotice = computed(() => this.searching() && this.elapsedMs() >= SLOW_NOTICE_MS);
  readonly elapsedLabel = computed(() => `${(this.elapsedMs() / 1000).toFixed(1)}s`);

  /**
   * Board-snapshot matches first (they need no round trip at all), then the
   * indexed prompt and status matches the backend found, minus anything the
   * board already covers.
   */
  readonly taskResults = computed<GlobalSearchItem[]>(() => {
    const q = this.query().trim().toLowerCase();
    const keyQuery = normalizeKey(q);
    if (q.length < 2) return [];
    const local = this.tasks()
      .filter(task => (keyQuery.length > 0 && normalizeKey(task.key).includes(keyQuery))
        || [task.title, task.state].some(value => value?.toLowerCase().includes(q)))
      .sort((a, b) => Number(normalizeKey(b.key) === keyQuery) - Number(normalizeKey(a.key) === keyQuery))
      .slice(0, 20)
      .map(task => ({
        domain: 'tasks' as const, projectName: task.projectName, projectColor: this.projectColor(task.projectName),
        title: task.title, subtitle: task.key || task.id, taskKey: task.taskKey, lane: task.state,
        referenceKey: task.key ?? undefined,
      }));
    const seen = new Set<string | undefined>(local.map(item => item.taskKey));
    return [...local, ...this.remote().tasks.filter(item => !seen.has(item.taskKey))]
      .sort((left, right) => Number(normalizeKey(right.referenceKey) === keyQuery)
        - Number(normalizeKey(left.referenceKey) === keyQuery))
      .slice(0, 20);
  });

  readonly groups = computed(() => {
    const remote = this.remote();
    const enabled = this.enabledDomains();
    const groups = [
      { domain: 'tasks' as const, label: 'Tasks', items: this.taskResults() },
      { domain: 'dossiers' as const, label: 'Dossiers', items: remote.dossiers },
      { domain: 'wiki' as const, label: 'Wiki', items: remote.wiki },
      { domain: 'commits' as const, label: 'Commits', items: remote.commits },
      { domain: 'files' as const, label: 'Files', items: remote.files },
    ].filter(group => enabled[group.domain]);
    const keyQuery = normalizeKey(this.query());
    return groups.sort((left, right) =>
      Number(hasExactKey(right.items, keyQuery)) - Number(hasExactKey(left.items, keyQuery)));
  });
  readonly flatResults = computed(() => this.groups().flatMap(group => group.items));

  show(): void {
    this.open.set(true);
    queueMicrotask(() => this.inputRef()?.nativeElement.focus());
  }

  close(): void {
    this.open.set(false);
    this.query.set('');
    this.cancel();
    this.reset();
  }

  onQuery(value: string): void {
    this.query.set(value);
    this.activeIndex.set(0);
    // Every keystroke retires the search in flight: its results are for a query
    // the operator has already moved on from, and leaving it running keeps a
    // repository fan-out alive for nothing.
    this.cancel();

    const q = value.trim();
    if (q.length < 2) {
      this.reset();
      return;
    }
    this.timer = setTimeout(() => this.run(q), DEBOUNCE_MS);
  }

  toggleDomain(domain: SearchDomain): void {
    const enabledCount = Object.values(this.enabledDomains()).filter(Boolean).length;
    if (this.enabledDomains()[domain] && enabledCount === 1) return;
    this.enabledDomains.update(current => ({ ...current, [domain]: !current[domain] }));
    this.onQuery(this.query());
  }

  /** Stops the search in flight without closing the palette or dropping results. */
  cancel(): void {
    if (this.timer) { clearTimeout(this.timer); this.timer = null; }
    this.controller?.abort();
    this.controller = null;
    this.stopTicker();
    this.domains.update(current => mapDomains(current, state =>
      state.status === 'searching' ? { ...state, status: 'idle' } : state));
  }

  private reset(): void {
    this.remote.set({ tasks: [], dossiers: [], wiki: [], commits: [], files: [] });
    this.domains.set(idleDomains());
    this.elapsedMs.set(0);
  }

  private async run(query: string): Promise<void> {
    const controller = new AbortController();
    this.controller = controller;
    this.remote.set({ tasks: [], dossiers: [], wiki: [], commits: [], files: [] });
    const enabled = this.enabledDomains();
    this.domains.set(mapDomains(idleDomains(), (state, domain) =>
      enabled[domain] ? { ...state, status: 'searching' } : state));
    this.startTicker();

    try {
      const selected = SEARCH_DOMAINS.filter(domain => enabled[domain]);
      for await (const frame of this.api.stream(query, controller.signal, selected)) {
        if (controller.signal.aborted) return;
        if (frame.event === 'tasks') {
          this.remote.update(current => ({ ...current, tasks: frame.data.items }));
          this.patch('tasks', { status: frame.data.error ? 'failed' : 'done', error: frame.data.error });
        } else if (frame.event === 'dossiers') {
          this.remote.update(current => ({ ...current, dossiers: frame.data.items }));
          this.patch('dossiers', { status: frame.data.error ? 'failed' : 'done', error: frame.data.error });
        } else if (frame.event === 'wiki') {
          this.remote.update(current => ({ ...current, wiki: frame.data.items }));
          this.patch('wiki', { status: frame.data.error ? 'failed' : 'done', error: frame.data.error });
        } else if (frame.event === 'progress') {
          this.patchGit({ completed: frame.data.completed, total: frame.data.total });
        } else if (frame.event === 'repository') {
          // Append, never reorder: groups the operator can already read must not
          // jump under the cursor when a slower repository answers.
          this.remote.update(current => ({
            ...current,
            commits: [...current.commits, ...frame.data.commits],
            files: [...current.files, ...frame.data.files],
          }));
          const failed = new Set(frame.data.failedDomains);
          for (const domain of ['commits', 'files'] as const) {
            this.patch(domain, {
              completed: frame.data.completed,
              total: frame.data.total,
              error: failed.has(domain)
                ? `${frame.data.projectName} could not be searched.`
                : this.domains()[domain].error,
            });
          }
        } else {
          this.patchGit({ status: 'done' });
        }
      }
    } catch {
      if (controller.signal.aborted) return;
      this.domains.update(current => mapDomains(current, state =>
        state.status === 'searching'
          ? { ...state, status: 'failed', error: 'Search is temporarily unavailable.' }
          : state));
    } finally {
      if (this.controller === controller) {
        this.controller = null;
        this.stopTicker();
        this.domains.update(current => mapDomains(current, state =>
          state.status === 'searching' ? { ...state, status: 'done' } : state));
      }
    }
  }

  private patch(domain: SearchDomain, change: Partial<SearchDomainState>): void {
    this.domains.update(current => ({ ...current, [domain]: { ...current[domain], ...change } }));
  }

  private patchGit(change: Partial<SearchDomainState>): void {
    if (this.enabledDomains().commits) this.patch('commits', change);
    if (this.enabledDomains().files) this.patch('files', change);
  }

  private startTicker(): void {
    this.elapsedMs.set(0);
    const startedAt = Date.now();
    this.stopTicker();
    this.ticker = setInterval(() => this.elapsedMs.set(Date.now() - startedAt), TICK_MS);
  }

  private stopTicker(): void {
    if (!this.ticker) return;
    clearInterval(this.ticker);
    this.ticker = null;
  }

  choose(item: GlobalSearchItem): void {
    if (item.domain === 'tasks' && item.taskKey) {
      // The key alone addresses the card. Indexed matches can come from the
      // archive, which the board snapshot deliberately omits, so looking the
      // task up in it first would silently swallow those results.
      this.tabs.open({ kind: 'task', taskKey: item.taskKey });
    } else if (item.domain === 'dossiers' && item.workbenchId) {
      this.tabs.open({
        kind: 'workbench', projectName: item.projectName, workbenchId: item.workbenchId,
        ...(item.projectId ? { projectId: item.projectId } : {}), title: item.title, key: item.dossierKey,
      });
    } else if (item.domain === 'commits' && item.sha) {
      this.boardFilters.setSoleProject(item.projectName);
      this.tabs.open({ kind: 'diff', commitSha: item.sha });
    } else if (item.domain === 'wiki' || item.domain === 'files') {
      const isWiki = item.domain === 'wiki' || item.isWiki;
      const wikiPath = isWiki ? item.path?.replace(/^docs\//i, '') : null;
      this.tabs.open({
        kind: 'hub',
        projectName: item.projectName,
        section: isWiki ? 'wiki' : 'git',
        ...(wikiPath ? { wikiTarget: { kind: 'page' as const, relPath: wikiPath } } : {}),
      });
    }
    this.close();
  }

  @HostListener('document:keydown', ['$event'])
  onDocumentKeydown(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      if (this.open()) this.close();
      else this.show();
      return;
    }
    if (!this.open()) return;
    // Escape closes, and closing cancels: one press both retires the search in
    // flight and dismisses the palette, so Escape never leaves work running.
    if (event.key === 'Escape') { event.preventDefault(); this.close(); return; }
    const results = this.flatResults();
    if (event.key === 'ArrowDown' && results.length) {
      event.preventDefault(); this.activeIndex.update(i => (i + 1) % results.length);
    } else if (event.key === 'ArrowUp' && results.length) {
      event.preventDefault(); this.activeIndex.update(i => (i - 1 + results.length) % results.length);
    } else if (event.key === 'Enter' && results[this.activeIndex()]) {
      event.preventDefault(); this.choose(results[this.activeIndex()]);
    }
  }

  resultIndex(item: GlobalSearchItem): number { return this.flatResults().indexOf(item); }

  updatedLabel(item: GlobalSearchItem): string | null {
    if (!item.updatedAt) return null;
    const value = new Date(item.updatedAt);
    return Number.isNaN(value.getTime()) ? item.updatedAt : value.toLocaleDateString();
  }

  private projectColor(name: string): string {
    let hash = 0;
    for (const char of name) hash = ((hash << 5) - hash + char.charCodeAt(0)) | 0;
    return `hsl(${Math.abs(hash) % 360} 58% 48%)`;
  }
}

function mapDomains(
  current: Record<SearchDomain, SearchDomainState>,
  change: (state: SearchDomainState, domain: SearchDomain) => SearchDomainState,
): Record<SearchDomain, SearchDomainState> {
  return {
    tasks: change(current.tasks, 'tasks'),
    dossiers: change(current.dossiers, 'dossiers'),
    wiki: change(current.wiki, 'wiki'),
    commits: change(current.commits, 'commits'),
    files: change(current.files, 'files'),
  };
}

function idleDomains(): Record<SearchDomain, SearchDomainState> {
  return { tasks: IDLE, dossiers: IDLE, wiki: IDLE, commits: IDLE, files: IDLE };
}

function normalizeKey(value: string | null | undefined): string {
  return (value ?? '').replace(/[^a-z0-9]/gi, '').toLowerCase();
}

function hasExactKey(items: readonly GlobalSearchItem[], query: string): boolean {
  return query.length > 0 && items.some(item => normalizeKey(item.referenceKey) === query);
}
