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

const RETIRED_ROLE: RemoteHost = {
  ...ROLE,
  id: 'e2e-leftover-1',
  name: 'e2e-leftover-1',
  clientId: 'e2e-leftover-1',
  serviceRole: 'runner',
  status: 'retired',
};

describe('RemoteHostRoleRowComponent', () => {
  it('offers Delete next to Revive for a retired role, and emits delete', () => {
    TestBed.configureTestingModule({
      imports: [RemoteHostRoleRowComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(RemoteHostRoleRowComponent);
    fixture.componentRef.setInput('host', RETIRED_ROLE);
    fixture.detectChanges();

    let emitted: { kind: string; id: string } | undefined;
    fixture.componentInstance.action.subscribe((event) => (emitted = event));

    const reviveButton = fixture.nativeElement.querySelector(
      '[data-testid="remote-host-action-revive"]',
    ) as HTMLButtonElement | null;
    const deleteButton = fixture.nativeElement.querySelector(
      '[data-testid="remote-host-action-delete"]',
    ) as HTMLButtonElement | null;
    expect(reviveButton).toBeTruthy();
    expect(deleteButton).toBeTruthy();

    deleteButton!.click();
    expect(emitted).toEqual({ kind: 'delete', id: RETIRED_ROLE.id });
  });

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
