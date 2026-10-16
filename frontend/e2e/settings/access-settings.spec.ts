import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const resultDir = process.env.JOB_RESULTS_DIR ?? 'test-results';
const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
const oneTimeSecret = ['api', 'one', 'time-value'].join('.');

test('owner manages access from Settings and sees each created token once', async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.route('**/api/auth/status', route => route.fulfill(json({
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
  })));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill(json({
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
  })));
  await page.route('**/api/auth/users', route => route.fulfill(json([
    { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
  ])));
  await page.route('**/api/auth/api-tokens', route => route.fulfill(json(route.request().method() === 'POST'
    ? { token: { id: 'tok_1', name: 'build bot' }, secret: oneTimeSecret }
    : [])));
  await page.route('**/api/auth/runner-enrollments', route => route.fulfill(json([])));
  await page.route('**/api/auth/runners', route => route.fulfill(json([])));

  await page.goto('/');
  await page.getByTestId('studio-ab-settings').click();
  await page.getByTestId('workspace-settings-rail-access').click();
  const access = page.getByTestId('access-settings');
  await expect(access.getByRole('heading', { name: 'Users' })).toBeVisible();
  await expect(access.getByRole('heading', { name: 'API tokens' })).toBeVisible();
  await expect(access.getByRole('heading', { name: 'Runner enrollments' })).toBeVisible();

  mkdirSync(resultDir, { recursive: true });
  await setTheme(page, 'light');
  await page.screenshot({ path: join(resultDir, 'access-light.png'), fullPage: true });
  await setTheme(page, 'dark');
  await page.screenshot({ path: join(resultDir, 'access-dark.png'), fullPage: true });

  await page.getByTestId('access-token-name').fill('build bot');
  await access.getByRole('textbox', { name: 'Projects' }).last().fill('PROJ-002');
  await access.getByRole('button', { name: 'Create token' }).click();
  await expect(page.getByTestId('access-one-time-secret')).toContainText(oneTimeSecret);
  await page.getByRole('button', { name: 'Dismiss' }).click();
  await expect(access).not.toContainText(oneTimeSecret);
});
