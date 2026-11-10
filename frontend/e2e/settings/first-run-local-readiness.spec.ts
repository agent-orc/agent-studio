import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';
import { setTheme } from '../helpers/theme';

test('fresh local checklist reaches Create sample card with a degraded runner and ok provider', async ({ page }) => {
  const now = new Date().toISOString();
  const json = (body: unknown) => (route: import('@playwright/test').Route) => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify(body),
  });
  const local = { profile: 'local', bootstrapRequired: true, authenticated: false, user: null };
  await page.route('**/api/auth/status', json(local));
  await page.route('**/api/v1/studio/auth/status', json(local));
  await page.route('**/api/projects', json([{
    id: 'PROJ-1', displayName: 'First Run Demo', shortCode: 'FR', workspaceId: 'ws-default',
    archived: false, storageLocation: '/tmp/first-run-demo', repositoryPath: '/tmp/first-run-demo',
    repositoryUrl: 'https://github.com/example/first-run-demo.git',
  }]));
  await page.route('**/api/first-run/projects/PROJ-1/repository', json({
    registration: { projectId: 'PROJ-1', repositoryUrl: 'https://github.com/example/first-run-demo.git',
      integrationRef: 'main', releaseRef: null, deliveryPolicy: 'reviewed-publication' },
    probes: [{ runnerId: 'first-run-runner', admitted: true, verdict: 'admitted', detail: null, observedAt: now }],
  }));
  await page.route('**/api/clients', json([{
    id: 'first-run-runner', displayName: 'First Run Runner', kind: 'service',
    registeredAt: now, lastSeenAt: now, runnerGitStatus: 'ready', runnerDaemonState: 'running',
    runnerActiveSlots: 0, runnerAvailableSlots: 1,
  }]));
  await page.route('**/api/v1/management/remote-hosts', json([{
    runnerId: 'first-run-runner', hostId: 'first-run-runner', name: 'First Run Runner',
    status: 'active', registeredAt: now, lastSeenAt: now,
    hostAdmission: { admissionState: 'open' }, capabilities: [{
      key: 'provider-auth:codex', category: 'provider-auth', advertisedStatus: 'ready',
      healthState: 'healthy', advertisedAt: now, freshUntil: new Date(Date.now() + 60_000).toISOString(),
      isFresh: true, consecutiveFailures: 0, signal: 'healthy', detail: 'Authenticated.',
    }],
  }]));
  await page.route('**/api/v1/management/links', json([{
    runnerId: 'first-run-runner', kind: 'ssh-reverse', state: 'degraded', since: now,
    lastHeartbeatAt: now, lastProbe: null, lastError: 'CPU above target', attempt: 1,
    nextRetryAt: null, childPid: null, notificationRaisedAt: null,
  }]));
  await page.route('**/api/tasks', route => route.request().method() === 'POST'
    ? json({ id: 'sample-card' })(route) : route.continue());
  await page.route('**/api/runner/First%20Run%20Demo/start', json({}));
  await page.route('**/api/tasks/sample-card**', json({ info: { state: '2-ready' } }));

  await page.goto('/#/workspace/settings/first-run');
  await expect(page.getByTestId('first-run-owner')).toHaveAttribute('data-state', 'ready');
  await expect(page.getByTestId('first-run-owner')).toContainText('No owner sign-in is needed');
  await expect(page.getByTestId('first-run-providers')).toHaveAttribute('data-state', 'ready');
  await expect(page.getByTestId('first-run-host')).toHaveAttribute('data-state', 'ready');
  await expect(page.getByTestId('first-run-git')).toHaveAttribute('data-state', 'ready');
  await page.getByTestId('first-run-demo-confirm').check();
  await expect(page.getByTestId('first-run-create-proof')).toBeEnabled();

  const results = process.env.JOB_RESULTS_DIR;
  if (results) {
    mkdirSync(results, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('first-run-owner').scrollIntoViewIfNeeded();
      await page.screenshot({ path: join(results, `first-run-local-${theme}-top.png`), fullPage: true });
      await page.getByTestId('first-run-create-proof').scrollIntoViewIfNeeded();
      await page.screenshot({ path: join(results, `first-run-local-${theme}.png`), fullPage: true });
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByTestId('first-run-owner').scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(results, 'first-run-local-390px.png'), fullPage: true });
    await page.getByTestId('first-run-create-proof').scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(results, 'first-run-local-390px-proof.png'), fullPage: true });
  }
  await page.getByTestId('first-run-create-proof').click();
  await expect(page.getByTestId('first-run-proof-id')).toContainText('sample-card');
});
