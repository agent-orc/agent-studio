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
  it('shows the role release id and a short, copyable commit beside the version', () => {
    TestBed.configureTestingModule({
      imports: [RemoteHostRoleRowComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(RemoteHostRoleRowComponent);
    fixture.componentRef.setInput('host', {
      ...ROLE,
      release: { releaseId: 'agt-review-20260911', version: '0.3.0', commit: 'abcdef1234567890' },
    });
    fixture.detectChanges();
    const row = fixture.nativeElement.querySelector('[data-testid="remote-host-role-release"]');
    expect(row.textContent).toContain('0.3.0');
    expect(row.textContent).toContain('agt-review-20260911');
    const commit = row.querySelector('[data-testid="remote-host-role-release-commit"]');
    expect(commit?.textContent).toContain('abcdef1');
    expect(commit?.getAttribute('aria-label')).toBe('Copy release commit abcdef1234567890');
  });

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

  it('shows review plane quota, ceiling, throttled share, and sustained alarm advice', () => {
    TestBed.configureTestingModule({
      imports: [RemoteHostRoleRowComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(RemoteHostRoleRowComponent);
    fixture.componentRef.setInput('host', {
      ...ROLE,
      roleMaxParallelism: 2,
      reviewPlane: {
        observedAt: '2026-09-18T14:00:00Z', cpuMax: '400000 100000',
        planeCpuCores: 4, cpuQuotaPercent: 400, hostCores: 12,
        workerEnvelopeCores: 2.4, workerEnvelopeCpuQuotaPercent: 480,
        currentCeiling: 2, rollingReviewDurationSeconds: 2453,
        throttledShare: 0.34, sustainedThrottling: true,
        alarmSuggestion: 'Raise the review role quota or lower the review ceiling.',
      },
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-review-plane"]')?.textContent)
      .toContain('quota 400% · ceiling 2 · throttled 34%');
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-review-plane-alarm"]')?.textContent)
      .toContain('Raise the review role quota or lower the review ceiling.');
  });
});
