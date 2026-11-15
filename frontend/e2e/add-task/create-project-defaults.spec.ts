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
    if (screenshots) await page.screenshot({ path: join(screenshots, 'create-success-toast.png') });
    await page.getByRole('button', { name: 'Open card' }).click();
    await expect(page.getByTestId('detail-panes')).toBeVisible();
    await expect.poll(async () => (await getJob(createdId!, watchPath)).model).toBe(model);
    expect((await getJob(createdId!, watchPath)).cliType).toBe('codex');
  } finally {
    releaseCreate?.();
    if (createdId) {
      await api(`/api/tasks/${encodeURIComponent(createdId)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' }).catch(() => {});
    }
  }
});

test('failed project defaults show a retry, then Create files the card', async ({ page }) => {
  const paths = await api<Array<{ path: string; name: string }>>('/api/watch-paths');
  const watchPath = paths[0].path;
  const title = `e2e-project-defaults-retry-${Date.now()}`;
  const screenshots = process.env['JOB_RESULTS_DIR'];
  let failDefaults = true;
  let createdId: string | undefined;
  await page.goto('/');
  await page.route('**/api/v1/projects*', async route => {
    if (failDefaults) {
      await route.fulfill({ status: 503, body: 'Project registry unavailable' });
      return;
    }
    const response = await route.fetch();
    const projects = await response.json() as Array<Record<string, unknown>>;
    const target = projects.find(project => project['storageLocation'] === watchPath);
    expect(target).toBeTruthy();
    target!['cliDefault'] = 'codex';
    target!['modelDefault'] = model;
    await route.fulfill({ response, json: projects });
  });

  try {
    await page.getByRole('button', { name: /add task/i }).first().click();
    await page.getByTestId('create-title').fill(title);
    await page.getByTestId('create-prompt').fill('Verify project-default recovery.');
    const error = page.getByTestId('create-defaults-error');
    await expect(error).toContainText('HTTP 503');
    await expect(page.getByTestId('create-submit')).toBeDisabled();
    if (screenshots) {
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await page.getByRole('dialog').screenshot({ path: join(screenshots, `create-defaults-error-${theme}--mocked.png`) });
      }
      await page.setViewportSize({ width: 390, height: 844 });
      await page.screenshot({ path: join(screenshots, 'create-defaults-error-390--mocked.png') });
    }
    failDefaults = false;
    await page.getByTestId('create-defaults-retry').click();
    await expect(error).toHaveCount(0);
    await expect(page.getByTestId('create-agent')).toHaveAttribute('aria-label', /Codex.*Sol/i);
    await expect(page.getByTestId('create-submit')).toBeEnabled({ timeout: 15_000 });
    const created = page.waitForResponse(response => response.request().method() === 'POST'
      && new URL(response.url()).pathname === '/api/tasks');
    await page.getByTestId('create-submit').click();
    const response = await created;
    expect(response.ok(), await response.text()).toBe(true);
    createdId = (await response.json() as { id: string }).id;
    await expect(page.getByTestId('create-dialog-header')).toHaveCount(0);
    expect((await getJob(createdId, watchPath)).model).toBe(model);
  } finally {
    if (createdId) {
      await api(`/api/tasks/${encodeURIComponent(createdId)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' }).catch(() => {});
    }
  }
});
