import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import type { ApiStallSnapshot } from '../../../../models/api-stall.model';
import { ApiStallPanelComponent } from './api-stall-panel.component';

function snapshot(overrides: Partial<ApiStallSnapshot> = {}): ApiStallSnapshot {
  return {
    status: 'ok',
    checkedAtUtc: '2026-11-01T09:00:00Z',
    intervalMs: 500,
    warningThresholdMs: 1000,
    alarmThresholdMs: 5000,
    alarmWindowMinutes: 60,
    maxDelayMs: 0,
    recent: [],
    ...overrides,
  };
}

async function mount() {
  await TestBed.configureTestingModule({
    imports: [ApiStallPanelComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ApiStallPanelComponent);
  const http = TestBed.inject(HttpTestingController);
  const request = http.expectOne('/api/diagnostics/api-stalls');
  return { fixture, request, host: fixture.nativeElement as HTMLElement };
}

describe('ApiStallPanelComponent', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('stays quiet while no stall is recorded', async () => {
    const { fixture, request, host } = await mount();
    request.flush(snapshot());
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="api-stall-status"]')?.textContent?.trim()).toBe('Responsive');
    expect(host.querySelector('[data-testid="api-stall-recent"]')).toBeNull();
    expect(host.textContent).toContain('No stall since the API started');
  });

  it('shows the alarm with the delay of each recent stall', async () => {
    const { fixture, request, host } = await mount();
    request.flush(snapshot({
      status: 'alarm',
      maxDelayMs: 61_000,
      recent: [
        { detectedAtUtc: '2026-11-01T09:10:00Z', delayMs: 61_000, gcPauseMs: 58_200, threadPoolThreads: 1480, pendingWorkItems: 312, processThreads: 1530 },
        { detectedAtUtc: '2026-11-01T09:05:00Z', delayMs: 1_400, gcPauseMs: 0, threadPoolThreads: 40, pendingWorkItems: 2, processThreads: 90 },
      ],
    }));
    fixture.detectChanges();
    const panel = host.querySelector('[data-testid="api-stall-panel"]');
    expect(panel?.getAttribute('data-status')).toBe('alarm');
    expect(host.querySelector('[data-testid="api-stall-status"]')?.textContent?.trim()).toBe('Stalled');
    expect(host.querySelector('[data-testid="api-stall-summary"]')?.textContent).toContain('61.0 s');
    const rows = host.querySelectorAll('[data-testid="api-stall-recent"] tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[0].getAttribute('data-alarm')).toBe('true');
    expect(rows[0].textContent).toContain('58.2 s');
    expect(rows[0].textContent).toContain('1480');
    expect(rows[1].getAttribute('data-alarm')).toBe('false');
  });

  it('hides itself when the route cannot be read', async () => {
    const { fixture, request, host } = await mount();
    request.flush('forbidden', { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="api-stall-panel"]')).toBeNull();
  });
});
