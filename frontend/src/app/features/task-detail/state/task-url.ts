import type { TaskInfo } from '../../../models/task.model';
import { routeSegmentOf, withRouteSegment } from '../../../services/url-hash.util';

export type TaskUrlHistoryMode = 'push' | 'replace';

/** Stable, globally resolvable task reference used in browser URLs. */
export function taskUrlKey(info: Pick<TaskInfo, 'key' | 'displayKey' | 'id'>): string | null {
  const key = info.key?.trim() || info.displayKey?.trim();
  if (key) return key;
  const id = info.id?.trim();
  return id && /^[A-Z][A-Z0-9]*-\d+$/i.test(id) ? id : null;
}

/** Build a shareable task URL while retaining unrelated query/hash state. */
export function taskUrl(reference: string, current: URL): string {
  const next = new URL(current.href);
  next.searchParams.delete('task');
  next.searchParams.delete('job');
  next.searchParams.delete('watchPath');
  const hash = withRouteSegment(next.hash, `/tasks/${encodeURIComponent(reference)}`);
  return `${next.pathname}${next.search}${hash}`;
}

/** Remove task-selection params without disturbing the active shell route. */
export function withoutTaskUrl(current: URL): string {
  const next = new URL(current.href);
  next.searchParams.delete('task');
  next.searchParams.delete('job');
  next.searchParams.delete('watchPath');
  const route = routeSegmentOf(next.hash);
  const hash = route?.startsWith('/tasks/') || route?.startsWith('/epics/')
    ? withRouteSegment(next.hash, null)
    : next.hash;
  return `${next.pathname}${next.search}${hash}`;
}

/** Read the canonical hash route, falling back to the pre-route query shape. */
export function taskReferenceFromUrl(current: URL): string | null {
  const route = routeSegmentOf(current.hash);
  if (route?.startsWith('/tasks/') || route?.startsWith('/epics/')) {
    const path = route.split('?', 1)[0];
    const raw = path.slice(path.startsWith('/tasks/') ? '/tasks/'.length : '/epics/'.length);
    if (raw && !raw.includes('/')) {
      try {
        return decodeURIComponent(raw);
      } catch {
        return raw;
      }
    }
  }
  return current.searchParams.get('task')?.trim() || null;
}

export function writeTaskUrl(reference: string, mode: TaskUrlHistoryMode, state?: unknown): void {
  if (typeof window === 'undefined') return;
  const next = taskUrl(reference, new URL(window.location.href));
  const current = `${window.location.pathname}${window.location.search}${window.location.hash}`;
  if (next === current) {
    if (state !== undefined) window.history.replaceState(state, '', next);
    return;
  }
  window.history[mode === 'push' ? 'pushState' : 'replaceState'](state ?? null, '', next);
}

export function clearTaskUrl(mode: TaskUrlHistoryMode = 'replace'): void {
  if (typeof window === 'undefined') return;
  const next = withoutTaskUrl(new URL(window.location.href));
  const current = `${window.location.pathname}${window.location.search}${window.location.hash}`;
  if (next === current) return;
  window.history[mode === 'push' ? 'pushState' : 'replaceState'](null, '', next);
}

export function taskNavigationHref(info: Pick<TaskInfo, 'key' | 'displayKey' | 'id'>): string | null {
  const key = taskUrlKey(info);
  if (!key || typeof window === 'undefined') return null;
  return taskUrl(key, new URL(window.location.href));
}
