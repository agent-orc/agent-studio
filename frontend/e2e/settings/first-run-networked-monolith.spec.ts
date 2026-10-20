import { mkdirSync } from 'node:fs';
import { basename, join } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';

const resultDir = process.env.JOB_RESULTS_DIR ?? 'test-results';

test('networked monolith explains that the first-run Task Server plane is unavailable', async ({ page }) => {
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace).toBeTruthy();
  const before = await page.request.get('/api/auth/status');
  expect(before.ok()).toBeTruthy();
  expect(await before.json()).toMatchObject({ profile: 'networked', bootstrapRequired: true });
  const created = await page.request.post('/api/auth/bootstrap', { data: {
    username: 'first-run-owner', displayName: 'Owner',
    password: ['first-run', 'password', 'long-enough'].join('-'),
    bootstrapCode: `${basename(workspace!)}-owner-code`,
  } });
  expect(created.ok(), `${created.status()}: ${await created.text()}`).toBeTruthy();
  const authenticated = await page.request.get('/api/auth/status');
  expect(await authenticated.json()).toMatchObject({ profile: 'networked', authenticated: true,
    user: { role: 'owner' } });

  const installation = await page.request.get('/api/v1/installation');
  const projects = await page.request.get('/api/v1/projects');
  expect(installation.status()).toBe(404);
  expect(projects.status()).toBe(404);

  await page.goto('/#/workspace/settings/first-run');
  await expect(page.getByTestId('first-run-checklist')).toBeVisible();
  const unavailable = page.getByTestId('first-run-task-server-unavailable');
  await expect(unavailable).toContainText('running without a Task Server connection');
  await expect(page.getByTestId('first-run-owner')).toHaveAttribute('data-state', 'pending');

  mkdirSync(resultDir, { recursive: true });
  await page.screenshot({ path: join(resultDir, 'first-run-networked-monolith.png'), fullPage: true });
});
