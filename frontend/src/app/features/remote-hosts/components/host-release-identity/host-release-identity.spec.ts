import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { RemoteHost } from '../../models/remote-host.model';
import { HostReleaseIdentityComponent } from './host-release-identity';

const HOST: RemoteHost = {
  id: 'agent-runner-01',
  name: 'agent-runner-01',
  role: 'remote',
  address: null,
  clientId: 'agent-runner-01',
  status: 'online',
  os: 'Linux',
  lastHeartbeatAt: '2026-09-15T08:00:00Z',
  uptimeLabel: null,
  capabilities: [],
  cliQuotas: [],
  stats: null,
  releaseId: 'agt-2660',
  release: {
    releaseId: 'agt-2660',
    version: '0.2.7',
    commit: 'abc123',
    builtAt: '2026-08-23T08:00:00Z',
  },
  releaseDrift: {
    runnerId: 'agent-runner-01',
    name: 'agent-runner-01',
    hostId: 'agent-runner-01',
    role: 'coding',
    release: null,
    state: 'behind',
    behindByHours: 456,
    behindForHours: 456,
    alarmDue: true,
    reason: 'Runner release is older than Stable.',
    lastSeenAt: '2026-09-15T08:00:00Z',
    heartbeatStale: false,
  },
};

describe('HostReleaseIdentityComponent', () => {
  it('renders the version, drift age, warning tone, and Stable comparison', () => {
    TestBed.configureTestingModule({
      imports: [HostReleaseIdentityComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(HostReleaseIdentityComponent);
    fixture.componentRef.setInput('host', HOST);
    fixture.componentRef.setInput('stableRelease', { version: '0.3.0' });
    fixture.componentRef.setInput('testIdPrefix', 'remote-host-release');
    fixture.componentRef.setInput('now', Date.parse('2026-09-15T08:05:00Z'));
    fixture.detectChanges();

    const marker = fixture.nativeElement.querySelector('[data-testid="remote-host-release-drift"]');
    expect(fixture.nativeElement.textContent).toContain('0.2.7');
    expect(marker?.textContent).toContain('19d behind');
    expect(marker?.getAttribute('data-tone')).toBe('warn');
    expect(fixture.componentInstance.tooltip()).toContain('Stable runs 0.3.0');
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-release-stale"]')?.textContent)
      .toContain('5m ago');
  });

  it('falls back to the legacy release id and renders no drift marker', () => {
    TestBed.configureTestingModule({
      imports: [HostReleaseIdentityComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(HostReleaseIdentityComponent);
    fixture.componentRef.setInput('host', { ...HOST, release: undefined, releaseDrift: null });
    fixture.componentRef.setInput('testIdPrefix', 'remote-host-role-release');
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('agt-2660');
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-role-release-drift"]')).toBeNull();
  });

  it('ages a release from its report when the client heartbeat is newer', () => {
    TestBed.configureTestingModule({ imports: [HostReleaseIdentityComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(HostReleaseIdentityComponent);
    fixture.componentRef.setInput('host', {
      ...HOST, release: HOST.release!, releaseDrift: { ...HOST.releaseDrift!, release: HOST.release! },
      lastHeartbeatAt: '2026-09-15T08:04:30Z',
    });
    fixture.componentRef.setInput('testIdPrefix', 'remote-host-release');
    fixture.componentRef.setInput('now', Date.parse('2026-09-15T08:05:00Z'));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-release-stale"]')?.textContent)
      .toContain('5m ago');
  });

  it('renders a commit chip only for a hex commit and recovers one from the release id', () => {
    TestBed.configureTestingModule({ imports: [HostReleaseIdentityComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(HostReleaseIdentityComponent);
    fixture.componentRef.setInput('host', { ...HOST, release: { ...HOST.release!, releaseId: '20260928T1211Z-v0.9.4-6759fe303', commit: 'sha' } });
    fixture.componentRef.setInput('testIdPrefix', 'remote-host-release');
    fixture.componentRef.setInput('showIdentity', true);
    fixture.detectChanges();
    expect(fixture.componentInstance.commit()).toBe('6759fe303');
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-release-commit"]')?.textContent).toContain('6759fe3');
    fixture.componentRef.setInput('host', { ...HOST, release: { ...HOST.release!, releaseId: 'local-release', commit: 'sha' } });
    fixture.detectChanges();
    expect(fixture.componentInstance.commit()).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-release-commit"]')).toBeNull();
  });

  /** AGT-3648 S4-F12: same version, older develop commit. */
  it('marks a build that differs from Stable with the Stable commit', () => {
    TestBed.configureTestingModule({ imports: [HostReleaseIdentityComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(HostReleaseIdentityComponent);
    fixture.componentRef.setInput('host', {
      ...HOST,
      release: { releaseId: '20261105T090000Z-review-f947f2b1c3d4', version: '0.9.5', commit: 'f947f2b1c3d4' },
      releaseDrift: { ...HOST.releaseDrift!, state: 'differs', behindByHours: null, behindForHours: 1, alarmDue: false,
        reason: 'The host runs commit f947f2b while Stable runs 3d61e8b, both as version 0.9.5.' },
    });
    fixture.componentRef.setInput('stableRelease', { version: '0.9.5', commit: '3d61e8b0c4ab' });
    fixture.componentRef.setInput('testIdPrefix', 'remote-host-release');
    fixture.componentRef.setInput('now', Date.parse('2026-09-15T08:00:30Z'));
    fixture.detectChanges();

    const marker = fixture.nativeElement.querySelector('[data-testid="remote-host-release-drift"]');
    expect(marker?.textContent).toContain('differs from Stable 3d61e8b');
    expect(marker?.getAttribute('data-tone')).toBe('calm');
    expect(fixture.componentInstance.tooltip()).toContain('Stable runs 0.9.5 (3d61e8b)');
  });

  it('reads a host without a comparable commit as release unknown', () => {
    TestBed.configureTestingModule({ imports: [HostReleaseIdentityComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(HostReleaseIdentityComponent);
    fixture.componentRef.setInput('host', {
      ...HOST,
      release: { releaseId: '0.9.5+sha.unknown', version: '0.9.5' },
      releaseDrift: { ...HOST.releaseDrift!, state: 'unknown', behindByHours: null, behindForHours: null, alarmDue: false,
        release: { releaseId: '0.9.5+sha.unknown', version: '0.9.5' },
        reason: 'The host reported no commit, so its release cannot be confirmed as Stable.' },
    });
    fixture.componentRef.setInput('testIdPrefix', 'remote-host-release');
    fixture.componentRef.setInput('now', Date.parse('2026-09-15T08:00:30Z'));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-release-drift"]')?.textContent)
      .toContain('release unknown');
  });
});
