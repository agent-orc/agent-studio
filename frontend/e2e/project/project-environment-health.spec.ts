import { expect, test, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { setTheme } from '../helpers/theme';

// IP-14: the project Operations tab and the workspace Environment view read the
// control-plane health checks. The health endpoints are mocked so the screenshots
// show an open incident, a close certificate expiry and a healthy stage.
const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results');
let projectName = '';

const sample = (ok: boolean, extra: Record<string, unknown> = {}) => ({
  at: '2026-10-19T08:00:00Z', ok, latencyMs: ok ? 118 : null, status: ok ? 200 : null,
  error: ok ? null : 'timeout', certificateNotAfter: null, ...extra,
});
const check = (extra: Record<string, unknown>) => ({
  id: 'http', kind: 'http', target: 'https://shop.example/healthz', derived: true, expectedStatus: 200,
  hasBodyMarker: false, intervalSeconds: 60, timeoutSeconds: 10, failureThreshold: 3, recoveryThreshold: 2,
  status: 'up', lastSample: sample(true), consecutiveFailures: 0, uptime24h: 1, uptime7d: 0.9993, uptime30d: 0.9989,
  latency24h: { p50Ms: 112, p95Ms: 236, p99Ms: 410 }, certificateNotAfter: null, ...extra,
});
const environments = (project: string) => [
  {
    project, environmentId: 'production', name: 'Production', kind: 'production', address: 'https://shop.example',
    status: 'down', uptime24h: 0.9861, uptime7d: 0.998, uptime30d: 0.9991, latency24h: { p50Ms: 131, p95Ms: 284, p99Ms: 512 },
    currentIncident: { id: 'production/http/20261019T074500Z', environmentId: 'production', checkId: 'http',
      startedAt: '2026-10-19T07:45:00Z', firstError: 'timeout', failedProbes: 15, endedAt: null,
      cardId: 'health-production-http-202610190745' },
    lastIncident: null,
    checks: [
      check({ status: 'down', lastSample: sample(false), consecutiveFailures: 15, uptime24h: 0.9861 }),
      check({ id: 'tls', kind: 'tls', target: 'shop.example:443', lastSample: sample(true, { status: null, latencyMs: 41 }),
        certificateNotAfter: '2026-10-29T08:00:00Z', latency24h: { p50Ms: 38, p95Ms: 61, p99Ms: 70 } }),
    ],
  },
  {
    project, environmentId: 'stage', name: 'Stage', kind: 'stage', address: 'https://stage.shop.example',
    status: 'up', uptime24h: 1, uptime7d: 0.9997, uptime30d: 0.9994, latency24h: { p50Ms: 98, p95Ms: 190, p99Ms: 260 },
    currentIncident: null,
    lastIncident: { id: 'stage/http/20261018T101000Z', environmentId: 'stage', checkId: 'http', startedAt: '2026-10-18T10:10:00Z',
      firstError: 'unexpected-status', failedProbes: 4, endedAt: '2026-10-18T10:16:00Z', cardId: 'health-stage-http-202610181010' },
    checks: [check({ target: 'https://stage.shop.example/healthz' })],
  },
];

async function mockHealth(page: Page): Promise<void> {
  await page.route('**/api/projects/*/environments/health', route => route.fulfill({ json: {
    project: projectName, capturedAt: '2026-10-19T08:00:00Z', probeOrigin: 'control-plane', environments: environments(projectName),
  } }));
  await page.route('**/api/environment/health', route => route.fulfill({ json: {
    capturedAt: '2026-10-19T08:00:00Z', probeOrigin: 'control-plane', environments: environments(projectName),
  } }));
}

test.beforeAll(async () => {
  fs.mkdirSync(results, { recursive: true });
  const paths = await api<{ name: string; path: string }[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  projectName = paths.find(p => /agent.?task/i.test(p.name))?.name ?? paths[0].name;
});

test('Operations tab shows uptime, latency, the open incident and certificate expiry in both themes', async ({ page }) => {
  await mockHealth(page);
  const slug = projectName.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  await page.goto(`/#/projects/${slug}/deploy`);
  await page.getByTestId('deploy-tab-operations').click();
  await expect(page.getByTestId('deploy-tab-operations')).toHaveAttribute('aria-current', 'page');
  const panel = page.getByTestId('environment-health-panel');
  await expect(panel).toBeVisible();
  const production = panel.getByTestId('environment-health-production');
  await expect(production).toHaveAttribute('data-status', 'down');
  await expect(production).toContainText('98.61 %');
  await expect(production).toContainText('284 ms');
  await expect(production.getByTestId('environment-health-incident')).toContainText('15 failed probes');
  await expect(production.getByTestId('environment-health-check-tls')).toContainText('Certificate expires in 10 days');
  await expect(panel.getByTestId('environment-health-stage')).toContainText('Last incident');

  for (const [width, height] of [[1440, 900], [1920, 1080]]) {
    await page.setViewportSize({ width, height });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: path.join(results, `environment-health-operations-${width}x${height}-${theme}.png`) });
    }
  }
});

test('Environment view lists application health across the workspace', async ({ page }) => {
  await mockHealth(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  // The title bar decides between the inline button and the overflow menu once it has rendered.
  await expect(page.getByTestId('studio-titlebar-theme')).toBeVisible();
  const inline = page.getByTestId('studio-titlebar-environment');
  if (await inline.isVisible()) await inline.click();
  else {
    await page.getByRole('button', { name: 'More navigation' }).click();
    await page.getByRole('menuitem', { name: 'Environment' }).click();
  }
  const overview = page.getByTestId('environment-health-overview');
  await expect(overview).toBeVisible();
  await expect(overview).toContainText('2 environments · 1 open incidents');
  const row = overview.locator('tr[data-status="down"]');
  await expect(row).toContainText('since Oct 19, 07:45 UTC · http');
  await overview.scrollIntoViewIfNeeded();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await overview.screenshot({ path: path.join(results, `environment-health-overview-1440-${theme}.png`) });
  }
});
