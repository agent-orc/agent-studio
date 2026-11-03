import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { CapabilityHealthComponent } from './capability-health';
import type { RemoteHost } from '../../models/remote-host.model';

describe('CapabilityHealthComponent', () => {
  it('distinguishes automatic drain and exposes recovery facts', () => {
    TestBed.configureTestingModule({ imports: [CapabilityHealthComponent] });
    const fixture = TestBed.createComponent(CapabilityHealthComponent);
    fixture.componentRef.setInput('host', {
      id: 'runner', name: 'Runner', role: 'remote', address: null, clientId: 'runner',
      status: 'draining', os: 'Linux', lastHeartbeatAt: new Date().toISOString(),
      uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null,
      hostAdmission: {
        hostId: 'host', admissionState: 'automatic-draining',
        automaticDrainReason: 'host:disk: DiskFull',
      },
      capabilityHealth: [{
        key: 'host:disk', category: 'foundation', advertisedStatus: 'ready',
        healthState: 'draining', reason: 'DiskFull', advertisedAt: new Date().toISOString(),
        freshUntil: new Date().toISOString(), isFresh: true, consecutiveFailures: 1,
        canaryClaimId: null, affectedClaims: ['run:one'], recoveryHistory: [],
      }],
    } satisfies RemoteHost);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Automatic whole-host drain');
    expect(text).toContain('host:disk');
    expect(text).toContain('run:one');
    expect(text).not.toContain('Operator-requested');
  });

  it('surfaces a stale Task Server route as an acute host state', () => {
    TestBed.configureTestingModule({ imports: [CapabilityHealthComponent] });
    const fixture = TestBed.createComponent(CapabilityHealthComponent);
    fixture.componentRef.setInput('host', {
      id: 'runner', name: 'Runner', role: 'remote', address: null, clientId: 'runner',
      status: 'offline', os: 'Linux', lastHeartbeatAt: '2026-08-01T15:30:00Z',
      uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null,
      capabilityHealth: [{
        key: 'task-server:connectivity', category: 'foundation', advertisedStatus: 'ready',
        healthState: 'healthy', reason: null, advertisedAt: '2026-08-01T15:30:00Z',
        freshUntil: '2026-08-01T15:33:00Z', isFresh: false, consecutiveFailures: 0,
        affectedClaims: [], recoveryHistory: [],
      }],
    } satisfies RemoteHost);
    fixture.detectChanges();

    const route: HTMLElement = fixture.nativeElement.querySelector(
      '[data-testid="remote-host-task-server-route-state"]');
    expect(route.getAttribute('data-tone')).toBe('unreachable');
    expect(route.textContent).toContain('Task Server route unreachable');
    expect(route.textContent).toContain('Check the tunnel');
  });

  it('does not label a fresh unavailable provider advertisement as healthy', () => {
    TestBed.configureTestingModule({ imports: [CapabilityHealthComponent] });
    const fixture = TestBed.createComponent(CapabilityHealthComponent);
    fixture.componentRef.setInput('host', {
      id: 'runner', name: 'Runner', role: 'remote', address: null, clientId: 'runner',
      status: 'degraded', os: 'Linux', lastHeartbeatAt: new Date().toISOString(),
      uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null,
      capabilityHealth: [{
        key: 'provider-auth:claude', category: 'provider-auth', advertisedStatus: 'unavailable',
        healthState: 'healthy', reason: null, detail: 'Not logged in',
        advertisedAt: new Date().toISOString(), freshUntil: new Date().toISOString(),
        isFresh: true, consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [],
      }],
    } satisfies RemoteHost);
    fixture.detectChanges();

    const capability: HTMLElement = fixture.nativeElement.querySelector(
      '[data-testid="remote-host-capability-provider-auth:claude"]',
    );
    expect(capability.getAttribute('data-state')).toBe('unavailable');
    expect(capability.textContent).toContain('unavailable');
    expect(capability.textContent).not.toContain('healthy');
    const state = capability.querySelector<HTMLElement>('[data-testid="remote-host-capability-state-provider-auth:claude"]')!;
    expect([state.dataset['tone'], state.dataset['icon']]).toEqual(['danger', 'close']);
  });
});
