import fs from 'node:fs';
import path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { setTheme } from '../helpers/theme';

const results = process.env.JOB_RESULTS_DIR ?? path.resolve('test-results');

test('health incident opens as an operations record at desktop and phone widths', async ({ page }) => {
  const pageErrors: string[] = [];
  const consoleErrors: string[] = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()); });
  // The isolated project has no optional Quality Studio connection or host health route.
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'ok' }));
  await page.route('**/api/integrations/quality-studio/status?*', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ state: 'not-connected', configured: false, owner: false, repositoryId: null }),
  }));
  const watch = (await api<{ name: string; path: string }[]>('/api/watch-paths?includeFixtures=true'))[0];
  expect(watch).toBeTruthy();
  const projectSlug = watch.name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  const id = `health-e2e-stage-${Date.now()}`;
  const prompt = `# Health incident: Stage

Control plane health checks failed.

- Incident: stage/http/20261019T080000Z
- Started: 2026-10-19 08:00 UTC
- Running release: v1.4.2
- Deployed: 2026-10-19 07:30 UTC
- Release cards: CARD-1, CARD-2
- Restart count: 4
- [Open Operations](#/projects/${projectSlug}/deploy?view=operations&environmentId=stage&since=2026-10-19T08%3A00%3A00Z)
- [Roll back](#/projects/${projectSlug}/deploy?view=tools&environmentId=stage)
- [Run runbook](#/projects/${projectSlug}/deploy?view=runbooks&environmentId=stage)

## Last crash or error log lines

- 2026-10-19 08:05 UTC error: process crashed`;
  await api('/api/tasks', { method: 'POST', body: JSON.stringify({ id,
    title: 'Health incident: Stage · 2026-10-19 08:00 UTC', watchPath: watch.path,
    promptMarkdown: prompt, creationSource: 'environment-health', createdBy: 'system',
    targetState: '1-preparation', taskType: 'bug', fixture: true }) });
  try {
    const taskUrl = `/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watch.path)}`;
    await page.goto(taskUrl);
    const card = page.getByTestId('health-incident-card');
    await expect(card).toBeVisible({ timeout: 20_000 });
    await expect(card).toContainText('v1.4.2');
    await expect(card).toContainText('process crashed');
    await expect(page.getByTestId('overview-agent')).toHaveCount(0);
    await expect(page.getByTestId('overview-pipeline')).toHaveCount(0);
    await expect(page.getByTestId('delivery-claim-panel')).toHaveCount(0);
    await expect(page.getByTestId('studio-triage-panel')).toHaveCount(0);
    for (const action of ['incident-open-operations', 'incident-roll-back', 'incident-run-runbook'])
      await expect(page.getByTestId(action)).toBeVisible();

    fs.mkdirSync(results, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await page.setViewportSize({ width: 1280, height: 900 });
      await setTheme(page, theme);
      await page.screenshot({ path: path.join(results, `health-incident-1280-${theme}.png`) });
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await setTheme(page, 'light');
    await expect(page.getByTestId('incident-open-operations')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
    for (const action of ['incident-open-operations', 'incident-roll-back', 'incident-run-runbook']) {
      const box = await page.getByTestId(action).boundingBox();
      expect(box?.height).toBeGreaterThanOrEqual(44);
    }
    await page.screenshot({ path: path.join(results, 'health-incident-390-light.png'), fullPage: true });
    await page.getByText('Last crash or error log lines').scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(results, 'health-incident-390-evidence-light.png') });
    await setTheme(page, 'dark');
    await page.screenshot({ path: path.join(results, 'health-incident-390-dark.png'), fullPage: true });
    await page.getByTestId('incident-open-operations').click();
    await expect(page.getByTestId('deploy-tab-operations')).toHaveAttribute('aria-current', 'page');
    await page.goto(taskUrl);
    await page.getByTestId('incident-roll-back').click();
    await expect(page).toHaveURL(/view=tools&environmentId=stage/);
    await expect(page.getByTestId('project-deployment-panel')).toBeVisible();
    await page.goto(taskUrl);
    await page.getByTestId('incident-run-runbook').click();
    await expect(page.getByTestId('deploy-tab-runbooks')).toHaveAttribute('aria-current', 'page');
    expect(pageErrors).toEqual([]);
    expect(consoleErrors).toEqual([]);
  } finally {
    await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watch.path)}`,
      { method: 'DELETE' }).catch(() => {});
  }
});
