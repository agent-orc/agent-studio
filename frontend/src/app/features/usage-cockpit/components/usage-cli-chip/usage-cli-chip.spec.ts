import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, type Type } from '@angular/core';

import type { UsageCli } from '../../models/usage-cockpit.model';
import { UsageCostChipComponent } from '../usage-cost-chip/usage-cost-chip';
import { UsageSlotChipComponent } from '../usage-slot-chip/usage-slot-chip';
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

async function mount<T>(type: Type<T>, inputs: Record<string, unknown>) {
  await TestBed.configureTestingModule({
    imports: [type],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(type);
  for (const [key, value] of Object.entries(inputs)) fixture.componentRef.setInput(key, value);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

describe('usage chips render one native button', () => {
  it('CLI chip is a single dialog trigger with its full accessible name', async () => {
    const fixture = await mount(UsageCliChipComponent, {
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

  it('CLI chip emits its id on activation', async () => {
    const fixture = await mount(UsageCliChipComponent, { cliId: 'codex', cli: codex, now: NOW });
    const emitted: string[] = [];
    fixture.componentInstance.activate.subscribe(id => emitted.push(id));
    (fixture.nativeElement as HTMLElement).querySelector('button')!.click();
    expect(emitted).toEqual(['codex']);
  });

  it('loading chips render no digits', async () => {
    const cliFixture = await mount(UsageCliChipComponent, { cliId: 'codex', cli: null, now: NOW });
    expect((cliFixture.nativeElement as HTMLElement).textContent).not.toMatch(/\d/);
    TestBed.resetTestingModule();
    const costFixture = await mount(UsageCostChipComponent, { cost: null, now: NOW });
    expect((costFixture.nativeElement as HTMLElement).textContent).not.toMatch(/\d/);
  });

  it('slot chip is an in-place disclosure with the shared marker first', async () => {
    const fixture = await mount(UsageSlotChipComponent, {
      slots: [{ name: 'remote', occupied: 2, capacity: 3, availability: { status: 'complete', observedAt: null, ttlSeconds: null } }],
      expanded: false,
    });
    const button = (fixture.nativeElement as HTMLElement).querySelector('button')!;
    expect(button.firstElementChild?.tagName.toLowerCase()).toBe('app-disclosure-marker');
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(button.hasAttribute('aria-haspopup')).toBe(false);
  });
});
