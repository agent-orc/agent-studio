import { expect, test, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { setTheme } from '../helpers/theme';

const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results');
let projectName = '';

async function stubStudioHub(page: Page): Promise<void> {
  await page.route('**/hubs/v1/studio/negotiate?*', route => route.fulfill({ json: {
    negotiateVersion: 1, connectionId: 'managed-databases-e2e', connectionToken: 'managed-databases-e2e',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  } }));
  await page.routeWebSocket('**/hubs/v1/studio*', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send(`{}\u001e`);
    });
  });
}

const database = (overrides: Record<string, unknown>) => ({
  environmentId: 'stage', name: 'orders', engine: 'postgres', version: '16.6', runningVersion: '16.4', size: 'small',
  resources: { cpus: 0.5, memoryMb: 512, pidsLimit: 256 }, variable: 'DATABASE_URL',
  credentialReference: 'secret://demo/stage/MANAGED_DB_ORDERS_PASSWORD', state: 'running', declared: true,
  volumeBytes: 48 * 1024 * 1024, observedAt: '2026-11-16T09:00:00Z', message: null,
  deletionRequestedBy: null, deletionRequestedAt: null, deletedAt: null, ...overrides,
});

test.beforeAll(async () => {
  fs.mkdirSync(results, { recursive: true });
  const paths = await api<{ name: string; path: string }[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  projectName = paths.find(p => /agent.?task/i.test(p.name))?.name ?? paths[0].name;
});

test('Operations shows managed databases per environment and deletes retained data only after typed confirmation', async ({ page }) => {
  const deletions: unknown[] = [];
  await stubStudioHub(page);
  await page.route('**/api/projects/*/environments', route => route.fulfill({ json: {
    project: projectName, environments: [{
      declaration: { id: 'stage', name: 'Stage', kind: 'stage', address: 'https://stage.example', healthUrl: null },
      deployedArtifact: null, health: 'unknown', pendingArtifact: null, history: [],
    }], promotions: [], jobs: [], failureCards: [],
  } }));
  await page.route('**/api/projects/*/environments/stage/volumes', route => route.fulfill({ json: {
    database: null, volumes: [], schedules: [], audit: [], adapterReady: false,
  } }));
  await page.route('**/api/projects/*/environments/stage/databases**', route => {
    if (route.request().method() === 'POST') {
      deletions.push(route.request().postDataJSON());
      return route.fulfill({ status: 202, body: '' });
    }
    return route.fulfill({ json: { databases: [
      database({}),
      database({ name: 'audit_log', version: '16.4', runningVersion: '16.4', state: 'retained', declared: false,
        variable: 'AUDIT_DATABASE_URL', credentialReference: 'secret://demo/stage/MANAGED_DB_AUDIT_LOG_PASSWORD' }),
      database({ name: 'reports', version: '17', runningVersion: '16.2', state: 'upgrade-refused', variable: 'REPORTS_URL',
        message: 'Database reports holds PostgreSQL 16 data; the deploy asks for 17. A major version change is refused.' }),
    ] } });
  });
  await page.addInitScript(name => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName: name, section: 'deploy' }],
    activeKey: `hub:${name}`,
  })), projectName);
  await page.goto('/');
  await expect(page.getByTestId('project-deploy-area')).toBeVisible({ timeout: 60_000 });
  await page.getByTestId('deploy-tab-operations').click();

  const panel = page.getByTestId('managed-databases-panel');
  // The panel is deferred until idle, which can take longer on a cold stack.
  await expect(panel).toContainText('secret://demo/stage/MANAGED_DB_ORDERS_PASSWORD', { timeout: 30_000 });
  const orders = panel.getByTestId('managed-database-stage-orders');
  await expect(orders).toContainText('PostgreSQL 16.4');
  await expect(orders).toContainText('(declared 16.6)');
  await expect(orders).toContainText('48.0 MB');
  await expect(orders).toContainText('DATABASE_URL_FILE');
  await expect(orders.getByRole('button')).toHaveCount(0);
  await expect(panel.getByTestId('managed-database-stage-reports')).toContainText('major version change is refused');

  const retained = panel.getByTestId('managed-database-stage-audit_log');
  await expect(retained).toContainText('Retained, not declared');
  await retained.getByRole('button', { name: 'Delete data', exact: true }).click();
  const confirm = retained.getByRole('button', { name: 'Delete data permanently' });
  await expect(confirm).toBeDisabled();
  await retained.getByTestId('managed-database-confirm-name').fill('audit');
  await expect(confirm).toBeDisabled();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await panel.screenshot({ path: path.join(results, `managed-databases-operations-${theme}.png`) });
  }
  await retained.getByTestId('managed-database-confirm-name').fill('audit_log');
  await confirm.click();
  await expect.poll(() => deletions.length).toBe(1);
  expect(deletions[0]).toEqual({ confirm: 'audit_log' });
  await expect(panel).toContainText('Deletion of audit_log requested');
});
