import { expect, test, type Page } from '@playwright/test';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { setTheme } from '../helpers/theme';

const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results');

interface MockRun { id: string; name: string; environmentId: string; state: string; requestedBy: string;
  requestedByName: string; approvedBy: string | null; approvedByName: string | null; verifies: string | null;
  verification: { target: string; passed: boolean; detail: string } | null; steps: { name: string; state: string; logRef: string }[] }

const verified = 'Health check http (https://example.com/health) passed: answered 200 after 41 ms.';

/** Serves the environments and runbook endpoints; the run settles after `settleAfter` history reads. */
async function mockRunbooks(page: Page, project: string, settleAfter: number): Promise<void> {
  let reads = 0;
  const queued: MockRun = { id: 'run-1', name: 'restart', environmentId: 'production', state: 'queued',
    requestedBy: 'usr_ecaff70110a1', requestedByName: 'Olivia Operator', approvedBy: 'usr_9b1c2d3e4f5a',
    approvedByName: 'Oscar Owner', verifies: 'health check http', verification: null,
    steps: [{ name: 'approval', state: 'succeeded', logRef: '' }, { name: 'restart', state: 'pending', logRef: '' },
      { name: 'verification', state: 'pending', logRef: '' }] };
  const settled: MockRun = { ...queued, state: 'succeeded',
    verification: { target: 'health check http', passed: true, detail: verified },
    steps: queued.steps.map(step => ({ ...step, state: 'succeeded' })) };
  await page.route(/\/api\/projects\/[^/]+\/environments$/, async route => {
    const declaration = (id: string, kind: string) => ({ id, name: id, kind });
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      project, environments: [
        { declaration: declaration('stage', 'stage'), deployedArtifact: null, health: 'unknown', history: [] },
        { declaration: declaration('production', 'production'), deployedArtifact: null, health: 'healthy', history: [] },
      ], promotions: [], failureCards: [],
    }) });
  });
  await page.route(/\/api\/projects\/[^/]+\/deploy\/(?:runbooks|environments\/[^/]+\/runbooks)/, async route => {
    const url = route.request().url();
    const method = route.request().method();
    let body: unknown = [];
    if (method === 'GET' && url.endsWith('/runbooks')) body = [
      { name: 'restart', steps: ['restart'], environments: [], verify: 'health:http' },
      { name: 'disk-cleanup', steps: ['disk-cleanup'], environments: [], verify: 'health:http' },
    ];
    else if (method === 'GET' && url.includes('/runs/') && url.endsWith('/log')) body = { lines: [
      { step: 'approval', message: 'Requested by Olivia Operator. Waiting for approval by Owner or Maintainer.' },
      { step: 'approval', message: 'Approved by Oscar Owner.' },
      { step: 'restart', message: 'Restarting container-service from /srv/apps/container-service/production/compose.yml' },
      { step: 'restart', message: 'NAME                IMAGE     STATUS' },
      { step: 'restart', message: 'container-service   app       Up 2 seconds (healthy)' },
      { step: 'restart', message: 'Operation succeeded.' },
      { step: 'verification', message: 'The host steps succeeded. Checking health check http.' },
      { step: 'verification', message: verified },
    ] };
    else if (method === 'GET') {
      const environment = new URL(url).searchParams.get('environmentId');
      if (environment === 'production') reads++;
      body = environment === 'production' ? [reads > settleAfter ? settled : queued] : [];
    }
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
}

async function openOperations(page: Page): Promise<string> {
  const projects = await api<{ name: string }[]>('/api/watch-paths');
  const project = projects.find(item => /agent.?task/i.test(item.name))?.name ?? projects[0].name;
  const slug = project.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  await mockRunbooks(page, project, 1);
  await page.goto(`/#/projects/${slug}/operations`);
  return project;
}

test('runbook history refreshes on its own and shows what the run verified', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await openOperations(page);
  const environment = page.getByTestId('project-environment-production');
  const runbooks = environment.getByTestId('environment-runbooks');
  await expect(runbooks).toContainText('Verifies health check http after the steps');
  await expect(runbooks.getByTestId('runbook-run-state')).toHaveText('queued');
  await expect(runbooks).toContainText('requested by Olivia Operator, approved by Oscar Owner');
  await expect(runbooks).not.toContainText('usr_');

  // M4: the run used to stay on queued until a manual Refresh.
  await expect(runbooks.getByTestId('runbook-run-state')).toHaveText('succeeded', { timeout: 15_000 });
  await expect(runbooks.getByTestId('runbook-verification')).toContainText(verified);
  await expect(runbooks.getByTestId('runbook-verification-state')).toHaveText('Verified');

  await runbooks.getByTestId('runbook-log-toggle-run-1').click();
  const restart = runbooks.getByTestId('runbook-log-step-restart');
  await expect(restart).toContainText('container-service   app       Up 2 seconds (healthy)');
  await expect(runbooks.getByTestId('runbook-log-step-verification')).toContainText('answered 200');
  await expect(page.getByTestId('project-environment-stage')).not.toContainText('Oscar Owner');

  await runbooks.scrollIntoViewIfNeeded();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await runbooks.screenshot({ path: path.join(results, `runbook-verified-log-1280-${theme}.png`) });
  }
});

test('runbook history fits a phone viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openOperations(page);
  const runbooks = page.getByTestId('project-environment-production').getByTestId('environment-runbooks');
  await expect(runbooks.getByTestId('runbook-run-state')).toHaveText('succeeded', { timeout: 15_000 });
  await runbooks.getByTestId('runbook-log-toggle-run-1').click();
  await expect(runbooks.getByTestId('runbook-log-step-restart')).toBeVisible();
  const overflow = await runbooks.evaluate(element => element.scrollWidth - element.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
  await runbooks.scrollIntoViewIfNeeded();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await runbooks.screenshot({ path: path.join(results, `runbook-verified-log-390-${theme}.png`) });
  }
});
