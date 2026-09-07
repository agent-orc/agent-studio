import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, HostListener, computed, effect, inject, input, model, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { TaskInfo } from '../../../../models/task.model';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { BoardFiltersService } from '../../../board';
import { StudioTabStateService } from '../../services/studio-tab-state.service';
import { GlobalSearchItem, GlobalSearchService, SEARCH_DOMAINS, SearchDomain } from './global-search.service';

/** Per-domain delivery state, rendered as one status row per group. */
export interface SearchDomainState {
  status: 'idle' | 'searching' | 'done';
  completed: number;
  total: number;
  error: string | null;
}

type DomainMap<T> = Record<SearchDomain, T>;

const IDLE: SearchDomainState = { status: 'idle', completed: 0, total: 0, error: null };
const DEBOUNCE_MS = 250;
/** After this long the palette explains itself instead of just spinning. */
const PATIENCE_NOTE_MS = 2000;
const MAX_PER_DOMAIN = 20;

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
  readonly remote = signal<DomainMap<GlobalSearchItem[]>>(emptyItems());
  readonly domainState = signal<DomainMap<SearchDomainState>>(idleDomains());
  readonly elapsedMs = signal(0);
  readonly activeIndex = signal(0);
  readonly inputRef = viewChild<ElementRef<HTMLInputElement>>('searchInput');
  private readonly focusWhenOpened = effect(() => {
    if (this.open()) queueMicrotask(() => this.inputRef()?.nativeElement.focus());
  });
  private debounceTimer: ReturnType<typeof setTimeout> | null = null;
  private elapsedTimer: ReturnType<typeof setInterval> | null = null;
  private controller: AbortController | null = null;
  private readonly modalStack = inject(ModalStackService);
  private modalStackDispose: (() => void) | null = null;

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => this.cancel());
    // Escape is arbitrated by the central modal stack, which consumes the
    // keystroke on the document capture phase whenever anything is stacked.
    // Without an entry of its own the palette's own document listener never
    // ran, so Escape did nothing whenever any other overlay was open.
    effect(() => {
      if (this.open()) {
        this.modalStackDispose ??= this.modalStack.pushUntilDestroyed(
          'global-search', () => { this.close(); return true; }, destroyRef);
      } else {
        this.modalStackDispose?.();
        this.modalStackDispose = null;
      }
    });
  }

  readonly searching = computed(() =>
    SEARCH_DOMAINS.some(domain => this.domainState()[domain].status === 'searching'));

  /** The board is already in memory, so identifier hits render on the keystroke. */
  private readonly localTaskResults = computed<GlobalSearchItem[]>(() => {
    const q = this.query().trim().toLowerCase();
    if (q.length < 2) return [];
    return this.tasks()
      .filter(task => [task.key, task.title, task.state].some(value => value?.toLowerCase().includes(q)))
      .sort((a, b) => Number(b.key?.toLowerCase() === q) - Number(a.key?.toLowerCase() === q))
      .slice(0, MAX_PER_DOMAIN)
      .map(task => ({
        domain: 'tasks' as const, projectName: task.projectName, projectColor: this.projectColor(task.projectName),
        title: task.title, subtitle: task.key || task.id, taskKey: task.taskKey, lane: task.state,
      }));
  });

  /**
   * Board hits first, then the indexed hits the server found that the board
   * does not hold (archived cards, prompt and status body text). Server results
   * are appended rather than merged so a row never moves under the cursor
   * while the operator is reaching for it.
   */
  readonly taskResults = computed<GlobalSearchItem[]>(() => {
    const local = this.localTaskResults();
    const seen = new Set(local.map(item => item.taskKey));
    const extra = this.remote().tasks.filter(item => !item.taskKey || !seen.has(item.taskKey));
    return [...local, ...extra].slice(0, MAX_PER_DOMAIN);
  });

  readonly groups = computed(() => [
    { domain: 'tasks' as const, label: 'Tasks', items: this.taskResults() },
    { domain: 'commits' as const, label: 'Commits', items: this.remote().commits },
    { domain: 'files' as const, label: 'Files', items: this.remote().files },
  ]);
  readonly flatResults = computed(() => this.groups().flatMap(group => group.items));

  /** Repository search is slow on a long history; say so rather than spin silently. */
  readonly showPatienceNote = computed(() =>
    this.searching() && this.elapsedMs() >= PATIENCE_NOTE_MS);

  show(): void {
    this.open.set(true);
    queueMicrotask(() => this.inputRef()?.nativeElement.focus());
  }

  close(): void {
    this.cancel();
    this.open.set(false);
    this.query.set('');
    this.reset();
  }

  onQuery(value: string): void {
    this.query.set(value);
    this.activeIndex.set(0);
    this.cancel();
    const q = value.trim();
    if (q.length < 2) {
      this.reset();
      return;
    }
    // Mark every domain as searching for the debounce window too. Otherwise the
    // rows keep the previous query's counts - "3 of 5 repositories" against a
    // term that is no longer being searched - until the request actually starts.
    this.domainState.set(searchingDomains());
    this.elapsedMs.set(0);
    this.debounceTimer = setTimeout(() => this.run(q), DEBOUNCE_MS);
  }

  /** Status line for one group: how far it got, or why it did not. */
  statusLabel(domain: SearchDomain): string {
    const state = this.domainState()[domain];
    const count = this.groups().find(group => group.domain === domain)?.items.length ?? 0;
    if (state.status === 'idle') return '';
    if (state.status === 'done') return `${count} ${count === 1 ? 'result' : 'results'}`;
    if (domain === 'tasks') return 'Searching…';
    return state.total > 0 ? `${state.completed} of ${state.total} repositories` : 'Searching…';
  }

  private run(q: string): void {
    const controller = new AbortController();
    this.controller = controller;
    this.remote.set(emptyItems());
    this.domainState.set(searchingDomains());
    this.elapsedMs.set(0);
    const startedAt = Date.now();
    this.elapsedTimer = setInterval(() => this.elapsedMs.set(Date.now() - startedAt), 100);

    this.api.stream(q, {
      start: event => this.patchAll(state => ({ ...state, total: event.repositories })),
      chunk: event => {
        this.remote.update(current => ({
          ...current,
          [event.domain]: [...current[event.domain], ...event.items].slice(0, MAX_PER_DOMAIN),
        }));
        // The task domain is answered in one shot; git domains close on progress.
        if (event.domain === 'tasks') this.patch('tasks', state => ({ ...state, status: 'done' }));
      },
      progress: event => this.patch(event.domain, state => ({
        ...state,
        completed: event.completed,
        total: event.total,
        status: event.completed >= event.total ? 'done' : 'searching',
      })),
      failure: event => this.patch(event.domain, state => ({ ...state, error: event.message })),
      done: () => this.finish(controller),
    }, controller.signal, MAX_PER_DOMAIN)
      .catch(() => {
        if (controller.signal.aborted) return;
        this.patchAll(state => ({ ...state, error: 'Search is temporarily unavailable.' }));
      })
      // A stream that ends without a `done` frame - a dropped connection, a
      // restarted backend - must still stop the clock. Otherwise the palette
      // spins forever on a search that is already over.
      .finally(() => this.finish(controller));
  }

  private finish(controller: AbortController): void {
    if (this.controller !== controller) return;
    this.stopElapsed();
    this.patchAll(state => (state.status === 'searching' ? { ...state, status: 'done' } : state));
  }

  /** Stops the debounce, the in-flight request, and the elapsed clock. */
  private cancel(): void {
    if (this.debounceTimer) clearTimeout(this.debounceTimer);
    this.debounceTimer = null;
    this.controller?.abort();
    this.controller = null;
    this.stopElapsed();
  }

  private stopElapsed(): void {
    if (this.elapsedTimer) clearInterval(this.elapsedTimer);
    this.elapsedTimer = null;
  }

  private reset(): void {
    this.remote.set(emptyItems());
    this.domainState.set(idleDomains());
    this.elapsedMs.set(0);
  }

  private patch(domain: SearchDomain, change: (state: SearchDomainState) => SearchDomainState): void {
    this.domainState.update(current => ({ ...current, [domain]: change(current[domain]) }));
  }

  private patchAll(change: (state: SearchDomainState) => SearchDomainState): void {
    this.domainState.update(current => ({
      tasks: change(current.tasks), commits: change(current.commits), files: change(current.files),
    }));
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

  private projectColor(name: string): string {
    let hash = 0;
    for (const char of name) hash = ((hash << 5) - hash + char.charCodeAt(0)) | 0;
    return `hsl(${Math.abs(hash) % 360} 58% 48%)`;
  }
}

function emptyItems(): DomainMap<GlobalSearchItem[]> {
  return { tasks: [], commits: [], files: [] };
}

function idleDomains(): DomainMap<SearchDomainState> {
  return { tasks: { ...IDLE }, commits: { ...IDLE }, files: { ...IDLE } };
}

function searchingDomains(): DomainMap<SearchDomainState> {
  const searching: SearchDomainState = { status: 'searching', completed: 0, total: 0, error: null };
  return { tasks: { ...searching }, commits: { ...searching }, files: { ...searching } };
}
