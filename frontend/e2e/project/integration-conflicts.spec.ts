import { test, expect } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const summary = {
  conflicts: 3, integratedCards: 12, per100IntegratedCards: 25,
  topFiles: [{ name: 'backend/Host/Program.cs', count: 2 }, { name: 'frontend/src/app/features/project-detail/state/deploy-area.model.ts', count: 1 }],
  cardPairs: [{ cardA: 'AGT-101', cardB: 'AGT-102', count: 2 }],
  daily: Array.from({ length: 7 }, (_, index) => ({
    day: `2026-11-${16 + index}T00:00:00Z`, conflicts: index === 5 ? 2 : index === 6 ? 1 : 0,
    integratedCards: index === 5 ? 8 : index === 6 ? 4 : 0,
    per100IntegratedCards: index >= 5 ? 25 : null,
  })),
};

test('Pipeline shows conflict rate, files, pairs and recoverable states in both themes', async ({ page, request }) => {
  test.setTimeout(120000);
  const paths = await (await request.get('/api/watch-paths')).json() as { name: string }[];
  expect(paths.length).toBeGreaterThan(0);
  const project = paths[0].name;
  const slug = project.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  const runtimeErrors: string[] = [];
  const consoleErrors: string[] = [];
  const failedResponses: { url: string; status: number }[] = [];
  page.on('response', response => { if (response.status() >= 400) failedResponses.push({ url: new URL(response.url()).pathname, status: response.status() }); });
  page.on('pageerror', error => runtimeErrors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()); });
  // The isolated workspace has no Quality Studio registry binding. Preserve the
  // unavailable state without making an expected 404 a capture console error.
  await page.route('**/api/integrations/quality-studio/status**', route => route.fulfill({ json: {
    state: 'unreachable', configured: false, owner: false, repositoryId: null,
  } }));
  const backend = process.env['PW_BACKEND_URL'];
  if (backend) await page.route('**/healthz', async route => {
    await route.fulfill({ response: await route.fetch({ url: `${backend}/healthz` }) });
  });
  await page.route('**/api/projects/*/integration-conflicts**', route => route.fulfill({ json: { summary } }));
  await page.goto(`/#/projects/${slug}/pipeline`);
  const panel = page.getByTestId('integration-conflicts');
  await expect(panel).toBeVisible({ timeout: 30000 });
  await expect(panel.getByTestId('integration-conflict-rate')).toContainText('25', { timeout: 30000 });
  await expect(panel.getByRole('table')).toContainText('Nov 22');
  await expect(panel.getByRole('list', { name: 'Top conflicting files' })).toContainText('backend/Host/Program.cs');
  await expect(panel.getByRole('list', { name: 'Colliding card pairs' })).toContainText('AGT-101 / AGT-102');
  const output = process.env['JOB_RESULTS_DIR'];
  expect(output).toBeTruthy();
  mkdirSync(output!, { recursive: true });
  const round = process.env['CONFLICT_CAPTURE_ROUND'] ?? 'round2';
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 1040 : 1000 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await panel.scrollIntoViewIfNeeded();
      expect(await panel.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
      await panel.screenshot({ path: join(output!, `integration-conflicts-${round}-${width}-${theme}--mocked.png`) });
    }
  }
  writeFileSync(join(output!, 'visual-capture.json'), JSON.stringify({ runtimeErrors, consoleErrors, failedResponses }, null, 2));
  expect(runtimeErrors).toEqual([]);
  expect(consoleErrors).toEqual([]);
  await expect(page.getByTestId('error-dialog-overlay')).not.toBeVisible();
  await page.route('**/api/projects/*/integration-conflicts**', route => route.fulfill({ status: 503 }));
  await panel.getByRole('button', { name: 'Refresh conflicts' }).click();
  await expect(panel.getByRole('alert')).toContainText('unavailable');
  await page.route('**/api/projects/*/integration-conflicts**', route => route.fulfill({ json: {
    summary: { conflicts: 0, integratedCards: 0, per100IntegratedCards: null, daily: [], topFiles: [], cardPairs: [] },
  } }));
  await panel.getByRole('button', { name: 'Refresh conflicts' }).click();
  await expect(panel).toContainText('No integrations recorded');
  await expect(panel).toContainText('History starts when capture is deployed');
});

test('Conflict history API returns a stable empty shape and rejects invalid ranges', async ({ request }) => {
  const watchResponse = await request.get('/api/watch-paths');
  expect(watchResponse.ok()).toBeTruthy();
  const watchPaths = await watchResponse.json() as { name: string }[];
  expect(watchPaths.length).toBeGreaterThan(0);
  const endpoint = `/api/projects/${encodeURIComponent(watchPaths[0].name)}/integration-conflicts`;
  const response = await request.get(endpoint);
  expect(response.status()).toBe(200);
  const body = await response.json();
  expect(body.project).toBe(watchPaths[0].name);
  expect(Array.isArray(body.records)).toBe(true);
  expect(Array.isArray(body.summary.daily)).toBe(true);
  expect(body.summary).toHaveProperty('per100IntegratedCards');
  expect((await request.get(`${endpoint}?from=2026-11-22T00:00:00Z&to=2026-11-21T00:00:00Z`)).status()).toBe(400);
  expect((await request.get(`${endpoint}?from=2020-01-01T00:00:00Z&to=2026-11-22T00:00:00Z`)).status()).toBe(400);
  expect((await request.get('/api/projects/agt-3969-absent-project/integration-conflicts')).status()).toBe(404);
});
