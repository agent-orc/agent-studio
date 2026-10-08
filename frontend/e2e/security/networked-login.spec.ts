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

test('standalone owner form uses the armed code only while bootstrap is open', async ({ page }) => {
  test.setTimeout(120_000);
  let bootstrapOpen = true;
  let bootstrapAttempts = 0;
  await page.route('**/hubs/v1/studio/negotiate?*', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({
      negotiateVersion: 1, connectionId: 'auth-e2e', connectionToken: 'auth-e2e',
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
    }),
  }));
  await page.routeWebSocket('**/hubs/v1/studio*', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send(`{}\u001e`);
    });
  });
  await page.route('**/api/**', route => route.fulfill({
    status: 200, contentType: 'application/json', body: '{}',
  }));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({
      bootstrapRequired: bootstrapOpen,
      bootstrapCodeRequired: bootstrapOpen,
      authenticated: false,
      user: null,
    }),
  }));
  await page.route('**/api/v1/studio/auth/bootstrap', async route => {
    bootstrapAttempts++;
    if (bootstrapAttempts === 1) {
      expect(route.request().postDataJSON()).toMatchObject({ bootstrapCode: 'wrong-code' });
      await route.fulfill({
        status: 401, contentType: 'application/json',
        body: JSON.stringify({ message: 'The installer owner code is invalid.' }),
      });
      return;
    }
    expect(route.request().postDataJSON()).toMatchObject({ bootstrapCode: 'installer-code' });
    bootstrapOpen = false;
    await route.fulfill({
      status: 201, contentType: 'application/json', body: JSON.stringify({
        recoveryCode: 'rcv_once',
        status: { bootstrapRequired: false, bootstrapCodeRequired: false, authenticated: false, user: null },
      }),
    });
  });

  await page.goto('/', { waitUntil: 'commit', timeout: 60_000 });
  await expect(page.getByRole('heading', { name: 'Create the first owner' })).toBeVisible({ timeout: 60_000 });
  await page.getByLabel('Username').fill('owner');
  await page.getByLabel('Installer owner code').fill('wrong-code');
  await page.getByLabel('Password').fill('owner password long enough');
  await page.getByRole('button', { name: 'Create owner' }).click();
  await expect(page.getByRole('alert')).toHaveText('The installer owner code is invalid.');
  await expect(page.getByRole('heading', { name: 'Create the first owner' })).toBeVisible();
  await page.getByLabel('Installer owner code').fill('installer-code');
  await page.getByRole('button', { name: 'Create owner' }).click();
  await expect(page.getByTestId('owner-recovery-code')).toHaveText('rcv_once');

  await page.reload({ waitUntil: 'commit', timeout: 60_000 });
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible({ timeout: 60_000 });
  await expect(page.getByLabel('Installer owner code')).toHaveCount(0);
});
