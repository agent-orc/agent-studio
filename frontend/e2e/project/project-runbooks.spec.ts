import { expect, test } from '@playwright/test';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { setTheme } from '../helpers/theme';

const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results');

test('environment page shows production runbook approval and log', async ({ page }) => {
  const projects = await api<{ name: string }[]>('/api/watch-paths');
  const project = projects.find(item => /agent.?task/i.test(item.name))?.name ?? projects[0].name;
  const slug = project.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  const runs = [{ id: 'run-1', name: 'restart', environmentId: 'production', state: 'queued',
    requestedBy: 'operator', approvedBy: 'owner', steps: [] }];
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
    if (method === 'GET' && url.endsWith('/runbooks')) body = [{ name: 'restart', steps: ['restart-container'], environments: [] }];
    else if (method === 'GET' && url.includes('/runs/') && url.endsWith('/log')) body = { lines: [
      { step: 'approval', message: 'Approved by owner.' }, { step: 'restart-container', message: 'Operation succeeded.' },
    ] };
    else if (method === 'GET') body = runs.filter(run => run.environmentId === new URL(url).searchParams.get('environmentId'));
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.goto(`/#/projects/${slug}/operations`);
  const environment = page.getByTestId('project-environment-production');
  await expect(environment.getByTestId('environment-runbooks')).toContainText('restart');
  await expect(environment.getByTestId('runbook-start-restart')).toBeEnabled();
  await expect(environment).toContainText('queued');
  await environment.getByRole('button', { name: 'Log', exact: true }).click();
  await expect(environment).toContainText('Operation succeeded.');
  await expect(environment).toContainText('approved by owner');
  await expect(page.getByTestId('project-environment-stage')).not.toContainText('approved by owner');
  await environment.getByText('approved by owner').first().scrollIntoViewIfNeeded();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: path.join(results, `runbook-environment-${theme}.png`) });
  }
});
