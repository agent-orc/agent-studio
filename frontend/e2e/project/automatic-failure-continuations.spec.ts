import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test } from '@playwright/test';
import { setTheme } from '../helpers/theme';

test('project failure continuation setting persists in both themes', async ({ page }) => {
  const project = 'continuation-demo';
  let enabled = true;
  let maxDeliveredReviewRounds = 4;
  let maxAutoReissueAttempts = 2;
  const writes: boolean[] = [];
  const settings = () => ({
    autoCommit: true, crashRecoveryEnabled: true, autoPushStrategy: 'never',
    automaticFailureContinuationsEnabled: enabled,
    maxDeliveredReviewRounds, maxAutoReissueAttempts,
    pickupMode: 'manual', executionLocation: 'local', integrationBranch: 'develop',
  });
  await page.route('**/api/**', route => route.fulfill({ json: [] }));
  await page.route('**/hubs/jobs/negotiate**', route => route.fulfill({ json: {
    connectionId: 'mock-jobs-connection', connectionToken: 'mock-jobs-connection',
    negotiateVersion: 1,
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
  } }));
  await page.routeWebSocket('**/hubs/jobs**', socket => {
    socket.onMessage(message => {
      if (String(message).includes('"protocol"')) socket.send('{}\x1e');
    });
  });
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
  await page.route('**/api/projects/continuation-demo/workbenches**', route => route.fulfill({ json: { items: [] } }));
  await page.route('**/api/projects/continuation-demo/execution', route => route.fulfill({ json: { valid: true, issues: [], source: 'subject-commit' } }));
  await page.route('**/api/projects/settings', route => route.fulfill({ json: { [project]: settings() } }));
  await page.route('**/api/projects/continuation-demo/snapshot', route => route.fulfill({ json: { settings: settings() } }));
  await page.route('**/api/runner/status**', route => route.fulfill({ json: { projects: {} } }));
  await page.route('**/api/cli/usage**', route => route.fulfill({ json: { items: [] } }));
  await page.route('**/api/cli/quota**', route => route.fulfill({ json: { snapshots: [] } }));
  await page.route('**/api/projects/continuation-demo/automatic-failure-continuations', async route => {
    expect(route.request().method()).toBe('PUT');
    enabled = route.request().postDataJSON().enabled;
    writes.push(enabled);
    await route.fulfill({ json: settings() });
  });
  await page.route('**/api/projects/continuation-demo/review-round-budgets', async route => {
    expect(route.request().method()).toBe('PUT');
    ({ maxDeliveredReviewRounds, maxAutoReissueAttempts } = route.request().postDataJSON());
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
  const control = page.getByTestId('project-detail-automatic-failure-continuations');
  await expect(control).toBeChecked();
  await control.uncheck();
  await expect.poll(() => writes.at(-1)).toBe(false);
  await page.reload();
  await expect(control).not.toBeChecked();
  await control.check();
  await expect.poll(() => writes.at(-1)).toBe(true);

  const rounds = page.getByTestId('project-detail-review-round-limit');
  const reissues = page.getByTestId('project-detail-review-reissue-limit');
  await expect(rounds).toHaveValue('4');
  await expect(reissues).toHaveValue('2');
  await rounds.fill('5');
  await rounds.blur();
  await expect.poll(() => maxDeliveredReviewRounds).toBe(5);
  await expect(rounds).toHaveValue('5');
  await reissues.fill('3');
  await reissues.blur();
  await expect.poll(() => maxAutoReissueAttempts).toBe(3);
  await page.reload();
  await expect(rounds).toHaveValue('5');
  await expect(reissues).toHaveValue('3');

  const results = process.env['JOB_RESULTS_DIR'];
  expect(results, 'durable evidence directory').toBeTruthy();
  mkdirSync(results!, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await control.scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(results!, `automatic-failure-continuations-${theme}.png`) });
  }
});
