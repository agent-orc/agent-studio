import { expect, test, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { setTheme } from '../helpers/theme';

const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results');
let projectName = '';

const projectSlug = (name: string) => name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

async function stubStudioHub(page: Page): Promise<void> {
  await page.route('**/hubs/v1/studio/negotiate?*', route => route.fulfill({ json: {
    negotiateVersion: 1, connectionId: 'hosted-data-e2e', connectionToken: 'hosted-data-e2e',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  } }));
  await page.routeWebSocket('**/hubs/v1/studio*', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send(`{}\u001e`);
    });
  });
}

test.beforeAll(async () => {
  fs.mkdirSync(results, { recursive: true });
  const paths = await api<{ name: string; path: string }[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  projectName = paths.find(p => /agent.?task/i.test(p.name))?.name ?? paths[0].name;
});

test('Operations shows database binding, volume schedule, restore confirmation and audit in both themes', async ({ page }) => {
  let hour: number | null = 4;
  const actions: string[] = [];
  await stubStudioHub(page);
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
  await page.goto(`/#/projects/${projectSlug(projectName)}/deploy`);
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
});

test('a delayed hosted-data response cannot replace the next project panel', async ({ page }) => {
  const projects = await api<{ workspaceId: string }[]>('/api/projects');
  const secondProjectName = 'Hosted Data Switch';
  const createdResponse = await page.request.post('/api/projects', {
    headers: { 'X-Client-Id': 'local-default' },
    data: { displayName: secondProjectName, shortCode: 'HDSW', workspaceId: projects[0]?.workspaceId ?? 'ws-default' },
  });
  expect(createdResponse.ok(), `${createdResponse.status()}: ${await createdResponse.text()}`).toBeTruthy();
  const created = await createdResponse.json() as { id: string };
  try {
    let releaseFirst!: () => void;
    let firstVolumeStarted!: () => void;
    const firstVolume = new Promise<void>(resolve => { firstVolumeStarted = resolve; });
    const delayed = new Promise<void>(resolve => { releaseFirst = resolve; });
    let firstResponseFinished = false;
    await stubStudioHub(page);
    await page.route('**/api/projects/*/environments', route => route.fulfill({ json: {
      environments: [{ declaration: { id: 'stage', name: 'Stage', kind: 'stage', address: 'https://stage.example', healthUrl: null },
        deployedArtifact: null, health: 'unknown', pendingArtifact: null, history: [] }],
      promotions: [], jobs: [], failureCards: [],
    } }));
    await page.route('**/api/projects/*/environments/stage/volumes', async route => {
      const requestedProject = decodeURIComponent(new URL(route.request().url()).pathname.split('/')[3]);
      const isSecondProject = requestedProject === secondProjectName || requestedProject === created.id;
      if (!isSecondProject) {
        firstVolumeStarted();
        await delayed;
      }
      await route.fulfill({ json: {
        database: { secretRef: `secret://${isSecondProject ? secondProjectName : projectName}/stage/DATABASE_URL`,
          environmentVariable: 'DATABASE_URL' },
        volumes: [], schedules: [], audit: [], adapterReady: false,
      } }).catch(() => {});
      if (!isSecondProject) firstResponseFinished = true;
    });

    await page.goto(`/#/projects/${projectSlug(projectName)}/deploy`);
    await page.getByTestId('deploy-tab-operations').click();
    await firstVolume;
    await page.evaluate(id => { window.location.hash = `#/projects/${id}/deploy`; }, created.id);
    await expect(page.getByTestId('project-deploy-area')).toContainText(`${secondProjectName} / Project delivery`);
    await page.getByTestId('deploy-tab-operations').click();
    const panel = page.getByTestId('hosted-data-panel');
    await expect(panel).toContainText(`secret://${secondProjectName}/stage/DATABASE_URL`);
    releaseFirst();
    await expect.poll(() => firstResponseFinished).toBe(true);
    await expect(panel).toContainText(`secret://${secondProjectName}/stage/DATABASE_URL`);
    await expect(panel).not.toContainText(`secret://${projectName}/stage/DATABASE_URL`);
    await panel.screenshot({ path: path.join(results, 'hosted-data-project-switch.png') });
  } finally {
    const deleted = await page.request.delete(`/api/projects/${encodeURIComponent(created.id)}`, {
      headers: { 'X-Client-Id': 'local-default' },
    });
    expect(deleted.ok(), `${deleted.status()}: ${await deleted.text()}`).toBeTruthy();
  }
});
