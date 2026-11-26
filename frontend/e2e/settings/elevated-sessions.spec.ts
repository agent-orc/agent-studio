import { test, expect } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { installOrchestratorChatBootstrap } from '../helpers/orchestrator-chat-bootstrap';
import { setTheme } from '../helpers/theme';
import { stubPilotOwner, json } from './access-pilot.fixture';

const results = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'elevated-sessions');

const round = 'round3';

test('round3: lifetime limits and elevated sessions at desktop and phone widths', async ({ page }) => {
  test.setTimeout(180_000);
  await installOrchestratorChatBootstrap(page);
  await stubPilotOwner(page);
  await page.clock.setFixedTime(new Date('2026-11-26T10:00:00Z'));
  await page.route('**/healthz', route => json(route, { status: 'ok' }));
  await page.route(url => ['/api/auth/iam/operator-approvals', '/api/auth/iam/standing-grants', '/api/auth/iam/break-glass/windows'].includes(url.pathname), route => json(route, []));
  await page.route('**/api/auth/iam/operator-catalogue', route => json(route, { operations: [], defaultGrantDays: 7, maxGrantDays: 30, defaultRatePerHour: 60, maxRatePerHour: 1000 }));
  await page.route('**/api/auth/audit/backups**', route => json(route, { entries: [], integrity: { state: 'verified', verifiedThrough: 0, alert: false } }));
  await page.route('**/api/usage/cockpit**', route => json(route, { workspaceId: 'ws_main', clis: [], cost: null }));
  await page.route('**/api/runner/orchestrator-feed**', route => json(route, { entries: [], cursor: null }));
  await page.route('**/api/workspaces/*/quota/admission', route => json(route, { workspaceId: 'ws_main', workspace: { codingAllowed: true, hostingAllowed: true, limited: false, status: 'ok', message: null }, projects: [] }));
  await page.route('**/api/watcher/stalls', route => json(route, []));
  await page.route('**/api/v1/studio/runner/status', route => json(route, { projects: {} }));
  await page.route('**/api/deploy/run-hosts', route => json(route, []));
  await page.route('**/api/watch-paths', route => json(route, []));
  await page.route('**/api/v1/workspaces', route => json(route, []));
  await page.route('**/api/v1/projects', route => json(route, [
    { id: 'PROJ-001', displayName: 'Website', shortCode: 'WEB', workspaceId: 'ws_main', storageLocation: '/w/website' },
    { id: 'PROJ-002', displayName: 'Mobile App', shortCode: 'APP', workspaceId: 'ws_main', storageLocation: '/w/app' },
  ]));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [], codeNotComplete: [],
    autoReview: [], humanReview: [], escalated: [], review: [], completed: [], archive: [], gitStateAt: null, stale: false,
  }));
  const pageErrors: string[] = [];
  const consoleErrors: { text: string; url: string }[] = [];
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push({ text: message.text(), url: message.location().url }); });
  page.on('pageerror', error => pageErrors.push(error.message));
  const calls: string[] = [];
  const session = (id: string) => ({ id, operatorAgentId: 'opa_console', name: 'Ops console', actor: `operator-agent:opa_console/session:${id}`,
    delegatedBy: 'human:usr_owner', createdAt: '2026-10-20T09:00:00Z', createdBy: 'usr_owner', expiresAt: '9999-12-31T23:59:59Z',
    chosenEnd: null, chosenLifetimeHours: 0, idleEnd: '2026-11-27T09:00:00Z', state: 'active', revokedAt: null,
    lastUsedAt: '2026-11-26T09:00:00Z', lastAddress: '198.51.100.24', renewedAt: '2026-11-26T09:45:00Z',
    newAddressAt: '2026-11-26T09:45:00Z', reviewDue: true, classes: ['project.testing'],
    device: { deviceId: 'dev_workstation', label: 'ops@workstation', bindingKind: 'tpm', keyThumbprint: 'public-thumbprint', requestedAddress: '198.51.100.7' } });
  let sessions = [session('ses_console'), { ...session('ses_memory'), chosenLifetimeHours: 24, chosenEnd: '2026-11-27T08:00:00Z',
    expiresAt: '2026-11-27T08:00:00Z', reviewDue: false, newAddressAt: null, device: { ...session('').device, bindingKind: 'memory', label: 'ops@server', deviceId: 'dev_server' } }];
  const agents = () => [{ id: 'opa_console', name: 'Ops console', description: 'Installation operator', createdAt: '2026-10-20T09:00:00Z', createdBy: 'usr_owner',
    disabledAt: null, sessions, devices: [] }];
  let limits = { defaultLifetimeHours: 168, ownerLifetimeHours: null, adminLifetimeHours: 168, idleHours: 24 };
  await page.route('**/api/auth/iam/operator-agents**', async route => {
    if (route.request().method() === 'GET') return json(route, agents());
    const action = route.request().url().split('/').at(-1)!;
    calls.push(action);
    if (action === 'end') sessions = sessions.filter(item => !route.request().url().includes(item.id));
    if (action === 'keep') sessions = sessions.map(item => ({ ...item, reviewDue: false }));
    if (action === 'end-elevation') sessions = sessions.map(item => ({ ...item, classes: [] }));
    if (route.request().method() === 'DELETE') sessions = [];
    return route.fulfill({ status: 204 });
  });
  await page.route('**/api/auth/iam/elevated-sessions/end-all', async route => { calls.push('end-all'); sessions = []; await route.fulfill({ status: 204 }); });
  await page.route('**/api/auth/iam/operator-kill-switch', route => json(route, {
    kills: [], drills: [], nextDrillAt: null, drillIntervalHours: 720, operationsServerConfigured: false, outOfBandRequestPending: false,
  }));
  await page.route('**/api/auth/audit/operator-activity**', route => json(route, {
    operatorAgents: agents(), entries: [],
    integrity: { state: 'verified', checkedAt: '2026-11-26T09:00:00Z', verifiedThrough: 0, message: null, alert: false },
    forwarding: { sinkKind: 'none', configured: false, headSequence: 0, forwardedSequence: 0, pending: 0,
      lastForwardedAt: null, oldestPendingAt: null, lastError: null, alert: false },
  }));
  await page.route('**/api/auth/iam/session-limits', async route => {
    if (route.request().method() === 'PUT') { calls.push('limits'); limits = route.request().postDataJSON(); }
    return json(route, limits);
  });
  await page.route('**/api/auth/step-up', async route => { calls.push('step-up'); await route.fulfill({ status: 204 }); });
  await page.setViewportSize({ width: 1280, height: 1000 });
  await page.goto('/#/workspace/settings/access', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  const elevated = page.getByTestId('access-elevated-sessions');
  await expect(elevated.getByText('ops@workstation', { exact: true })).toBeVisible({ timeout: 30_000 });
  await expect(elevated.getByText('New renewal address').first()).toBeVisible();
  await expect(elevated.getByText('Elevated session running 30 days')).toBeVisible();
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 1500 : 1000 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await elevated.scrollIntoViewIfNeeded();
      mkdirSync(results, { recursive: true });
      await expect(page.getByTestId('error-dialog')).toHaveCount(0);
      await elevated.screenshot({ path: join(results, `${round}-${width}-${theme}--mocked.png`), animations: 'disabled' });
      await page.getByTestId('access-session-limits').evaluate(element => element.scrollIntoView({ block: 'center' }));
      await page.getByTestId('access-session-limits').screenshot({ path: join(results, `${round}-limits-${width}-${theme}--mocked.png`), animations: 'disabled' });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
    }
  }
  await elevated.getByRole('button', { name: 'Keep', exact: true }).click();
  await expect(elevated.getByText('Elevated session running 30 days')).toHaveCount(0);
  await elevated.getByRole('button', { name: 'End elevation', exact: true }).first().click();
  await expect.poll(() => calls.includes('end-elevation')).toBeTruthy();
  await elevated.getByRole('button', { name: 'End session', exact: true }).first().click();
  await expect(elevated.getByText('ops@workstation', { exact: true })).toHaveCount(0);
  await elevated.getByRole('button', { name: 'Forget device', exact: true }).click();
  await expect(elevated.getByText('No live sessions with classes or a lifetime longer than 8 hours.')).toBeVisible();
  expect(calls).not.toContain('step-up');
  sessions = [session('ses_console')];
  await page.reload({ waitUntil: 'domcontentloaded', timeout: 30_000 });
  await elevated.getByRole('button', { name: 'End all elevated sessions', exact: true }).click();
  await expect.poll(() => calls.includes('end-all')).toBeTruthy();
  const form = page.getByTestId('access-session-limits');
  await form.getByLabel('Idle limit (hours)').fill('12');
  await form.getByLabel('Authenticator or recovery code').fill('123456');
  await form.getByRole('button', { name: 'Save session limits', exact: true }).click();
  await expect(form.getByText('Session limits saved and audited.')).toBeVisible();
  expect(limits.idleHours).toBe(12);
  expect(calls.slice(-2)).toEqual(['step-up', 'limits']);
  expect(pageErrors).toEqual([]);
  expect(consoleErrors).toEqual([]);
  await expect(page.getByTestId('error-dialog')).toHaveCount(0);
  writeFileSync(join(results, `${round}-capture-report.json`), JSON.stringify({ viewports: [1280, 390], themes: ['light', 'dark'], pageErrors, consoleErrors, overflow: false, actions: calls }, null, 2));
});
