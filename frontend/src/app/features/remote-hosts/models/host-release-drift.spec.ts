import { describe, expect, it } from 'vitest';
import {
  releaseAgeLabel,
  releaseDriftLabel,
  releaseDriftTone,
  releaseDriftTooltip,
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
    expect(releaseDriftLabel(drift({ behindByHours: null }))).toBe('behind Stable');
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
});
