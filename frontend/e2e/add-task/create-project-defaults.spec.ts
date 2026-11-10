import { test, expect } from '@playwright/test';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { getJob } from '../helpers/jobs';
import { setTheme } from '../helpers/theme';

const model = 'gpt-6-sol';

test('create uses project agent defaults, closes once, and links the card', async ({ page }) => {
  const paths = await api<Array<{ path: string; name: string }>>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  const watchPath = paths[0].path;
  const title = `e2e-project-defaults-${Date.now()}`;
  const screenshots = process.env['JOB_RESULTS_DIR'];
  let releaseCreate: (() => void) | undefined;
  let posted: Record<string, unknown> | undefined;
  let createdId: string | undefined;

  await page.route('**/api/v1/projects*', async route => {
    const response = await route.fetch();
    const projects = await response.json() as Array<Record<string, unknown>>;
    const target = projects.find(project => project['storageLocation'] === watchPath);
    expect(target).toBeTruthy();
    target!['cliDefault'] = 'codex';
    target!['modelDefault'] = model;
    await route.fulfill({ response, json: projects });
  });
  await page.route('**/api/tasks', async route => {
    if (route.request().method() !== 'POST') return route.fallback();
    posted = route.request().postDataJSON() as Record<string, unknown>;
    await new Promise<void>(resolve => { releaseCreate = resolve; });
    const response = await route.fetch();
    createdId = (await response.json() as { id: string }).id;
    await route.fulfill({ response });
  });

  try {
    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();
    await page.getByTestId('create-project-select').selectOption(watchPath);
    await expect(page.getByTestId('create-agent')).toHaveAttribute('aria-label', /Codex.*Sol/i);
    await page.getByTestId('create-title').fill(title);
    if (screenshots) {
      await setTheme(page, 'light');
      await page.screenshot({ path: join(screenshots, 'create-defaults-light.png') });
      await setTheme(page, 'dark');
      await page.screenshot({ path: join(screenshots, 'create-defaults-dark.png') });
      await page.setViewportSize({ width: 390, height: 844 });
      await page.screenshot({ path: join(screenshots, 'create-defaults-390.png') });
      await page.setViewportSize({ width: 1280, height: 900 });
      await setTheme(page, 'light');
    }

    await page.evaluate(() => {
      const original = Storage.prototype.setItem;
      Storage.prototype.setItem = function (key, value) {
        if (key === 'lastCreateWatchPath') throw new DOMException('Storage full', 'QuotaExceededError');
        return original.call(this, key, value);
      };
    });

    await page.getByTestId('create-submit').click();
    await expect(page.getByTestId('create-submit')).toHaveAttribute('aria-busy', 'true');
    await expect(page.getByTestId('create-submit')).toBeDisabled();
    await expect(page.getByRole('dialog')).toBeVisible();
    expect(posted?.['cliType']).toBe('codex');
    expect(posted?.['model']).toBe(model);
    expect(posted?.['modelExplicit']).toBe(true);

    releaseCreate?.();
    await expect(page.getByTestId('create-dialog-header')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Open card' })).toBeVisible();
    expect(createdId).toBeTruthy();
    await expect.poll(async () => (await getJob(createdId!, watchPath)).model).toBe(model);
    expect((await getJob(createdId!, watchPath)).cliType).toBe('codex');
    await expect(page.getByTestId('task-card').filter({ hasText: title }).getByTestId('task-card-effective-model')).toContainText(/sol/i);
    if (screenshots) await page.screenshot({ path: join(screenshots, 'create-success-toast.png') });
    await page.getByRole('button', { name: 'Open card' }).click();
    await expect(page.getByTestId('detail-panes')).toBeVisible();
  } finally {
    releaseCreate?.();
    if (createdId) {
      await api(`/api/tasks/${encodeURIComponent(createdId)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' }).catch(() => {});
    }
  }
});
