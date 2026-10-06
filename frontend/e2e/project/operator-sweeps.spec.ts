import * as fs from 'fs';
import * as path from 'path';
import { test, expect } from '../fixtures/dev-backend';
import type { Page } from '@playwright/test';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3011: the operator sweeps block under the pipeline health alarm, against
 * the live fixture backend (no route mocks for the sweeps). Proves the real
 * projection renders, that pause and resume go through the API, and that a
 * pause is persisted (it survives a reload). The test resumes what it paused.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'operator-sweeps');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'operator-sweeps');
})();

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

async function proxyBackend(page: Page, baseUrl: string): Promise<void> {
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/crash-recovery/pending') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ pending: [] }) });
      return;
    }
    if (/^\/api\/cli\/[^/]+\/models$/.test(url.pathname)) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ models: [], source: 'e2e' }) });
      return;
    }
    const response = await route.fetch({ url: `${baseUrl}${url.pathname}${url.search}`, timeout: 30_000 });
    await route.fulfill({ response });
  });
  await page.route('**/api/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
}

test('operator sweeps (real): projection renders and a pause persists across reload', async ({ page, devBackend }) => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  await proxyBackend(page, devBackend.baseUrl);
  const paths = await (await fetch(`${devBackend.baseUrl}/api/watch-paths`)).json() as WatchPath[];
  const project = paths.find(p => /playwright/i.test(p.name)) ?? paths[0];
  expect(project, 'needs at least one watched project').toBeTruthy();
  const api = `${devBackend.baseUrl}/api/projects/${encodeURIComponent(project.name)}/operator-sweeps`;

  const projection = await (await fetch(api)).json();
  expect(projection.sweeps.map((s: { sweep: string }) => s.sweep)).toEqual(['fix-rounds', 'gate-triage', 'salvage']);
  expect(projection.maxRoundsPerCard).toBe(4);

  try {
    await page.setViewportSize({ width: 1440, height: 1600 });
    await page.goto(`/#/projects/${slugFor(project.name)}/pipeline`);
    await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });
    const block = page.getByTestId('operator-sweeps');
    await expect(block).toBeVisible();
    await expect(page.getByTestId('pipeline-health')).toBeVisible();
    for (const sweep of ['fix-rounds', 'gate-triage', 'salvage'])
      await expect(page.getByTestId(`operator-sweep-${sweep}`)).toBeVisible();

    await page.getByTestId('operator-sweep-toggle-gate-triage').click();
    await expect(page.getByTestId('operator-sweep-toggle-gate-triage')).toHaveText('Resume');
    await expect(page.getByTestId('operator-sweep-gate-triage')).toContainText('Paused');

    // Persisted: the API and a fresh page load both still say paused.
    const afterPause = await (await fetch(api)).json();
    expect(afterPause.sweeps.find((s: { sweep: string }) => s.sweep === 'gate-triage').paused).toBe(true);
    await page.reload();
    await expect(page.getByTestId('operator-sweep-toggle-gate-triage')).toHaveText('Resume', { timeout: 15_000 });

    await setTheme(page, 'light');
    await block.screenshot({ path: path.join(SCREENSHOT_DIR, 'operator-sweeps-paused--light--real.png') });
    await setTheme(page, 'dark');
    await block.screenshot({ path: path.join(SCREENSHOT_DIR, 'operator-sweeps-paused--dark--real.png') });

    await page.getByTestId('operator-sweep-toggle-gate-triage').click();
    await expect(page.getByTestId('operator-sweep-toggle-gate-triage')).toHaveText('Pause');
    await setTheme(page, 'light');
    await page.locator('app-pipeline-health-block').screenshot({
      path: path.join(SCREENSHOT_DIR, 'pipeline-health-with-sweeps--light--real.png'),
    });
  } finally {
    await fetch(`${api}/gate-triage/resume`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
  }
});
