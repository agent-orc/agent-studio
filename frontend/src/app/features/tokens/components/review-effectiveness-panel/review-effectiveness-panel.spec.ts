import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ReviewEffectivenessPanelComponent } from './review-effectiveness-panel';
import type { ReviewEffectivenessRow, ReviewEffectivenessSummary } from '../../models/review-effectiveness.model';

function row(overrides: Partial<ReviewEffectivenessRow>): ReviewEffectivenessRow {
  return {
    level: 'grade', model: 'gpt-6-sol', reasoningLevel: 'high', reviews: 4, approvals: 3, blocks: 1,
    blockerRate: 0.25, missedApprovals: 1, missRate: 1 / 3, missesBySource: { gate: 1 },
    inputTokens: 4000, outputTokens: 800, cacheReadTokens: 1200, cacheCreationTokens: 0,
    pricedReviews: 3, totalCostUsd: 0.6, costPerReviewUsd: 0.2,
    findings: { blocker: 1, major: 1, minor: 1, nit: 2, unclassified: 0 }, realFindings: 3,
    costPerRealFindingUsd: 0.2, averageDurationMs: 90_000,
    ...overrides,
  };
}

const summary: ReviewEffectivenessSummary = {
  generatedAtUtc: '2026-10-21T08:00:00Z',
  sinceUtc: '2026-09-21T08:00:00Z',
  rows: [
    row({ level: 'aspect', reasoningLevel: 'medium', costPerReviewUsd: null, costPerRealFindingUsd: null, missRate: null, approvals: 0, missedApprovals: 0 }),
    row({}),
    row({ reasoningLevel: 'xhigh', costPerReviewUsd: 0.9, missRate: 0.05 }),
  ],
  coverage: { reviews: 12, pricedReviews: 6, reviewsWithTokens: 12, reviewsWithModel: 12, evidence: 2, misses: 2 },
};

async function setup() {
  await TestBed.configureTestingModule({
    imports: [ReviewEffectivenessPanelComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ReviewEffectivenessPanelComponent);
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  return { fixture, http, el: fixture.nativeElement as HTMLElement };
}

describe('ReviewEffectivenessPanelComponent', () => {
  it('loads the 30-day summary and renders one row per route with unknown values as n/a', async () => {
    const { fixture, http, el } = await setup();
    const request = http.expectOne(req => req.url === '/api/review-effectiveness');
    expect(request.request.params.get('days')).toBe('30');
    request.flush(summary);
    fixture.detectChanges();
    await fixture.whenStable();

    const rows = el.querySelectorAll('[data-testid^="review-effectiveness-row-"]');
    expect(rows.length).toBe(3);
    const high = el.querySelector('[data-testid="review-effectiveness-row-grade|gpt-6-sol|high"]')!;
    expect(high.textContent).toContain('25.0%');
    expect(high.textContent).toContain('33.3%');
    expect(high.textContent).toContain('1/3');
    expect(high.textContent).toContain('$0.200');
    expect(high.textContent).toContain('1/1/1/2');
    const aspect = el.querySelector('[data-testid="review-effectiveness-row-aspect|gpt-6-sol|medium"]')!;
    expect(aspect.textContent).toContain('n/a');
    expect(el.querySelector('[data-testid="review-effectiveness-coverage"]')?.textContent).toContain('12 reviews');
    expect(el.querySelector('[data-testid="review-effectiveness-export"]')?.getAttribute('href'))
      .toBe('/api/review-effectiveness/export?days=30');
    http.verify();
  });

  it('filters by level without reloading and reloads when the window changes', async () => {
    const { fixture, http, el } = await setup();
    http.expectOne(req => req.url === '/api/review-effectiveness').flush(summary);
    fixture.detectChanges();

    (el.querySelector('[data-testid="review-effectiveness-level-aspect"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelectorAll('[data-testid^="review-effectiveness-row-"]').length).toBe(1);
    http.expectNone(req => req.url === '/api/review-effectiveness');

    (el.querySelector('[data-testid="review-effectiveness-days-90"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const reload = http.expectOne(req => req.url === '/api/review-effectiveness');
    expect(reload.request.params.get('days')).toBe('90');
    reload.flush({ ...summary, rows: [] });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="review-effectiveness-empty"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="review-effectiveness-export"]')?.getAttribute('href'))
      .toBe('/api/review-effectiveness/export?days=90');
  });

  it('shows an error when the summary cannot be loaded', async () => {
    const { fixture, http, el } = await setup();
    http.expectOne(req => req.url === '/api/review-effectiveness')
      .flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(el.querySelector('[data-testid="review-effectiveness-error"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="review-effectiveness-table"]')).toBeNull();
  });
});
