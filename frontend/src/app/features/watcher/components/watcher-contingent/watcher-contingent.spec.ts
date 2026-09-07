import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { WatcherContingentComponent } from './watcher-contingent';
import type { WatcherStatus } from '../../models/watcher.model';

function status(overrides: Partial<WatcherStatus['contingent']> = {}): WatcherStatus {
  return {
    snapshot: {
      enabled: true, lastRunAtUtc: '2026-09-06T20:05:00Z', lastRunFailedAtUtc: null,
      sweeps: 2, signalsCollected: 64, findingsDetected: 8, openCases: 8,
      pendingProposals: 3, backlogCases: 1, proposalsCreatedLastRun: 3,
      commentsAppendedLastRun: 0, modelCallsLastRun: 3, resolvedLastRun: 0,
      lastRunElapsedMs: 180, lastError: null,
    },
    enabled: true,
    intervalSeconds: 300,
    analysisEnabled: true,
    analysisTier: 'sol-medium',
    detectorClasses: ['repetition', 'contradiction', 'silence', 'drift', 'hygiene'],
    sources: ['bus'],
    contingent: {
      limits: {
        modelCallsPerDay: 20, modelCallsPerWeek: 80,
        tokensPerDay: 500000, tokensPerWeek: 2000000,
        proposalsPerDay: 5, proposalsPerWeek: 20,
        commentsPerDay: 20, commentsPerWeek: 80,
      },
      usage: {
        modelCallsDay: 3, modelCallsWeek: 7, tokensDay: 42000, tokensWeek: 96000,
        proposalsDay: 3, proposalsWeek: 6, commentsDay: 0, commentsWeek: 1,
        costUsdDay: 0.42, costUsdWeek: 0.9, unpricedCallsDay: 0,
      },
      dayStartUtc: '2026-09-06T00:00:00Z',
      weekStartUtc: '2026-08-31T00:00:00Z',
      exhaustedDimensions: [],
      backlogCases: 1,
      proposalsBlocked: false,
      modelCallsBlocked: false,
      costUsdDayDisplay: '0.42',
      ...overrides,
    },
    stale: false,
    storeAvailable: true,
  };
}

async function build(response: WatcherStatus) {
  await TestBed.configureTestingModule({
    imports: [WatcherContingentComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();
  const fixture = TestBed.createComponent(WatcherContingentComponent);
  const http = TestBed.inject(HttpTestingController);
  http.expectOne('/api/watcher/status').flush(response);
  fixture.detectChanges();
  return fixture;
}

describe('WatcherContingentComponent', () => {
  it('renders one row per budget dimension with its day and week window', async () => {
    const fixture = await build(status());

    const rows = fixture.componentInstance.rows();
    expect(rows).toHaveLength(8);
    expect(rows.map(row => row.id)).toContain('proposals-week');
    expect(rows.filter(row => row.window === 'day')).toHaveLength(4);
  });

  it('treats a closed dimension as fully consumed rather than dividing by zero', async () => {
    const fixture = await build(status({
      limits: {
        modelCallsPerDay: 0, modelCallsPerWeek: 0, tokensPerDay: 0, tokensPerWeek: 0,
        proposalsPerDay: 0, proposalsPerWeek: 0, commentsPerDay: 0, commentsPerWeek: 0,
      },
    }));

    const row = fixture.componentInstance.rows().find(item => item.id === 'proposals-day')!;
    expect(row.usedPct).toBe(100);
    expect(row.exhausted).toBe(true);
  });

  it('leaves the bar empty for a dimension the operator left without a ceiling', async () => {
    const fixture = await build(status({
      limits: {
        modelCallsPerDay: null, modelCallsPerWeek: null, tokensPerDay: null, tokensPerWeek: null,
        proposalsPerDay: null, proposalsPerWeek: null, commentsPerDay: null, commentsPerWeek: null,
      },
    }));

    const row = fixture.componentInstance.rows().find(item => item.id === 'tokens-day')!;
    expect(row.usedPct).toBeNull();
    expect(row.exhausted).toBe(false);
  });

  it('shows an unknown spend instead of a zero cost when a call had no price', async () => {
    const fixture = await build(status({ costUsdDayDisplay: 'unknown' }));

    expect(fixture.componentInstance.costToday()).toBe('unknown');
  });

  it('reports the backlog an exhausted contingent produced', async () => {
    const fixture = await build(status({
      exhaustedDimensions: ['proposals per day'],
      backlogCases: 8,
      proposalsBlocked: true,
    }));

    expect(fixture.componentInstance.exhausted()).toBe(true);
    expect(fixture.componentInstance.backlog()).toBe(8);
  });
});
