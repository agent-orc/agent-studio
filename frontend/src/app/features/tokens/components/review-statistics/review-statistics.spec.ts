import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { ReviewStatisticsComponent } from './review-statistics';
import type { ReviewStatistics } from './review-statistics.model';

const metric = { median: 120, p90: 240, samples: 2, last7d: [null, null, null, 120, null, null, 240], last30d: Array(30).fill(null) };
const fixtureData: ReviewStatistics = {
  deliveryTokens: metric, deliveryUsd: { ...metric, median: 0.12, p90: 0.24 },
  roundsUntilAcceptance: { ...metric, median: 2, p90: 3 },
  tokensPerAcceptedCard: metric, usdPerAcceptedCard: { ...metric, median: 0.12 },
  reviewSharePercent: { ...metric, median: 12, p90: 24 },
  byStepTokens: { 'requirement-fit': metric }, byStepUsd: { 'requirement-fit': { ...metric, median: 0.12 } },
  byModelLevelTokens: { 'gpt-6-sol / medium': metric },
  byModelLevelUsd: { 'gpt-6-sol / medium': { ...metric, median: 0.12 } },
  acceptedCards: ['Demo/AGT-1'],
  reviews: [{ project: 'Demo', jobId: 'AGT-1', attempt: 'one', step: 'requirement-fit',
    at: '2026-10-20T12:00:00Z', model: 'gpt-6-sol', level: 'medium', host: 'review-1', tokens: 120, usd: 0.12 }],
  warning: null,
};

describe('ReviewStatisticsComponent', () => {
  it('renders review tiles and filters the linked evidence list', async () => {
    await TestBed.configureTestingModule({
      imports: [ReviewStatisticsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ReviewStatisticsComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const request = http.expectOne(request => request.url === '/api/projects/Demo/token-usage/reviews');
    expect(request.request.params.get('days')).toBe('30');
    request.flush(fixtureData);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="review-tile-delivery-tokens"]')?.textContent).toContain('120');
    expect(host.querySelector('[data-testid="review-tile-delivery-tokens"] svg path')?.getAttribute('d')).toContain('M');
    expect(host.querySelector('[data-testid="review-tile-rounds"]')?.textContent).toContain('2');
    host.querySelector<HTMLAnchorElement>('[data-testid="review-tile-step-requirement-fit"]')!.click();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="review-evidence"]')?.textContent).toContain('AGT-1');

    host.querySelector<HTMLSelectElement>('[data-testid="review-filter-range"]')!.value = '7';
    host.querySelector<HTMLSelectElement>('[data-testid="review-filter-range"]')!
      .dispatchEvent(new Event('change'));
    await fixture.whenStable();
    http.expectOne(request => request.url === '/api/projects/Demo/token-usage/reviews'
      && request.params.get('days') === '7').flush(fixtureData);
    fixture.detectChanges();
    http.verify();
  });
});
