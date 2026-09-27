/**
 * Central "large-diff gate" shared by every expensive diff/text render
 * surface. One threshold plus one predicate so all surfaces agree on
 * what counts as "too big to auto-render", and only this file has to
 * change to tune it.
 *
 * Metric choice (AC: evaluate file size vs. changed lines): render cost
 * in both surfaces scales with the number of rendered diff *lines* -
 * diff2html builds one table row per line, Markdown/code surfaces emit
 * one row or block per source line, and the run viewer runs an async
 * syntax-highlight pass per line plus one DOM node per line. Line count
 * is therefore the primary, most robust signal. Raw byte size is a
 * secondary guard for the pathological "few lines, enormous bytes" case
 * (minified bundles, generated assets, single-line lockfiles) where line
 * count alone would under-count the real layout/highlight cost.
 *
 * Tune every surface from here: change the two thresholds and every diff
 * render stop follows.
 */
export const LARGE_DIFF_LINE_THRESHOLD = 500;
export const LARGE_DIFF_BYTE_THRESHOLD = 50_000;

export interface DiffSizeMetrics {
  readonly lines: number;
  readonly bytes: number;
}

/** Count lines (newline-delimited) and UTF-16 length of a unified-diff blob. */
export function measureDiff(text: string | null | undefined): DiffSizeMetrics {
  if (!text) return { lines: 0, bytes: 0 };
  let lines = 1;
  for (let i = 0; i < text.length; i++) {
    if (text.charCodeAt(i) === 10) lines++;
  }
  return { lines, bytes: text.length };
}

/** True when a diff blob is over either threshold and should be gated. */
export function isLargeDiff(text: string | null | undefined): boolean {
  const m = measureDiff(text);
  return m.lines >= LARGE_DIFF_LINE_THRESHOLD || m.bytes >= LARGE_DIFF_BYTE_THRESHOLD;
}

/** Compact "523 lines · 18 KB" label for the gated-file placeholder. */
export function describeDiffSize(text: string | null | undefined): string {
  const m = measureDiff(text);
  const size = m.bytes >= 1024 ? `${Math.round(m.bytes / 1024)} KB` : `${m.bytes} B`;
  const lineLabel = m.lines === 1 ? 'line' : 'lines';
  return `${m.lines.toLocaleString('en-US')} ${lineLabel} · ${size}`;
}
