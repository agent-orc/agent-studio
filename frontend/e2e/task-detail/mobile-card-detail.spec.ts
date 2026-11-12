import { expect, test } from '@playwright/test';
import { mkdirSync } from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';
import { createJob, getJob, moveJob } from '../helpers/jobs';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

interface WatchPath { path: string; }

const shots = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve(__dirname, '../../test-results/mobile-card-detail');

test('390 px card detail uses one page scroll, a pane switcher, and a full-width lane action in both themes', async ({ page }) => {
  const watchPaths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!watchPaths.length) throw new Error('No watch path configured for task detail.');
  const watchPath = watchPaths[0].path;
  const id = `e2e-mobile-detail-${Date.now()}`;
  const created = await createJob({
    id,
    title: 'Mobile review card',
    watchPath,
    targetState: '2-ready',
    mode: 'concept',
    requiresIntegration: false,
    promptMarkdown: '# Mobile review card\n\nReview the result and accept the work.',
  });

  try {
    for (let attempt = 0; attempt < 25; attempt++) {
      try { await getJob(created.id, watchPath); break; }
      catch { await new Promise(resolve => setTimeout(resolve, 200)); }
    }
    await api(`/api/tasks/${encodeURIComponent(created.id)}/concept-dossier?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'POST',
      body: JSON.stringify({ noDossierNeeded: true, reason: 'Visual review fixture.' }),
    });
    await moveJob(created.id, watchPath, '5-human-review');
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(watchPath)}`);
    await expect(page.getByTestId('studio-task').locator('app-job-detail')).toBeVisible({ timeout: 30_000 });
    await dismissDevErrorDialog(page);

    const switcher = page.getByTestId('detail-mobile-pane-switcher');
    const action = page.getByTestId('studio-mobile-triage-action-mark-done');
    await expect(switcher).toBeVisible();
    await expect(page.getByTestId('detail-mobile-pane-git')).toHaveCount(0);
    await expect(action).toBeVisible();
    await expect(action).toContainText('Accept');
    await expect(page.getByTestId('studio-triage-action-mark-done')).toBeHidden();
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
    const buttonBox = await action.boundingBox();
    const paneBox = await page.getByTestId('detail-panes').boundingBox();
    expect(buttonBox!.width).toBeGreaterThan(paneBox!.width - 32);
    expect(await action.evaluate(button => getComputedStyle(button).backgroundColor)).not.toBe('rgba(0, 0, 0, 0)');

    await expect(page.getByTestId('pane-prompt')).toBeVisible();
    await expect(page.getByTestId('pane-protocol')).toBeHidden();
    await expect(page.getByTestId('pane-git')).toBeHidden();

    const scroll = await page.getByTestId('pane-prompt-body').evaluate((body) => {
      let main = body.parentElement;
      while (main && getComputedStyle(main).overflowY !== 'auto') main = main.parentElement;
      if (!main) throw new Error('Card page has no vertical scroll owner.');
      const spacer = document.createElement('div');
      spacer.style.height = '1200px';
      body.append(spacer);
      main.scrollTop = 300;
      const result = {
        pageScroll: main.scrollTop,
        paneOverflow: getComputedStyle(body).overflowY,
        paneScroll: (body as HTMLElement).scrollTop,
      };
      spacer.remove();
      main.scrollTop = 0;
      return result;
    });
    expect(scroll.pageScroll).toBeGreaterThan(0);
    expect(scroll.paneOverflow).toBe('visible');
    expect(scroll.paneScroll).toBe(0);

    mkdirSync(shots, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: path.join(shots, `mobile-card-overview-${theme}--real.png`), animations: 'disabled' });

      await page.getByTestId('detail-mobile-pane-result').click();
      await expect(page.getByTestId('pane-protocol')).toBeVisible();
      await expect(page.getByTestId('pane-prompt')).toBeHidden();
      await expect(page.getByTestId('pane-git')).toBeHidden();
      await expect(page.getByTestId('pane-protocol-body')).toHaveCSS('overflow-y', 'visible');
      await page.screenshot({ path: path.join(shots, `mobile-card-result-${theme}--real.png`), animations: 'disabled' });

      await page.getByTestId('detail-mobile-pane-overview').click();
    }
    await page.setViewportSize({ width: 600, height: 844 });
    await expect(switcher).toBeHidden();
    await expect(action).toBeHidden();
    await expect(page.getByTestId('studio-triage-action-mark-done')).toBeVisible();
    await page.setViewportSize({ width: 1024, height: 844 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.getByTestId('studio-triage-action-mark-done')).toBeVisible();
      await page.screenshot({ path: path.join(shots, `card-header-1024-${theme}--real.png`), animations: 'disabled' });
    }
  } finally {
    await api<void>(`/api/tasks/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'DELETE',
    }).catch(() => undefined);
  }
});
