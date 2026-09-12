import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import type { WorkbenchOverviewItem } from '../../../../models/project-docs.model';
import { routeSegmentOf, withRouteSegment } from '../../../../services/url-hash.util';
import { NowTickService } from '../../../../services/now-tick.service';

export type WorkbenchSortKey =
  | 'default'
  | 'status'
  | 'updatedAt'
  | 'project'
  | 'key'
  | 'openDecisions'
  | 'reviewAge';
export type WorkbenchSortDirection = 'asc' | 'desc';
export type WorkbenchReviewFilter = 'all' | 'never-reviewed' | 'older-30' | 'current' | 'partially-superseded' | 'superseded' | 'historical';

export interface WorkbenchOverviewViewState {
  query: string;
  sortKey: WorkbenchSortKey;
  direction: WorkbenchSortDirection;
  reviewFilter: WorkbenchReviewFilter;
}

export const WORKBENCH_SORT_OPTIONS: readonly {
  key: Exclude<WorkbenchSortKey, 'default'>;
  label: string;
}[] = [
  { key: 'status', label: 'Status' },
  { key: 'updatedAt', label: 'Last movement' },
  { key: 'project', label: 'Project' },
  { key: 'key', label: 'Key' },
  { key: 'openDecisions', label: 'Open decisions' },
  { key: 'reviewAge', label: 'Review age' },
];

const STORAGE_KEY = 'atp.studio.workbenchOverview.view.v1';
const DEFAULT_STATE: WorkbenchOverviewViewState = {
  query: '',
  sortKey: 'default',
  direction: 'desc',
  reviewFilter: 'all',
};
const DEFAULT_DIRECTIONS: Record<Exclude<WorkbenchSortKey, 'default'>, WorkbenchSortDirection> = {
  status: 'asc',
  updatedAt: 'desc',
  project: 'asc',
  key: 'asc',
  openDecisions: 'desc',
  reviewAge: 'desc',
};

@Injectable()
export class WorkbenchOverviewViewStateService {
  private readonly destroyRef = inject(DestroyRef);
  private readonly collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });
  private readonly now = inject(NowTickService).now;
  private scopeKey = 'all';

  readonly query = signal('');
  readonly sortKey = signal<WorkbenchSortKey>('default');
  readonly direction = signal<WorkbenchSortDirection>('desc');
  readonly reviewFilter = signal<WorkbenchReviewFilter>('all');

  constructor() {
    const onHashChange = () => this.hydrate();
    globalThis.addEventListener?.('hashchange', onHashChange);
    this.destroyRef.onDestroy(() => globalThis.removeEventListener?.('hashchange', onHashChange));
  }

  setScope(projectName: string | null): void {
    this.scopeKey = projectName ? `project:${projectName}` : 'all';
    this.hydrate();
  }

  setQuery(value: string): void {
    this.query.set(value);
    this.commit();
  }

  setReviewFilter(value: WorkbenchReviewFilter): void {
    this.reviewFilter.set(value);
    this.commit();
  }

  selectSort(key: Exclude<WorkbenchSortKey, 'default'>): void {
    if (this.sortKey() === key) {
      this.direction.update(value => value === 'asc' ? 'desc' : 'asc');
    } else {
      this.sortKey.set(key);
      this.direction.set(DEFAULT_DIRECTIONS[key]);
    }
    this.commit();
  }

  reset(): void {
    this.apply(DEFAULT_STATE);
    this.commit();
  }

  hasActiveState(): boolean {
    return this.query().trim().length > 0 || this.sortKey() !== 'default' || this.reviewFilter() !== 'all';
  }

  filter(items: readonly WorkbenchOverviewItem[], statusLabel: (item: WorkbenchOverviewItem) => string): WorkbenchOverviewItem[] {
    const query = this.query().trim().toLocaleLowerCase();
    return items.filter(item => {
      const review = item.workbench.review;
      const reviewFilter = this.reviewFilter();
      const matchesReview = reviewFilter === 'all'
        || reviewFilter === 'never-reviewed' && !review
        || reviewFilter === 'older-30' && Boolean(review && Date.parse(review.reviewedAt) < this.now() - 30 * 86_400_000)
        || review?.verdict === reviewFilter;
      if (!matchesReview) return false;
      if (!query) return true;
      return [
      item.workbench.key ?? item.workbench.id,
      item.workbench.title,
      item.projectName,
      item.workbench.status,
      statusLabel(item),
      review?.verdict ?? 'never reviewed',
    ].some(value => value.toLocaleLowerCase().includes(query));
    });
  }

  sort(items: readonly WorkbenchOverviewItem[], statusLabel: (item: WorkbenchOverviewItem) => string): WorkbenchOverviewItem[] {
    const key = this.sortKey();
    if (key === 'default') return [...items];
    const factor = this.direction() === 'asc' ? 1 : -1;
    return [...items].sort((left, right) => factor * this.compare(left, right, key, statusLabel));
  }

  private compare(
    left: WorkbenchOverviewItem,
    right: WorkbenchOverviewItem,
    key: Exclude<WorkbenchSortKey, 'default'>,
    statusLabel: (item: WorkbenchOverviewItem) => string,
  ): number {
    if (key === 'updatedAt') {
      return Date.parse(left.workbench.updatedAtUtc) - Date.parse(right.workbench.updatedAtUtc);
    }
    if (key === 'openDecisions') {
      return openDecisionCount(left) - openDecisionCount(right);
    }
    if (key === 'reviewAge') {
      return reviewAge(left, this.now()) - reviewAge(right, this.now());
    }
    const values: Record<Exclude<WorkbenchSortKey, 'default' | 'updatedAt' | 'openDecisions' | 'reviewAge'>, [string, string]> = {
      status: [statusLabel(left), statusLabel(right)],
      project: [left.projectName, right.projectName],
      key: [left.workbench.key ?? left.workbench.id, right.workbench.key ?? right.workbench.id],
    };
    return this.collator.compare(...values[key]);
  }

  private hydrate(): void {
    const routeState = readRouteState(globalThis.location?.hash ?? '');
    const storedState = this.readStoredState();
    const next = routeState?.present ? routeState.state : storedState ?? DEFAULT_STATE;
    this.apply(next);
    this.writeStoredState(next);
    if (routeState && !routeState.present && storedState) this.writeRouteState(next);
  }

  private commit(): void {
    const state = this.currentState();
    this.writeStoredState(state);
    this.writeRouteState(state);
  }

  private currentState(): WorkbenchOverviewViewState {
    return { query: this.query(), sortKey: this.sortKey(), direction: this.direction(), reviewFilter: this.reviewFilter() };
  }

  private apply(state: WorkbenchOverviewViewState): void {
    this.query.set(state.query);
    this.sortKey.set(state.sortKey);
    this.direction.set(state.direction);
    this.reviewFilter.set(state.reviewFilter);
  }

  private readStoredState(): WorkbenchOverviewViewState | null {
    try {
      const raw = globalThis.sessionStorage?.getItem(STORAGE_KEY);
      if (!raw) return null;
      const parsed = JSON.parse(raw) as { version?: unknown; scopes?: Record<string, unknown> };
      return parsed.version === 1 ? validatedState(parsed.scopes?.[this.scopeKey]) : null;
    } catch {
      return null;
    }
  }

  private writeStoredState(state: WorkbenchOverviewViewState): void {
    try {
      const raw = globalThis.sessionStorage?.getItem(STORAGE_KEY);
      const parsed = raw ? JSON.parse(raw) as { version?: unknown; scopes?: Record<string, unknown> } : null;
      const scopes = parsed?.version === 1 && parsed.scopes ? parsed.scopes : {};
      globalThis.sessionStorage?.setItem(STORAGE_KEY, JSON.stringify({
        version: 1,
        scopes: { ...scopes, [this.scopeKey]: state },
      }));
    } catch {
      // Session persistence is optional when storage is unavailable.
    }
  }

  private writeRouteState(state: WorkbenchOverviewViewState): void {
    if (!globalThis.location || !globalThis.history) return;
    const route = routeSegmentOf(globalThis.location.hash);
    if (!route || !route.split('?', 1)[0].endsWith('/workbenches')) return;
    const query = new URLSearchParams();
    if (state.query.trim()) query.set('q', state.query.trim());
    if (state.reviewFilter !== 'all') query.set('review', state.reviewFilter);
    if (state.sortKey !== 'default') {
      query.set('sort', state.sortKey);
      query.set('dir', state.direction);
    }
    const path = route.split('?', 1)[0];
    const packed = query.toString();
    const target = withRouteSegment(
      globalThis.location.hash,
      packed ? `${path}?dossier=${encodeURIComponent(packed)}` : path,
    );
    if (target === globalThis.location.hash) return;
    globalThis.history.replaceState(
      null,
      '',
      globalThis.location.pathname + globalThis.location.search + target,
    );
  }
}

function readRouteState(hash: string): { present: boolean; state: WorkbenchOverviewViewState } | null {
  const route = routeSegmentOf(hash);
  if (!route) return null;
  const queryIndex = route.indexOf('?');
  const path = queryIndex >= 0 ? route.slice(0, queryIndex) : route;
  if (!path.endsWith('/workbenches')) return null;
  const query = new URLSearchParams(queryIndex >= 0 ? route.slice(queryIndex + 1) : '');
  const packed = query.get('dossier');
  const view = new URLSearchParams(packed ?? '');
  const present = packed !== null;
  const sortKey = isSortKey(view.get('sort')) ? view.get('sort') as WorkbenchSortKey : 'default';
  const direction = isDirection(view.get('dir'))
    ? view.get('dir') as WorkbenchSortDirection
    : sortKey === 'default' ? 'desc' : DEFAULT_DIRECTIONS[sortKey];
  const reviewFilter = isReviewFilter(view.get('review')) ? view.get('review') as WorkbenchReviewFilter : 'all';
  return { present, state: { query: view.get('q') ?? '', sortKey, direction, reviewFilter } };
}

function validatedState(value: unknown): WorkbenchOverviewViewState | null {
  if (!value || typeof value !== 'object') return null;
  const candidate = value as Partial<WorkbenchOverviewViewState>;
  if (typeof candidate.query !== 'string' || !isSortKey(candidate.sortKey) || !isDirection(candidate.direction)) return null;
  return { query: candidate.query, sortKey: candidate.sortKey, direction: candidate.direction, reviewFilter: isReviewFilter(candidate.reviewFilter) ? candidate.reviewFilter : 'all' };
}

function isReviewFilter(value: unknown): value is WorkbenchReviewFilter {
  return value === 'all' || value === 'never-reviewed' || value === 'older-30'
    || value === 'current' || value === 'partially-superseded' || value === 'superseded' || value === 'historical';
}

function reviewAge(item: WorkbenchOverviewItem, now: number): number {
  return item.workbench.review ? now - Date.parse(item.workbench.review.reviewedAt) : Number.POSITIVE_INFINITY;
}

function isSortKey(value: unknown): value is WorkbenchSortKey {
  return value === 'default' || WORKBENCH_SORT_OPTIONS.some(option => option.key === value);
}

function isDirection(value: unknown): value is WorkbenchSortDirection {
  return value === 'asc' || value === 'desc';
}

function openDecisionCount(item: WorkbenchOverviewItem): number {
  return item.workbench.openDecisionCount
    ?? (item.workbench.status === 'decision-pending' ? 1 : 0);
}
