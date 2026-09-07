import { ChangeDetectionStrategy, Component, ElementRef, HostListener, OnDestroy, computed, effect, inject, input, model, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { Subscription } from 'rxjs';
import type { TaskInfo } from '../../../../models/task.model';
import { BoardFiltersService } from '../../../board';
import { StudioTabStateService } from '../../services/studio-tab-state.service';
import { GlobalSearchItem, GlobalSearchService, GlobalSearchStreamEvent, SearchDomain } from './global-search.service';

/** What a domain row reports while the palette is open. */
export type SearchPhase = 'idle' | 'searching' | 'done' | 'failed' | 'cancelled';

export interface SearchDomainState {
  phase: SearchPhase;
  count: number;
  repositoriesDone: number;
  repositoriesTotal: number;
  error: string | null;
}

/** Keystrokes settle before a search starts. */
const DEBOUNCE_MS = 250;
/** After this long the palette says repository search can take a moment. */
const PATIENCE_MS = 2_000;
const TICK_MS = 200;
const MIN_QUERY_LENGTH = 2;
/**
 * Rows rendered per git domain. Every repository contributes up to the backend
 * limit, so a fifteen-repository workspace can stream several hundred matches;
 * rendering them all would make keyboard navigation quadratic. The status row
 * still reports everything that was found.
 */
const MAX_ROWS_PER_DOMAIN = 30;

/**
 * The stream carries one settle signal for the git domains, but only the domain
 * that actually failed may read as failed. A domain that answered while its
 * sibling failed is done, not silent.
 */
function settledPhase(phase: SearchPhase, error: string | null): SearchPhase {
  if (error) return 'failed';
  return phase === 'failed' ? 'done' : phase;
}

@Component({
  selector: 'app-global-search',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './global-search.component.html',
  styleUrl: './global-search.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GlobalSearchComponent implements OnDestroy {
  private readonly api = inject(GlobalSearchService);
  private readonly tabs = inject(StudioTabStateService);
  private readonly boardFilters = inject(BoardFiltersService);
  readonly tasks = input<readonly TaskInfo[]>([]);
  readonly open = model(false);
  readonly query = signal('');
  readonly activeIndex = signal(0);
  readonly inputRef = viewChild<ElementRef<HTMLInputElement>>('searchInput');

  /** Server-side matches, appended per domain as the stream delivers them. */
  private readonly remoteTasks = signal<GlobalSearchItem[]>([]);
  private readonly remoteCommits = signal<GlobalSearchItem[]>([]);
  private readonly remoteFiles = signal<GlobalSearchItem[]>([]);
  private readonly taskPhase = signal<SearchPhase>('idle');
  private readonly gitPhase = signal<SearchPhase>('idle');
  private readonly repositoriesDone = signal(0);
  private readonly repositoriesTotal = signal(0);
  private readonly domainErrors = signal<Partial<Record<SearchDomain, string>>>({});
  readonly elapsedMs = signal(0);

  private readonly focusWhenOpened = effect(() => {
    if (this.open()) queueMicrotask(() => this.inputRef()?.nativeElement.focus());
  });
  private debounceTimer: ReturnType<typeof setTimeout> | null = null;
  private ticker: ReturnType<typeof setInterval> | null = null;
  private stream: Subscription | null = null;
  private startedAt = 0;

  /**
   * Board-snapshot matches. They need no network, so they paint on the first
   * keystroke and anchor the Tasks group; server matches (which also cover
   * prompt and status text and the archive) append below them.
   */
  readonly localTaskResults = computed<GlobalSearchItem[]>(() => {
    const q = this.query().trim().toLowerCase();
    if (q.length < MIN_QUERY_LENGTH) return [];
    return this.tasks()
      .filter(task => [task.key, task.title, task.state].some(value => value?.toLowerCase().includes(q)))
      .sort((a, b) => Number(b.key?.toLowerCase() === q) - Number(a.key?.toLowerCase() === q))
      .slice(0, 20)
      .map(task => ({
        domain: 'tasks' as const, projectName: task.projectName, projectColor: this.projectColor(task.projectName),
        title: task.title, subtitle: task.key || task.id, taskKey: task.taskKey, lane: task.state,
      }));
  });

  readonly taskResults = computed<GlobalSearchItem[]>(() => {
    const local = this.localTaskResults();
    const seen = new Set(local.map(item => item.taskKey).filter(Boolean));
    return [...local, ...this.remoteTasks().filter(item => !item.taskKey || !seen.has(item.taskKey))].slice(0, 20);
  });

  readonly searching = computed(() => this.taskPhase() === 'searching' || this.gitPhase() === 'searching');
  readonly showPatienceNote = computed(() => this.searching() && this.elapsedMs() >= PATIENCE_MS);

  readonly groups = computed(() => [
    { domain: 'tasks' as const, label: 'Tasks', items: this.taskResults(), state: this.taskState() },
    {
      domain: 'commits' as const, label: 'Commits',
      items: this.remoteCommits().slice(0, MAX_ROWS_PER_DOMAIN), state: this.gitState('commits'),
    },
    {
      domain: 'files' as const, label: 'Files',
      items: this.remoteFiles().slice(0, MAX_ROWS_PER_DOMAIN), state: this.gitState('files'),
    },
  ]);
  readonly flatResults = computed(() => this.groups().flatMap(group => group.items));

  ngOnDestroy(): void {
    this.cancelInFlight();
  }

  show(): void {
    this.open.set(true);
    queueMicrotask(() => this.inputRef()?.nativeElement.focus());
  }

  close(): void {
    this.cancelInFlight();
    this.open.set(false);
    this.query.set('');
    this.resetResults('idle');
  }

  onQuery(value: string): void {
    this.query.set(value);
    this.activeIndex.set(0);
    // Every keystroke aborts the request already in flight; without this the
    // palette races stale responses and keeps paying for abandoned queries.
    this.cancelInFlight();
    if (this.debounceTimer) clearTimeout(this.debounceTimer);
    const q = value.trim();
    if (q.length < MIN_QUERY_LENGTH) {
      this.resetResults('idle');
      return;
    }
    this.resetResults('searching');
    this.startedAt = Date.now();
    this.elapsedMs.set(0);
    this.ticker = setInterval(() => this.elapsedMs.set(Date.now() - this.startedAt), TICK_MS);
    this.debounceTimer = setTimeout(() => {
      this.stream = this.api.searchStream(q).subscribe({
        next: event => this.applyFrame(event),
        error: () => this.failRemaining('Search is temporarily unavailable.'),
      });
    }, DEBOUNCE_MS);
  }

  /** Stops the in-flight search and leaves whatever already arrived on screen. */
  cancelSearch(): void {
    if (!this.searching()) return;
    this.cancelInFlight();
    if (this.taskPhase() === 'searching') this.taskPhase.set('cancelled');
    if (this.gitPhase() === 'searching') this.gitPhase.set('cancelled');
  }

  private applyFrame(event: GlobalSearchStreamEvent): void {
    switch (event.kind) {
      case 'meta':
        this.repositoriesTotal.set(event.payload.repositories);
        break;
      case 'tasks':
        this.remoteTasks.set(event.payload.items);
        if (event.payload.error) this.recordError('tasks', event.payload.error);
        this.taskPhase.set(event.payload.error ? 'failed' : 'done');
        break;
      case 'repository':
        this.repositoriesDone.set(event.payload.index);
        this.repositoriesTotal.set(event.payload.total);
        // Append, never re-sort: the rows the operator is already reading must
        // not jump when the next repository answers.
        if (event.payload.commits.length) this.remoteCommits.update(items => [...items, ...event.payload.commits]);
        if (event.payload.files.length) this.remoteFiles.update(items => [...items, ...event.payload.files]);
        if (event.payload.commitsError) this.recordError('commits', `${event.payload.name}: ${event.payload.commitsError}`);
        if (event.payload.filesError) this.recordError('files', `${event.payload.name}: ${event.payload.filesError}`);
        break;
      case 'done':
        // The closing summary is generic; a repository frame that already named
        // the repository it failed on is the better message to keep.
        for (const [domain, message] of Object.entries(event.payload.errors))
          if (!this.domainErrors()[domain as SearchDomain]) this.recordError(domain as SearchDomain, message);
        this.settle(Object.keys(event.payload.errors).length > 0 ? 'failed' : 'done');
        break;
      case 'failed':
        this.failRemaining('Search is temporarily unavailable.');
        break;
    }
  }

  private settle(phase: SearchPhase): void {
    this.stopTicker();
    if (this.taskPhase() === 'searching') this.taskPhase.set(phase);
    this.gitPhase.set(phase);
  }

  /**
   * The stream itself died, so no domain can be trusted to be complete. Both
   * git rows must say so: a row left without an error would settle to "0
   * results" and read as an answer.
   */
  private failRemaining(message: string): void {
    this.recordError('commits', message);
    this.recordError('files', message);
    if (this.taskPhase() === 'searching') this.recordError('tasks', message);
    this.settle('failed');
  }

  private recordError(domain: SearchDomain, message: string): void {
    this.domainErrors.update(errors => ({ ...errors, [domain]: message }));
  }

  private taskState(): SearchDomainState {
    const error = this.domainErrors().tasks ?? null;
    return {
      phase: settledPhase(this.taskPhase(), error),
      count: this.taskResults().length,
      repositoriesDone: 0,
      repositoriesTotal: 0,
      error,
    };
  }

  private gitState(domain: 'commits' | 'files'): SearchDomainState {
    const error = this.domainErrors()[domain] ?? null;
    return {
      phase: settledPhase(this.gitPhase(), error),
      count: domain === 'commits' ? this.remoteCommits().length : this.remoteFiles().length,
      repositoriesDone: this.repositoriesDone(),
      repositoriesTotal: this.repositoriesTotal(),
      error,
    };
  }

  /** The line under a group heading: what that domain is doing right now. */
  statusLabel(state: SearchDomainState): string {
    if (state.error) return state.error;
    switch (state.phase) {
      case 'searching':
        return state.repositoriesTotal > 0
          ? `Searching ${state.repositoriesDone} of ${state.repositoriesTotal} repositories…`
          : 'Searching…';
      case 'cancelled':
        return 'Cancelled.';
      case 'done':
        if (state.count === 1) return '1 result';
        // Never let a cap read as "that is all there was".
        return state.count > MAX_ROWS_PER_DOMAIN
          ? `${state.count} results, showing the first ${MAX_ROWS_PER_DOMAIN}`
          : `${state.count} results`;
      default:
        return '';
    }
  }

  elapsedLabel(): string {
    return `${(this.elapsedMs() / 1000).toFixed(1)}s`;
  }

  private cancelInFlight(): void {
    if (this.debounceTimer) {
      clearTimeout(this.debounceTimer);
      this.debounceTimer = null;
    }
    this.stream?.unsubscribe();
    this.stream = null;
    this.stopTicker();
  }

  private stopTicker(): void {
    if (!this.ticker) return;
    clearInterval(this.ticker);
    this.ticker = null;
  }

  private resetResults(phase: SearchPhase): void {
    this.remoteTasks.set([]);
    this.remoteCommits.set([]);
    this.remoteFiles.set([]);
    this.domainErrors.set({});
    this.repositoriesDone.set(0);
    this.repositoriesTotal.set(0);
    this.taskPhase.set(phase);
    this.gitPhase.set(phase);
    if (phase !== 'searching') this.elapsedMs.set(0);
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
      // Escape stops a running search first so the operator can keep the
      // results that already arrived; a second Escape closes the palette.
      if (this.searching()) this.cancelSearch();
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

  private projectColor(name: string): string {
    let hash = 0;
    for (const char of name) hash = ((hash << 5) - hash + char.charCodeAt(0)) | 0;
    return `hsl(${Math.abs(hash) % 360} 58% 48%)`;
  }
}
