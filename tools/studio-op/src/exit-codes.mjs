/**
 * Stable exit codes of studio-op. Scripts and agent sessions branch on these
 * numbers, so a value never changes meaning; new codes take new numbers.
 * The table is documented in docs/operations/setup/studio-op.md.
 */
export const EXIT = Object.freeze({
  OK: 0,
  FAILURE: 1,
  USAGE: 2,
  CREDENTIAL: 3,
  FORBIDDEN: 4,
  NOT_FOUND: 5,
  CONFLICT: 6,
  ACCEPT_REFUSED: 7,
  PREFLIGHT_BLOCKED: 8,
  WATCHER_ACTIVE: 9,
  UNREACHABLE: 10,
  BATCH_ISOLATION_ACTIVE: 11,
  APPROVAL_REQUIRED: 12,
  // studio-op watch: one code per class of the first actionable event.
  EVENT_ESCALATION: 20,
  EVENT_HUMAN_REVIEW: 21,
  EVENT_DECISION_CARD: 22,
  EVENT_STALL: 23,
  EVENT_BATCH: 24,
  EVENT_PUSH: 25,
  EVENT_QUOTA: 26,
  EVENT_STREAM_GAP: 27,
  WATCHER_LEASE_LOST: 28,
  EVENT_OTHER: 29,
  // SIGINT or SIGTERM ended a watch; the lease and lock are released.
  INTERRUPTED: 130,
});

const EVENT_CLASSES = new Map([
  ['escalation.opened', EXIT.EVENT_ESCALATION],
  ['human-review.ready', EXIT.EVENT_HUMAN_REVIEW],
  ['decision-card.opened', EXIT.EVENT_DECISION_CARD],
  ['backend.stalled', EXIT.EVENT_STALL],
  ['progress.idle-with-ready', EXIT.EVENT_STALL],
  ['batch.red', EXIT.EVENT_BATCH],
  ['batch.isolating', EXIT.EVENT_BATCH],
  ['batch.running-without-gate', EXIT.EVENT_BATCH],
  ['batch.record-unreadable', EXIT.EVENT_BATCH],
  ['push.pending-overdue', EXIT.EVENT_PUSH],
  ['push.blocked', EXIT.EVENT_PUSH],
  ['quota.exhausted', EXIT.EVENT_QUOTA],
  ['quota.fallback-active', EXIT.EVENT_QUOTA],
  // The stream lost events older than its retention: re-read triage and capacity.
  ['stream.gap', EXIT.EVENT_STREAM_GAP],
]);

/** The heartbeat only reports the lease, so it never wakes the watcher. */
const NOT_ACTIONABLE = new Set(['watcher.heartbeat']);

/**
 * The exit code for an event type, or null when the watcher keeps waiting.
 * A type this release does not know still wakes the watcher: missing a new
 * decision is worse than one extra wake.
 */
export function eventExitCode(type) {
  if (NOT_ACTIONABLE.has(type)) return null;
  return EVENT_CLASSES.get(type) ?? EXIT.EVENT_OTHER;
}

/** Maps a Studio answer to an exit code; `error` is the answer's error code. */
export function httpExitCode(status, error) {
  if (status === 401) return EXIT.CREDENTIAL;
  if (status === 403)
    return error === 'approval-required' || error === 'override-requires-t2-request'
      ? EXIT.APPROVAL_REQUIRED : EXIT.FORBIDDEN;
  if (status === 404) return EXIT.NOT_FOUND;
  if (status === 409 && error === 'watcher-active') return EXIT.WATCHER_ACTIVE;
  if (status === 409 && error === 'batch-isolation-active') return EXIT.BATCH_ISOLATION_ACTIVE;
  if (status === 409 || status === 412 || status === 428) return EXIT.CONFLICT;
  if (status === 400 || status === 422) return EXIT.USAGE;
  return EXIT.FAILURE;
}
