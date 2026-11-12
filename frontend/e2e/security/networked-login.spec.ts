import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const screenshotDir = path.join(process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', '..', 'results'), 'security');

test('signed-in user without project access sees one explanation', async ({ page }) => {
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ profile: 'networked', bootstrapRequired: false, authenticated: true,
      user: { id: 'usr_rui', username: 'rui', displayName: 'Rui', role: 'viewer', projects: [], disabled: false, mustChangePassword: false } }),
  }));
  mkdirSync(screenshotDir, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await page.addInitScript(value => localStorage.setItem('atp.studio.theme', value), theme);
    await page.goto('/');
    await expect(page.getByTestId('no-project-access')).toHaveText('You currently have no access to any project. Ask the owner.');
    await expect(page.getByText('Failed to load archived tasks.')).toHaveCount(0);
    await page.screenshot({ path: path.join(screenshotDir, `no-project-access-${theme}.png`) });
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: path.join(screenshotDir, 'no-project-access-390.png') });
});

test('first owner sign-in shows the restored archive and security review', async ({ page }) => {
  const evidence = path.join(process.env.JOB_RESULTS_DIR ?? screenshotDir, 'restore-security');
  mkdirSync(evidence, { recursive: true });
  const owner = { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner',
    projects: [], disabled: false, mustChangePassword: false };
  const notice = { archiveId: 'studio-backup-drill', archiveCreatedAt: '2026-11-09T06:00:00Z',
    restoredAt: '2026-11-10T06:00:00Z' };
  await page.route('**/api/auth/status', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ profile: 'networked', bootstrapRequired: false, authenticated: false }),
  }));
  await page.route('**/api/auth/login', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ profile: 'networked', bootstrapRequired: false, authenticated: true,
      user: owner, restoreNotice: notice }),
  }));
  let acknowledged = false;
  await page.route('**/api/auth/restore-notice/acknowledge', route => {
    expect(route.request().postDataJSON()).toEqual({ archiveId: notice.archiveId });
    acknowledged = true;
    return route.fulfill({ status: 204, body: '' });
  });
  await page.goto('/');
  await page.getByLabel('Username').fill('owner');
  await page.getByLabel('Password').fill('owner password long enough');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const gate = page.getByTestId('auth-gate');
  await expect(gate.getByRole('heading', { name: 'Review security after restore' })).toBeVisible();
  await expect(gate.getByTestId('restore-archive-id')).toHaveText(notice.archiveId);
  await expect(gate.getByTestId('restore-security-checks').locator('li')).toHaveCount(5);
  await setTheme(page, 'light');
  await page.screenshot({ path: path.join(evidence, 'restore-notice-light.png'), fullPage: true });
  await setTheme(page, 'dark');
  await page.screenshot({ path: path.join(evidence, 'restore-notice-dark.png'), fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: path.join(evidence, 'restore-notice-390.png'), fullPage: true });
  await gate.getByTestId('restore-review-access').click();
  await expect(gate).toHaveCount(0);
  expect(acknowledged).toBe(true);
  await expect(page).toHaveURL(/#\/workspace\/settings\/access$/);
});

test('auth gate follows saved light and dark themes at phone width', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ profile: 'networked', bootstrapRequired: false, authenticated: false, user: null }),
  }));
  for (const theme of ['light', 'dark'] as const) {
    await page.addInitScript(value => localStorage.setItem('atp.studio.theme', value), theme);
    await page.goto('/');
    await expect(page.getByTestId('auth-gate')).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
    const background = await page.getByTestId('auth-gate').evaluate(element => getComputedStyle(element).backgroundColor);
    expect(background).toBe(theme === 'light' ? 'rgb(255, 255, 255)' : 'rgb(15, 15, 26)');
    await page.screenshot({ path: path.join(process.env.JOB_RESULTS_DIR ?? screenshotDir, `auth-390-${theme}.png`), animations: 'disabled' });
  }
});

test('networked Studio gates the workspace behind same-origin login', async ({ page }) => {
  mkdirSync(screenshotDir, { recursive: true });
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({
    status: 200, contentType: 'application/json', body: '[]',
  }));
  await page.route('**/api/v1/management/links', route => route.fulfill({
    status: 200, contentType: 'application/json', body: '[]',
  }));
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
  await page.route('**/api/auth/users', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));

  await page.goto('/');
  await expect(page.getByTestId('auth-gate')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  await page.screenshot({ path: path.join(screenshotDir, 'networked-login.png'), fullPage: true });

  // The unauthenticated live-update socket remains disconnected. Its offline
  // grace period must never disable sign-in after the page has been open.
  await page.waitForTimeout(4_600);
  await page.screenshot({ path: path.join(screenshotDir, 'networked-login-after-offline.png'), fullPage: true });

  await page.getByLabel('Username').fill('owner');
  await page.getByLabel('Password').fill('not-stored-password');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByTestId('auth-gate')).toHaveCount(0);

  const stored = await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
  expect(stored).not.toContain('not-stored-password');
  expect(stored).not.toMatch(/rnr\.|ssn\./);

  await expect(page.getByTestId('studio-titlebar-account')).toContainText('Owner');
  await page.getByTestId('studio-titlebar-account').click();
  await page.getByTestId('studio-account-menu-item-sign-out').click();
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
      profile: 'networked',
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
        status: 400, contentType: 'application/json',
        body: JSON.stringify({ message: 'Check the owner details and try again.', errors: { bootstrapCode: ['A valid installer owner code is required.'] } }),
      });
      return;
    }
    expect(route.request().postDataJSON()).toMatchObject({ bootstrapCode: 'installer-code' });
    bootstrapOpen = false;
    await route.fulfill({
      status: 201, contentType: 'application/json', body: JSON.stringify({
        recoveryCode: 'rcv_once',
        status: { profile: 'networked', bootstrapRequired: false, bootstrapCodeRequired: false, authenticated: false, user: null },
      }),
    });
  });

  await page.goto('/', { waitUntil: 'commit', timeout: 60_000 });
  await expect(page.getByRole('heading', { name: 'Create the first owner' })).toBeVisible({ timeout: 60_000 });
  await expect(page.getByText('Use 12 to 128 characters. Do not include your username.')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Installer guide' })).toHaveAttribute('href', /install\.md$/);
  await page.setViewportSize({ width: 1280, height: 960 });
  await setTheme(page, 'light');
  mkdirSync(screenshotDir, { recursive: true });
  await page.screenshot({ path: path.join(screenshotDir, 'first-owner-light.png'), fullPage: true });
  await setTheme(page, 'dark');
  await page.screenshot({ path: path.join(screenshotDir, 'first-owner-dark.png'), fullPage: true });
  await page.setViewportSize({ width: 390, height: 720 });
  await page.getByRole('button', { name: 'Create owner' }).scrollIntoViewIfNeeded();
  await expect(page.getByRole('button', { name: 'Create owner' })).toBeInViewport();
  await page.setViewportSize({ width: 390, height: 960 });
  await page.screenshot({ path: path.join(screenshotDir, 'first-owner-390-dark.png'), fullPage: true });
  await expect(page.locator('body')).toHaveJSProperty('scrollWidth', 390);
  await page.getByLabel('Username').fill('owner');
  await page.getByLabel('Password').fill('owner');
  await page.getByRole('button', { name: 'Create owner' }).click();
  await expect(page.getByText('Password must be 12 to 128 characters.')).toBeVisible();
  await expect(page.getByText('Password must not contain the username.')).toBeVisible();
  expect(bootstrapAttempts).toBe(0);
  await page.getByLabel('Installer owner code').fill('wrong-code');
  await page.getByLabel('Password').fill('correct horse battery staple');
  await page.getByRole('button', { name: 'Create owner' }).click();
  await expect(page.getByText('A valid installer owner code is required.')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Create the first owner' })).toBeVisible();
  await page.getByLabel('Installer owner code').fill('installer-code');
  await page.getByRole('button', { name: 'Create owner' }).click();
  await expect(page.getByTestId('owner-recovery-code')).toHaveText('rcv_once');

  await page.reload({ waitUntil: 'commit', timeout: 60_000 });
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible({ timeout: 60_000 });
  await expect(page.getByLabel('Installer owner code')).toHaveCount(0);
});

test('one-time Product invitation lets the recipient choose a password', async ({ page }) => {
  const resultDir = process.env['JOB_RESULTS_DIR'] || path.resolve(__dirname, '..', '..', '..', 'results');
  mkdirSync(resultDir, { recursive: true });
  await page.route('**/api/**', route => route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ bootstrapRequired: false, authenticated: false, user: null }),
  }));
  await page.route('**/api/v1/studio/auth/invite/accept', route => {
    expect(route.request().postDataJSON()).toEqual({ token: 'sti_inv_example', newPassword: 'product password long enough' });
    return route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ status: { bootstrapRequired: false, authenticated: true,
        user: { userId: 'usr_product', username: 'product', displayName: 'Product User', role: 'contributor',
          projectIds: ['prj-assigned'], disabled: false, mustChangePassword: false } } }),
    });
  });
  await page.goto('/?invite=sti_inv_example');
  await expect(page.getByRole('heading', { name: 'Accept invitation' })).toBeVisible();
  await page.screenshot({ path: path.join(resultDir, 'product-invitation.png'), fullPage: true });
  await page.getByLabel('New password').fill('product password long enough');
  await page.getByRole('button', { name: 'Join project' }).click();
  await expect(page.getByTestId('auth-gate')).toHaveCount(0);
  await expect(page).toHaveURL(/\/#\/board$/);
  expect(new URL(page.url()).search).toBe('');
});
