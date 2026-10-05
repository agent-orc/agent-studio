import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'node:path';

const screenshotDir = path.resolve(__dirname, '..', '..', '..', 'results', 'security');

test('networked Studio gates the workspace behind same-origin login', async ({ page }) => {
  mkdirSync(screenshotDir, { recursive: true });
  await page.route('**/api/**', route => route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/v1/studio/auth/login', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      profile: 'networked', bootstrapRequired: false, authenticated: true,
      user: { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
    }),
  }));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'networked', bootstrapRequired: false, authenticated: false, user: null }),
  }));
  await page.route('**/api/v1/studio/auth/logout', route => route.fulfill({ status: 204, body: '' }));

  await page.goto('/');
  await expect(page.getByTestId('auth-gate')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  await page.screenshot({ path: path.join(screenshotDir, 'networked-login.png'), fullPage: true });

  await page.getByLabel('Username').fill('owner');
  await page.getByLabel('Password').fill('not-stored-password');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByTestId('auth-gate')).toHaveCount(0);

  const stored = await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
  expect(stored).not.toContain('not-stored-password');
  expect(stored).not.toMatch(/rnr\.|ssn\./);

  await page.getByTestId('status-bar-sign-out').click();
  await expect(page.getByTestId('auth-gate')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
});
