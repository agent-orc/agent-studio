import { describe, expect, it } from 'vitest';
import {
  differsFromStableLabel,
  releaseAgeLabel,
  releaseDriftLabel,
  releaseDriftTone,
  releaseDriftTooltip,
  reviewImageComparison,
  shortImageDigest,
  stableReleaseLabel,
  worstDrift,
  type HostReleaseDrift,
} from './host-release-drift';

function drift(overrides: Partial<HostReleaseDrift> = {}): HostReleaseDrift {
  return {
    runnerId: 'agent-runner-01',
    name: 'agent-runner-01',
    hostId: 'agent-runner-01',
    role: 'coding',
    release: {
      releaseId: 'agt-host-20260823T060000Z-bbbbbbb',
      version: '0.2.7',
      commit: 'bbbbbbb2222',
      builtAt: '2026-08-23T06:00:00Z',
    },
    state: 'behind',
    behindByHours: 458,
    behindForHours: 458,
    alarmDue: true,
    reason: 'The host build is 19d 2h older than the Stable build.',
    lastSeenAt: '2026-09-15T11:59:00Z',
    heartbeatStale: false,
    ...overrides,
  };
}

describe('host release drift labels (AGT-2826)', () => {
  it('renders the age a host lags the Stable release', () => {
    expect(releaseDriftLabel(drift())).toBe('19d behind');
    expect(releaseDriftLabel(drift({ behindByHours: 5, behindForHours: 5, alarmDue: false })))
      .toBe('5h behind');
  });

  it('never claims drift for a current or unreported host', () => {
    expect(releaseDriftLabel(drift({ state: 'current' }))).toBeNull();
    expect(releaseDriftLabel(drift({ state: 'unknown', release: null }))).toBeNull();
    expect(releaseDriftLabel(null)).toBeNull();
  });

  /** An ordering without a measurable gap still has to say something honest. */
  it('falls back to a wordy marker when the age is unknown', () => {
    expect(releaseDriftLabel(drift({ behindByHours: null, behindForHours: null }))).toBe('behind Stable');
    expect(releaseDriftLabel(drift({ behindByHours: null, behindForHours: 0 }))).toBe('behind Stable');
  });

  /**
   * Live 12.10.2026: Stable carried no build stamp, so every behind host read
   * "behind Stable" with no age although the server had observed it for hours.
   */
  it('shows the observed duration when the release-age gap is unknown', () => {
    expect(releaseDriftLabel(drift({ behindByHours: null, behindForHours: 3.5, alarmDue: false })))
      .toBe('behind Stable for 4h');
    expect(releaseDriftLabel(drift({ behindByHours: null, behindForHours: 50 })))
      .toBe('behind Stable for 2d');
  });

  it('prefers the release-age gap over the observed duration', () => {
    expect(releaseDriftLabel(drift({ behindByHours: 5, behindForHours: 30 }))).toBe('5h behind');
  });

  it('rounds a sub-hour gap up so the marker never reads zero', () => {
    expect(releaseAgeLabel(0.4)).toBe('1h behind');
    expect(releaseAgeLabel(0)).toBeNull();
    expect(releaseAgeLabel(null)).toBeNull();
  });

  /** Only a drift past the grace window is acute (R4). */
  it('keeps a rolling update calm and marks a settled drift as warn', () => {
    expect(releaseDriftTone(drift({ alarmDue: false }))).toBe('calm');
    expect(releaseDriftTone(drift())).toBe('warn');
    expect(releaseDriftTone(drift({ state: 'current' }))).toBe('ok');
    expect(releaseDriftTone(drift({ state: 'unknown' }))).toBe('calm');
    expect(releaseDriftTone(null)).toBe('calm');
  });

  it('explains the verdict against the named Stable release', () => {
    expect(releaseDriftTooltip(drift(), { version: '0.3.0' }))
      .toBe('The host build is 19d 2h older than the Stable build. Stable runs 0.3.0.');
    expect(releaseDriftTooltip(null, { version: '0.3.0' })).toBeNull();
  });

  it('names the Stable release for the header, or nothing when it is unknown', () => {
    expect(stableReleaseLabel({ version: '0.3.0' })).toBe('Stable 0.3.0');
    expect(stableReleaseLabel({ version: '0.9.5', commit: '3d61e8b0c4' })).toBe('Stable 0.9.5 · 3d61e8b');
    expect(stableReleaseLabel({ version: '0.9.5', commit: 'unknown' })).toBe('Stable 0.9.5');
    expect(stableReleaseLabel({ version: '  ' })).toBeNull();
    expect(stableReleaseLabel(null)).toBeNull();
  });

  /** A machine must never read calmer than the roles it summarises (R3). */
  it('summarises a machine with the worst of its role verdicts', () => {
    const current = drift({ state: 'current', alarmDue: false });
    const lagging = drift({ alarmDue: false });
    const alarmed = drift();

    expect(worstDrift([current, lagging])).toBe(lagging);
    expect(worstDrift([current, lagging, alarmed])).toBe(alarmed);
    expect(worstDrift([current])).toBe(current);
    expect(worstDrift([null, undefined])).toBeNull();
  });

  /**
   * AGT-3648 S4-F12: a review host built from an older develop commit kept
   * VERSION 0.9.5 and read "current". The server now says "differs"; the
   * marker names the Stable commit it differs from.
   */
  it('marks a build that only differs from Stable with the Stable commit', () => {
    const differs = drift({ state: 'differs', behindByHours: null, behindForHours: 2, alarmDue: false });
    expect(releaseDriftLabel(differs, { version: '0.9.5', commit: '3d61e8b0c4' })).toBe('differs from Stable 3d61e8b');
    expect(releaseDriftLabel(differs, { version: '0.9.5' })).toBe('differs from Stable');
    expect(differsFromStableLabel({ version: '0.9.5', commit: 'sha' })).toBe('differs from Stable');
    expect(releaseDriftTone(differs)).toBe('calm');
    expect(releaseDriftTone({ ...differs, alarmDue: true })).toBe('warn');
    expect(worstDrift([drift({ state: 'current', alarmDue: false }), differs])).toBe(differs);
  });

  it('reads a reported release without a comparable commit as release unknown', () => {
    expect(releaseDriftLabel(drift({ state: 'unknown', release: { releaseId: '0.9.5+sha.unknown', version: '0.9.5' } })))
      .toBe('release unknown');
  });

  it('compares the running review image with the one the server expects', () => {
    const running = `registry.example/review@sha256:${'a'.repeat(64)}`;
    const expected = `registry.example/review@sha256:${'b'.repeat(64)}`;
    expect(reviewImageComparison({ releaseId: 'r', version: '0.9.5', imageRef: running }, drift({ expectedImageRef: expected })))
      .toEqual({ running, expected, state: 'differs' });
    expect(reviewImageComparison({ releaseId: 'r', version: '0.9.5', imageRef: running },
      drift({ expectedImageRef: `other.example/review@sha256:${'a'.repeat(64)}` }))?.state).toBe('matches');
    expect(reviewImageComparison({ releaseId: 'r', version: '0.9.5', imageRef: running }, drift())?.state).toBe('unpinned');
    expect(reviewImageComparison({ releaseId: 'r', version: '0.9.5' }, drift({ expectedImageRef: expected }))?.state).toBe('unreported');
    expect(reviewImageComparison({ releaseId: 'r', version: '0.9.5' }, drift())).toBeNull();
    expect(shortImageDigest(running)).toBe('sha256:aaaaaaaaaaaa');
  });
});
