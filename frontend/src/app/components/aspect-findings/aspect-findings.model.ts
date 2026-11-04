/**
 * Central model + parsing for aspect-runner findings (ADR-0025 auto-review).
 *
 * The orchestrator's per-aspect verdicts surface in several places — the
 * Timeline reopen/escalate rows, the Overview completion-loop strip, and the
 * orchestrator chat log — historically as a single preformatted blob:
 *
 *   - **requirement-fit** [concerns]: missing edge-case test
 *   - **code-quality** [block]: helper duplicated across modules
 *
 * Rendered as plain text that reads as raw `**`/`[]` markdown. This module
 * is the single source of truth that turns that blob (or the newer
 * structured `details["findings"]` JSON the backend now also writes) into a
 * typed list the central {@link AspectFindingsListComponent} renders as
 * toned verdict chips. Keeping the parse + tone mapping here means every
 * surface reads findings the same way and the tones come from one place.
 */

import type { StatusTone } from '../status-indicator/status-indicator.component';

/** One parsed aspect finding: which aspect, its verdict token, and why. */
export interface AspectFinding {
  aspect: string;
  /** Normalised verdict token: 'pass' | 'concerns' | 'block' (or raw fallback). */
  verdict: string;
  reason: string;
}

/** Tone suffix used to colour a verdict chip via the central severity tokens. */
export type AspectVerdictTone = 'ok' | 'warn' | 'danger' | 'neutral';

/**
 * Map an aspect verdict token to its central tone (ASS-737 semantic
 * tokens): pass → ok, concerns → warn, block → danger. Tolerant of the
 * spelling drift the backend's own parser accepts (concern/blocked) so a
 * fallback-parsed blob still tones correctly.
 */
export function aspectVerdictTone(verdict: string | null | undefined): AspectVerdictTone {
  switch ((verdict ?? '').trim().toLowerCase()) {
    case 'pass':
      return 'ok';
    case 'concerns':
    case 'concern':
      return 'warn';
    case 'block':
    case 'blocked':
      return 'danger';
    default:
      return 'neutral';
  }
}

/** Shared status indicator tone per aspect verdict tone (AGT-3403). */
const ASPECT_VERDICT_STATUS_TONES: Readonly<Record<AspectVerdictTone, StatusTone>> = {
  ok: 'success',
  warn: 'warning',
  danger: 'danger',
  neutral: 'neutral',
};

/** Map an aspect verdict tone (`ok` / `warn` / `danger` / `neutral`) to a shared status tone; unknown is neutral. */
export function aspectToneStatusTone(tone: AspectVerdictTone | null | undefined): StatusTone {
  return (tone && ASPECT_VERDICT_STATUS_TONES[tone]) || 'neutral';
}

/** Map an aspect verdict token to a shared status indicator tone; unknown tokens are neutral. */
export function aspectVerdictStatusTone(verdict: string | null | undefined): StatusTone {
  return aspectToneStatusTone(aspectVerdictTone(verdict));
}

/** Short, stable chip label for a verdict token (the token itself, normalised). */
export function aspectVerdictLabel(verdict: string | null | undefined): string {
  const token = (verdict ?? '').trim().toLowerCase();
  switch (token) {
    case 'pass':
      return 'pass';
    case 'concerns':
    case 'concern':
      return 'concerns';
    case 'block':
    case 'blocked':
      return 'block';
    default:
      return token || 'finding';
  }
}

// One aspect-finding line: an optional `- `/`* ` list bullet, an optional
// **bold** label, a [token] verdict in brackets, then `: reason`. The label
// is captured non-greedily and excludes `*`/`[`/`]` so the bold markers and
// the bracket never bleed into it.
const FINDING_LINE = new RegExp(
  String.raw`^(?:[-*]\s+)?\*{0,2}\s*(?<aspect>[^*\[\]]+?)\s*\*{0,2}\s*\[\s*(?<verdict>[A-Za-z]+)\s*\]\s*:\s*(?<reason>.*)$`,
);

/**
 * Parse the legacy preformatted findings blob into a typed list. Each line
 * of the form `**{aspect}** [{verdict}]: {reason}` (with or without a `- `
 * bullet and with or without the `**` bold markers) becomes one finding.
 * Lines that do not match the shape are skipped, so a plain reason string
 * (no `[token]`) yields an empty list and the caller can fall back to
 * rendering it as text.
 */
export function parseAspectFindings(raw: string | null | undefined): AspectFinding[] {
  if (!raw) return [];
  const out: AspectFinding[] = [];
  for (const line of raw.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    const m = FINDING_LINE.exec(trimmed);
    if (!m?.groups) continue;
    const aspect = m.groups['aspect'].trim();
    const verdict = m.groups['verdict'].trim().toLowerCase();
    const reason = m.groups['reason'].trim();
    if (!aspect) continue;
    out.push({ aspect, verdict, reason });
  }
  return out;
}

/**
 * Read the structured `details["findings"]` JSON string the backend writes
 * for aspect-driven reopens. Returns [] when the field is absent or
 * malformed (the caller falls back to {@link parseAspectFindings} on the
 * `gap` blob). Each element must have an `aspect`; `verdict`/`reason`
 * default to empty so a partial row still renders.
 */
export function parseFindingsJson(raw: string | null | undefined): AspectFinding[] {
  if (!raw) return [];
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return [];
  }
  if (!Array.isArray(parsed)) return [];
  const out: AspectFinding[] = [];
  for (const item of parsed) {
    if (!item || typeof item !== 'object') continue;
    const rec = item as Record<string, unknown>;
    const aspect = typeof rec['aspect'] === 'string' ? rec['aspect'].trim() : '';
    if (!aspect) continue;
    const verdict = typeof rec['verdict'] === 'string' ? rec['verdict'].trim().toLowerCase() : '';
    const reason = typeof rec['reason'] === 'string' ? rec['reason'].trim() : '';
    out.push({ aspect, verdict, reason });
  }
  return out;
}

/**
 * Resolve the findings for a value that may be either the structured
 * `findings` JSON (preferred) or the legacy preformatted `gap`/`reason`
 * blob. Used by every host surface so the structured-first / parse-fallback
 * order lives in one place.
 */
export function resolveAspectFindings(
  structuredJson: string | null | undefined,
  blob: string | null | undefined,
): AspectFinding[] {
  const structured = parseFindingsJson(structuredJson);
  if (structured.length > 0) return structured;
  return parseAspectFindings(blob);
}
