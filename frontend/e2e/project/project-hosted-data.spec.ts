import { expect, test, type Page, type Request } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { setTheme } from '../helpers/theme';

const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results');
let projectName = '';

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

async function openDeployHubTabs(page: Page, projects: string[]): Promise<void> {
  await page.addInitScript(names => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1,
    tabs: [{ kind: 'board', projectName: '__all__' }, ...names.map(name => ({ kind: 'hub', projectName: name, section: 'deploy' }))],
    activeKey: `hub:${names[0]}`,
  })), projects);
  await page.goto('/');
  await expect(page.getByTestId('project-deploy-area')).toBeVisible({ timeout: 60_000 });
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
  await openDeployHubTabs(page, [projectName]);
  await page.getByTestId('deploy-tab-operations').click();
  const panel = page.getByTestId('hosted-data-panel');
  // The panel is deferred until idle, which can take longer on a cold stack.
  await expect(panel).toContainText('secret://demo/stage/DATABASE_URL', { timeout: 30_000 });
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
  const secondProjectName = 'Hosted Data Switch';
  const secretFor = (project: string) => `secret://${project}/stage/DATABASE_URL`;
  let releaseFirst!: () => void;
  let firstVolumeStarted!: () => void;
  const firstVolume = new Promise<void>(resolve => { firstVolumeStarted = resolve; });
  const delayed = new Promise<void>(resolve => { releaseFirst = resolve; });
  const volumesProject = (request: Request) => decodeURIComponent(new URL(request.url()).pathname.split('/')[3]);
  const isFirstVolumes = (request: Request) => request.url().endsWith('/environments/stage/volumes') && volumesProject(request) === projectName;
  const firstOutcome = new Promise<'finished' | 'failed'>(resolve => {
    page.on('requestfinished', request => { if (isFirstVolumes(request)) resolve('finished'); });
    page.on('requestfailed', request => { if (isFirstVolumes(request)) resolve('failed'); });
  });
  await stubStudioHub(page);
  // The second hub tab needs a registered name, or the shell purges it on boot.
  await page.route('**/api/watch-paths', async route => {
    const paths = await (await route.fetch()).json() as { name: string; path: string }[];
    const first = paths.find(entry => entry.name === projectName) ?? paths[0];
    await route.fulfill({ json: [...paths, { ...first, name: secondProjectName, path: `${first.path}-hosted-data-switch` }] });
  });
  await page.route('**/api/projects/*/environments', route => route.fulfill({ json: {
    environments: [{ declaration: { id: 'stage', name: 'Stage', kind: 'stage', address: 'https://stage.example', healthUrl: null },
      deployedArtifact: null, health: 'unknown', pendingArtifact: null, history: [] }],
    promotions: [], jobs: [], failureCards: [],
  } }));
  await page.route('**/api/projects/*/environments/stage/volumes', async route => {
    const requestedProject = volumesProject(route.request());
    const body = { json: {
      database: { secretRef: secretFor(requestedProject), environmentVariable: 'DATABASE_URL' },
      volumes: [], schedules: [], audit: [], adapterReady: false,
    } };
    if (requestedProject === secondProjectName) return route.fulfill(body);
    firstVolumeStarted();
    await delayed;
    // The panel cancels this request on the project switch, so fulfilling it may report an aborted request.
    await route.fulfill(body).catch((error: Error) => console.log(`First project response not delivered: ${error.message}`));
  });

  await openDeployHubTabs(page, [projectName, secondProjectName]);
  await page.getByTestId('deploy-tab-operations').click();
  await expect(page.getByTestId('hosted-data-panel')).toBeVisible({ timeout: 30_000 });
  await firstVolume;
  await page.getByTestId(`studio-tab-hub:${secondProjectName}`).click();
  await expect(page.getByTestId('project-deploy-area')).toContainText(`${secondProjectName} / Project delivery`);
  const panel = page.getByTestId('hosted-data-panel');
  await expect(panel).toContainText(secretFor(secondProjectName));
  releaseFirst();
  expect(await firstOutcome, 'the switch must cancel the first project load').toBe('failed');
  await expect(panel).toContainText(secretFor(secondProjectName));
  await expect(panel).not.toContainText(secretFor(projectName));
  await panel.screenshot({ path: path.join(results, 'hosted-data-project-switch.png') });
});
