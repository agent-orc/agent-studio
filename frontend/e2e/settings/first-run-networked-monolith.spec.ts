import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';

const resultDir = process.env.JOB_RESULTS_DIR ?? 'test-results';
const json = (body: unknown, status = 200) => ({
  status, contentType: 'application/json', body: JSON.stringify(body),
});

test('networked monolith explains that the first-run Task Server plane is unavailable', async ({ page }) => {
  await page.route('**/api/auth/status', route => route.fulfill(json({
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'owner', username: 'owner', displayName: 'Owner', role: 'owner',
      projects: [], disabled: false, mustChangePassword: false },
  })));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill(json({
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'owner', username: 'owner', displayName: 'Owner', role: 'owner',
      projects: [], disabled: false, mustChangePassword: false },
  })));
  await page.route('**/api/v1/installation', route => route.fulfill(json({ error: 'not-found' }, 404)));
  await page.route('**/api/v1/projects', route => route.fulfill(json({ error: 'not-found' }, 404)));

  await page.goto('/#/workspace/settings/first-run');
  const unavailable = page.getByTestId('first-run-task-server-unavailable');
  await expect(unavailable).toContainText('running without a Task Server connection');
  await expect(page.getByTestId('first-run-owner')).toHaveAttribute('data-state', 'pending');

  mkdirSync(resultDir, { recursive: true });
  await page.screenshot({ path: join(resultDir, 'first-run-networked-monolith.png'), fullPage: true });
});
