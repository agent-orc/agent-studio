import { afterEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';

import type { UsageCli } from '../../models/usage-cockpit.model';
import { UsageCliChipComponent } from './usage-cli-chip';

const NOW = Date.parse('2026-09-25T14:58:00Z');

const codex: UsageCli = {
  cliId: 'codex', primary: true, plan: null, source: null,
  windows: [
    { id: 'codex/weekly', label: 'Weekly', usedPct: 15, resetAtUtc: null, resetLabel: null, suspiciousReason: null },
    { id: 'codex/5-hour', label: '5-hour', usedPct: 32, resetAtUtc: null, resetLabel: null, suspiciousReason: null },
  ],
  fetchedAt: '2026-09-25T14:55:00Z', ttlSeconds: 600,
  suspicious: false, suspiciousReason: null, probeFailedAt: null, limited: false, limitedReason: null,
  availability: { status: 'complete', observedAt: '2026-09-25T14:55:00Z', ttlSeconds: 600 },
};

async function mount(inputs: Record<string, unknown>) {
  await TestBed.configureTestingModule({
    imports: [UsageCliChipComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(UsageCliChipComponent);
  for (const [key, value] of Object.entries(inputs)) fixture.componentRef.setInput(key, value);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

describe('UsageCliChipComponent', () => {
  afterEach(() => vi.useRealTimers());

  it('is a single dialog trigger with its full accessible name', async () => {
    const fixture = await mount({
      cliId: 'codex', cli: codex, timeZone: 'Europe/Berlin', now: NOW, expanded: true, controls: 'usage-detail',
    });
    const host = fixture.nativeElement as HTMLElement;
    const buttons = host.querySelectorAll('button');
    expect(buttons).toHaveLength(1);
    const button = buttons[0];
    expect(button.type).toBe('button');
    expect(button.getAttribute('aria-haspopup')).toBe('dialog');
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(button.getAttribute('aria-controls')).toBe('usage-detail');
    expect(button.getAttribute('aria-label'))
      .toBe('Codex, weekly 15 percent used, current five-hour window 32 percent used. Open usage.');
    const parts = Array.from(button.querySelectorAll('span'))
      .filter(span => span.children.length === 0)
      .map(span => span.textContent?.trim());
    expect(parts).toEqual(['Codex', 'WK', '15%', '5H', '32%']);
  });

  it('emits its id on activation', async () => {
    const fixture = await mount({ cliId: 'codex', cli: codex, now: NOW });
    const emitted: string[] = [];
    fixture.componentInstance.activate.subscribe(id => emitted.push(id));
    (fixture.nativeElement as HTMLElement).querySelector('button')!.click();
    expect(emitted).toEqual(['codex']);
  });

  it('renders no digits while loading', async () => {
    const fixture = await mount({ cliId: 'codex', cli: null, now: NOW });
    expect((fixture.nativeElement as HTMLElement).textContent).not.toMatch(/\d/);
  });

  it('turns stale on its own once the TTL elapses when the host passes no reference time', async () => {
    vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] });
    vi.setSystemTime(Date.parse('2026-09-25T14:56:00Z'));
    const fixture = await mount({ cliId: 'codex', cli: codex });
    const button = (fixture.nativeElement as HTMLElement).querySelector('button')!;
    expect(button.dataset['state']).toBe('normal');

    vi.advanceTimersByTime(10 * 60_000);
    fixture.detectChanges();
    expect(button.dataset['state']).toBe('stale');

    fixture.destroy();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('uses the host reference time instead of its own clock when one is passed', async () => {
    vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] });
    vi.setSystemTime(Date.parse('2026-09-25T16:00:00Z'));
    const fixture = await mount({ cliId: 'codex', cli: codex, now: NOW });
    const button = (fixture.nativeElement as HTMLElement).querySelector('button')!;
    expect(button.dataset['state']).toBe('normal');
  });
});
