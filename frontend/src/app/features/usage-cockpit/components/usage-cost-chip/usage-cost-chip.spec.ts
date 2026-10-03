import { afterEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';

import type { UsageCostProjection } from '../../models/usage-cockpit.model';
import { UsageCostChipComponent } from './usage-cost-chip';

const cost: UsageCostProjection = {
  currency: 'USD',
  calendar: {
    timeZone: 'Europe/Berlin', weekStart: 1,
    dayStartUtc: '2026-09-24T22:00:00Z', dayEndUtc: '2026-09-25T22:00:00Z',
    weekStartUtc: '2026-09-20T22:00:00Z', weekEndUtc: '2026-09-27T22:00:00Z',
  },
  todayUsd: 12.48, weekUsd: 68.2, projects: [],
  coverage: { status: 'complete', observedAt: '2026-09-25T14:57:00Z', ttlSeconds: 60 },
  pricingVersion: 'TokenEconomy/1', normalizationVersion: 'v1',
  ledgerEndpointTemplate: '/api/projects/{project}/token-usage/summary',
};

async function mount(inputs: Record<string, unknown>) {
  await TestBed.configureTestingModule({
    imports: [UsageCostChipComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(UsageCostChipComponent);
  for (const [key, value] of Object.entries(inputs)) fixture.componentRef.setInput(key, value);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

describe('UsageCostChipComponent', () => {
  afterEach(() => vi.useRealTimers());

  it('renders no digits while loading', async () => {
    const fixture = await mount({ cost: null, now: Date.parse('2026-09-25T14:58:00Z') });
    expect((fixture.nativeElement as HTMLElement).textContent).not.toMatch(/\d/);
  });

  it('turns stale on its own once the ledger TTL elapses when the host passes no reference time', async () => {
    vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] });
    vi.setSystemTime(Date.parse('2026-09-25T14:57:30Z'));
    const fixture = await mount({ cost });
    const button = (fixture.nativeElement as HTMLElement).querySelector('button')!;
    expect(button.dataset['state']).toBe('normal');

    vi.advanceTimersByTime(2 * 60_000);
    fixture.detectChanges();
    expect(button.dataset['state']).toBe('stale');
  });
});
