import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { ConsumerModelPolicyComponent } from './consumer-model-policy.component';

describe('ConsumerModelPolicyComponent', () => {
  it('renders one stable row per consumer and saves a changed primary', async () => {
    await TestBed.configureTestingModule({
      imports: [ConsumerModelPolicyComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ConsumerModelPolicyComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const policy = {
      version: 1,
      consumers: {
        'review-grade': {
          primary: { cli: 'claude', model: 'claude-opus-5', thinking: 'high' },
          route: null,
          fallbacks: [{ cli: 'codex', model: 'gpt-6-sol', thinking: 'high' }],
          sameClassOnly: true, stickyWithinCard: true, costGuardFactor: null,
        },
      },
      caps: {}, costGuardFactor: 2,
      hysteresis: { returnBelowCapPoints: 10, minDwellMinutes: 30 },
      migration: { inheritedSteps: 36, explicitSteps: 36, projects: 12, applied: true },
    };
    http.expectOne('/api/model-selection/policy').flush(policy);
    http.expectOne('/api/model-selection/effective').flush({ routes: [] });
    http.expectOne('/api/token-pricing/route-models').flush({ models: [{ cli: 'codex', model: 'gpt-6-sol', label: 'Sol', thinkingLevels: ['high'], price: { inputPerMillion: 2, outputPerMillion: 10 } }] });
    fixture.detectChanges();
    await fixture.whenStable();
    const row = fixture.nativeElement.querySelector('[data-testid="consumer-policy-review-grade"]');
    expect(row).toBeTruthy();
    expect(fixture.nativeElement.textContent).toContain('36 inherited');
    const input = row.querySelector('select') as HTMLSelectElement;
    input.value = 'codex:gpt-6-sol';
    input.dispatchEvent(new Event('change'));
    http.expectNone('/api/model-selection/policy/consumers/review-grade');
    fixture.componentInstance.update('review-grade', 'primary', 'codex:gpt-6-sol:high');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.componentInstance.preview('review-grade');
    const preview = http.expectOne('/api/token-pricing/what-if');
    expect(preview.request.body.consumer).toBe('one-shot');
    preview.flush({ receipts: 2, actualUsd: 10, alternativeUsd: 4, savingUsd: 6,
      projectedWeeklySavingUsd: 6, savingPercent: 60, coverage: 'complete', unpricedActual: 0, unpricedAlternative: 0,
      qualityCaveat: 'Quality may differ', correctnessFloor: 'Correctness floor applies' });
    fixture.componentInstance.save('review-grade');
    const save = http.expectOne('/api/model-selection/policy/consumers/review-grade');
    expect(save.request.method).toBe('PUT');
    expect(save.request.body.primary).toEqual({ cli: 'codex', model: 'gpt-6-sol', thinking: 'high' });
    save.flush({ ...policy, consumers: { 'review-grade': save.request.body } });
    http.expectOne('/api/model-selection/effective').flush({ routes: [] });
    fixture.detectChanges();
    await fixture.whenStable();
    const savedRow = fixture.nativeElement.querySelector('[data-testid="consumer-policy-review-grade"]');
    expect(fixture.componentInstance.policy()?.consumers['review-grade'].primary?.model).toBe('gpt-6-sol');
    expect(savedRow.querySelector('select').value, savedRow.outerHTML).toBe('codex:gpt-6-sol');
  });
});
