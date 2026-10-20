import { mkdirSync } from 'node:fs';
import { basename, join } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';

const resultDir = process.env.JOB_RESULTS_DIR ?? 'test-results';

test('networked monolith explains that the first-run Task Server plane is unavailable', async ({ page }) => {
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace).toBeTruthy();
  const before = await page.request.get('/api/auth/status');
  expect(before.ok()).toBeTruthy();
  const status = await before.json() as { profile: string; bootstrapRequired: boolean };
  expect(status.profile).toBe('networked');
  const password = ['first-run', 'password', 'long-enough'].join('-');
  const session = status.bootstrapRequired
    ? await page.request.post('/api/auth/bootstrap', { data: {
      username: 'first-run-owner', displayName: 'Owner', password,
      bootstrapCode: `${basename(workspace!)}-owner-code`,
    } })
    : await page.request.post('/api/auth/login', { data: { username: 'first-run-owner', password } });
  expect(session.ok(), `${session.status()}: ${await session.text()}`).toBeTruthy();
  // The test proxy is HTTP while the networked backend sets a Secure cookie.
  // Use its supported local cookie name for this loopback-only browser run.
  const token = session.headers()['set-cookie']?.match(/__Host-agentstudio-session=([^;,\s]+)/)?.[1];
  expect(token).toBeTruthy();
  await page.context().addCookies([{
    name: 'agentstudio-session', value: token!, url: process.env.PW_BASE_URL!, sameSite: 'Lax',
  }]);
  const authenticated = await page.request.get('/api/auth/status');
  expect(await authenticated.json()).toMatchObject({ profile: 'networked', authenticated: true,
    user: { role: 'owner' } });

  const installation = await page.request.get('/api/v1/installation');
  const projects = await page.request.get('/api/v1/projects');
  expect(installation.status()).toBe(404);
  expect(projects.ok()).toBeTruthy();
  expect(Array.isArray(await projects.json())).toBe(true);

  await page.goto('/#/workspace/settings/first-run');
  await expect(page.getByTestId('first-run-checklist')).toBeVisible();
  const unavailable = page.getByTestId('first-run-task-server-unavailable');
  await expect(unavailable).toContainText('running without a Task Server connection');
  await expect(page.getByTestId('first-run-owner')).toHaveAttribute('data-state', 'pending');

  mkdirSync(resultDir, { recursive: true });
  await page.screenshot({ path: join(resultDir, 'first-run-networked-monolith.png'), fullPage: true });
});
