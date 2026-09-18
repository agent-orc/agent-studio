import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test } from '@playwright/test';
import { setTheme } from '../helpers/theme';

// Real Project settings components with isolated HTTP fixtures. No live project is mutated.
test('integration gate reuse choices persist and render in both themes', async ({ page }) => {
  const project = 'reuse-demo';
  let enabled: boolean | null = null;
  const writes: (boolean | null)[] = [];
  const settings = () => ({
    autoCommit: true, crashRecoveryEnabled: true, autoPushStrategy: 'never',
    integrationGateReviewReuse: enabled, integrationGateReviewReuseEffective: enabled ?? true,
    pickupMode: 'manual', executionLocation: 'agent-runner-01', integrationBranch: 'develop',
  });
  await page.route('**/api/**', route => route.fulfill({ json: [] }));
  await page.route(/\/api\/(?:jobs|tasks)\/grouped/, route => route.fulfill({ json: {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
    humanReview: [], completed: [], archive: [],
  } }));
  await page.route('**/api/auth/status', route => route.fulfill({ json: {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  } }));
  await page.route('**/api/watch-paths**', route => route.fulfill({ json: [
    { name: project, path: '/tasks', rootPath: '/repo', repositoryPath: '/repo' },
  ] }));
  await page.route('**/api/projects/reuse-demo/workbenches**', route => route.fulfill({ json: { items: [] } }));
  await page.route('**/api/projects/reuse-demo/execution', route => route.fulfill({ json: { valid: true, issues: [], source: 'subject-commit' } }));
  await page.route('**/api/projects/settings', route => route.fulfill({ json: { [project]: settings() } }));
  await page.route('**/api/projects/reuse-demo/snapshot', route => route.fulfill({ json: { settings: settings() } }));
  await page.route('**/api/runner/status**', route => route.fulfill({ json: { projects: {} } }));
  await page.route('**/api/cli/usage**', route => route.fulfill({ json: { items: [] } }));
  await page.route('**/api/cli/quota**', route => route.fulfill({ json: { snapshots: [] } }));
  await page.route('**/api/projects/reuse-demo/integration-gate-review-reuse', async route => {
    expect(route.request().method()).toBe('PUT');
    enabled = route.request().postDataJSON().enabled;
    writes.push(enabled);
    await route.fulfill({ json: settings() });
  });
  await page.setViewportSize({ width: 1440, height: 1100 });
  await page.addInitScript(projectName => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName, section: 'settings' }],
      activeKey: `hub:${projectName}`,
    }));
  }, project);
  await page.goto('/');
  const control = page.getByTestId('project-detail-integration-gate-reuse');
  await expect(control).toBeVisible();
  await expect(control).toHaveValue('inherit');
  for (const [choice, value] of [['enabled', true], ['disabled', false], ['inherit', null]] as const) {
    await control.selectOption(choice);
    await expect.poll(() => writes.at(-1)).toBe(value);
    await expect(control).toHaveValue(choice);
  }
  expect(writes).toEqual([true, false, null]);
  await expect(page.getByText('Unexpected application error', { exact: true })).toHaveCount(0);
  await page.reload();
  await expect(control).toHaveValue('inherit');
  const results = process.env['JOB_RESULTS_DIR'];
  expect(results, 'durable evidence directory').toBeTruthy();
  mkdirSync(results!, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await control.scrollIntoViewIfNeeded();
    await expect(page.getByText('Unexpected application error', { exact: true })).toHaveCount(0);
    await page.screenshot({ path: join(results!, `integration-gate-reuse-${theme}--mocked.png`) });
  }
});
