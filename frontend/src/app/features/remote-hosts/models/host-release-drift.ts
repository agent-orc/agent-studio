/**
 * Execution-host release drift (AGT-2826).
 *
 * A runner host used to show only an opaque release label, so an operator could
 * not tell that agent-runner-01 still ran an agent-host build from three weeks
 * earlier while Stable and the Task Server ran v0.3.0. The server owns the
 * comparison - the same verdict drives the operator-feed alarm - and exposes it
 * at `GET /api/v1/management/host-releases`. This module holds the wire shape
 * and the pure label helpers the table renders; it never re-decides the verdict.
 */

/** Deployment identity a runner daemon reports in registration and heartbeat. */
export interface HostReleaseIdentity {
  releaseId: string;
  version: string;
  commit?: string | null;
  builtAt?: string | null;
  /** Digest-pinned image a review role runs in (AGT-3689); null outside a container. */
  imageRef?: string | null;
}

/** The release the server runs; every host row is measured against it. */
export interface StableReleaseIdentity {
  version: string;
  commit?: string | null;
  builtAt?: string | null;
}

/**
 *  current - runs the Stable release, or something newer
 *  behind  - provably older than Stable (acute once past the grace window)
 *  differs - the Stable version from another commit, order unknown (AGT-3689)
 *  unknown - nothing comparable was reported; never rendered as an accusation
 */
export type HostReleaseDriftState = 'current' | 'behind' | 'differs' | 'unknown';

/** One runner role compared against Stable. Mirrors `HostReleaseDriftEntry`. */
export interface HostReleaseDrift {
  runnerId: string;
  name: string;
  hostId: string;
  role: string;
  release: HostReleaseIdentity | null;
  state: HostReleaseDriftState;
  /** Release-age gap: how much older this build is than the Stable build. */
  behindByHours: number | null;
  /** Duration the alarm is judged on; equals the gap when both builds are stamped. */
  behindForHours: number | null;
  alarmDue: boolean;
  reason: string;
  lastSeenAt: string;
  heartbeatStale: boolean;
  /** Review image the deploy plan pinned for this host, when the server was told (AGT-3689). */
  expectedImageRef?: string | null;
}

/** Wire shape of `GET /api/v1/management/host-releases`. */
export interface HostReleaseDriftSnapshot {
  observedAt: string;
  stable: StableReleaseIdentity;
  behindCount: number;
  /** Hosts on the Stable version but another commit (AGT-3689). */
  differsCount?: number;
  hosts: readonly HostReleaseDrift[];
}

/**
 * Compact age for the drift marker: "5h behind", "19d behind". Sub-hour gaps
 * round up to one hour so the marker never reads "0h behind".
 */
export function releaseAgeLabel(hours: number | null | undefined): string | null {
  const span = compactHours(hours);
  return span ? `${span} behind` : null;
}

/**
 * Short marker next to the release label, or null when there is nothing to mark.
 * Without two build stamps there is no release-age gap; the marker then shows
 * how long the server has observed the host behind Stable, worded so it is not
 * read as the gap itself.
 *
 * A build that only differs names the Stable commit it differs from, and a host
 * that reported a release the server cannot compare (no commit) reads
 * "release unknown" rather than looking current (AGT-3689).
 */
export function releaseDriftLabel(
  drift: HostReleaseDrift | null | undefined,
  stable?: StableReleaseIdentity | null,
): string | null {
  if (!drift) return null;
  if (drift.state === 'differs') return differsFromStableLabel(stable);
  if (drift.state === 'unknown') return drift.release ? 'release unknown' : null;
  if (drift.state !== 'behind') return null;
  const observed = compactHours(drift.behindForHours);
  return releaseAgeLabel(drift.behindByHours)
    ?? (observed ? `behind Stable for ${observed}` : 'behind Stable');
}

/** "differs from Stable 3d61e8b", or without the sha when Stable reports none. */
export function differsFromStableLabel(stable: StableReleaseIdentity | null | undefined): string {
  const commit = shortCommit(stable?.commit);
  return commit ? `differs from Stable ${commit}` : 'differs from Stable';
}

/** The seven-character form of a hex commit; null for anything that is not a sha. */
export function shortCommit(commit: string | null | undefined): string | null {
  const value = commit?.trim();
  return value && /^[0-9a-f]{7,40}$/i.test(value) ? value.slice(0, 7) : null;
}

function compactHours(hours: number | null | undefined): string | null {
  if (hours === null || hours === undefined || !Number.isFinite(hours) || hours <= 0) return null;
  if (hours < 24) return `${Math.max(1, Math.round(hours))}h`;
  return `${Math.round(hours / 24)}d`;
}

/**
 * Only a drift past the grace window is acute (R4). A host that is merely a few
 * hours behind is a rolling update in progress and renders calm.
 */
export function releaseDriftTone(drift: HostReleaseDrift | null | undefined): 'ok' | 'warn' | 'calm' {
  // 'differs' follows 'behind': calm while the server has only just seen it.
  if (!drift || drift.state === 'unknown') return 'calm';
  if (drift.state === 'current') return 'ok';
  return drift.alarmDue ? 'warn' : 'calm';
}

export function releaseDriftTooltip(
  drift: HostReleaseDrift | null | undefined,
  stable: StableReleaseIdentity | null | undefined,
): string | null {
  if (!drift) return null;
  const commit = shortCommit(stable?.commit);
  const reference = stable ? ` Stable runs ${stable.version}${commit ? ` (${commit})` : ''}.` : '';
  return `${drift.reason}${reference}`;
}

/**
 * "Stable 0.9.5 · 3d61e8b" for the page header, so every row has a visible
 * reference. The commit matters because VERSION does not move with every
 * develop build (AGT-3689).
 */
export function stableReleaseLabel(stable: StableReleaseIdentity | null | undefined): string | null {
  const version = stable?.version?.trim();
  if (!version) return null;
  const commit = shortCommit(stable?.commit);
  return commit ? `Stable ${version} · ${commit}` : `Stable ${version}`;
}

/** True for a verdict that says the host does not run the Stable build. */
export function driftsFromStable(drift: HostReleaseDrift | null | undefined): boolean {
  return drift?.state === 'behind' || drift?.state === 'differs';
}

/** The machine-level verdict is the worst of its roles: one late role is drift. */
export function worstDrift(
  drifts: readonly (HostReleaseDrift | null | undefined)[],
): HostReleaseDrift | null {
  const known = drifts.filter((drift): drift is HostReleaseDrift => !!drift);
  if (!known.length) return null;
  return known.reduce((worst, drift) => rank(drift) > rank(worst) ? drift : worst);
}

function rank(drift: HostReleaseDrift): number {
  if (!driftsFromStable(drift)) return drift.state === 'unknown' ? 1 : 0;
  return drift.alarmDue ? 3 : 2;
}

/** Running versus expected review image for one role (AGT-3689). */
export interface ReviewImageComparison {
  running: string | null;
  expected: string | null;
  state: 'matches' | 'differs' | 'unpinned' | 'unreported';
}

/**
 * Puts the image a review role reports running next to the image the server
 * expects for its host. Null when neither side knows an image, so a coding
 * role renders nothing.
 */
export function reviewImageComparison(
  release: HostReleaseIdentity | null | undefined,
  drift: HostReleaseDrift | null | undefined,
): ReviewImageComparison | null {
  const running = release?.imageRef?.trim() || null;
  const expected = drift?.expectedImageRef?.trim() || null;
  if (!running && !expected) return null;
  const state = !running ? 'unreported'
    : !expected ? 'unpinned'
      : imageDigest(running) === imageDigest(expected) ? 'matches' : 'differs';
  return { running, expected, state };
}

/** The digest part of `repository@sha256:...`, which is what identifies the image. */
export function imageDigest(ref: string | null | undefined): string | null {
  const match = ref?.match(/@(sha256:[0-9a-f]{64})$/i);
  return match ? match[1].toLowerCase() : null;
}

/** "sha256:3f9c1a2b4d5e" - enough of the digest to compare at a glance. */
export function shortImageDigest(ref: string | null | undefined): string | null {
  return imageDigest(ref)?.slice(0, 19) ?? null;
}
