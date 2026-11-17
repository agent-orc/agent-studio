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
  deletionRequestedBy: null, deletionRequestedAt: null, deletedAt: null,
  ready: true, connections: 3, maxConnections: 100, backupPolicy: { intervalHours: 24, retentionDays: 30, minimumKept: 7 },
  volumeAlertBytes: 5 * 1024 * 1024 * 1024, lastBackupAt: '2026-11-16T02:00:00Z', backupStatus: 'ok', lastBackupError: null,
  lastBackupAttemptAt: '2026-11-16T02:00:00Z', backups: [], operations: [], environmentKind: 'stage', ...overrides,
});

const backup = (id: string, takenAt: string, overrides: Record<string, unknown> = {}) => ({
  id, takenAt, bytes: 46 * 1024 * 1024, kind: 'scheduled', verifiedAt: null, verifiedTables: null, ...overrides,
});

async function openOperations(page: Page): Promise<void> {
  await page.addInitScript(name => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName: name, section: 'deploy' }],
    activeKey: `hub:${name}`,
  })), projectName);
  await page.goto('/');
  await expect(page.getByTestId('project-deploy-area')).toBeVisible({ timeout: 60_000 });
  await page.getByTestId('deploy-tab-operations').click();
}

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
  await openOperations(page);

  const panel = page.getByTestId('managed-databases-panel');
  // The panel is deferred until idle, which can take longer on a cold stack.
  await expect(panel).toContainText('secret://demo/stage/MANAGED_DB_ORDERS_PASSWORD', { timeout: 30_000 });
  const orders = panel.getByTestId('managed-database-stage-orders');
  await expect(orders).toContainText('PostgreSQL 16.4');
  await expect(orders).toContainText('(declared 16.6)');
  await expect(orders).toContainText('48.0 MB');
  await expect(orders).toContainText('DATABASE_URL_FILE');
  await expect(orders.getByRole('button')).toHaveText(['Back up now']);
  await expect(panel.getByTestId('managed-database-stage-reports')).toContainText('major version change is refused');

  const retained = panel.getByTestId('managed-database-stage-audit_log');
  await expect(retained).toContainText('Retained, not declared');
  await retained.getByRole('button', { name: 'Delete data', exact: true }).click();
  await expect(retained.getByTestId('managed-database-confirm')).toContainText('removes the volume and every backup of audit_log');
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

test('Operations shows backups, an awaiting production restore and the restore form', async ({ page }) => {
  const posts: { url: string; body: unknown }[] = [];
  await page.setViewportSize({ width: 1440, height: 2400 });
  await stubStudioHub(page);
  await page.route('**/api/projects/*/environments', route => route.fulfill({ json: {
    project: projectName, environments: [{
      declaration: { id: 'production', name: 'Production', kind: 'production', address: 'https://example.test', healthUrl: null },
      deployedArtifact: null, health: 'unknown', pendingArtifact: null, history: [],
    }], promotions: [], jobs: [], failureCards: [],
  } }));
  await page.route('**/api/projects/*/environments/production/volumes', route => route.fulfill({ json: {
    database: null, volumes: [], schedules: [], audit: [], adapterReady: false,
  } }));
  const production = { environmentId: 'production', environmentKind: 'production' };
  const backups = [
    backup('b6', '2026-11-16T02:00:00Z', { verifiedAt: '2026-11-16T03:10:00Z', verifiedTables: 12 }),
    backup('b5', '2026-11-15T14:20:00Z', { kind: 'manual', bytes: 45 * 1024 * 1024 }),
    backup('b4', '2026-11-15T02:00:00Z', { bytes: 44 * 1024 * 1024 }),
    backup('b3', '2026-11-14T09:45:00Z', { kind: 'pre-restore', bytes: 43 * 1024 * 1024 }),
    backup('b2', '2026-11-14T02:00:00Z', { bytes: 43 * 1024 * 1024, verifiedAt: '2026-11-14T03:00:00Z', verifiedTables: 12 }),
    backup('b1', '2026-11-13T02:00:00Z', { bytes: 42 * 1024 * 1024 }),
  ];
  await page.route('**/api/projects/*/environments/production/databases**', route => {
    if (route.request().method() === 'POST') {
      posts.push({ url: new URL(route.request().url()).pathname, body: route.request().postDataJSON() });
      return route.fulfill({ status: 202, json: { operation: { id: 'op-new', state: 'awaiting-approval' } } });
    }
    return route.fulfill({ json: { databases: [
      database({ ...production, credentialReference: 'secret://demo/production/MANAGED_DB_ORDERS_PASSWORD', connections: 14,
        volumeBytes: 52 * 1024 * 1024, backups, operations: [
          { id: 'op-3', kind: 'restore', backupId: 'b4', sourceDatabase: 'orders', targetDatabase: 'orders', state: 'awaiting-approval',
            requestedBy: 'maria', requestedAt: '2026-11-16T09:12:00Z', approvedBy: null, approvedAt: null, finishedAt: null, message: null },
          { id: 'op-2', kind: 'verify', backupId: 'b6', sourceDatabase: 'orders', targetDatabase: 'orders', state: 'succeeded',
            requestedBy: 'olga', requestedAt: '2026-11-16T03:05:00Z', approvedBy: null, approvedAt: null,
            finishedAt: '2026-11-16T03:10:00Z', message: 'Restored 12 tables into a scratch database.' },
        ] }),
      database({ ...production, name: 'orders_restored', variable: 'ORDERS_RESTORED_URL', volumeBytes: 1024 * 1024,
        credentialReference: 'secret://demo/production/MANAGED_DB_ORDERS_RESTORED_PASSWORD', lastBackupAt: '2026-11-15T02:00:00Z',
        backupStatus: 'overdue', lastBackupError: 'pg_dump: connection to the database timed out.', ready: false, connections: 0,
        volumeAlertBytes: 1024 * 1024, backups: [backup('r1', '2026-11-15T02:00:00Z', { bytes: 1024 * 1024 })] }),
    ] } });
  });
  await openOperations(page);

  const panel = page.getByTestId('managed-databases-panel');
  await expect(panel).toContainText('secret://demo/production/MANAGED_DB_ORDERS_PASSWORD', { timeout: 30_000 });
  const orders = panel.getByTestId('managed-database-production-orders');
  await expect(orders.getByTestId('managed-database-ready')).toContainText('Ready');
  await expect(orders.getByTestId('managed-database-connections')).toHaveText('14 of 100');
  await expect(orders.getByTestId('managed-database-volume')).toContainText('52.0 MB of 5.0 GB alert threshold');
  await expect(orders.getByTestId('managed-database-last-backup')).toHaveText('Last backup 16 Nov 2026, 02:00 UTC');
  await expect(orders.getByTestId('managed-database-backup-status')).toContainText('Backups current');
  await expect(orders.getByTestId('managed-database-backup-policy')).toHaveText('Every 24 h, kept 30 days, at least 7 kept');
  await expect(orders.getByTestId('managed-database-backup-b6')).toContainText('Verified 16 Nov, 12 tables');
  await expect(orders.getByTestId('managed-database-backup-b3')).toContainText('Before restore');
  await expect(orders.getByTestId('managed-database-backup-b1')).toHaveCount(0);
  await expect(orders.getByTestId('managed-database-backups-toggle')).toHaveText('Show all 6 backups');

  const restored = panel.getByTestId('managed-database-production-orders_restored');
  await expect(restored.getByTestId('managed-database-backup-status')).toContainText('Backup overdue');
  await expect(restored.getByTestId('managed-database-backup-error')).toContainText('timed out');
  await expect(restored.getByTestId('managed-database-volume-alert')).toBeVisible();
  await expect(restored.getByTestId('managed-database-ready')).toContainText('Not ready');

  const awaiting = orders.getByTestId('managed-database-operation-op-3');
  await expect(awaiting).toContainText('Awaiting approval');
  await expect(awaiting).toContainText('of the backup from 15 Nov 2026, 02:00 UTC into orders');
  await expect(awaiting.getByRole('button', { name: 'Approve restore' })).toBeVisible();
  await expect(orders.getByTestId('managed-database-operation-op-2')).toContainText('Restored 12 tables');

  await orders.getByTestId('managed-database-backup-b6').getByRole('button', { name: 'Restore…' }).click();
  const form = orders.getByTestId('managed-database-restore-form');
  await expect(form).toContainText('This is production, so the restore waits for an Owner approval.');
  await form.getByTestId('managed-database-restore-target-orders_restored').check();
  await expect(form).toContainText('Type orders_restored to replace its data');
  const submit = form.getByRole('button', { name: 'Restore backup' });
  await form.getByTestId('managed-database-restore-confirm').fill('orders_rest');
  await expect(submit).toBeDisabled();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await panel.screenshot({ path: path.join(results, `managed-databases-backups-${theme}.png`) });
  }

  await form.getByTestId('managed-database-restore-confirm').fill('orders_restored');
  await submit.click();
  await expect.poll(() => posts.length).toBe(1);
  expect(posts[0]).toEqual({ url: expect.stringMatching(/\/environments\/production\/databases\/orders\/operations$/),
    body: { kind: 'restore', backupId: 'b6', targetDatabase: 'orders_restored', confirm: 'orders_restored' } });
  await expect(panel.getByRole('status').filter({ hasText: 'Restore into' }))
    .toHaveText('Restore into orders_restored waits for an Owner approval.');

  await awaiting.getByRole('button', { name: 'Approve restore' }).click();
  await expect.poll(() => posts.length).toBe(2);
  expect(posts[1].url).toMatch(/\/databases\/orders\/operations\/op-3\/approve$/);
  expect(posts[1].body).toEqual({});
});
