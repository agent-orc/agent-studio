/**
 * AGT-3585: groups automatic interventions that share a cause and arrived
 * close together, so a burst after a long outage reads as "45 cards, 2
 * causes" instead of 45 unrelated rows. Shared by the Activity feed and the
 * Escalated lane; both supply their own cause and time accessors.
 */

/** Two members of one burst are at most this far apart. */
export const CAUSE_BURST_WINDOW_MS = 15 * 60_000;

export interface CauseBurst<T> {
  /** Cause plus the oldest member time; stays stable while the burst grows. */
  readonly key: string;
  /** Null for an item without a recorded cause or time (always alone). */
  readonly cause: string | null;
  /** Members in input order. */
  readonly members: T[];
  readonly firstMs: number;
  readonly lastMs: number;
}

/**
 * Splits `items` into bursts. An item joins its cause's burst when it is at
 * most `windowMs` after the previous member, so one long outage chains into a
 * single burst while an unrelated repeat hours later starts a new one. Bursts
 * come back in the order of their first member in `items`; every item lands in
 * exactly one burst, so member counts always sum to `items.length`.
 */
export function groupCauseBursts<T>(
  items: readonly T[],
  causeOf: (item: T) => string | null,
  timeOf: (item: T) => number,
  windowMs = CAUSE_BURST_WINDOW_MS,
): CauseBurst<T>[] {
  const byCause = new Map<string, { item: T; index: number; ms: number }[]>();
  const burstOf = new Array<number>(items.length);
  const causeOfBurst = new Map<number, string | null>();
  let next = 0;
  items.forEach((item, index) => {
    const cause = causeOf(item);
    const ms = timeOf(item);
    if (cause === null || !Number.isFinite(ms)) {
      causeOfBurst.set(next, null);
      burstOf[index] = next++;
      return;
    }
    const list = byCause.get(cause) ?? [];
    list.push({ item, index, ms });
    byCause.set(cause, list);
  });
  for (const [cause, list] of byCause) {
    list.sort((a, b) => a.ms - b.ms || a.index - b.index);
    let previous = Number.NEGATIVE_INFINITY;
    let current = -1;
    for (const member of list) {
      if (member.ms - previous > windowMs) {
        current = next++;
        causeOfBurst.set(current, cause);
      }
      burstOf[member.index] = current;
      previous = member.ms;
    }
  }

  const bursts = new Map<number, { cause: string | null; members: T[]; times: number[] }>();
  items.forEach((item, index) => {
    const id = burstOf[index];
    const burst = bursts.get(id) ?? { cause: causeOfBurst.get(id) ?? null, members: [], times: [] };
    burst.members.push(item);
    burst.times.push(timeOf(item));
    bursts.set(id, burst);
  });
  return [...bursts.entries()].map(([id, burst]) => {
    const finite = burst.times.filter(Number.isFinite);
    const firstMs = finite.length ? Math.min(...finite) : Number.NaN;
    const lastMs = finite.length ? Math.max(...finite) : Number.NaN;
    return {
      key: burst.cause === null ? `single\u0000${id}` : `${burst.cause}\u0000${firstMs}`,
      cause: burst.cause,
      members: burst.members,
      firstMs,
      lastMs,
    };
  });
}
