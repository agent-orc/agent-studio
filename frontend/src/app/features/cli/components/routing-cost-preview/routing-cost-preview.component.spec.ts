import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { RoutingCostPreviewComponent } from './routing-cost-preview.component';

describe('RoutingCostPreviewComponent', () => {
  it('sends UTC period and consumer, rejects stale results, and renders unknown costs', async () => {
    await TestBed.configureTestingModule({ imports: [RoutingCostPreviewComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] }).compileComponents();
    const fixture = TestBed.createComponent(RoutingCostPreviewComponent);
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component.from.set('2026-11-01'); component.to.set('2026-11-08');
    component.model.set('claude-haiku-4-5'); component.consumer.set('review-aspect');
    component.preview();
    const stale = http.expectOne('/api/token-pricing/what-if');
    expect(stale.request.body).toMatchObject({ from: '2026-11-01T00:00:00Z', to: '2026-11-08T00:00:00Z',
      consumer: 'review-aspect', alternativeModel: 'claude-haiku-4-5' });
    component.reset(); stale.flush({ receipts: 9 });
    expect(component.result()).toBeNull();
    component.preview();
    http.expectOne('/api/token-pricing/what-if').flush({ receipts: 3, actualUsd: 10, alternativeUsd: null,
      savingUsd: null, savingPercent: null, projectedWeeklySavingUsd: null, unpricedActual: 0, unpricedAlternative: 3,
      coverage: 'partial', qualityCaveat: 'Model quality may differ', correctnessFloor: 'Correctness floor applies' });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Alternative Unknown');
    expect(fixture.nativeElement.textContent).toContain('Correctness floor applies');
    http.verify();
  });
});
