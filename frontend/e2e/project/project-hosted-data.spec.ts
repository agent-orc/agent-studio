import { expect, test } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { setTheme } from '../helpers/theme';

const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results');
let projectName = '';

test.beforeAll(async () => {
  fs.mkdirSync(results, { recursive: true });
  const paths = await api<{ name: string; path: string }[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  projectName = paths.find(p => /agent.?task/i.test(p.name))?.name ?? paths[0].name;
});

test('Operations shows database binding, volume schedule, restore confirmation and audit in both themes', async ({ page }) => {
  let hour: number | null = 4;
  const actions: string[] = [];
  await page.route('**/hubs/v1/studio/negotiate?*', route => route.fulfill({ json: {
    negotiateVersion: 1, connectionId: 'hosted-data-e2e', connectionToken: 'hosted-data-e2e',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  } }));
  await page.routeWebSocket('**/hubs/v1/studio*', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send(`{}\u001e`);
    });
  });
  await page.route('**/api/projects/*/environments', route => route.fulfill({ json: {
    project: projectName, environments: [{
      declaration: { id: 'stage', name: 'Stage', kind: 'stage', address: 'https://stage.example', healthUrl: null },
      deployedArtifact: null, health: 'unknown', pendingArtifact: null, history: [],
    }], promotions: [], jobs: [], failureCards: [],
  } }));
  await page.route('**/api/projects/*/environments/stage/volumes**', route => {
    const url = route.request().url();
    const method = route.request().method();
    if (method !== 'GET') {
      actions.push(`${method} ${url.split('/volumes/')[1]}`);
      if (method === 'PUT') hour = (route.request().postDataJSON() as { hourUtc: number | null }).hourUtc;
      return route.fulfill({ json: {} });
    }
    return route.fulfill({ json: {
      database: { secretRef: 'secret://demo/stage/DATABASE_URL', environmentVariable: 'DATABASE_URL' },
      volumes: [{ name: 'uploads', mountPath: '/app/uploads' }], adapterReady: true,
      schedules: [{ volume: 'uploads', hourUtc: hour, lastAttemptAt: '2026-10-20T04:00:00Z', lastError: null,
        backups: [{ id: 'a'.repeat(32), createdAt: '2026-10-20T04:00:00Z', bytes: 2048, sha256: 'b'.repeat(64) }] }],
      audit: [{ id: 'audit-1', at: '2026-10-20T04:00:00Z', actor: 'owner', action: 'backup', volume: 'uploads',
        backupId: 'a'.repeat(32), outcome: 'succeeded' }],
    } });
  });
  const slug = projectName.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  await page.goto(`/#/projects/${slug}/deploy`);
  await page.getByTestId('deploy-tab-operations').click();
  const panel = page.getByTestId('hosted-data-panel');
  await expect(panel).toContainText('secret://demo/stage/DATABASE_URL');
  const volume = panel.getByTestId('hosted-volume-stage-uploads');
  await expect(volume).toContainText('/app/uploads');
  await volume.getByRole('combobox').selectOption('8');
  await expect.poll(() => actions.length).toBe(1);
  expect(actions[0]).toBe('PUT uploads/schedule');
  await expect(volume.getByRole('combobox')).toHaveValue('8');
  await volume.getByRole('button', { name: 'Restore', exact: true }).click();
  expect(actions.length).toBe(1);
  await volume.getByRole('button', { name: 'Confirm restore' }).click();
  await expect.poll(() => actions.length).toBe(2);
  expect(actions[1]).toContain('POST uploads/backups/');
  await expect(panel).toContainText('Data audit');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await panel.screenshot({ path: path.join(results, `hosted-data-operations-${theme}.png`) });
  }

  let releaseOlderRequest!: () => void;
  const olderRequest = new Promise<void>(resolve => { releaseOlderRequest = resolve; });
  let refreshCount = 0;
  await page.route('**/api/projects/*/environments', async route => {
    const requestNumber = ++refreshCount;
    if (requestNumber === 1) await olderRequest;
    await route.fulfill({ json: {
      project: projectName,
      environments: requestNumber === 1
        ? [{ declaration: { id: 'stage', name: 'Stale stage' } }]
        : [],
      promotions: [], jobs: [], failureCards: [],
    } });
  });
  await panel.getByRole('button', { name: 'Refresh data' }).click();
  await expect.poll(() => refreshCount).toBe(1);
  await panel.getByRole('button', { name: 'Refresh data' }).click();
  await expect(panel).toContainText('No environments are declared.');
  const staleResponse = page.waitForResponse(async response =>
    response.url().endsWith('/environments') &&
    (await response.json()).environments?.[0]?.declaration?.name === 'Stale stage');
  const staleVolume = page.waitForResponse(response =>
    response.url().includes('/environments/stage/volumes') && response.request().method() === 'GET');
  releaseOlderRequest();
  await staleResponse;
  await staleVolume;
  await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
  await expect(panel).not.toContainText('Stale stage');
});
