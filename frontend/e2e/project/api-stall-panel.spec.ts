import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3369 evidence: Operations shows the Studio API's own stall detector.
 *   1. Against the real backend, the panel renders the live detector status.
 *   2. With the detector route stubbed to the 1 November 2026 finding (a
 *      61-second stall), the panel raises the alarm and lists the stalls with
 *      their delay, GC pause and thread-pool figures, in both themes.
 *
 * Screenshots land in JOB_RESULTS_DIR when set, otherwise in a sibling
 * playwright-screenshots/ tree.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'api-stall-panel');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'api-stall-panel');
})();

let projectName = '';

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  projectName = paths[0].name;
});

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

async function openOperations(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto(`/#/projects/${slugFor(projectName)}/operations`);
  await expect(page.getByTestId('project-operations-area')).toBeVisible({ timeout: 15_000 });
}

test('Operations shows the live API stall detector', async ({ page }) => {
  await openOperations(page);

  const panel = page.getByTestId('api-stall-panel');
  await expect(panel).toBeVisible({ timeout: 15_000 });
  // A loaded test host may record a slow tick; the status is whatever the
  // detector reports, never missing.
  await expect(panel).toHaveAttribute('data-status', /^(ok|warning|alarm)$/);
  await expect(page.getByTestId('api-stall-status')).toHaveText(/Responsive|Slow|Stalled/);

  await panel.screenshot({ path: path.join(SCREENSHOT_DIR, '01-live-detector.png') });
});

test('Operations raises the alarm for a stall above five seconds', async ({ page }) => {
  await page.route('**/api/diagnostics/api-stalls', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      status: 'alarm',
      checkedAtUtc: '2026-11-01T09:12:00Z',
      intervalMs: 500,
      warningThresholdMs: 1000,
      alarmThresholdMs: 5000,
      alarmWindowMinutes: 60,
      maxDelayMs: 61_000,
      recent: [
        { detectedAtUtc: '2026-11-01T09:10:00Z', delayMs: 61_000, gcPauseMs: 58_200, threadPoolThreads: 1480, pendingWorkItems: 312, processThreads: 1530 },
        { detectedAtUtc: '2026-11-01T09:04:30Z', delayMs: 7_400, gcPauseMs: 6_900, threadPoolThreads: 1210, pendingWorkItems: 95, processThreads: 1260 },
        { detectedAtUtc: '2026-11-01T08:58:10Z', delayMs: 1_300, gcPauseMs: 0, threadPoolThreads: 64, pendingWorkItems: 12, processThreads: 120 },
      ],
    }),
  }));
  await openOperations(page);

  const panel = page.getByTestId('api-stall-panel');
  await expect(panel).toHaveAttribute('data-status', 'alarm');
  await expect(page.getByTestId('api-stall-status')).toHaveText('Stalled');
  await expect(page.getByTestId('api-stall-summary')).toContainText('61.0 s');
  const rows = page.getByTestId('api-stall-recent').locator('tbody tr');
  await expect(rows).toHaveCount(3);
  await expect(rows.nth(0)).toHaveAttribute('data-alarm', 'true');
  await expect(rows.nth(0)).toContainText('58.2 s');
  await expect(rows.nth(0)).toContainText('1480');
  await expect(rows.nth(2)).toHaveAttribute('data-alarm', 'false');

  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await panel.screenshot({ path: path.join(SCREENSHOT_DIR, `02-alarm-${theme}.png`) });
  }
});
