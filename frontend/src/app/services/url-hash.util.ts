/**
 * Shared segment model for the URL hash.
 *
 * Several independent surfaces persist state in `window.location.hash`:
 *
 *   - route overlays own a single route segment starting with `/`
 *     (`/workspace/settings`, `/projects/<slug>/wiki?page=x`,
 *     `/project/<slug>/feed`);
 *   - the board filter bar owns the `filters=<encoded>` key-value segment;
 *   - legacy segments (`diff`) ride along untouched.
 *
 * Segments are joined with `&`: `#/workspace/settings&filters=...`. Before this
 * util existed every writer hand-rolled its own string handling; the filter
 * writer preserved foreign segments while the overlay writers overwrote the
 * whole hash and matched it verbatim on read. The mix produced hybrid hashes
 * like `#filters=...&/workspace/settings` that no reader recognised (stale
 * settings cargo, dropped filters, project shells yanked shut). The rules are
 * now explicit and shared:
 *
 *   1. At most ONE route segment exists; writing a route replaces any other
 *      route (navigating somewhere means you left the previous overlay).
 *   2. Key-value segments coexist with the route and with each other; writing
 *      one never disturbs the rest of the hash.
 *   3. Readers match against their own segment, never the whole hash.
 *   4. Unknown segments are always preserved.
 *
 * Canonical segment order on write: route first, then key-value and legacy
 * segments in their original order. Values inside a key-value segment must be
 * percent-encoded by the caller so they cannot contain a raw `&`.
 */

/** Keys shared by usage-detail links and the settings token-ledger route. */
export const LEDGER_SCOPE_KEYS = {
  workspace: 'ledger-workspace',
  range: 'ledger-range',
  from: 'ledger-from',
  to: 'ledger-to',
  project: 'ledger-project',
  zone: 'ledger-zone',
} as const;

/** Split a `#a&b&c` hash (or '') into its non-empty segments. */
export function hashSegments(hash: string): string[] {
  return (hash || '')
    .replace(/^#/, '')
    .split('&')
    .filter(s => s.length > 0);
}

/** True when the segment is a route segment (`/workspace/...`, ...). */
export function isRouteSegment(segment: string): boolean {
  return segment.startsWith('/');
}

const ROUTE_QUERY_KEYS = new Set(['view', 'level', 'overlay', 'env', 'focus', 'page', 'folder', 'host', 'link', 'node']);

function routeParts(segments: readonly string[]): { route: string; start: number; end: number } | null {
  const start = segments.findIndex(isRouteSegment);
  if (start < 0) return null;
  let end = start + 1;
  let route = segments[start];
  if (route.includes('?')) {
    while (end < segments.length && !isRouteSegment(segments[end]) &&
      ROUTE_QUERY_KEYS.has(segments[end].split('=', 1)[0]) && segments[end].includes('=')) {
      route += `&${segments[end]}`;
      end++;
    }
  }
  return { route, start, end };
}

/** The hash's single route segment, including its query, or null. */
export function routeSegmentOf(hash: string): string | null {
  return routeParts(hashSegments(hash))?.route ?? null;
}

/** The (still encoded) value of a sibling key-value segment, or null. */
export function kvValueOf(hash: string, key: string): string | null {
  const segments = hashSegments(hash);
  const route = routeParts(segments);
  const prefix = `${key}=`;
  const seg = segments.find((part, index) =>
    (!route || index < route.start || index >= route.end) && !isRouteSegment(part) && part.startsWith(prefix));
  return seg != null ? seg.slice(prefix.length) : null;
}

/** Replace the route while preserving independent sibling segments. */
export function withRouteSegment(hash: string, route: string | null): string {
  const segments = hashSegments(hash);
  const current = routeParts(segments);
  const rest = segments.filter((part, index) =>
    !isRouteSegment(part) && (!current || index < current.start || index >= current.end));
  return joinSegments(route ? [route, ...rest] : rest);
}

/**
 * Upsert (or with `value = null` remove) the `key=value` segment, keeping the
 * route and every other segment. `dropKeys` removes additional legacy spellings
 * of the same state (e.g. the pre-Cycle-9 `filter=` alongside `filters=`).
 * Returns the full hash including `#`, or '' when no segment remains.
 */
export function withKvSegment(
  hash: string,
  key: string,
  value: string | null,
  dropKeys: readonly string[] = [],
): string {
  const removed = new Set([key, ...dropKeys].map(k => `${k}=`));
  const segments = hashSegments(hash);
  const route = routeParts(segments);
  const rest = segments.filter((part, index) =>
    (!route || index < route.start || index >= route.end) &&
    !isRouteSegment(part) && ![...removed].some(prefix => part.startsWith(prefix)));
  const next: string[] = [];
  if (route) next.push(route.route);
  if (value != null) next.push(`${key}=${value}`);
  next.push(...rest);
  return joinSegments(next);
}

function joinSegments(segments: readonly string[]): string {
  return segments.length > 0 ? `#${segments.join('&')}` : '';
}
