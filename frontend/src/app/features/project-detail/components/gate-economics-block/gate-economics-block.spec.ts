import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, expect, it } from 'vitest';
import { GateEconomicsBlockComponent } from './gate-economics-block';

afterEach(() => TestBed.inject(HttpTestingController).verify());

it('keeps the newest requested window when responses complete out of order', () => {
  TestBed.configureTestingModule({
    imports: [GateEconomicsBlockComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  });
  const fixture = TestBed.createComponent(GateEconomicsBlockComponent);
  const http = TestBed.inject(HttpTestingController);
  const first = http.expectOne('/api/gate-economics?window=7d');
  fixture.componentInstance.setWindow('30d');
  const next = http.expectOne('/api/gate-economics?window=30d');
  next.flush({ window: '30d', rows: [] });
  first.flush({ window: '7d', rows: [] });
  expect(fixture.componentInstance.data()?.window).toBe('30d');
  fixture.destroy();
});

const baseline = (fullSuiteRuns: number | null, gateSourceRuns: number, leadTimeCards: number) => ({
  leadTimeMedianMinutes: leadTimeCards ? 30 : null, leadTimeP90Minutes: leadTimeCards ? 60 : null,
  leadTimeCards, leadTimeUnknownCards: 1,
  gateMinutesPerCardMedian: null, gateMinutesPerCardP90: null, gateMinutesCards: 0, gateMinutesUnknownCards: 2,
  fullSuiteRuns, fullSuiteRunsPerDay: fullSuiteRuns == null ? null : fullSuiteRuns / 7,
  redDevelopMedianMinutes: null, redDevelopP90Minutes: null,
  gateSourceBytesMedian: gateSourceRuns ? 52_428_800 : null, gateSourceBytesP90: gateSourceRuns ? 104_857_600 : null,
  gateSourceSecondsMedian: gateSourceRuns ? 12.5 : null, gateSourceSecondsP90: gateSourceRuns ? 20 : null,
  gateSourceRuns,
});

it('renders the delivery-target baseline with unknown inputs and an aggregate equal to its project rows', () => {
  TestBed.configureTestingModule({
    imports: [GateEconomicsBlockComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  });
  const fixture = TestBed.createComponent(GateEconomicsBlockComponent);
  TestBed.inject(HttpTestingController).expectOne('/api/gate-economics?window=7d').flush({
    window: '7d', asOfUtc: '2026-10-01T12:00:00Z', boardReadWindowMinutes: 60,
    boardRead: [
      { label: 'tasks/grouped', name: 'Board', requests: 158, p50Ms: 3139.5, p95Ms: 5598.4, gitSpawnsPerRequest: 0 },
      { label: 'tasks/list', name: 'Card list', requests: 0, p50Ms: null, p95Ms: null, gitSpawnsPerRequest: null },
    ],
    rows: [
      { project: 'All projects', baseline: baseline(5, 3, 4) },
      { project: 'Alpha', projectShortCode: 'AL', baseline: baseline(5, 3, 4) },
      { project: 'Beta', projectShortCode: 'BE', baseline: baseline(null, 0, 0) },
    ],
  });
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const table = host.querySelector('[data-testid="pipeline-baseline-metrics"]')!;
  const sum = (testId: string) => [...table.querySelectorAll(`[data-testid="pipeline-baseline-project"] [data-testid="${testId}"]`)]
    .map(cell => Number(cell.textContent!.trim())).filter(value => !Number.isNaN(value)).reduce((a, b) => a + b, 0);
  const all = table.querySelector('[data-testid="pipeline-baseline-all"]')!;
  expect(Number(all.querySelector('[data-testid="baseline-full-suite-runs"]')!.textContent!.trim())).toBe(sum('baseline-full-suite-runs'));
  expect(Number(all.querySelector('[data-testid="baseline-source-runs"]')!.textContent!.trim())).toBe(sum('baseline-source-runs'));
  const beta = table.querySelector('[data-project="Beta"]')!.textContent!;
  expect(beta).toContain('Unknown / Unknown');
  expect(beta).toContain('Not measured');
  expect(beta).not.toContain('0 min');
  expect(table.querySelector('[data-project="Alpha"]')!.textContent).toContain('50 MB / 100 MB');
  expect(fixture.componentInstance.size(43_008)).toBe('42 KB');
  const board = host.querySelector('[data-testid="board-read-metrics"]')!;
  expect(board.querySelector('[data-handler="tasks/grouped"]')!.textContent).toContain('3,139.5 ms / 5,598.4 ms');
  expect(board.querySelector('[data-handler="tasks/list"]')!.textContent).toContain('Unknown / Unknown');
  fixture.destroy();
});
