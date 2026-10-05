/**
 * Shared segment model for the URL hash.
 *
 * Several independent surfaces persist state in `window.location.hash`:
 *
 *   - route overlays own a single route segment starting with `/`
 *     (`/workspace/settings`, `/projects/<slug>/wiki?page=x`, `/epics`,
 *     `/project/<slug>/feed`);
 *   - the board filter bar owns the `filters=<encoded>` key-value segment;
 *   - legacy segments (`diff`, `epics:<name>`) ride along untouched.
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

/** True when the segment is a route segment (`/workspace/...`, `/epics`, ...). */
export function isRouteSegment(segment: string): boolean {
  return segment.startsWith('/');
}

/** The hash's single route segment (leading `/` included), or null. */
export function routeSegmentOf(hash: string): string | null {
  return hashSegments(hash).find(isRouteSegment) ?? null;
}

/** The (still encoded) value of the `key=` segment, or null when absent. */
export function kvValueOf(hash: string, key: string): string | null {
  const prefix = `${key}=`;
  const seg = hashSegments(hash).find(s => !isRouteSegment(s) && s.startsWith(prefix));
  return seg != null ? seg.slice(prefix.length) : null;
}

/**
 * Replace (or with `route = null` remove) the hash's route segment, keeping
 * every non-route segment. Returns the full hash including `#`, or '' when no
 * segment remains - ready for `pathname + search + result`.
 */
export function withRouteSegment(hash: string, route: string | null): string {
  const rest = hashSegments(hash).filter(s => !isRouteSegment(s));
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
  const route = segments.find(isRouteSegment) ?? null;
  const rest = segments.filter(s =>
    !isRouteSegment(s) && ![...removed].some(prefix => s.startsWith(prefix)));
  const next: string[] = [];
  if (route) next.push(route);
  if (value != null) next.push(`${key}=${value}`);
  next.push(...rest);
  return joinSegments(next);
}

function joinSegments(segments: readonly string[]): string {
  return segments.length > 0 ? `#${segments.join('&')}` : '';
}
