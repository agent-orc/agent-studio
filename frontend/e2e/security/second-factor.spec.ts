import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const resultDir = process.env.JOB_RESULTS_DIR ?? 'test-results';

test('Recovery sign-in shows the remaining count and offers factor registration in both themes', async ({ page }) => {
  mkdirSync(resultDir, { recursive: true });
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/**', route => route.fulfill(json([])));
  await page.route('**/api/v1/studio/board**', route => route.fulfill(json({
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], review: [], autoReview: [], humanReview: [], completed: [], archive: [],
  })));
  await page.route('**/api/tasks/archive**', route => route.fulfill(json({ items: [], total: 0 })));
  await page.route('**/api/v1/workspaces**', route => route.fulfill(json([])));
  await page.route('**/api/v1/projects', route => route.fulfill(json([])));
  await page.route('**/api/environment**', route => route.fulfill(json({ isDev: false, devTools: {} })));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ at: '2026-11-02T09:00:00Z', sessions: [] })));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({ at: '2026-11-02T09:00:00Z', ttlSeconds: 600, snapshots: [] })));
  await page.route('**/api/usage/cockpit**', route => route.fulfill(json({
    snapshotVersion: 1, workspaceId: null, generatedAt: '2026-11-02T09:00:00Z',
    timeZone: 'UTC', weekStart: 1, clis: [], cost: null, runs: [], slots: [], sources: {},
  })));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill(json({ projects: {} })));
  await page.route('**/api/auth/status', route => route.fulfill(json({
    profile: 'networked', bootstrapRequired: false, authenticated: true, recoveryCodesRemaining: 2,
    recoveryEnrollmentAllowed: true,
    user: { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [],
      disabled: false, mustChangePassword: false, hasFactor: true },
  })));
  await page.route('**/api/auth/sessions', route => route.fulfill(json([])));
  await page.route('**/api/auth/totp/start', route => route.fulfill(json({ secret: 'TESTSECRET' })));
  await page.route('**/api/auth/totp/confirm', route => route.fulfill(json({ recoveryCodes: ['CODE1', 'CODE2'] })));
  await page.goto('/');
  const notice = page.getByTestId('recovery-sign-in-notice');
  await expect(notice).toContainText('You signed in with a recovery code; 2 remain');
  await expect(page.getByRole('button', { name: 'Register a new authenticator' })).toBeVisible();
  await setTheme(page, 'light');
  await page.screenshot({ path: join(resultDir, 'recovery-sign-in-light.png'), fullPage: true });
  await setTheme(page, 'dark');
  await page.screenshot({ path: join(resultDir, 'recovery-sign-in-dark.png'), fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: join(resultDir, 'recovery-sign-in-390px.png'), fullPage: true });
  await page.getByRole('button', { name: 'Register a new authenticator' }).click();
  await page.getByRole('button', { name: 'Add authenticator' }).click();
  await expect(page.getByTestId('session-totp-secret')).toHaveText('TESTSECRET');
  await expect(page.getByTestId('enrollment-step-up')).toHaveCount(0);
  await page.getByLabel('Authenticator code').fill('123456');
  await page.getByRole('button', { name: 'Verify authenticator' }).click();
  await expect(page.getByTestId('factor-bound-notice')).toContainText('New authenticator registered');
});

test('Owner enrollment requires a verified authenticator and shows recovery codes once', async ({ page }) => {
  mkdirSync(resultDir, { recursive: true });
  const secret = Array.from({ length: 32 }, (_, index) => String.fromCharCode(65 + index % 26)).join('');
  const recovery = Array.from({ length: 2 }, (_, index) => `recovery-${index}-${Date.now()}`);
  let confirmed = false;
  await page.route('**/api/**', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/auth/status', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({
      profile: 'networked', bootstrapRequired: false, authenticated: true,
      secondFactorEnrollmentRequired: true,
      user: { id: 'usr_test', username: 'owner', displayName: 'Owner', role: 'owner',
        projects: [], disabled: false, mustChangePassword: false },
    }),
  }));
  await page.route('**/api/auth/totp/start', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ secret }),
  }));
  await page.route('**/api/auth/totp/confirm', route => {
    expect(route.request().postDataJSON()).toEqual({ code: '123456' });
    confirmed = true;
    return route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ recoveryCodes: recovery }) });
  });
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Set up a second factor' })).toBeVisible();
  await page.getByRole('button', { name: 'Set up authenticator' }).click();
  await expect(page.getByTestId('totp-secret')).toHaveText(secret);
  await setTheme(page, 'light');
  await page.screenshot({ path: join(resultDir, 'second-factor-light--mocked.png'), fullPage: true });
  await setTheme(page, 'dark');
  await page.screenshot({ path: join(resultDir, 'second-factor-dark--mocked.png'), fullPage: true });
  await page.getByLabel('Authenticator code').fill('123456');
  await page.getByRole('button', { name: 'Verify authenticator' }).click();
  await expect(page.getByTestId('factor-recovery-codes')).toContainText(recovery[0]);
  expect(confirmed).toBe(true);
  await page.getByRole('button', { name: 'I stored the codes' }).click();
  await expect(page.getByTestId('factor-recovery-codes')).toHaveCount(0);
});

test('Owner can inspect and revoke sessions and receives a new-session notice', async ({ page }) => {
  mkdirSync(resultDir, { recursive: true });
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  const sessions = [
    { id: 'current', device: 'Current browser', address: '192.0.2.10',
      createdAt: '2026-11-02T09:00:00Z', lastUseAt: '2026-11-02T09:10:00Z', current: true },
  ];
  await page.route('**/api/**', route => route.fulfill(json([])));
  await page.route('**/api/v1/studio/board**', route => route.fulfill(json({
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], review: [], autoReview: [], humanReview: [], completed: [], archive: [],
  })));
  await page.route('**/api/tasks/archive**', route => route.fulfill(json({ items: [], total: 0 })));
  await page.route('**/api/v1/workspaces**', route => route.fulfill(json([])));
  await page.route('**/api/v1/projects', route => route.fulfill(json([])));
  await page.route('**/api/environment**', route => route.fulfill(json({ isDev: false, devTools: {} })));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ at: '2026-11-02T09:00:00Z', sessions: [] })));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({ at: '2026-11-02T09:00:00Z', ttlSeconds: 600, snapshots: [] })));
  await page.route('**/api/usage/cockpit**', route => route.fulfill(json({
    snapshotVersion: 1, workspaceId: null, generatedAt: '2026-11-02T09:00:00Z',
    timeZone: 'UTC', weekStart: 1, clis: [], cost: null, runs: [], slots: [], sources: {},
  })));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill(json({ projects: {} })));
  await page.route('**/api/auth/status', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({
      profile: 'networked', bootstrapRequired: false, authenticated: true,
      user: { id: 'usr_test', username: 'owner', displayName: 'Owner', role: 'owner',
        projects: [], disabled: false, mustChangePassword: false },
    }),
  }));
  await page.route('**/api/auth/sessions', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify(sessions),
  }));
  await page.route('**/api/auth/sessions/*', route => {
    sessions.splice(sessions.findIndex(session => session.id === route.request().url().split('/').at(-1)), 1);
    return route.fulfill({ status: 204 });
  });
  await page.route('**/api/auth/sessions/revoke-others', route => {
    sessions.splice(1);
    return route.fulfill({ status: 204 });
  });
  await page.goto('/');
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  await page.getByTestId('session-manager-toggle').click();
  await expect(page.getByTestId('session-row')).toHaveCount(1);
  sessions.push({ id: 'new', device: 'Second browser', address: '192.0.2.11',
    createdAt: '2026-11-02T10:00:00Z', lastUseAt: '2026-11-02T10:00:00Z', current: false });
  await page.getByTestId('session-manager-panel').getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(page.getByTestId('new-session-notice')).toBeVisible();
  await expect(page.getByTestId('session-row')).toHaveCount(2);
  await setTheme(page, 'light');
  await page.screenshot({ path: join(resultDir, 'sessions-light--mocked.png'), fullPage: true });
  await setTheme(page, 'dark');
  await page.screenshot({ path: join(resultDir, 'sessions-dark--mocked.png'), fullPage: true });
  await page.getByTestId('session-row').filter({ hasText: 'Second browser' }).getByRole('button', { name: 'Revoke' }).click();
  await expect(page.getByTestId('session-row')).toHaveCount(1);
  sessions.push({ id: 'newer', device: 'Another browser', address: '192.0.2.12',
    createdAt: '2026-11-02T11:00:00Z', lastUseAt: '2026-11-02T11:00:00Z', current: false });
  await page.getByTestId('session-manager-panel').getByRole('button', { name: 'Refresh', exact: true }).click();
  await page.getByRole('button', { name: 'Revoke all other sessions' }).click();
  await expect(page.getByTestId('session-row')).toHaveCount(1);
});
