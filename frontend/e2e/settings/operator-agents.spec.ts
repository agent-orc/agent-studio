import { test, expect, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * SM-1 (AGT-3372): the Owner manages operator agents and reads their audit
 * trail in Settings > Access. SM-2 (AGT-3373): the Owner ends one session,
 * ends all operator access with the kill switch, and runs a drill. Every
 * operator, kill switch, and audit route is mocked so the flow is
 * deterministic; screenshots land in JOB_RESULTS_DIR for review.
 */
const resultDir = process.env.JOB_RESULTS_DIR ?? join('test-results', 'operator-agents');
const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
/** Built at runtime so no credential-shaped literal lands in source. */
const sessionToken = ['oas', 'opa_studio', 'ses_e2e', Math.random().toString(36).slice(2)].join('.');
const created = '2026-11-01T09:00:00Z';

type SessionState = 'active' | 'expired' | 'revoked';

function session(agentId: string, id: string, name: string | null, state: SessionState, createdAt: string, expiresAt: string, lastUsedAt: string | null) {
  return {
    id, operatorAgentId: agentId, name, actor: `operator-agent:${agentId}/session:${id}`, delegatedBy: 'usr_owner',
    createdAt, createdBy: 'usr_owner', expiresAt, renewedAt: null, lastUsedAt,
    revokedAt: state === 'revoked' ? expiresAt : null, state,
  };
}

function entry(sequence: number, agentId: string, sessionId: string, operation: string, target: string | null, outcome: string, at: string) {
  return {
    sequence, at, stream: 'iam', actor: `operator-agent:${agentId}/session:${sessionId}`, session: sessionId, delegatedBy: 'usr_owner',
    operation, target, parameterDigest: `sha256-${sequence.toString(16).padStart(4, '0')}${'5e1f'.repeat(15)}`, outcome,
    hash: `h${sequence}`, previousHash: `h${sequence - 1}`,
  };
}

function operatorState() {
  const agents = [{
    id: 'opa_release', name: 'release-operator', description: 'Checks release candidates' as string | null, createdAt: created, createdBy: 'usr_owner', disabledAt: null as string | null,
    sessions: [
      session('opa_release', 'ses_morning', 'Morning check', 'active', '2026-11-02T07:00:00Z', '2026-11-02T15:00:00Z', '2026-11-02T08:40:00Z'),
      session('opa_release', 'ses_yesterday', null, 'expired', '2026-11-01T09:00:00Z', '2026-11-01T17:00:00Z', '2026-11-01T16:10:00Z'),
    ],
  }];
  const entries = [
    entry(46, 'opa_release', 'ses_morning', 'task.move', 'PROJ-001/AGT-12', 'succeeded', '2026-11-02T08:40:00Z'),
    entry(45, 'opa_release', 'ses_morning', 'task.comment', 'PROJ-001/AGT-12', 'succeeded', '2026-11-02T08:39:12Z'),
    entry(44, 'opa_release', 'ses_morning', 'settings.write', 'workspace/retention', 'denied', '2026-11-02T08:20:41Z'),
    entry(43, 'opa_release', 'ses_yesterday', 'task.read', 'PROJ-002/AGT-7', 'succeeded', '2026-11-01T16:10:00Z'),
    { ...entry(42, 'opa_release', 'ses_yesterday', 'session.start', null, 'succeeded', '2026-11-01T09:00:00Z'), parameterDigest: null },
  ];
  return {
    agents,
    entries,
    integrity: { state: 'verified', checkedAt: '2026-11-02T08:00:00Z', verifiedThrough: 45, message: null as string | null, alert: false },
    forwarding: {
      sinkKind: 'syslog', configured: true, headSequence: 46, forwardedSequence: 44, pending: 2,
      lastForwardedAt: '2026-11-02T08:30:00Z' as string | null, oldestPendingAt: '2026-11-02T08:39:12Z' as string | null,
      lastError: null as string | null, alert: false,
    },
    killSwitch: {
      kills: [] as Record<string, unknown>[],
      drills: [drill('drill_oct', 'scheduled-drill', '2026-11-01T03:00:00Z', 418.4, true, null)] as Record<string, unknown>[],
      nextDrillAt: '2026-12-01T03:00:00Z' as string | null,
      drillIntervalHours: 720,
      operationsServerConfigured: true,
      outOfBandRequestPending: false,
    },
  };
}

function drill(id: string, trigger: string, startedAt: string, durationMs: number, passed: boolean, failure: string | null) {
  return {
    id, trigger, startedAt, durationMs, studioRefusedMs: passed ? 2.6 : null, operationsServerState: passed ? 'revoked' : 'pending',
    operationsServerMs: passed ? durationMs - 3 : null, otherSessionsKept: true, passed, failure, operatorAgentId: 'opa_drill',
  };
}

function kill(id: string, kind: 'all' | 'session', at: string, agentIds: string[], sessionIds: string[], reason: string | null, operationsState: string) {
  return {
    id, kind, trigger: 'studio', actor: 'human:usr_owner', reason, at, operatorAgentIds: agentIds, sessionIds, allOperators: kind === 'all',
    operationsServer: {
      state: operationsState, attempts: operationsState === 'not-required' ? 0 : 1, lastAttemptAt: at,
      lastError: operationsState === 'pending' ? 'http-503' : null,
      revokedPrincipalIds: operationsState === 'revoked' ? ['release-console'] : [], cancelledOperations: operationsState === 'revoked' ? 1 : 0,
      completedAt: operationsState === 'revoked' ? at : null,
    },
    notifiedAt: at,
  };
}

async function stubNetworkedOwner(page: Page): Promise<ReturnType<typeof operatorState>> {
  const owner = { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false };
  const status = { profile: 'networked', bootstrapRequired: false, authenticated: true, user: owner };
  await page.route('**/hubs/v1/studio/negotiate?*', route => json(route, {
    negotiateVersion: 1, connectionId: 'operator-e2e', connectionToken: 'operator-e2e',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  }));
  await page.routeWebSocket('**/hubs/v1/studio*', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  await page.route('**/api/auth/status', route => json(route, status));
  await page.route('**/api/auth/sessions', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, status));
  await page.route('**/api/projects', route => route.request().method() === 'GET'
    ? json(route, [{ id: 'PROJ-001', displayName: 'Website', shortCode: 'WEB', workspaceId: 'ws_main', storageLocation: '/w/website', sortOrder: 0 }])
    : route.continue());
  await page.route('**/api/auth/iam', route => json(route, {
    roles: [{ id: 'owner', displayName: 'Owner', description: 'Break-glass role.', permissions: [], bindableScopes: ['workspace'], serviceAccountBindable: false }],
    permissions: [], iamVersion: 1, groups: [], invitations: [],
    users: [owner],
    serviceAccounts: [
      { id: 'sa_deploy', name: 'deploy-bot', description: 'Stage deploys', createdAt: created, tokens: [] },
      // The account behind an operator agent: the service-account panel must not list it.
      { id: 'sa_opa_release', name: 'release-operator', createdAt: created, origin: 'operator-agent', createdBy: 'usr_owner', tokens: [] },
    ],
    bindings: [{ id: 'rb_owner', principalKind: 'user', principalId: 'usr_owner', role: 'owner', scope: { kind: 'workspace' }, source: 'bootstrap', createdAt: created }],
  }));
  await page.route('**/api/auth/runner-enrollments', route => json(route, []));
  await page.route('**/api/auth/runners', route => json(route, []));
  return stubOperatorRoutes(page, operatorState());
}

async function stubOperatorRoutes(page: Page, state: ReturnType<typeof operatorState>) {
  await page.route(url => url.pathname.startsWith('/api/auth/iam/operator-agents'), async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname.split('/').slice(5);
    if (request.method() === 'GET' && path.length === 0) return json(route, state.agents);
    if (request.method() === 'POST' && path.length === 0) {
      const body = request.postDataJSON() as { name: string; description?: string };
      const agent = { id: 'opa_studio', name: body.name, description: body.description ?? null, createdAt: '2026-11-02T09:00:00Z', createdBy: 'usr_owner', disabledAt: null, sessions: [] as ReturnType<typeof session>[] };
      state.agents.push(agent);
      return json(route, agent);
    }
    if (request.method() === 'POST' && path[1] === 'sessions' && path.length === 2) {
      const body = request.postDataJSON() as { name?: string; lifetimeHours?: number };
      const agent = state.agents.find(candidate => candidate.id === path[0])!;
      const expiresAt = new Date(Date.parse('2026-11-02T09:05:00Z') + (body.lifetimeHours ?? 8) * 3_600_000).toISOString();
      agent.sessions.push(session(agent.id, 'ses_e2e', body.name ?? null, 'active', '2026-11-02T09:05:00Z', expiresAt, null));
      state.entries.unshift(entry(47, agent.id, 'ses_e2e', 'session.start', null, 'succeeded', '2026-11-02T09:05:00Z'));
      state.forwarding = { ...state.forwarding, headSequence: 47, pending: 3 };
      return json(route, {
        operatorAgentId: agent.id, sessionId: 'ses_e2e', token: sessionToken,
        actor: `operator-agent:${agent.id}/session:ses_e2e`, delegatedBy: 'usr_owner', expiresAt,
      });
    }
    if (request.method() === 'POST' && path[1] === 'sessions' && path[3] === 'kill') {
      const agent = state.agents.find(candidate => candidate.id === path[0])!;
      agent.sessions = agent.sessions.map(item => item.id === path[2] ? { ...item, state: 'revoked' as SessionState, revokedAt: '2026-11-02T09:10:00Z' } : item);
      const record = kill('kill_session', 'session', '2026-11-02T09:10:00Z', [agent.id], [path[2]], null, 'not-required');
      state.killSwitch.kills.unshift(record);
      return json(route, record);
    }
    return json(route, { message: 'Unexpected operator route in the e2e stub.' }, 400);
  });
  await page.route(url => url.pathname.startsWith('/api/auth/iam/operator-kill-switch'), route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() === 'GET') return json(route, state.killSwitch);
    if (path.endsWith('/end-all')) {
      const body = request.postDataJSON() as { reason?: string };
      const live = state.agents.flatMap(agent => agent.sessions.filter(item => item.state === 'active').map(item => item.id));
      state.agents = state.agents.map(agent => ({
        ...agent, disabledAt: '2026-11-02T09:12:00Z',
        sessions: agent.sessions.map(item => item.state === 'active' ? { ...item, state: 'revoked' as SessionState, revokedAt: '2026-11-02T09:12:00Z' } : item),
      }));
      const record = kill('kill_all', 'all', '2026-11-02T09:12:00Z', state.agents.map(agent => agent.id), live, body.reason ?? null, 'revoked');
      state.killSwitch.kills.unshift(record);
      return json(route, record);
    }
    if (path.endsWith('/drills')) {
      const result = drill('drill_manual', 'manual-drill', '2026-11-02T09:14:00Z', 236.9, true, null);
      state.killSwitch.drills.unshift(result);
      return json(route, result);
    }
    return json(route, { message: 'Unexpected kill switch route in the e2e stub.' }, 400);
  });
  await page.route(url => url.pathname === '/api/auth/audit/operator-activity', route => {
    const sessionId = new URL(route.request().url()).searchParams.get('sessionId');
    return json(route, {
      operatorAgents: state.agents,
      entries: sessionId ? state.entries.filter(item => item.session === sessionId) : state.entries,
      integrity: state.integrity,
      forwarding: state.forwarding,
    });
  });
  await page.route(url => url.pathname === '/api/auth/audit/verify', route => {
    state.integrity = { ...state.integrity, state: 'verified', checkedAt: '2026-11-02T09:06:00Z', verifiedThrough: state.entries[0].sequence };
    return json(route, state.integrity);
  });
  return state;
}

async function openAccess(page: Page) {
  await page.goto('/#/workspace/settings/access', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  await expect(page.getByTestId('workspace-settings-rail-access')).toBeVisible({ timeout: 30_000 });
  const access = page.getByTestId('access-area');
  await expect(access.getByRole('heading', { name: 'Users' })).toBeVisible();
  return access;
}

async function shoot(page: Page, selector: string, name: string) {
  mkdirSync(resultDir, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    const section = page.locator(selector);
    await section.scrollIntoViewIfNeeded();
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    await section.screenshot({ path: join(resultDir, `${name}-${theme}.png`) });
  }
}

test('owner creates an operator agent, starts a session once, and reads the audited activity', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1400 });
  await stubNetworkedOwner(page);
  const access = await openAccess(page);

  const operators = access.getByTestId('access-operator-agents');
  await expect(access.getByRole('heading', { name: 'Operator agents' })).toBeVisible();
  await expect(operators.getByTestId('access-operator-session-ses_morning')).toContainText('operator-agent:opa_release/session:ses_morning');
  await expect(operators.getByTestId('access-operator-session-ses_morning')).toContainText('Active');
  await expect(operators.getByTestId('access-operator-session-ses_yesterday')).toContainText('Expired');
  await expect(access.getByTestId('access-service-accounts')).toContainText('deploy-bot');
  await expect(access.getByTestId('access-service-accounts')).not.toContainText('release-operator');

  await operators.getByTestId('access-operator-agent-name').fill('studio-operator');
  await operators.getByTestId('access-operator-agent-description').fill('Runs studio checks for the Owner');
  await operators.getByTestId('access-operator-agent-create').click();
  await expect(operators.getByTestId('access-operator-agent-opa_studio')).toContainText('studio-operator');

  await operators.getByTestId('access-operator-session-name-opa_studio').fill('Evening check');
  await expect(operators.getByTestId('access-operator-session-lifetime-opa_studio')).toHaveValue('8');
  await operators.getByTestId('access-operator-session-lifetime-opa_studio').selectOption('4');
  await operators.getByTestId('access-operator-session-start-opa_studio').click();
  const issued = operators.getByTestId('access-operator-session-issued');
  await expect(issued.getByTestId('access-operator-session-token')).toHaveText(sessionToken);
  await expect(issued).toContainText('only the Owner can renew it');
  await expect(operators.getByTestId('access-operator-session-ses_e2e')).toContainText('operator-agent:opa_studio/session:ses_e2e');
  await shoot(page, 'section[aria-labelledby="access-operators-title"]', 'operator-agents');

  await issued.getByTestId('access-operator-session-done').click();
  await expect(operators.getByTestId('access-operator-session-issued')).toHaveCount(0);
  await expect(access).not.toContainText(sessionToken);

  const activity = access.getByTestId('access-operator-activity');
  await expect(activity.getByTestId('access-audit-entry-47')).toContainText('session.start');
  await expect(activity.getByTestId('access-audit-entry-46')).toContainText('sha256-002e5');
  await expect(activity.getByTestId('access-audit-integrity')).toContainText('Chain verified through entry 45');
  await expect(activity.getByTestId('access-audit-integrity')).not.toHaveAttribute('data-tone', 'alert');
  await expect(activity.getByTestId('access-audit-forwarding')).toContainText('Forwarded 44 of 47 to syslog');
  await expect(activity.getByTestId('access-audit-forwarding')).toContainText('3 pending');
  await activity.getByTestId('access-audit-verify').click();
  await expect(activity.getByTestId('access-audit-integrity')).toContainText('Chain verified through entry 47');

  await activity.getByTestId('access-activity-session-ses_morning').click();
  await expect(activity.getByTestId('access-activity-session-ses_morning')).toHaveAttribute('aria-current', 'true');
  await expect(activity.getByTestId('access-audit-entry-43')).toHaveCount(0);
  await expect(activity.getByTestId('access-audit-entry-44')).toContainText('denied');
  await activity.getByTestId('access-activity-session-all').click();
  await expect(activity.getByTestId('access-audit-entry-43')).toBeVisible();
  await shoot(page, 'section[aria-labelledby="access-operator-activity-title"]', 'operator-activity');
});

test('the activity status strip turns acute only for a server alert', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1100 });
  const state = await stubNetworkedOwner(page);
  state.integrity = { state: 'failed', checkedAt: '2026-11-02T08:00:00Z', verifiedThrough: 43, message: 'Hash mismatch at entry 44.', alert: true };
  state.forwarding = { ...state.forwarding, configured: false, sinkKind: 'none', forwardedSequence: 0, pending: 46, lastForwardedAt: null, oldestPendingAt: null, alert: false };
  const access = await openAccess(page);

  const activity = access.getByTestId('access-operator-activity');
  await expect(activity.getByTestId('access-audit-integrity')).toHaveAttribute('data-tone', 'alert');
  await expect(activity.getByTestId('access-audit-integrity')).toContainText('Hash mismatch at entry 44.');
  await expect(activity.getByTestId('access-audit-forwarding')).toContainText('No off-host sink configured');
  await expect(activity.getByTestId('access-audit-forwarding')).not.toHaveAttribute('data-tone', 'alert');
  await shoot(page, '[data-testid="access-audit-status"]', 'operator-activity-alert');
});

test('owner ends one session, then all operator access, and runs a drill', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1400 });
  await stubNetworkedOwner(page);
  const access = await openAccess(page);
  const operators = access.getByTestId('access-operator-agents');
  const killSwitch = access.getByTestId('access-operator-kill-switch');

  await expect(access.getByRole('heading', { name: 'Kill switch' })).toBeVisible();
  await expect(killSwitch.getByTestId('access-kill-last')).toContainText('Never used');
  await expect(killSwitch.getByTestId('access-kill-drill')).toHaveAttribute('data-state', 'passed');
  await expect(killSwitch.getByTestId('access-kill-drill-result')).toContainText('Drill passed in 418 ms');
  await expect(killSwitch.getByTestId('access-kill-next-drill')).toContainText('Next scheduled drill');

  // Per-session kill: only the runaway session ends.
  await operators.getByTestId('access-operator-session-kill-ses_morning').click();
  await expect(page.getByTestId('confirm-dialog-detail')).toContainText('release-operator: Morning check');
  await page.getByTestId('confirm-dialog-confirm').click();
  await expect(operators.getByTestId('access-operator-session-ses_morning')).toHaveAttribute('data-state', 'revoked');
  await expect(operators.getByTestId('access-operator-agent-opa_release')).not.toContainText('Disabled');
  await expect(killSwitch.getByTestId('access-kill-kill_session')).toContainText('One session');

  // Every operator agent is disabled by the switch.
  await killSwitch.getByTestId('access-kill-reason').fill('Unexpected restarts overnight');
  await killSwitch.getByTestId('access-kill-end-all').click();
  await expect(page.getByTestId('confirm-dialog-detail')).toContainText('Unexpected restarts overnight');
  await page.getByTestId('confirm-dialog-confirm').click();
  await expect(operators.getByTestId('access-operator-agent-opa_release')).toContainText('Disabled');
  await expect(killSwitch.getByTestId('access-kill-last')).toContainText('All operator access, Studio, 1 agent');
  await expect(killSwitch.getByTestId('access-kill-last-operations')).toContainText('Operations Server principals revoked');
  await expect(killSwitch.getByTestId('access-kill-last')).not.toHaveAttribute('data-tone', 'alert');
  await expect(killSwitch.getByTestId('access-kill-reason')).toHaveValue('');

  await killSwitch.getByTestId('access-kill-run-drill').click();
  await expect(killSwitch.getByTestId('access-kill-drill-result')).toContainText('Drill passed in 237 ms');
  await expect(killSwitch.getByTestId('access-kill-drill')).toContainText('Manual drill');
  await shoot(page, 'section[aria-labelledby="access-kill-switch-title"]', 'operator-kill-switch');
});

test('the kill switch turns acute only for a pending revocation, a pending host command, or a failed drill', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1100 });
  const state = await stubNetworkedOwner(page);
  state.killSwitch = {
    ...state.killSwitch,
    kills: [kill('kill_oob', 'all', '2026-11-02T09:20:00Z', ['opa_release'], ['ses_morning'], 'Studio unreachable', 'pending')],
    drills: [drill('drill_failed', 'scheduled-drill', '2026-11-02T03:00:00Z', 10_412.5, false, 'operations-server-unreachable')],
    outOfBandRequestPending: true,
  };
  state.killSwitch.kills[0] = { ...state.killSwitch.kills[0], trigger: 'out-of-band', actor: 'system:control-plane-host' };
  const access = await openAccess(page);
  const killSwitch = access.getByTestId('access-operator-kill-switch');

  await expect(killSwitch.getByTestId('access-kill-pending')).toContainText('A host command is ending all operator access.');
  await expect(killSwitch.getByTestId('access-kill-last')).toHaveAttribute('data-tone', 'alert');
  await expect(killSwitch.getByTestId('access-kill-last')).toContainText('Host command');
  await expect(killSwitch.getByTestId('access-kill-last-operations')).toContainText('Operations Server revocation pending (http-503)');
  await expect(killSwitch.getByTestId('access-kill-drill')).toHaveAttribute('role', 'alert');
  await expect(killSwitch.getByTestId('access-kill-drill-result')).toContainText('Drill failed: operations-server-unreachable');
  await expect(killSwitch.getByTestId('access-kill-kill_oob')).toContainText('Control plane host');
  await shoot(page, 'section[aria-labelledby="access-kill-switch-title"]', 'operator-kill-switch-alert');
});
