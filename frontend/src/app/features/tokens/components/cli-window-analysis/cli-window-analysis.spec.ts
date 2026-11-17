import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { UsageCockpitService } from '../../../usage-cockpit/services/usage-cockpit.service';
import { CliWindowAnalysisComponent } from './cli-window-analysis';

describe('CliWindowAnalysisComponent', () => {
  it('shows parsed reset with raw provider text and uses the backend five-hour length', async () => {
    await TestBed.configureTestingModule({
      imports: [CliWindowAnalysisComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
        { provide: UsageCockpitService, useValue: { snapshot: () => ({ timeZone: 'UTC' }) } }],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliWindowAnalysisComponent);
    const resetAt = new Date(Date.now() + 2.5 * 3_600_000).toISOString();
    fixture.componentRef.setInput('cliType', 'codex');
    fixture.componentRef.setInput('quotaRows', [{ cliType: 'codex', plan: 'Pro', windows: [{
      label: '5-hour', windowHours: 5, usedPct: 25, resetAt, resetLabel: 'in 2h 30m',
    }] }]);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url === '/api/workspace/tokens/subscription-week'
      && request.params.get('cli') === 'codex').flush({
      cli: 'codex', subscriptionKey: null, startUtc: null, resetAtUtc: null,
      usedPct: null, observedAt: null, totalTokens: 0, receiptCount: 0,
      cards: [], kinds: [], models: [], receipts: [], notice: 'No current provider week.',
    });
    http.expectOne(request => request.url === '/api/workspace/tokens/cli-window'
      && request.params.get('cliType') === 'codex').flush({
      fromUtc: new Date(Date.parse(resetAt) - 5 * 3_600_000).toISOString(),
      toUtc: new Date().toISOString(), durationHours: 5, calls: 1,
      inputTokens: 10, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 10,
    });
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('in 2h 30m');
    expect(text).toContain('UTC');
    expect(text).toContain('10.0% / h');
    expect(text).toContain('Recorded attribution');
    expect(text).toContain('10');
    http.verify();
  });
});
