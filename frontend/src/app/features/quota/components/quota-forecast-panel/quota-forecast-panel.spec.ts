import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { QuotaForecastPanelComponent } from './quota-forecast-panel';
import type { QuotaHistoryResponse } from '../../models/quota-history.model';

const WEEKLY = 'Current week (all models)';

function history(reachesFullBeforeReset: boolean): QuotaHistoryResponse {
  return {
    cliType: 'claude', hours: 48, from: '2026-09-27T04:27:00Z', to: '2026-09-29T04:30:00Z',
    retentionDays: 14, rateLookbackHours: 3,
    windows: [
      {
        label: 'Current session', kind: 'session',
        points: [{ at: '2026-09-29T04:27:00Z', usedPct: 92, resetAt: '2026-09-29T06:00:00Z' }],
        forecast: {
          status: 'insufficient-data', currentPct: 92, currentAt: '2026-09-29T04:27:00Z', ratePctPerHour: null,
          rateFrom: null, forecastFullAt: null, resetAt: '2026-09-29T06:00:00Z', reachesFullBeforeReset: false,
        },
      },
      {
        label: WEEKLY, kind: 'weekly',
        points: [
          { at: '2026-09-29T01:27:00Z', usedPct: 77, resetAt: null },
          { at: '2026-09-29T04:27:00Z', usedPct: 83, resetAt: null },
        ],
        forecast: {
          status: reachesFullBeforeReset ? 'full-before-reset' : 'resets-first',
          currentPct: 83, currentAt: '2026-09-29T04:27:00Z', ratePctPerHour: 2,
          rateFrom: '2026-09-29T01:27:00Z', forecastFullAt: '2026-09-29T12:57:00Z',
          resetAt: reachesFullBeforeReset ? '2026-10-02T08:00:00Z' : '2026-09-29T10:00:00Z',
          reachesFullBeforeReset,
        },
      },
    ],
  };
}

const routes = {
  profiles: {
    claude: {
      cliType: 'claude', primaryModel: null, primaryThinkingLevel: null,
      fallbackCliType: 'codex', fallbackModel: 'gpt-5.6-sol', fallbackThinkingLevel: null,
    },
  },
  routes: [], catalogueVersion: 'v1', callersCannotReroute: [],
  states: {
    claude: { cliType: 'claude', state: 'normal', activeSince: null, preferenceExpiresAt: null,
      windows: [{ label: WEEKLY, usedPct: 83, capPct: 95, resetAt: null }] },
    codex: { cliType: 'codex', state: 'normal', activeSince: null, preferenceExpiresAt: null, windows: [] },
  },
};

async function render(reachesFullBeforeReset: boolean) {
  await TestBed.configureTestingModule({
    imports: [QuotaForecastPanelComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();
  const fixture = TestBed.createComponent(QuotaForecastPanelComponent);
  fixture.detectChanges();
  const http = TestBed.inject(HttpTestingController);
  http.expectOne('/api/cli/quota').flush({
    at: '2026-09-29T04:30:00Z', ttlSeconds: 600,
    snapshots: [{ cliType: 'claude', windows: [], fetchedAt: '2026-09-29T04:27:00Z' }],
  });
  http.expectOne(req => req.url === '/api/cli/quota/history'
    && req.params.get('cli') === 'claude' && req.params.get('hours') === '48').flush(history(reachesFullBeforeReset));
  http.expectOne('/api/cli/quota/model-routes').flush(routes);
  fixture.detectChanges();
  return { fixture, http, el: fixture.nativeElement as HTMLElement };
}

describe('QuotaForecastPanelComponent', () => {
  it('renders the weekly rate, forecast, curve, and session gauge per CLI', async () => {
    const { el, http } = await render(true);
    const card = el.querySelector('[data-testid="quota-forecast-cli"]');
    expect(card?.getAttribute('data-cli')).toBe('claude');
    expect(el.querySelector('[data-testid="quota-stat-current"]')?.textContent?.trim()).toBe('83%');
    expect(el.querySelector('[data-testid="quota-stat-rate"]')?.textContent?.trim()).toBe('2.0%/h');
    expect(el.querySelector('[data-testid="quota-forecast-sentence"]')?.textContent).toContain('before the reset');
    expect(el.querySelector('[data-testid="quota-curve"]')).not.toBeNull();
    const gauge = el.querySelector('[data-testid="quota-session-gauge"]');
    expect(gauge?.getAttribute('data-tone')).toBe('critical');
    expect(gauge?.querySelector('[role="meter"]')?.getAttribute('aria-valuenow')).toBe('92');
    http.verify();
  });

  it('names the armed fallback when 100% comes before the reset', async () => {
    const { el } = await render(true);
    const fallback = el.querySelector('[data-testid="quota-fallback"]');
    expect(fallback?.getAttribute('data-armed')).toBe('true');
    expect(el.querySelector('[data-testid="quota-fallback-target"]')?.textContent?.trim()).toBe('codex · gpt-5.6-sol');
    expect(fallback?.textContent).toContain('Armed');
    expect(fallback?.textContent).toContain('95% cap');
  });

  it('shows no fallback notice when the reset comes first', async () => {
    const { el } = await render(false);
    expect(el.querySelector('[data-testid="quota-fallback"]')).toBeNull();
    expect(el.querySelector('[data-testid="quota-forecast-badge"]')).toBeNull();
  });
});
