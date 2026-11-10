import { groupCauseBursts } from '../../../../models/cause-burst';
import type { OrchestratorLogEntry } from '../../models/orchestrator.model';

/**
 * AGT-3585: card-scoped automatic entries that can repeat in a burst. A
 * decision stays one row because each one can be overridden on its own.
 */
const BURST_KINDS: ReadonlySet<string> = new Set(['alert', 'intervention', 'action']);

/** Card keys such as `PLAT-20`, with the `:` that joins them to a subject. */
const CARD_KEY = /:?\b[A-Z][A-Z0-9]*-\d+\b/g;

export interface FeedBurst {
  /** Stable while the burst grows: cause plus the oldest member time. */
  readonly key: string;
  readonly kind: OrchestratorLogEntry['kind'];
  readonly topic: string;
  /** The shared summary with the card reference removed. */
  readonly label: string;
  /** Members, newest first. */
  readonly entries: OrchestratorLogEntry[];
  /** Distinct cards among the members; a card can repeat inside one burst. */
  readonly cards: number;
  readonly newestTs: string;
  readonly oldestTs: string;
  readonly projects: string[];
}

/** One stream row: a single entry or a burst of entries sharing a cause. */
export interface FeedRow {
  readonly key: string;
  readonly ts: string;
  readonly entry?: OrchestratorLogEntry;
  readonly burst?: FeedBurst;
}

/** The summary of a card-scoped entry without the card it names. */
export function feedCauseLabel(entry: OrchestratorLogEntry): string {
  let text = entry.summary ?? '';
  if (entry.jobId) text = text.split(entry.jobId).join('');
  return text
    .replace(CARD_KEY, '')
    .replace(/\s+([:.,;])/g, '$1')
    .replace(/:(\s*:)+/g, ':')
    .replace(/\s{2,}/g, ' ')
    .trim();
}

/** Null when the entry never joins a burst. */
export function feedCause(entry: OrchestratorLogEntry): string | null {
  if (!entry.jobId || !BURST_KINDS.has(entry.kind)) return null;
  return [entry.kind, entry.topic, feedCauseLabel(entry)].join('\u0000');
}

/**
 * Folds `entries` (newest first) into stream rows. Two or more entries with
 * one cause inside the burst window become one burst row at the position of
 * its newest member; every other entry keeps its own row. Each entry appears
 * in exactly one row, so the rows account for every entry (R3).
 */
export function buildFeedRows(
  entries: readonly OrchestratorLogEntry[],
  entryKey: (entry: OrchestratorLogEntry) => string,
  projectFallback: string,
): FeedRow[] {
  const rows: FeedRow[] = [];
  for (const burst of groupCauseBursts(entries, feedCause, entry => Date.parse(entry.ts))) {
    if (burst.cause === null || burst.members.length < 2) {
      for (const entry of burst.members) rows.push({ key: entryKey(entry), ts: entry.ts, entry });
      continue;
    }
    const first = burst.members[0];
    const last = burst.members[burst.members.length - 1];
    rows.push({
      key: `burst\u0000${burst.key}`,
      ts: first.ts,
      burst: {
        key: burst.key,
        kind: first.kind,
        topic: first.topic,
        label: feedCauseLabel(first),
        entries: burst.members,
        cards: new Set(burst.members.map(entry => `${entry.watchPath ?? ''}\u0000${entry.jobId}`)).size,
        newestTs: first.ts,
        oldestTs: last.ts,
        projects: [...new Set(burst.members.map(entry => entry.project || projectFallback))].sort(),
      },
    });
  }
  return rows;
}
