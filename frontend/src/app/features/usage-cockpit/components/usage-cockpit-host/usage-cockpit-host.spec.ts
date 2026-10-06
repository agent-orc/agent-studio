import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, describe, expect, it } from 'vitest';

import type { UsageCli, UsageCockpitResponse } from '../../models/usage-cockpit.model';
import { UsageCockpitHostComponent } from './usage-cockpit-host';

function snapshot(workspaceId: string): UsageCockpitResponse {
  const now = Date.now();
  const observedAt = new Date(now - 1000).toISOString();
  const end = new Date(now + 86_400_000).toISOString();
  const start = new Date(now - 86_400_000).toISOString();
  const cli = (cliId: string, usedPct: number, limited: boolean): UsageCli => ({
    cliId, primary: cliId === 'codex', plan: null, source: null,
    windows: [{ id: `${cliId}/weekly`, label: 'Weekly', usedPct, resetAtUtc: end, resetLabel: null, suspiciousReason: null }],
    fetchedAt: observedAt, ttlSeconds: 600, suspicious: false, suspiciousReason: null,
    probeFailedAt: null, limited, limitedReason: limited ? 'Rate limit reached' : null,
    availability: { status: 'complete', observedAt, ttlSeconds: 600 },
  });
  const calendar = { timeZone: 'Europe/Berlin', weekStart: 1, dayStartUtc: start, dayEndUtc: end, weekStartUtc: start, weekEndUtc: end };
  return {
    snapshotVersion: 1, workspaceId, timeZone: calendar.timeZone, weekStart: 1,
    generatedAt: observedAt, calendar,
    clis: [cli('codex', 15, false), cli('claude', 3, true)],
    cost: {
      currency: 'USD', calendar, todayUsd: 52, weekUsd: 52, projects: [],
      coverage: { status: 'complete', observedAt, ttlSeconds: 600 },
      pricingVersion: 'test', normalizationVersion: 'test', ledgerEndpointTemplate: '',
      dailyBudgetUsd: 50,
    },
    runs: [], slots: [], sources: {},
  };
}

describe('production usage cockpit host', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('feeds the endpoint snapshot to the alarm service and real chips', async () => {
    TestBed.configureTestingModule({
      imports: [UsageCockpitHostComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(UsageCockpitHostComponent);
    fixture.componentInstance.secondaryVisible.set(true);
    fixture.componentRef.setInput('workspaceName', 'Workspace A');
    fixture.componentRef.setInput('workspaces', [{ id: 'ws-a', displayName: 'Workspace A', projects: [] }]);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const request = http.expectOne(req => req.url === '/api/usage/cockpit' && req.params.get('workspaceId') === 'ws-a');
    request.flush(snapshot('ws-a'));
    await fixture.whenStable();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="usage-cli-chip-claude"]')?.getAttribute('data-alarm')).toBe('limited');
    expect(root.querySelector('[data-testid="usage-cost-chip"]')?.getAttribute('data-alarm')).toBe('warning');
    expect(root.querySelector('[data-testid="usage-alarm-status"]')?.textContent).toContain('Claude is limited');

    fixture.componentInstance.secondaryVisible.set(false);
    fixture.componentInstance.phone.set(true);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="usage-cli-chip-claude"]')).toBeNull();
    expect(root.querySelector('[data-testid="usage-hidden-alarm"]')?.getAttribute('data-severity')).toBe('limited');
    expect(root.querySelector('[data-testid="usage-cost-chip"]')?.textContent).not.toContain('USD');

    fixture.componentRef.setInput('workspaceName', 'Workspace B');
    fixture.componentRef.setInput('workspaces', [{ id: 'ws-b', displayName: 'Workspace B', projects: [] }]);
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="usage-cli-chip-claude"]')).toBeNull();
    http.expectOne(req => req.params.get('workspaceId') === 'ws-b').flush(snapshot('ws-b'));
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="usage-hidden-alarm"]')?.getAttribute('data-severity')).toBe('limited');
    fixture.destroy();
  });

  it('shows a failed first read as unavailable, then recovers on a trusted read', async () => {
    TestBed.configureTestingModule({
      imports: [UsageCockpitHostComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(UsageCockpitHostComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const initial = http.expectOne(req => req.url === '/api/usage/cockpit');
    initial.flush('Unavailable', { status: 503, statusText: 'Service Unavailable' });
    await fixture.whenStable();

    const root = fixture.nativeElement as HTMLElement;
    const cli = root.querySelector('[data-testid="usage-cli-chip-codex"]');
    const cost = root.querySelector('[data-testid="usage-cost-chip"]');
    expect(cli?.getAttribute('data-state')).toBe('unknown');
    expect(cli?.getAttribute('aria-label')).toContain('could not be loaded');
    expect(cost?.getAttribute('data-state')).toBe('unknown');
    expect(cost?.textContent).toContain('N/A');
    expect(cost?.getAttribute('aria-label')).not.toContain('$0.00');

    fixture.componentInstance.refresh();
    http.expectOne(req => req.url === '/api/usage/cockpit').flush(snapshot('ws-a'));
    await fixture.whenStable();
    expect(cli?.getAttribute('data-state')).toBe('normal');
    expect(cost?.getAttribute('data-alarm')).toBe('warning');

    fixture.componentInstance.refresh();
    http.expectOne(req => req.url === '/api/usage/cockpit')
      .flush('Unavailable', { status: 503, statusText: 'Service Unavailable' });
    await fixture.whenStable();
    expect(cli?.getAttribute('data-state')).toBe('stale');
    expect(cost?.getAttribute('data-state')).toBe('stale');
    expect(cost?.getAttribute('data-alarm')).toBe('warning');
    expect(cost?.getAttribute('aria-label')).toContain('latest refresh failed');
    fixture.destroy();
  });
});
