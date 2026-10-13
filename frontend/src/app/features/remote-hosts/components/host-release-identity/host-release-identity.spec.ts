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
    fixture.detectChanges();

    const marker = fixture.nativeElement.querySelector('[data-testid="remote-host-release-drift"]');
    expect(fixture.nativeElement.textContent).toContain('0.2.7');
    expect(marker?.textContent).toContain('19d behind');
    expect(marker?.getAttribute('data-tone')).toBe('warn');
    expect(fixture.componentInstance.tooltip()).toContain('Stable runs 0.3.0');
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
});
