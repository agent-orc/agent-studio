import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { WatcherContingentPanelComponent } from './watcher-contingent-panel';
import type { WatcherContingentSnapshot } from '../../models/watcher.model';

function snapshot(overrides: Partial<WatcherContingentSnapshot> = {}): WatcherContingentSnapshot {
  return {
    budget: {
      dailyTokens: 200_000,
      weeklyTokens: 1_000_000,
      dailyModelCalls: 20,
      weeklyModelCalls: 100,
      dailyProposals: 10,
      weeklyProposals: 40,
      dailyComments: 20,
      weeklyComments: 80,
    },
    daily: { tokens: 44_000, modelCalls: 1, proposals: 8, comments: 0 },
    weekly: { tokens: 44_000, modelCalls: 1, proposals: 8, comments: 0 },
    weeklyDollars: 0.25,
    exhausted: false,
    backlogCases: 0,
    dailyTokensRemaining: 156_000,
    weeklyTokensRemaining: 956_000,
    dailyProposalsRemaining: 2,
    weeklyProposalsRemaining: 32,
    ...overrides,
  };
}

describe('WatcherContingentPanelComponent', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [WatcherContingentPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function mount(body: WatcherContingentSnapshot) {
    const fixture = TestBed.createComponent(WatcherContingentPanelComponent);
    fixture.detectChanges();
    http.expectOne('/api/watcher/contingent').flush(body);
    fixture.detectChanges();
    return fixture;
  }

  it('renders one row per budget and window', () => {
    const fixture = mount(snapshot());

    // Four budgets (proposals, comments, model calls, tokens) in two windows.
    expect(fixture.componentInstance.rows().length).toBe(8);
  });

  it('shows the backlog so an empty inbox is not read as an empty workspace', () => {
    const fixture = mount(snapshot({ backlogCases: 8, exhausted: true }));
    const element: HTMLElement = fixture.nativeElement;

    expect(fixture.componentInstance.backlog()).toBe(8);
    expect(element.querySelector('[data-testid="watcher-backlog"]')?.textContent)
      .toContain('8');
    expect(element.querySelector('[data-testid="watcher-contingent-exhausted"]')).toBeTruthy();
  });

  it('does not show the exhausted banner while budget remains', () => {
    const fixture = mount(snapshot());
    const element: HTMLElement = fixture.nativeElement;

    expect(element.querySelector('[data-testid="watcher-contingent-exhausted"]')).toBeNull();
  });

  it('treats a zero budget as fully spent rather than as unset', () => {
    const fixture = mount(snapshot({
      budget: { ...snapshot().budget, dailyProposals: 0 },
      daily: { tokens: 0, modelCalls: 0, proposals: 0, comments: 0 },
    }));

    const row = fixture.componentInstance.rows().find(candidate => candidate.key === 'proposals-day');
    expect(row?.usedPct).toBe(100);
    expect(fixture.componentInstance.severity(row?.usedPct ?? null)).toBe('crit');
  });

  it('renders an unknown weekly price as unknown, never as zero', () => {
    const fixture = mount(snapshot({ weeklyDollars: null }));
    const element: HTMLElement = fixture.nativeElement;

    const cost = element.querySelector('[data-testid="watcher-contingent-cost"]');
    expect(cost?.textContent).toContain('unknown');
    expect(cost?.textContent).not.toContain('$0.00');
  });

  it('clamps the bar fill to the visible range', () => {
    const fixture = mount(snapshot());
    const component = fixture.componentInstance;

    expect(component.barWidth(null)).toBe(0);
    expect(component.barWidth(-10)).toBe(0);
    expect(component.barWidth(250)).toBe(100);
  });

  it('abbreviates token counts but never counts', () => {
    const fixture = mount(snapshot());
    const component = fixture.componentInstance;

    expect(component.formatCount(44_000, 'tokens')).toBe('44k');
    expect(component.formatCount(1_500_000, 'tokens')).toBe('1.5M');
    expect(component.formatCount(8, 'count')).toBe('8');
  });
});
