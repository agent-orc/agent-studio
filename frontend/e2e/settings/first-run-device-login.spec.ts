import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import type { Route } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { setTheme } from '../helpers/theme';

const shots = resolve(process.env.JOB_RESULTS_DIR ?? '../results', 'first-run-device-login');
const json = (body: unknown) => (route: Route) => route.fulfill({
  status: 200, contentType: 'application/json', body: JSON.stringify(body),
});

// AGT-3225: the first-run checklist starts provider sign-in through the same Task Server
// device-login API as the Access and Execution Hosts actions.
test('networked first-run provider sign-in uses the shared device-login API', async ({ page }) => {
  mkdirSync(shots, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 900 });
  const now = new Date().toISOString();
  const owner = {
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'owner', username: 'owner', displayName: 'Owner', role: 'owner',
      projects: [], disabled: false, mustChangePassword: false },
  };
  await page.route('**/api/auth/status', json(owner));
  await page.route('**/api/v1/studio/auth/status', json(owner));
  await page.route('**/api/v1/installation', json({ installationId: 'install-a', ownerBootstrapped: true }));
  await page.route('**/api/v1/projects', json([]));
  await page.route('**/api/v1/management/links', json([]));
  // A live studio socket keeps the offline guard from refusing the cancel POST on leave.
  await page.route('**/hubs/v1/studio/negotiate**', json({
    negotiateVersion: 1, connectionId: 'first-run', connectionToken: 'first-run',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  }));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol"')) socket.send('{}\u001e');
    });
  });
  await page.route('**/api/v1/management/remote-hosts', json([{
    runnerId: 'coding-a', name: 'Coding host A', hostId: 'host-a', instanceId: 'i-1',
    runnerVersion: '0.9.5', protocolVersion: 1, status: 'active', registeredAt: now, lastSeenAt: now,
    hostAdmission: {}, capabilities: [{
      key: 'provider-auth:claude', category: 'provider-auth', advertisedStatus: 'unavailable',
      healthState: 'unhealthy', advertisedAt: now, freshUntil: now, isFresh: true,
      consecutiveFailures: 2, signal: 'signed-out', detail: 'Claude is signed out.',
    }],
  }]));
  const started: unknown[] = [];
  await page.route('**/api/v1/management/device-logins', route => {
    started.push(route.request().postDataJSON());
    return json({
      handle: 'dlogin_first_run', state: 'queued', sessionKey: 'browser-session',
      verificationUrl: null, userCode: null, expiresAt: new Date(Date.now() + 900_000).toISOString(),
    })(route);
  });
  const sessionKeys: (string | undefined)[] = [];
  await page.route('**/api/v1/management/device-logins/*', route => {
    sessionKeys.push(route.request().headers()['x-device-login-session']);
    return json({
      handle: 'dlogin_first_run', state: 'running', detail: null, sessionKey: null,
      verificationUrl: 'https://claude.ai/oauth/authorize', userCode: null,
      expiresAt: new Date(Date.now() + 900_000).toISOString(),
    })(route);
  });
  const cancelKeys: (string | undefined)[] = [];
  await page.route('**/api/v1/management/device-logins/*/cancel', route => {
    cancelKeys.push(route.request().headers()['x-device-login-session']);
    return route.fulfill({ status: 200 });
  });
  let legacyCalls = 0;
  await page.route('**/api/v1/management/runner-device-logins**', route => {
    legacyCalls += 1;
    return route.fulfill({ status: 404 });
  });

  await page.goto('/#/workspace/settings/first-run', { waitUntil: 'domcontentloaded' });
  const row = page.getByTestId('first-run-provider-coding-a:claude');
  await expect(row).toBeVisible();
  await row.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByTestId('first-run-device-url')).toHaveAttribute('href', 'https://claude.ai/oauth/authorize');
  await expect(page.getByTestId('first-run-device-login')).toContainText('Claude on Coding host A: running');
  expect(started).toEqual([{ hostId: 'host-a', provider: 'claude', targetRunnerId: 'coding-a' }]);
  expect(sessionKeys[0]).toBe('browser-session');
  await expect.poll(() => sessionKeys.length).toBeGreaterThanOrEqual(2);
  expect(sessionKeys[1]).toBe('browser-session');
  expect(legacyCalls).toBe(0);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.getByTestId('first-run-providers').screenshot({ path: join(shots, `first-run-device-login-${theme}--mocked.png`) });
  }
  // Leaving the checklist cancels the running session with its key (AGT-3225 follow-up).
  await page.getByRole('complementary', { name: 'Settings sections' }).getByRole('button', { name: 'Overview' }).click();
  await expect(page.getByTestId('first-run-device-login')).toHaveCount(0);
  await expect.poll(() => cancelKeys).toEqual(['browser-session']);
});
