import { test, expect, type WebSocketRoute } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { installOrchestratorChatBootstrap } from '../helpers/orchestrator-chat-bootstrap';
import { setTheme } from '../helpers/theme';
import { stubPilotOwner, json } from './access-pilot.fixture';

const results = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'elevated-sessions');

const round = process.env.ES7_CAPTURE_ROUND ?? 'es7-round1';

test('classes, re-arm, header, activity filters and session actions at desktop and phone widths', async ({ page }) => {
  test.setTimeout(360_000);
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
  await page.route('**/api/v1/management/credential-inventory', route => json(route, { items: [] }));
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
  mkdirSync(results, { recursive: true });
  const recordErrors = () => writeFileSync(join(results, `${round}-browser-errors.json`), JSON.stringify({ pageErrors, consoleErrors }, null, 2));
  page.on('console', message => {
    if (message.type() === 'error') { consoleErrors.push({ text: message.text(), url: message.location().url }); recordErrors(); }
  });
  page.on('pageerror', error => { pageErrors.push(error.message); recordErrors(); });
  const calls: string[] = [];
  const session = (id: string) => ({ id, operatorAgentId: 'opa_console', name: 'Ops console', actor: `operator-agent:opa_console/session:${id}`,
    delegatedBy: 'human:usr_owner', createdAt: '2026-10-20T09:00:00Z', createdBy: 'usr_owner', expiresAt: '9999-12-31T23:59:59Z',
    chosenEnd: null, chosenLifetimeHours: 0, idleEnd: '2026-11-27T09:00:00Z', state: 'active', revokedAt: null,
    lastUsedAt: '2026-11-26T09:00:00Z', lastAddress: '198.51.100.24', renewedAt: '2026-11-26T09:45:00Z',
    newAddressAt: '2026-11-26T09:45:00Z', reviewDue: true, classes: ['project.testing', 'cards.accept-override'],
    elevation: { classes: [
      { id: 'project.testing', mode: 'direct-action', confirmedBy: 'usr_owner', armingId: 'arm_console', armedAt: '2026-11-25T10:30:00Z', armedUntil: '2026-11-26T10:30:00Z', ratePerHour: 6, uses: [{ at: '2026-11-26T09:30:00Z', project: 'PROJ-001' }] },
      { id: 'cards.accept-override', mode: 'batch', confirmedBy: 'usr_owner', armingId: null, armedAt: null, armedUntil: null, ratePerHour: 30, uses: [] },
    ] },
    device: { deviceId: 'dev_workstation', label: 'ops@workstation', bindingKind: 'tpm', keyThumbprint: 'public-thumbprint', requestedAddress: '198.51.100.7' } });
  const resetSessions = () => [session('ses_console'), { ...session('ses_memory'), chosenLifetimeHours: 24, chosenEnd: '2026-11-27T08:00:00Z',
    expiresAt: '2026-11-27T08:00:00Z', reviewDue: false, newAddressAt: null, device: { ...session('').device, bindingKind: 'memory', label: 'ops@server', deviceId: 'dev_server' } }];
  let sessions = resetSessions();
  let agentReads = 0;
  const agents = () => [{ id: 'opa_console', name: 'Ops console', description: 'Installation operator', createdAt: '2026-10-20T09:00:00Z', createdBy: 'usr_owner',
    disabledAt: null, sessions, devices: [] }];
  let limits = { defaultLifetimeHours: 168, ownerLifetimeHours: null, adminLifetimeHours: 168, idleHours: 24 };
  await page.route('**/api/auth/iam/operator-agents**', async route => {
    if (route.request().method() === 'GET') { agentReads++; return json(route, agents()); }
    const action = route.request().url().split('/').at(-1)!;
    calls.push(action);
    if (action === 'end') sessions = sessions.filter(item => !route.request().url().includes(item.id));
    if (action === 'keep') sessions = sessions.map(item => ({ ...item, reviewDue: false }));
    if (action === 'end-elevation') sessions = sessions.map(item => ({ ...item, classes: [], elevation: { classes: [] }, elevationRateEndedAt: null }));
    if (action === 'rearm') {
      expect(calls.at(-2)).toBe('step-up');
      expect(route.request().postDataJSON()).toEqual({ classId: 'project.testing' });
    }
    if (route.request().method() === 'DELETE') sessions = [];
    return route.fulfill({ status: 204 });
  });
  await page.route('**/api/auth/iam/elevated-sessions/end-all', async route => { calls.push('end-all'); sessions = []; await route.fulfill({ status: 204 }); });
  await page.route('**/api/auth/iam/operator-kill-switch', route => json(route, {
    kills: [], drills: [], nextDrillAt: null, drillIntervalHours: 720, operationsServerConfigured: false, outOfBandRequestPending: false,
  }));
  await page.route('**/api/auth/audit/operator-activity**', route => json(route, {
    operatorAgents: agents(), entries: [{ sequence: 99, at: '2026-11-26T09:30:00Z', stream: 'operator-agent', actor: 'operator-agent:opa_console/session:ses_console', session: 'ses_console', delegatedBy: 'usr_owner', operation: 'PUT /api/projects/Website/testing', target: 'Website', outcome: '200', parameterDigest: null, hash: 'hash', previousHash: 'previous', details: { elevationClass: 'project.testing', reason: 'Owner requested strict testing', instructionSource: 'owner-chat', armingId: 'arm_console', deviceThumbprint: 'public-thumbprint', approvedBy: 'usr_owner' } }],
    integrity: { state: 'verified', checkedAt: '2026-11-26T09:00:00Z', verifiedThrough: 0, message: null, alert: false },
    forwarding: { sinkKind: 'none', configured: false, headSequence: 0, forwardedSequence: 0, pending: 0,
      lastForwardedAt: null, oldestPendingAt: null, lastError: null, alert: false },
  }));
  await page.route('**/api/auth/iam/session-limits', async route => {
    if (route.request().method() === 'PUT') { calls.push('limits'); limits = route.request().postDataJSON(); }
    return json(route, limits);
  });
  await page.route(url => ['/api/auth/step-up', '/api/v1/studio/auth/step-up'].includes(url.pathname), async route => { calls.push('step-up'); await route.fulfill({ status: 204 }); });
  await page.setViewportSize({ width: 1280, height: 1000 });
  await page.goto('/#/workspace/settings/access', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  const elevated = page.getByTestId('access-elevated-sessions');
  await expect(elevated.getByText('ops@workstation', { exact: true })).toBeVisible({ timeout: 30_000 });
  await expect(elevated.getByText('New renewal address').first()).toBeVisible();
  await expect(elevated.getByText('Elevated session running 30 days')).toBeVisible();
  for (const width of [390, 1024, 1280]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 1000 });
    for (const theme of ['light', 'dark'] as const) {
      if (!sessions[0]?.classes.length) {
        sessions = resetSessions();
        await page.reload({ waitUntil: 'domcontentloaded', timeout: 30_000 });
        await expect(elevated.getByText('ops@workstation', { exact: true })).toBeVisible({ timeout: 30_000 });
      }
      await setTheme(page, theme);
      const chip = page.getByTestId('studio-elevation-trigger');
      await expect(chip).toBeVisible();
      await elevated.getByText('ops@workstation', { exact: true }).scrollIntoViewIfNeeded();
      mkdirSync(results, { recursive: true });
      if (width !== 1024) await page.screenshot({ path: join(results, `${round}-header-${width}-${theme}--mocked.png`), animations: 'disabled' });
      await chip.click();
      const dialog = page.getByTestId('studio-elevation-dialog');
      await expect(dialog.getByText('A direct-action class lapses within one hour.', { exact: false })).toBeVisible();
      await expect(dialog.getByText('project.testing', { exact: true }).first()).toBeVisible();
      await dialog.getByRole('button', { name: 'Re-arm project.testing', exact: true }).first().click();
      await dialog.getByLabel('Authenticator or recovery code for project.testing').fill('123456');
      if (width !== 1024) await dialog.screenshot({ path: join(results, `${round}-rearm-${width}-${theme}--mocked.png`), animations: 'disabled' });
      await dialog.getByRole('button', { name: 'Confirm Re-arm', exact: true }).click();
      await expect(dialog.getByLabel('Authenticator or recovery code for project.testing')).toHaveCount(0);
      await dialog.getByRole('button', { name: 'Open Elevated sessions in Settings', exact: true }).click();
      await expect(dialog).toHaveCount(0);
      await expect(page.locator('#access-elevated-sessions-title')).toBeFocused();
      await page.getByTestId('access-area').getByRole('button', { name: 'Elevated sessions', exact: true }).click();
      await elevated.getByText('ops@workstation', { exact: true }).scrollIntoViewIfNeeded();
      mkdirSync(results, { recursive: true });
      await expect(page.getByTestId('error-dialog')).toHaveCount(0);
      if (width !== 1024) await page.screenshot({ path: join(results, `${round}-${width}-${theme}--mocked.png`), animations: 'disabled' });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
      const activity = page.getByTestId('access-operator-activity');
      await page.getByTestId('access-area').getByRole('button', { name: 'Operator activity', exact: true }).click();
      await activity.scrollIntoViewIfNeeded();
      await activity.getByRole('combobox', { name: 'Operator agent', exact: true }).selectOption('opa_console');
      const filterRequest = page.waitForRequest(request => request.url().includes('/operator-activity') && request.url().includes('elevatedOnly=true') && request.url().includes('operatorAgentId=opa_console'));
      await activity.getByLabel('Elevated actions only').check();
      await filterRequest;
      await activity.getByTestId('access-activity-session-ses_console').click();
      await expect(activity.getByText('Owner requested strict testing', { exact: false }).first()).toBeVisible();
      await activity.getByText('Owner requested strict testing', { exact: false }).first().scrollIntoViewIfNeeded();
      if (width !== 1024) await page.screenshot({ path: join(results, `${round}-activity-${width}-${theme}--mocked.png`), animations: 'disabled' });
      await elevated.getByRole('button', { name: 'End elevation', exact: true }).first().click();
      await expect(page.getByTestId('studio-elevation-trigger')).toHaveCount(0);
    }
  }
  await elevated.getByRole('button', { name: 'Keep', exact: true }).click();
  await expect(elevated.getByText('Elevated session running 30 days')).toHaveCount(0);
  await elevated.getByRole('button', { name: 'End session', exact: true }).first().click();
  await expect(elevated.getByText('ops@workstation', { exact: true })).toHaveCount(0);
  await elevated.getByRole('button', { name: 'Forget device', exact: true }).click();
  await expect(elevated.getByText('No live sessions with classes or a lifetime longer than 8 hours.')).toBeVisible();
  expect(calls.filter(action => action === 'rearm')).toHaveLength(6);
  sessions = [session('ses_console')];
  await page.reload({ waitUntil: 'domcontentloaded', timeout: 30_000 });
  await expect(page.getByTestId('studio-elevation-trigger')).toBeVisible({ timeout: 30_000 });
  await page.getByTestId('access-area').getByRole('button', { name: 'Elevated sessions', exact: true }).click();
  await elevated.getByRole('button', { name: 'End all elevated sessions', exact: true }).click();
  await expect.poll(() => calls.includes('end-all')).toBeTruthy();
  await page.getByTestId('access-area').getByRole('button', { name: 'Session limits', exact: true }).click();
  const form = page.getByTestId('access-session-limits');
  await form.getByLabel('Idle limit (hours)').fill('12');
  await form.getByLabel('Authenticator or recovery code').fill('123456');
  await form.getByRole('button', { name: 'Save session limits', exact: true }).click();
  await expect(form.getByText('Session limits saved and audited.')).toBeVisible();
  expect(limits.idleHours).toBe(12);
  expect(calls.slice(-2)).toEqual(['step-up', 'limits']);
  await page.route('**/api/runner/orchestrator-feed**', route => json(route, { entries: [{
    ts: '2026-11-26T10:00:00Z', kind: 'action', topic: 'operator-elevated-action',
    summary: 'Testing strategy: fast to strict. Owner requested strict testing',
    project: 'Website', testingUndoSequence: 99,
  }], generatedAtUtc: '2026-11-26T10:00:00Z' }));
  await page.route('**/api/projects/Website/testing/undo/99', async route => {
    expect(route.request().method()).toBe('POST');
    expect(route.request().postDataJSON()).toEqual({});
    calls.push('undo');
    return json(route, { strategy: 'fast' });
  });
  await page.goto('/#/feed', { waitUntil: 'domcontentloaded' });
  const change = page.getByTestId('orchestrator-feed-entry').filter({ hasText: 'Testing strategy: fast to strict' });
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 1000 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(change.getByRole('button', { name: 'Undo', exact: true })).toBeVisible();
      if (width === 390) expect((await change.getByRole('button', { name: 'Undo', exact: true }).boundingBox())!.height).toBeGreaterThanOrEqual(44);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
      await page.screenshot({ path: join(results, `${round}-undo-${width}-${theme}--mocked.png`), animations: 'disabled' });
    }
  }
  await change.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect(change.getByRole('button', { name: 'Restored', exact: true })).toBeDisabled();
  expect(calls.filter(action => action === 'undo')).toHaveLength(1);
  // Keep the mocked hub's liveness clock aligned with the timer probe.
  const studioSockets = new Set<WebSocketRoute>();
  await page.routeWebSocket('**/hubs/v1/studio*', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) {
        socket.send('{}\u001e');
        studioSockets.add(socket);
      }
    });
    socket.onClose(() => studioSockets.delete(socket));
  });
  const sendHeartbeat = () => {
    for (const socket of studioSockets) socket.send('{"type":6}\u001e');
  };
  // Run the timer probe last so it does not affect the workflow's page reloads.
  sessions = resetSessions();
  await page.clock.install({ time: new Date('2026-11-26T10:00:00Z') });
  await page.reload({ waitUntil: 'domcontentloaded', timeout: 30_000 });
  await expect(page.getByTestId('studio-elevation-trigger')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('access-elevated-sessions')).toHaveCount(0);
  await expect.poll(() => studioSockets.size).toBeGreaterThan(0);
  const readsBeforeTick = agentReads;
  sendHeartbeat();
  await page.clock.fastForward(15_000);
  expect(agentReads).toBe(readsBeforeTick);
  const refreshed = page.waitForResponse(response => response.url().endsWith('/api/auth/iam/operator-agents')
    && response.request().method() === 'GET');
  for (let tick = 0; tick < 3; tick++) {
    sendHeartbeat();
    await page.clock.fastForward(15_000);
  }
  await refreshed;
  expect(agentReads).toBe(readsBeforeTick + 1);
  expect(pageErrors).toEqual([]);
  expect(consoleErrors).toEqual([]);
  await expect(page.getByTestId('error-dialog')).toHaveCount(0);
  writeFileSync(join(results, `${round}-capture-report.json`), JSON.stringify({ viewports: [1280, 1024, 390], themes: ['light', 'dark'], pageErrors, consoleErrors, overflow: false, actions: calls }, null, 2));
});
