import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const resultDir = process.env.JOB_RESULTS_DIR ?? 'test-results';
const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
const oneTimeSecret = ['api', 'one', 'time-value'].join('.');

test('owner manages access from Settings and sees each created token once', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-10-20T09:00:00Z') });
  const consoleErrors: string[] = [];
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()); });
  page.on('pageerror', error => consoleErrors.push(error.message));
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.route('**/favicon.ico', route => route.fulfill({ status: 204, body: '' }));
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'ok' }));
  await page.route('**/update/status**', route => route.fulfill(json({ available: false })));
  await page.route('**/api/**', route => route.fulfill(json([])));
  await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill(json({
    negotiateVersion: 1, connectionId: 'access-e2e', connectionToken: 'access-e2e',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  })));
  await page.route('**/api/watch-paths', route => route.fulfill(json([])));
  await page.route('**/api/v1/studio/board**', route => route.fulfill(json({
    backlog: [], preparation: [], orchestratorPrep: [], failedPickup: [], codeNotComplete: [],
    autoReview: [], ready: [], progress: [], review: [], humanReview: [], escalated: [],
    completed: [], archive: [],
  })));
  await page.routeWebSocket('**/hubs/v1/studio*', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send(`{}\u001e`);
    });
  });
  await page.route('**/api/auth/status', route => route.fulfill(json({
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
  })));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill(json({
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
  })));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill(json({ projects: {} })));
  await page.route('**/api/usage/cockpit**', route => route.fulfill(json({ workspaceId: 'web', clis: [], cost: null })));
  await page.route('**/api/v1/management/credential-inventory**', route => route.fulfill(json({ generatedAt: '2026-10-20T09:00:00Z', items: [] })));
  await page.route('**/api/cli/usage', route => route.fulfill(json({ at: '2026-10-20T09:00:00Z', sessions: [] })));
  await page.route('**/api/crash-recovery/pending', route => route.fulfill(json({ pending: [] })));
  await page.route('**/api/auth/users', route => route.fulfill(json([
    { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
  ])));
  const tokens: { id: string; name: string; role: string; projects: string[]; createdBy: string; createdAt: string; expiresAt: string; lastUsedAt: string | null; revokedAt: string | null }[] = [];
  await page.route('**/api/auth/api-tokens**', route => {
    const request = route.request();
    if (request.method() === 'POST') {
      const body = request.postDataJSON() as { name: string; role: string; projects: string[]; expiresAt: string };
      const token = { id: 'tok_1', ...body, createdBy: 'owner', createdAt: '2026-10-20T09:00:00Z', lastUsedAt: null, revokedAt: null };
      tokens.push(token);
      return route.fulfill(json({ token, secret: oneTimeSecret }));
    }
    if (request.method() === 'DELETE') {
      const id = new URL(request.url()).pathname.split('/').at(-1);
      const index = tokens.findIndex(token => token.id === id);
      if (index >= 0) tokens.splice(index, 1);
      return route.fulfill({ status: 204, body: '' });
    }
    return route.fulfill(json(tokens));
  });
  await page.route('**/api/auth/runner-enrollments', route => route.fulfill(json([])));
  await page.route('**/api/auth/runners', route => route.fulfill(json([])));

  await page.goto('/');
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  await page.getByTestId('studio-ab-settings').click();
  await page.getByTestId('workspace-settings-rail-access').click();
  const access = page.getByTestId('access-settings');
  await expect(access.getByRole('heading', { name: 'Users' })).toBeVisible();
  await expect(access.getByRole('heading', { name: 'API tokens' })).toBeVisible();
  await expect(access.getByRole('heading', { name: 'Runner enrollments' })).toBeVisible();
  await expect(access.getByText('No API tokens yet.')).toBeVisible();

  mkdirSync(resultDir, { recursive: true });
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: join(resultDir, `access-${width}-${theme}.png`), fullPage: true });
    }
  }

  await page.getByTestId('access-token-name').fill('build bot');
  const tokenForm = access.locator('form').nth(1);
  await tokenForm.getByLabel('Projects').fill('PROJ-002');
  await page.getByTestId('access-token-name').focus();
  await page.keyboard.press('Tab');
  await expect(tokenForm.getByLabel('Role')).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(tokenForm.getByLabel('Projects')).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(tokenForm.getByLabel('Valid days')).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(access.getByRole('button', { name: 'Create token' })).toBeFocused();
  await access.getByRole('button', { name: 'Create token' }).click();
  await expect(page.getByTestId('access-one-time-secret')).toContainText(oneTimeSecret);
  await page.getByRole('button', { name: 'Dismiss' }).click();
  await expect(access).not.toContainText(oneTimeSecret);
  await expect(access).toContainText('build bot');
  await access.getByRole('button', { name: 'Revoke' }).click();
  await expect(access).not.toContainText('build bot');
  expect(consoleErrors).toEqual([]);
});

test('Access renders empty and unavailable token states', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-10-20T09:00:00Z') });
  await page.route('**/api/**', route => route.fulfill(json([])));
  await page.route('**/api/auth/status', route => route.fulfill(json({ profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false } })));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill(json({ profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false } })));
  await page.route('**/api/auth/users', route => route.fulfill(json([])));
  await page.route('**/api/auth/api-tokens', route => route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Token service unavailable' }) }));
  await page.route('**/api/auth/runner-enrollments', route => route.fulfill(json([])));
  await page.route('**/api/auth/runners', route => route.fulfill(json([])));
  await page.goto('/#/workspace/settings/access', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('access-settings')).toBeVisible();
  await expect(page.getByRole('alert')).toContainText('Token service unavailable');
  await expect(page.getByTestId('access-settings').getByText('No API tokens yet.')).toBeVisible();
});
