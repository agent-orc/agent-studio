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
    fixture.detectChanges();
    const row = fixture.nativeElement.querySelector('[data-testid="consumer-policy-review-grade"]');
    expect(row).toBeTruthy();
    expect(fixture.nativeElement.textContent).toContain('36 inherited');
    const input = row.querySelector('input') as HTMLInputElement;
    input.value = 'codex:gpt-6-sol:high';
    input.dispatchEvent(new Event('change'));
    const save = http.expectOne('/api/model-selection/policy/consumers/review-grade');
    expect(save.request.method).toBe('PUT');
    expect(save.request.body.primary).toEqual({ cli: 'codex', model: 'gpt-6-sol', thinking: 'high' });
    save.flush({ ...policy, consumers: { 'review-grade': save.request.body } });
    http.expectOne('/api/model-selection/effective').flush({ routes: [] });
    fixture.detectChanges();
    expect(row.querySelector('input').value).toBe('codex:gpt-6-sol:high');
  });
});
