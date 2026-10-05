import { describe, expect, it } from 'vitest';
import type { CliModelRoutesResponse } from './services/quota-api.service';
import type { QuotaForecast, QuotaHistoryWindow } from './models/quota-history.model';
import {
  forecastSentence,
  formatDuration,
  formatRate,
  nearestCurvePoint,
  quotaCurveGeometry,
  quotaFallbackStatus,
  quotaTone,
} from './quota-forecast.util';

const WEEKLY = 'Current week (all models)';

function routes(overrides: Partial<CliModelRoutesResponse> = {}): CliModelRoutesResponse {
  return {
    profiles: {
      claude: {
        cliType: 'claude', primaryModel: 'claude-opus-5-5', primaryThinkingLevel: 'high',
        fallbackCliType: 'codex', fallbackModel: 'gpt-5.6-sol', fallbackThinkingLevel: 'high',
        isFallbackDerived: true,
      },
    },
    routes: [],
    catalogueVersion: 'v1',
    states: {
      claude: {
        cliType: 'claude', state: 'normal', activeSince: null, preferenceExpiresAt: null,
        windows: [{ label: WEEKLY, usedPct: 83, capPct: 95, resetAt: null }],
      },
      codex: { cliType: 'codex', state: 'normal', activeSince: null, preferenceExpiresAt: null, windows: [] },
    },
    callersCannotReroute: [],
    ...overrides,
  };
}

describe('quotaFallbackStatus', () => {
  it('is armed when a fallback route exists and the fallback CLI is below its cap', () => {
    const status = quotaFallbackStatus(routes(), 'claude', WEEKLY);
    expect(status).toMatchObject({
      target: 'codex · gpt-5.6-sol (high)', armed: true, engaged: false, derived: true, capPct: 95,
    });
    expect(status.reason).toBe('Switches when this window reaches its 95% cap.');
  });

  it('is not armed when the fallback CLI is itself at its cap', () => {
    const r = routes();
    r.states['codex'].state = 'fallback-active';
    const status = quotaFallbackStatus(r, 'claude', WEEKLY);
    expect(status.armed).toBe(false);
    expect(status.target).toBe('codex · gpt-5.6-sol (high)');
    expect(status.reason).toBe('codex is at its own usage cap.');
  });

  it('is not armed without a configured fallback model', () => {
    const r = routes();
    r.profiles['claude'] = { ...r.profiles['claude'], fallbackModel: null };
    expect(quotaFallbackStatus(r, 'claude', WEEKLY)).toMatchObject({
      target: null, armed: false, reason: 'No fallback route is configured.',
    });
  });

  it('reports an already engaged fallback', () => {
    const r = routes();
    r.states['claude'].state = 'fallback-preferred';
    expect(quotaFallbackStatus(r, 'claude', WEEKLY)).toMatchObject({ armed: true, engaged: true });
  });

  it('does not check a same-CLI fallback against its own cap state', () => {
    const r = routes();
    r.profiles['claude'] = { ...r.profiles['claude'], fallbackCliType: null, fallbackModel: 'claude-sonnet-5', fallbackThinkingLevel: null };
    r.states['claude'].state = 'fallback-active';
    expect(quotaFallbackStatus(r, 'claude', WEEKLY)).toMatchObject({
      target: 'claude · claude-sonnet-5', armed: true, engaged: true,
    });
  });

  it('is not armed when the routes could not be loaded', () => {
    expect(quotaFallbackStatus(null, 'claude')).toMatchObject({ armed: false, target: null });
  });
});

function forecast(overrides: Partial<QuotaForecast> = {}): QuotaForecast {
  return {
    status: 'full-before-reset', currentPct: 83, currentAt: '2026-09-29T04:27:00Z', ratePctPerHour: 2,
    rateFrom: '2026-09-29T01:27:00Z', forecastFullAt: '2026-09-29T12:57:00Z',
    resetAt: '2026-10-02T08:00:00Z', reachesFullBeforeReset: true, ...overrides,
  };
}

describe('quotaCurveGeometry', () => {
  const window: QuotaHistoryWindow = {
    label: WEEKLY,
    kind: 'weekly',
    points: [
      { at: '2026-09-28T04:27:00Z', usedPct: 50, resetAt: null },
      { at: '2026-09-28T20:27:00Z', usedPct: 67, resetAt: null },
      { at: '2026-09-28T21:27:00Z', usedPct: 69, resetAt: null },
      { at: '2026-09-29T04:27:00Z', usedPct: 83, resetAt: null },
    ],
    forecast: forecast(),
  };
  const g = quotaCurveGeometry(window, '2026-09-27T04:27:00Z', '2026-09-29T04:27:00Z');

  it('maps time to x and percent to a fixed 0 to 100 scale', () => {
    expect(g.points).toHaveLength(4);
    expect(g.points[3].y).toBeLessThan(g.points[0].y);
    expect(g.gridY.map(line => line.pct)).toEqual([0, 50, 100]);
    expect(g.gridY[2].y).toBe(g.plotTop);
    expect(g.gridY[0].y).toBe(g.plotBottom);
    // 48 h of history plus 12 h of look-ahead: now sits at 80 % of the plot width.
    expect(g.nowX).toBeCloseTo(g.plotLeft + 0.8 * (g.plotRight - g.plotLeft), 0);
  });

  it('breaks the line across gaps longer than four hours and keeps lone readings visible', () => {
    expect(g.path.match(/M/g)).toHaveLength(3);          // 16 h and 7 h gaps
    expect(g.isolated.map(p => p.usedPct)).toEqual([50, 83]);
  });

  it('extends a dashed forecast from the latest reading, clipped at the look-ahead edge', () => {
    expect(g.forecast).not.toBeNull();
    expect(g.forecast!.x1).toBe(g.points[3].x);
    // 12 h at 2 %/h from 83 % would pass 100 % at 8.5 h, so the line ends at 100 % inside the plot.
    expect(g.forecast!.y2).toBe(g.plotTop);
    expect(g.forecast!.x2).toBeLessThan(g.plotRight);
  });

  it('omits the reset marker when the reset is outside the plotted range', () => {
    expect(g.resetX).toBeNull();
    const soon = quotaCurveGeometry(
      { ...window, forecast: forecast({ resetAt: '2026-09-29T10:00:00Z' }) },
      '2026-09-27T04:27:00Z', '2026-09-29T04:27:00Z');
    expect(soon.resetX).not.toBeNull();
  });

  it('ends the forecast at the reset when the reset comes first', () => {
    const resetsFirst = quotaCurveGeometry(
      { ...window, forecast: forecast({ status: 'resets-first', resetAt: '2026-09-29T08:27:00Z', reachesFullBeforeReset: false }) },
      '2026-09-27T04:27:00Z', '2026-09-29T04:27:00Z');
    expect(resetsFirst.forecast!.x2).toBe(resetsFirst.resetX);
    // 4 h at 2 %/h from 83 % ends at 91 %, below the 100 % line.
    expect(resetsFirst.forecast!.y2).toBeGreaterThan(resetsFirst.plotTop);
  });

  it('draws no forecast without a positive rate', () => {
    const idle = quotaCurveGeometry(
      { ...window, forecast: forecast({ status: 'idle', ratePctPerHour: 0, forecastFullAt: null }) },
      '2026-09-27T04:27:00Z', '2026-09-29T04:27:00Z');
    expect(idle.forecast).toBeNull();
  });

  it('finds the nearest reading to a pointer position', () => {
    expect(nearestCurvePoint(g.points, g.points[1].x + 1)).toBe(1);
    expect(nearestCurvePoint([], 10)).toBe(-1);
  });
});

describe('forecast wording', () => {
  const now = Date.parse('2026-09-29T04:27:00Z');
  const when = (iso: string) => iso.slice(11, 16) + 'Z';

  it('states the forecast time and the remaining duration', () => {
    expect(forecastSentence(forecast(), now, when)).toBe('100% at 12:57Z (in 8h 30m), before the reset.');
    expect(forecastSentence(forecast({ status: 'resets-first', reachesFullBeforeReset: false }), now, when))
      .toBe('Would reach 100% at 12:57Z; the reset comes first.');
    expect(forecastSentence(forecast({ status: 'insufficient-data' }), now, when)).toContain('Not enough readings');
    expect(forecastSentence(forecast({ status: 'idle' }), now, when)).toContain('No growth');
  });

  it('formats durations, rates, and tones', () => {
    expect(formatDuration(8.5 * 3_600_000)).toBe('8h 30m');
    expect(formatDuration(12 * 60_000)).toBe('12m');
    expect(formatDuration(52 * 3_600_000)).toBe('2d 4h');
    expect(formatDuration(-1)).toBe('now');
    expect(formatRate(2)).toBe('2.0%/h');
    expect(formatRate(null)).toBe('unknown');
    expect([quotaTone(40), quotaTone(75), quotaTone(95), quotaTone(null)]).toEqual(['ok', 'warn', 'critical', 'ok']);
  });
});
