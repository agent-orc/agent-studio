import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { RemoteHost } from '../../models/remote-host.model';
import { RemoteHostRoleRowComponent, roleSlotTotal } from './remote-host-role-row';

const ROLE: RemoteHost = {
  id: 'agent-runner-01-review',
  name: 'agent-runner-01-review',
  role: 'remote',
  serviceRole: 'review',
  roleMaxParallelism: 6,
  address: null,
  clientId: 'agent-runner-01-review',
  status: 'online',
  os: 'Linux',
  lastHeartbeatAt: '2026-08-12T07:00:00Z',
  uptimeLabel: null,
  capabilities: [],
  cliQuotas: [],
  stats: null,
};

describe('RemoteHostRoleRowComponent', () => {
  it('uses the role-local review ceiling instead of n/a', () => {
    expect(roleSlotTotal(ROLE)).toBe(6);

    TestBed.configureTestingModule({
      imports: [RemoteHostRoleRowComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(RemoteHostRoleRowComponent);
    fixture.componentRef.setInput('host', ROLE);
    fixture.componentRef.setInput('activeSlots', 2);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-role-label"]')?.textContent)
      .toContain('Review');
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-slots-summary"]')?.textContent)
      .toContain('2 / 6');
  });

  it('shows the recent restart time and lost-review count on the role row', () => {
    TestBed.configureTestingModule({
      imports: [RemoteHostRoleRowComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(RemoteHostRoleRowComponent);
    fixture.componentRef.setInput('host', {
      ...ROLE,
      restartedAt: '2026-09-07T04:55:00Z',
      reviewsLost: 6,
    });
    fixture.detectChanges();

    const restart = fixture.nativeElement.querySelector(
      '[data-testid="remote-host-role-restart"]',
    );
    expect(restart?.textContent).toContain('Restarted at');
    expect(restart?.textContent).toContain('6 reviews lost');
  });
});
