import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, HostListener, computed, effect, inject, input, model, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { TaskInfo } from '../../../../models/task.model';
import { BoardFiltersService } from '../../../board';
import { StudioTabStateService } from '../../services/studio-tab-state.service';
import { GlobalSearchChunk, GlobalSearchItem, GlobalSearchService, SEARCH_DOMAINS, SearchDomain } from './global-search.service';

/** Long enough that typing a task key is one request, short enough to feel live. */
const DEBOUNCE_MS = 250;
/** After this much waiting the palette explains that repository search takes a moment. */
const SLOW_NOTICE_MS = 2_000;
const ELAPSED_TICK_MS = 100;
const RESULT_LIMIT = 20;

type DomainStatus = 'idle' | 'searching' | 'done' | 'failed';

interface DomainState {
  status: DomainStatus;
  /** Repositories answered so far, and how many there are. Both 0 for tasks. */
  completed: number;
  total: number;
  error: string | null;
}

type DomainStates = Record<SearchDomain, DomainState>;
type DomainItems = Record<SearchDomain, GlobalSearchItem[]>;

const DOMAIN_LABELS: Record<SearchDomain, string> = { tasks: 'Tasks', commits: 'Commits', files: 'Files' };

const states = (status: DomainStatus): DomainStates => ({
  tasks: { status, completed: 0, total: 0, error: null },
  commits: { status, completed: 0, total: 0, error: null },
  files: { status, completed: 0, total: 0, error: null },
});
const noItems = (): DomainItems => ({ tasks: [], commits: [], files: [] });
const mapStates = (current: DomainStates, project: (state: DomainState) => DomainState): DomainStates => ({
  tasks: project(current.tasks), commits: project(current.commits), files: project(current.files),
});

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
  readonly remote = signal<DomainItems>(noItems());
  readonly domains = signal<DomainStates>(states('idle'));
  readonly elapsedMs = signal(0);
  readonly activeIndex = signal(0);
  readonly inputRef = viewChild<ElementRef<HTMLInputElement>>('searchInput');
  private readonly focusWhenOpened = effect(() => {
    if (this.open()) queueMicrotask(() => this.inputRef()?.nativeElement.focus());
  });
  private timer: ReturnType<typeof setTimeout> | null = null;
  private clock: ReturnType<typeof setInterval> | null = null;
  private controller: AbortController | null = null;
  private startedAt = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  readonly searching = computed(() => SEARCH_DOMAINS.some(domain => this.domains()[domain].status === 'searching'));
  readonly slowNotice = computed(() => this.searching() && this.elapsedMs() >= SLOW_NOTICE_MS);
  readonly elapsedLabel = computed(() => `${(this.elapsedMs() / 1000).toFixed(1)}s`);

  readonly taskResults = computed<GlobalSearchItem[]>(() => {
    const q = this.query().trim().toLowerCase();
    if (q.length < 2) return [];
    const local = this.tasks()
      .filter(task => [task.key, task.title, task.state].some(value => value?.toLowerCase().includes(q)))
      .sort((a, b) => Number(b.key?.toLowerCase() === q) - Number(a.key?.toLowerCase() === q))
      .slice(0, RESULT_LIMIT)
      .map(task => ({
        domain: 'tasks' as const, projectName: task.projectName, projectColor: this.projectColor(task.projectName),
        title: task.title, subtitle: task.key || task.id, taskKey: task.taskKey, lane: task.state,
      }));
    // The board snapshot answers instantly but only knows key, title, and lane.
    // The indexed server search also reads prompt and status text, so it finds
    // cards the snapshot cannot; those are appended, never reordered in.
    const shown = new Set(local.map(item => item.taskKey));
    const extra = this.remote().tasks.filter(item => !item.taskKey || !shown.has(item.taskKey));
    return [...local, ...extra].slice(0, RESULT_LIMIT);
  });

  readonly groups = computed(() => SEARCH_DOMAINS.map(domain => ({
    domain,
    label: DOMAIN_LABELS[domain],
    items: domain === 'tasks' ? this.taskResults() : this.remote()[domain],
    state: this.domains()[domain],
  })));
  readonly flatResults = computed(() => this.groups().flatMap(group => group.items));

  show(): void {
    this.open.set(true);
    queueMicrotask(() => this.inputRef()?.nativeElement.focus());
  }

  close(): void {
    this.open.set(false);
    this.query.set('');
    this.stop();
    this.remote.set(noItems());
    this.domains.set(states('idle'));
  }

  /** Drops an in-flight search without closing the palette. */
  cancel(): void {
    this.stop();
    this.domains.update(current => mapStates(current, state =>
      state.status === 'searching' ? { ...state, status: 'idle' } : state));
  }

  onQuery(value: string): void {
    this.query.set(value);
    this.activeIndex.set(0);
    // Every keystroke supersedes the previous request, so stop paying for it.
    this.stop();
    this.remote.set(noItems());
    const query = value.trim();
    if (query.length < 2) {
      this.domains.set(states('idle'));
      return;
    }
    this.domains.set(states('searching'));
    this.startClock();
    this.timer = setTimeout(() => void this.run(query), DEBOUNCE_MS);
  }

  /** Per-domain status line: "12" when done, "3 of 15 repositories" while sweeping. */
  progressDetail(domain: SearchDomain, count: number): string {
    const state = this.domains()[domain];
    if (state.status === 'failed') return state.error ?? 'failed';
    if (state.status === 'done') return `${count}`;
    if (state.status === 'idle') return 'cancelled';
    return state.total > 0 ? `${state.completed} of ${state.total} repositories` : 'searching';
  }

  choose(item: GlobalSearchItem): void {
    if (item.domain === 'tasks' && item.taskKey) {
      const task = this.tasks().find(candidate => candidate.taskKey === item.taskKey);
      if (task) {
        this.tabs.open({ kind: 'task', taskKey: task.taskKey });
      }
    } else if (item.domain === 'commits' && item.sha) {
      this.boardFilters.setSoleProject(item.projectName);
      this.tabs.open({ kind: 'diff', commitSha: item.sha });
    } else if (item.domain === 'files') {
      const wikiPath = item.isWiki ? item.path?.replace(/^docs\//i, '') : null;
      this.tabs.open({
        kind: 'hub',
        projectName: item.projectName,
        section: item.isWiki ? 'wiki' : 'git',
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
    if (event.key === 'Escape') {
      event.preventDefault();
      // Escape cancels the running sweep first; a second Escape closes.
      if (this.searching()) this.cancel();
      else this.close();
      return;
    }
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

  private async run(query: string): Promise<void> {
    const controller = new AbortController();
    this.controller = controller;
    try {
      await this.api.searchStream(query, RESULT_LIMIT, controller.signal, chunk => {
        if (this.controller === controller) this.applyChunk(chunk);
      });
      if (this.controller === controller) this.settle('done');
    } catch {
      // An aborted or superseded request already cleared this.controller, so it
      // resolves as a cancellation rather than as a failed search.
      if (this.controller === controller) this.settle('failed');
    }
  }

  private applyChunk(chunk: GlobalSearchChunk): void {
    if (chunk.items.length) {
      this.remote.update(current => ({ ...current, [chunk.domain]: [...current[chunk.domain], ...chunk.items] }));
    }
    this.domains.update(current => {
      const previous = current[chunk.domain];
      const error = chunk.error ?? previous.error;
      const completed = Math.max(previous.completed, chunk.completed);
      const total = Math.max(previous.total, chunk.total);
      const finished = completed >= total;
      return {
        ...current,
        [chunk.domain]: {
          status: finished ? (error ? 'failed' : 'done') : 'searching',
          completed, total, error,
        },
      };
    });
  }

  private settle(outcome: 'done' | 'failed'): void {
    this.stop();
    this.domains.update(current => mapStates(current, state => state.status !== 'searching' ? state : {
      ...state,
      status: outcome === 'done' && !state.error ? 'done' : 'failed',
      error: outcome === 'failed' ? state.error ?? 'Search is temporarily unavailable.' : state.error,
      completed: outcome === 'done' ? state.total : state.completed,
    }));
  }

  private startClock(): void {
    this.startedAt = Date.now();
    this.elapsedMs.set(0);
    this.clock ??= setInterval(() => this.elapsedMs.set(Date.now() - this.startedAt), ELAPSED_TICK_MS);
  }

  /** Cancels the debounce, the in-flight request, and the elapsed timer. */
  private stop(): void {
    if (this.timer) { clearTimeout(this.timer); this.timer = null; }
    if (this.clock) { clearInterval(this.clock); this.clock = null; }
    this.controller?.abort();
    this.controller = null;
  }

  private projectColor(name: string): string {
    let hash = 0;
    for (const char of name) hash = ((hash << 5) - hash + char.charCodeAt(0)) | 0;
    return `hsl(${Math.abs(hash) % 360} 58% 48%)`;
  }
}
