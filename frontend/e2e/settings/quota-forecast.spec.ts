import { test, expect, type Page, type Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3001 · Quota forecast in CLI Management (mocked).
 *
 * The section renders, per CLI, the weekly curve of the last 48 hours, the
 * rate over the last 3 hours, the forecast time of 100%, the reset time, and
 * the session window as a small gauge. When 100% is forecast before the reset
 * it names the configured fallback and whether it is armed.
 *
 * Every /api call is mocked, so the spec needs only a frontend server.
 */

const resultsDir = process.env.JOB_RESULTS_DIR ?? path.join(process.cwd(), 'test-results');
const WEEKLY = 'Current week (all models)';
const HOUR = 3_600_000;

function iso(ms: number): string { return new Date(ms).toISOString(); }

/** Claude on the operator's night: 0.5 %/h for two days, 2 %/h over the last 3 hours. */
function claudeHistory(now: number) {
  const latest = now - 5 * 60_000;
  const reset = latest + 3 * 24 * HOUR;
  const points = [];
  for (let i = 96; i >= 0; i--) {
    const pct = i > 6 ? 50 + (96 - i) * 0.25 : 72.5 + (6 - i);
    points.push({ at: iso(latest - i * 30 * 60_000), usedPct: pct, resetAt: iso(reset) });
  }
  return {
    cliType: 'claude', hours: 48, from: iso(now - 48 * HOUR), to: iso(now), retentionDays: 14, rateLookbackHours: 3,
    windows: [
      {
        label: 'Current session', kind: 'session',
        points: [{ at: iso(latest), usedPct: 62, resetAt: iso(latest + 2 * HOUR) }],
        forecast: {
          status: 'insufficient-data', currentPct: 62, currentAt: iso(latest), ratePctPerHour: null,
          rateFrom: null, forecastFullAt: null, resetAt: iso(latest + 2 * HOUR), reachesFullBeforeReset: false,
        },
      },
      {
        label: WEEKLY, kind: 'weekly', points,
        forecast: {
          status: 'full-before-reset', currentPct: 78.5, currentAt: iso(latest), ratePctPerHour: 2,
          rateFrom: iso(latest - 3 * HOUR), forecastFullAt: iso(latest + 10.75 * HOUR),
          resetAt: iso(reset), reachesFullBeforeReset: true,
        },
      },
    ],
  };
}

/** Codex: slow weekly climb that resets first. */
function codexHistory(now: number) {
  const latest = now - 8 * 60_000;
  const reset = latest + 6 * HOUR;
  const points = [];
  for (let i = 48; i >= 0; i--) points.push({ at: iso(latest - i * HOUR), usedPct: 20 + (48 - i) * 0.4, resetAt: iso(reset) });
  return {
    cliType: 'codex', hours: 48, from: iso(now - 48 * HOUR), to: iso(now), retentionDays: 14, rateLookbackHours: 3,
    windows: [
      {
        label: '5-hour', kind: 'session',
        points: [{ at: iso(latest), usedPct: 18, resetAt: iso(latest + 3 * HOUR) }],
        forecast: {
          status: 'idle', currentPct: 18, currentAt: iso(latest), ratePctPerHour: 0, rateFrom: iso(latest - 3 * HOUR),
          forecastFullAt: null, resetAt: iso(latest + 3 * HOUR), reachesFullBeforeReset: false,
        },
      },
      {
        label: 'Weekly', kind: 'weekly', points,
        forecast: {
          status: 'resets-first', currentPct: 39.2, currentAt: iso(latest), ratePctPerHour: 0.4,
          rateFrom: iso(latest - 3 * HOUR), forecastFullAt: iso(latest + 152 * HOUR),
          resetAt: iso(reset), reachesFullBeforeReset: false,
        },
      },
    ],
  };
}

const modelRoutes = {
  profiles: {
    claude: {
      cliType: 'claude', primaryModel: 'claude-opus-5-5', primaryThinkingLevel: 'high',
      fallbackCliType: 'codex', fallbackModel: 'gpt-5.6-sol', fallbackThinkingLevel: 'high', isFallbackDerived: true,
    },
    codex: {
      cliType: 'codex', primaryModel: null, primaryThinkingLevel: null,
      fallbackCliType: null, fallbackModel: null, fallbackThinkingLevel: null,
    },
  },
  routes: [], catalogueVersion: 'TokenEconomy-v1', callersCannotReroute: [],
  states: {
    claude: { cliType: 'claude', state: 'normal', activeSince: null, preferenceExpiresAt: null,
      windows: [{ label: WEEKLY, usedPct: 78.5, capPct: 95, resetAt: null }] },
    codex: { cliType: 'codex', state: 'normal', activeSince: null, preferenceExpiresAt: null, windows: [] },
  },
};

/** Open CLI Management the way an operator does: Settings, then the rail entry. */
async function openCliManagement(page: Page): Promise<void> {
  await page.goto('/');
  await page.getByTestId('status-bar-settings').click();
  await page.getByTestId('workspace-settings-rail-caps').click();
  await expect(page.getByTestId('cli-admin-panel')).toBeVisible({ timeout: 20_000 });
}

async function installRoutes(page: Page, now: number): Promise<void> {
  const json = (body: unknown) => (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) }).catch(() => undefined);
  await page.route('**/api/**', json([]));
  await page.route('**/api/auth/status', json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }));
  await page.route('**/api/crash-recovery/pending', json({ pending: [] }));
  await page.route('**/api/tasks/grouped**', json({
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [], review: [],
    autoReview: [], humanReview: [], escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watcher/status', json({
    options: { enabled: false, intervalSeconds: 300, persistenceSweepsBeforeProposal: 3, suppressionDays: 7 },
    lastRun: null, contingent: null,
  }));
  await page.route('**/api/tasks/archive**', json({ items: [], total: 0, offset: 0, limit: 50 }));
  await page.route('**/api/environment**', json({ isDev: false, devTools: {} }));
  await page.route('**/api/cli/*/models*', json({ models: [], source: 'stubbed' }));
  await page.route('**/api/cli/usage**', json({ at: iso(now), sessions: [] }));
  await page.route(/\/api\/runner\/status(\?|$)/, json({ projects: {} }));
  await page.route('**/api/cli/quota**', route => {
    const url = new URL(route.request().url());
    switch (url.pathname) {
      case '/api/cli/quota':
        return json({
          at: iso(now), ttlSeconds: 600,
          snapshots: ['claude', 'codex'].map(cliType => ({ cliType, fetchedAt: iso(now), plan: null, windows: [], source: null, rawSample: null, error: null })),
        })(route);
      case '/api/cli/quota/history':
        return json(url.searchParams.get('cli') === 'claude' ? claudeHistory(now) : codexHistory(now))(route);
      case '/api/cli/quota/model-routes':
        return json(modelRoutes)(route);
      case '/api/cli/quota/caps':
        return json({ defaultCapPct: 95, caps: {} })(route);
      case '/api/cli/quota/wait-policy':
        return json({ enabled: false, thresholdMinutes: 30 })(route);
      default:
        return json({})(route);
    }
  });
}

test('quota forecast shows curve, rate, forecast, reset, session gauge, and the armed fallback', async ({ page }) => {
  fs.mkdirSync(resultsDir, { recursive: true });
  const now = Date.now();
  await installRoutes(page, now);
  await page.setViewportSize({ width: 1440, height: 1100 });
  await openCliManagement(page);

  const panel = page.getByTestId('quota-forecast-panel');
  await expect(panel).toBeVisible({ timeout: 20_000 });

  const claude = panel.locator('[data-testid="quota-forecast-cli"][data-cli="claude"]');
  const claudeWeekly = claude.getByTestId('quota-weekly');
  await expect(claudeWeekly).toHaveAttribute('data-status', 'full-before-reset');
  await expect(claudeWeekly.getByTestId('quota-stat-current')).toHaveText('79%');
  await expect(claudeWeekly.getByTestId('quota-stat-rate')).toHaveText('2.0%/h');
  await expect(claudeWeekly.getByTestId('quota-forecast-sentence')).toContainText('before the reset');
  await expect(claudeWeekly.getByTestId('quota-forecast-badge')).toBeVisible();
  await expect(claudeWeekly.getByTestId('quota-curve-line')).toBeVisible();
  await expect(claudeWeekly.getByTestId('quota-curve-forecast')).toBeAttached();
  await expect(claude.getByTestId('quota-session-gauge')).toContainText('62%');

  const fallback = claudeWeekly.getByTestId('quota-fallback');
  await expect(fallback).toHaveAttribute('data-armed', 'true');
  await expect(fallback.getByTestId('quota-fallback-target')).toHaveText('codex · gpt-5.6-sol (high)');
  await expect(fallback).toContainText('Armed');
  await expect(fallback).toContainText('95% cap');

  const codexWeekly = panel.locator('[data-testid="quota-forecast-cli"][data-cli="codex"]').getByTestId('quota-weekly');
  await expect(codexWeekly).toHaveAttribute('data-status', 'resets-first');
  await expect(codexWeekly.getByTestId('quota-fallback')).toHaveCount(0);
  await expect(codexWeekly.getByTestId('quota-curve-reset')).toBeAttached();

  // Hover the curve: the crosshair tooltip snaps to the nearest reading.
  const svg = claudeWeekly.locator('svg');
  await svg.scrollIntoViewIfNeeded();
  const box = await svg.boundingBox();
  expect(box).not.toBeNull();
  await page.mouse.move(box!.x + box!.width * 0.78, box!.y + box!.height / 2);
  await expect(claudeWeekly.getByTestId('quota-curve-tooltip')).toBeVisible();

  const section = page.getByTestId('cli-admin-quota-forecast');
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await section.scrollIntoViewIfNeeded();
    const themedBox = await svg.boundingBox();
    await page.mouse.move(themedBox!.x + themedBox!.width * 0.78, themedBox!.y + themedBox!.height / 2);
    await expect(claudeWeekly.getByTestId('quota-curve-tooltip')).toBeVisible();
    await section.screenshot({ path: path.join(resultsDir, `quota-forecast-${theme}.png`) });
  }
});

test('quota forecast shows an empty state before the first reading', async ({ page }) => {
  const now = Date.now();
  await installRoutes(page, now);
  await page.route('**/api/cli/quota/history**', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ cliType: 'claude', hours: 48, from: iso(now - 48 * HOUR), to: iso(now), retentionDays: 14, rateLookbackHours: 3, windows: [] }),
  }));
  await openCliManagement(page);
  await expect(page.getByTestId('quota-forecast-empty')).toBeVisible({ timeout: 20_000 });
});
