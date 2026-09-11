import { routeSegmentOf, withRouteSegment } from '../../../services/url-hash.util';
import { studioProjectSlug } from '../../../services/studio-project-slug.util';
import type { StudioTab } from '../studio-shell.types';

export { studioProjectSlug } from '../../../services/studio-project-slug.util';

export const TASK_DETAIL_TABS = [
  'overview',
  'timeline',
  'evidence',
  'code-review',
  'description',
] as const;

export type TaskDetailRouteTab = typeof TASK_DETAIL_TABS[number];
export type TaskInspectorRouteTab = 'task' | 'activity' | 'protocol';

export type StudioRoute =
  | { kind: 'board'; projectSlug: string | null }
  | { kind: 'feed' }
  | { kind: 'chat-history' }
  | { kind: 'hub'; projectSlug: string; section: string; page: string | null; folder: string | null }
  | { kind: 'workbenches'; projectSlug: string | null }
  | { kind: 'workbench'; projectSlug: string; workbenchId: string }
  | { kind: 'task'; reference: string; tab: TaskDetailRouteTab; inspector: TaskInspectorRouteTab }
  | { kind: 'epics'; projectSlug: string | null }
  | { kind: 'epic'; reference: string }
  | { kind: 'workspace-settings'; section: string; detail: string | null };

/**
 * Parse the single canonical Studio route from the shared hash segment model.
 *
 * Route-local state is expressed as a query on the hash path. Independent
 * cross-surface state, currently board filters, remains a sibling hash
 * key-value segment and is therefore ignored here.
 */
export function parseStudioRoute(hash: string): StudioRoute | null {
  const raw = routeSegmentOf(hash);
  if (!raw) return null;
  const queryIndex = raw.indexOf('?');
  const path = queryIndex >= 0 ? raw.slice(0, queryIndex) : raw;
  const query = new URLSearchParams(queryIndex >= 0 ? raw.slice(queryIndex + 1) : '');
  const segments = path.split('/').filter(Boolean).map(safeDecode);

  if (segments.length === 1 && segments[0] === 'board') {
    return { kind: 'board', projectSlug: null };
  }
  if (segments.length === 1 && segments[0] === 'feed') {
    return { kind: 'feed' };
  }
  if (segments.length === 1 && segments[0] === 'chat-history') {
    return { kind: 'chat-history' };
  }
  if (segments.length === 1 && segments[0] === 'epics') {
    return { kind: 'epics', projectSlug: null };
  }
  if (segments.length === 1 && segments[0] === 'workbenches') {
    return { kind: 'workbenches', projectSlug: null };
  }
  if (segments.length === 2 && segments[0] === 'epics' && segments[1]) {
    return { kind: 'epic', reference: segments[1] };
  }
  if (segments.length === 2 && segments[0] === 'tasks' && segments[1]) {
    const [viewTab, viewInspector] = (query.get('view') ?? '').split(':', 2);
    return {
      kind: 'task',
      reference: segments[1],
      tab: isTaskDetailTab(viewTab) ? viewTab : 'overview',
      inspector: isTaskInspectorTab(viewInspector) ? viewInspector : 'protocol',
    };
  }
  if (segments[0] === 'workspace' && segments[1] === 'settings') {
    if (segments.length === 2) {
      return { kind: 'workspace-settings', section: 'overview', detail: null };
    }
    if (segments.length === 3 && segments[2]) {
      return { kind: 'workspace-settings', section: segments[2], detail: null };
    }
    if (segments.length === 4 && segments[2] === 'tokens' && segments[3]) {
      return { kind: 'workspace-settings', section: 'tokens', detail: segments[3] };
    }
    return null;
  }
  if (segments[0] !== 'projects' || !segments[1]) return null;

  const projectSlug = segments[1];
  if (segments.length === 3 && segments[2] === 'board') {
    return { kind: 'board', projectSlug };
  }
  if (segments.length === 3 && segments[2] === 'epics') {
    return { kind: 'epics', projectSlug };
  }
  if (segments.length === 3 && segments[2] === 'workbenches') {
    return { kind: 'workbenches', projectSlug };
  }
  if (segments.length === 4 && segments[2] === 'workbenches' && segments[3]) {
    return { kind: 'workbench', projectSlug, workbenchId: segments[3] };
  }
  if (segments.length <= 3) {
    return {
      kind: 'hub',
      projectSlug,
      section: segments[2] || 'overview',
      page: nonBlank(query.get('page')),
      folder: nonBlank(query.get('folder')),
    };
  }
  return null;
}

/** Canonical hash path for an active editor tab. */
export function studioRouteForTab(
  tab: StudioTab,
  publicTaskReference: string | null = null,
): string | null {
  switch (tab.kind) {
    case 'board':
      return tab.projectName === '__all__'
        ? '/board'
        : `/projects/${studioProjectSlug(tab.projectName)}/board`;
    case 'feed':
      return '/feed';
    case 'chat-history':
      return '/chat-history';
    case 'hub': {
      const base = `/projects/${studioProjectSlug(tab.projectName)}`;
      return !tab.section || tab.section === 'overview' ? base : `${base}/${encodeURIComponent(tab.section)}`;
    }
    case 'workbench':
      return `/projects/${encodeURIComponent(tab.projectId || studioProjectSlug(tab.projectName))}/workbenches/${encodeURIComponent(tab.workbenchId)}`;
    case 'workbenches':
      return tab.projectName
        ? `/projects/${studioProjectSlug(tab.projectName)}/workbenches`
        : '/workbenches';
    case 'task':
      return publicTaskReference ? `/tasks/${encodeURIComponent(publicTaskReference)}` : null;
    case 'epics':
      return tab.projectName
        ? `/projects/${studioProjectSlug(tab.projectName)}/epics`
        : '/epics';
    case 'epic':
      return `/epics/${encodeURIComponent(publicTaskReference || tab.epicKey)}`;
    case 'workspace-settings':
      return null;
    default:
      return null;
  }
}

/**
 * Mirror an active Studio surface into the address bar.
 *
 * A user-visible surface transition gets its own history entry so Back and
 * Forward restore the previous editor surface. Cold boot and legacy URLs that
 * do not name a Studio surface are canonicalized in place. Route hydration
 * and popstate already carry the requested route, so they are naturally
 * no-ops and cannot create a duplicate history entry.
 *
 * Route-local query state already present on the same base path is retained,
 * so a later signal refresh cannot erase a Wiki page or Task tab selection.
 */
export function navigateStudioRoute(route: string): void {
  if (typeof window === 'undefined') return;
  const current = routeSegmentOf(window.location.hash);
  if (sameRouteBase(current, route)) return;
  const target = withRouteSegment(window.location.hash, route);
  if (target === window.location.hash) return;
  const method = current ? 'pushState' : 'replaceState';
  window.history[method](
    null,
    '',
    window.location.pathname + window.location.search + target,
  );
}

/** Replace only the query carried by the current canonical route. */
export function replaceStudioRouteQuery(
  updates: Readonly<Record<string, string | null>>,
): void {
  if (typeof window === 'undefined') return;
  const current = routeSegmentOf(window.location.hash);
  if (!current) return;
  const queryIndex = current.indexOf('?');
  const path = queryIndex >= 0 ? current.slice(0, queryIndex) : current;
  const query = new URLSearchParams(queryIndex >= 0 ? current.slice(queryIndex + 1) : '');
  for (const [key, value] of Object.entries(updates)) {
    if (value == null || value === '') query.delete(key);
    else query.set(key, value);
  }
  const suffix = query.toString();
  const next = suffix ? `${path}?${suffix}` : path;
  const target = withRouteSegment(window.location.hash, next);
  if (target === window.location.hash) return;
  window.history.replaceState(
    null,
    '',
    window.location.pathname + window.location.search + target,
  );
}

/** Route the two visible Task-detail tab strips through one hash query value. */
export function replaceTaskViewRoute(
  tab: TaskDetailRouteTab,
  inspector: TaskInspectorRouteTab,
): void {
  replaceStudioRouteQuery({
    view: tab === 'overview' && inspector === 'protocol' ? null : `${tab}:${inspector}`,
  });
}

export function isTaskDetailTab(value: string | null): value is TaskDetailRouteTab {
  return value !== null && (TASK_DETAIL_TABS as readonly string[]).includes(value);
}

export function isTaskInspectorTab(value: string | null): value is TaskInspectorRouteTab {
  return value === 'task' || value === 'activity' || value === 'protocol';
}

function sameRouteBase(current: string | null, target: string): boolean {
  if (!current) return false;
  return current.split('?', 1)[0] === target.split('?', 1)[0];
}

function safeDecode(value: string): string {
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

function nonBlank(value: string | null): string | null {
  return value?.trim() ? value : null;
}
