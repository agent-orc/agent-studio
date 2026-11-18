import { expect, test, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { getJob } from '../helpers/jobs';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

// The playthrough: the task detail route showed an empty band under the top bar.
// Before the card resolves, the tab-action strip had no actions but still took
// its own row on a phone (and an empty slot in the tab bar on a desktop),
// because the projected `[studioTabActions]` wrapper is always present.
const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results', 'task-route-no-empty-band');

declare global {
  interface Window { __emptyActionFrames?: number; __sampledFrames?: number }
}

/** Counts animation frames in which the action strip is visible without a rendered action. */
async function sampleActionStrip(page: Page): Promise<void> {
  await page.addInitScript(() => {
    window.__emptyActionFrames = 0;
    window.__sampledFrames = 0;
    const tick = () => {
      const strip = document.querySelector<HTMLElement>('[data-testid="studio-tab-actions"]');
      if (strip) {
        window.__sampledFrames = (window.__sampledFrames ?? 0) + 1;
        const actions = strip.querySelector('app-studio-tab-actions');
        if (strip.getBoundingClientRect().height > 0 && (!actions || actions.childElementCount === 0))
          window.__emptyActionFrames = (window.__emptyActionFrames ?? 0) + 1;
      }
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });
}

test('the task detail route has no empty band under the top bar at 1280 and 390 px in both themes', async ({ page }) => {
  test.setTimeout(180_000);
  fs.mkdirSync(results, { recursive: true });
  const watchPaths = await api<{ path: string }[]>('/api/watch-paths?includeFixtures=true');
  const watchPath = watchPaths[0].path;
  const created = await api<{ id: string }>('/api/tasks', { method: 'POST', body: JSON.stringify({
    id: `health-production-${Date.now()}`, title: 'Health incident: Production is failing', watchPath,
    targetState: '0-backlog', taskType: 'bug', tags: ['environment-health'], requiresIntegration: false,
    creationSource: 'environment-health', createdBy: 'system', fixture: true,
    promptMarkdown: '# Health incident: Production\n\nThe production health check fails.',
  }) });
  let key = created.id;
  for (let attempt = 0; attempt < 25; attempt++) {
    try { key = (await getJob(created.id, watchPath) as { key?: string }).key ?? created.id; break; }
    catch { await new Promise(resolve => setTimeout(resolve, 200)); }
  }

  await sampleActionStrip(page);
  // Hold the card reads so the route stays in its loading phase long enough to be sampled.
  await page.route(url => url.pathname.startsWith('/api/tasks/') && url.pathname.includes(created.id), async route => {
    await new Promise(resolve => setTimeout(resolve, 600));
    await route.continue().catch(() => undefined);
  });

  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
    await page.goto(`/#/tasks/${encodeURIComponent(key)}`);
    await expect(page.getByTestId('studio-task').locator('app-job-detail')).toBeVisible({ timeout: 30_000 });
    await dismissDevErrorDialog(page);
    await expect(page.getByTestId('studio-task-next')).toBeVisible();

    const frames = await page.evaluate(() => ({ empty: window.__emptyActionFrames ?? 0, sampled: window.__sampledFrames ?? 0 }));
    expect(frames.sampled, `sampled frames at ${width} px`).toBeGreaterThan(0);
    expect(frames.empty, `frames with an empty action strip at ${width} px`).toBe(0);

    const gap = await page.evaluate(() => {
      const tabbar = document.querySelector('[data-testid="studio-tabbar"]')!.getBoundingClientRect();
      const main = document.querySelector('[data-testid="studio-task"]')!.getBoundingClientRect();
      return Math.round(main.top - tabbar.bottom);
    });
    expect(gap, `space between the tab bar and the task at ${width} px`).toBeLessThanOrEqual(1);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: path.join(results, `task-route-${width}-${theme}.png`) });
    }
    await setTheme(page, 'light');
  }
});
