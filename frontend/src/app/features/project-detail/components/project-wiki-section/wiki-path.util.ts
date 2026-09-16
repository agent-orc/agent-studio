/**
 * Path and formatting helpers the Knowledge surfaces share, split out of
 * `project-wiki-section.ts` in AGT-2819 so the section and its extracted child
 * components read the same implementations instead of each keeping a copy.
 */

/** Everything before the last slash; empty string for a root-level entry. */
export function wikiParentDir(rel: string): string {
  const i = rel.lastIndexOf('/');
  return i >= 0 ? rel.slice(0, i) : '';
}

/** Everything after the last slash. */
export function wikiBasename(rel: string): string {
  const i = rel.lastIndexOf('/');
  return i >= 0 ? rel.slice(i + 1) : rel;
}

/** Joins a directory and a name, tolerating an empty directory. */
export function wikiJoinRel(dir: string, name: string): string {
  return dir ? `${dir}/${name}` : name;
}

/** Filesystem-safe slug for a generated sidecar or task id; never empty. */
export function wikiSlug(value: string): string {
  return value.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'document';
}

/** Locale date-time for a doc header line; blank on missing, verbatim on bad input. */
export function wikiFormatTimestamp(iso: string | null | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

/**
 * Best available message for a failed Knowledge request: the API's own error
 * text, then the exception message, then the caller's fallback.
 */
export function wikiDescribeError(err: unknown, fallback: string): string {
  if (!err) return fallback;
  const e = err as { error?: { error?: string }; message?: string };
  return e.error?.error ?? e.message ?? fallback;
}
