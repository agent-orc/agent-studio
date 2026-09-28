import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';

import type { UsageCli, UsageCockpitResponse } from '../../models/usage-cockpit.model';
import { UsageAlarmStateService } from '../../state/usage-alarm-state.service';
import {
  alarmsForSource, EMPTY_USAGE_ALARM_STATE, hiddenProviderAlarms, observeSnapshot, reduceUsageAlarms,
} from '../../usage-alarm.policy';
import { UsageCliChipComponent } from '../usage-cli-chip/usage-cli-chip';
import { UsageAlarmStatusComponent } from './usage-alarm-status';

const NOW = Date.parse('2026-09-25T14:58:00Z');

function cli(cliId: string, weekly: number, limited = false): UsageCli {
  return {
    cliId, primary: cliId === 'codex', plan: null, source: null,
    windows: [
      { id: `${cliId}/weekly`, label: 'Weekly', usedPct: weekly, resetAtUtc: '2026-09-29T07:00:00Z', resetLabel: null, suspiciousReason: null },
      { id: `${cliId}/5-hour`, label: '5-hour', usedPct: 20, resetAtUtc: '2026-09-25T16:30:00Z', resetLabel: null, suspiciousReason: null },
    ],
    fetchedAt: '2026-09-25T14:55:00Z', ttlSeconds: 600,
    suspicious: false, suspiciousReason: null, probeFailedAt: null,
    limited, limitedReason: limited ? 'Rate limit reached' : null,
    availability: { status: 'complete', observedAt: '2026-09-25T14:55:00Z', ttlSeconds: 600 },
  };
}

const snapshot = (clis: UsageCli[]): UsageCockpitResponse => ({
  snapshotVersion: 1, workspaceId: 'ws-1', timeZone: 'Europe/Berlin', weekStart: 1, generatedAt: '2026-09-25T14:58:00Z',
  calendar: { timeZone: 'Europe/Berlin', weekStart: 1, dayStartUtc: '', dayEndUtc: '', weekStartUtc: '', weekEndUtc: '' },
  clis, cost: null as never, runs: [], slots: [], sources: {},
});

describe('usage alarm status region', () => {
  it('is a polite atomic status region that speaks a transition once', async () => {
    TestBed.configureTestingModule({ imports: [UsageAlarmStatusComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(UsageAlarmStatusComponent);
    const service = TestBed.inject(UsageAlarmStateService);
    fixture.detectChanges();
    const region = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="usage-alarm-status"]')!;
    expect(region.getAttribute('role')).toBe('status');
    expect(region.getAttribute('aria-live')).toBe('polite');
    expect(region.getAttribute('aria-atomic')).toBe('true');
    expect(region.textContent?.trim()).toBe('');

    service.ingest(snapshot([cli('codex', 85)]), NOW);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(region.textContent?.trim()).toBe('Codex weekly quota 85 percent used, above 80 percent.');

    // A numeric tick changes no alarm and leaves the region untouched.
    const before = region.firstChild;
    service.ingest(snapshot([cli('codex', 86)]), NOW + 30_000);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(region.firstChild).toBe(before);
    expect(region.textContent?.trim()).toBe('Codex weekly quota 85 percent used, above 80 percent.');
  });
});

function latched(clis: UsageCli[]) {
  return reduceUsageAlarms(EMPTY_USAGE_ALARM_STATE, 'ws-1', observeSnapshot(snapshot(clis), NOW), NOW).state;
}

describe('CLI chip alarm treatments', () => {
  async function chip(inputs: Record<string, unknown>) {
    TestBed.configureTestingModule({ imports: [UsageCliChipComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(UsageCliChipComponent);
    for (const [key, value] of Object.entries(inputs)) fixture.componentRef.setInput(key, value);
    fixture.detectChanges();
    await fixture.whenStable();
    return (fixture.nativeElement as HTMLElement).querySelector('button')!;
  }

  it('spells Limited on the full chip and uses the mark on the compact chip', async () => {
    const alarms = alarmsForSource(latched([cli('codex', 12, true)]), 'cli:codex');

    const full = await chip({ cliId: 'codex', cli: cli('codex', 12, true), now: NOW, alarms });
    expect(full.dataset['alarm']).toBe('limited');
    expect(full.querySelector('[data-testid="usage-cli-chip-alarm-label"]')?.textContent).toBe('Limited');
    expect(full.querySelector('[data-window="session"]')).not.toBeNull();

    TestBed.resetTestingModule();
    const compact = await chip({ cliId: 'codex', cli: cli('codex', 12, true), now: NOW, alarms, compact: true });
    expect(compact.querySelector('[data-testid="usage-cli-chip-alarm-label"]')).toBeNull();
    expect(compact.querySelector('[data-testid="usage-cli-chip-alarm-mark"]')).not.toBeNull();
    expect(compact.querySelector('[data-window="session"]')).toBeNull();
    expect(compact.getAttribute('aria-label')).toContain('Limited: Codex is limited by the provider');
  });

  it('marks hidden-provider alarms on the primary chip without another number', async () => {
    const state = latched([cli('codex', 15), cli('claude', 3, true)]);
    const button = await chip({
      cliId: 'codex', cli: cli('codex', 15), now: NOW, compact: true,
      alarms: alarmsForSource(state, 'cli:codex'), hiddenAlarms: hiddenProviderAlarms(state, ['codex']),
    });
    const mark = button.querySelector('[data-testid="usage-hidden-alarm"]')!;
    expect(mark.getAttribute('aria-hidden')).toBe('true');
    expect(mark.textContent?.trim()).toBe('');
    expect(button.dataset['alarm']).toBeUndefined();
    expect(button.getAttribute('aria-label')).toContain('Also needs attention: Claude is limited by the provider: Rate limit reached.');
  });
});
