import { test, expect, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * SM-1 (AGT-3372): the Owner manages operator agents and reads their audit
 * trail in Settings > Access. Every operator and audit route is mocked so the
 * flow is deterministic; screenshots land in JOB_RESULTS_DIR for review.
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
  await stubGrantRoutes(page);
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
    return json(route, { message: 'Unexpected operator route in the e2e stub.' }, 400);
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

/** SM-4 (AGT-3375): the operation catalogue and the Owner's standing grants, with a step-up per issue or renewal. */
function grantState() {
  const grant = (id: string, overrides: Record<string, unknown> = {}) => ({
    id, principalId: 'opa_release', principalName: 'release-operator', tier: 'T1', operations: ['card.requeue', 'card.steer'],
    scope: { kind: 'project', project: 'PROJ-001' }, ratePerHour: 6, window: null as { startUtc: string; endUtc: string } | null,
    issuedBy: 'usr_owner', issuedAt: '2026-11-01T09:00:00Z', expiresAt: '2026-11-08T09:00:00Z', renewedAt: null as string | null,
    renewedBy: null as string | null, revokedAt: null as string | null, revokedBy: null as string | null,
    reason: 'Requeue stalled cards', revokeReason: null as string | null, state: 'active', usesLastHour: 2, ...overrides,
  });
  return {
    grant,
    grants: [
      grant('sgr_live'),
      grant('sgr_old', {
        operations: ['service.restart'], scope: { kind: 'host', project: null, host: 'build-host' }, ratePerHour: 2,
        issuedAt: '2026-10-20T09:00:00Z', expiresAt: '2026-10-27T09:00:00Z', state: 'expired', usesLastHour: 0,
        reason: 'Restart the indexer during the migration',
      }),
    ],
    created: [] as Record<string, unknown>[],
    stepUps: [] as unknown[],
    renewed: [] as { id: string; days: number }[],
  };
}

const catalogue = {
  operations: [
    { id: 'observe', tier: 'T0', scopeKind: 'workspace', title: 'Read board, cards, runs, logs, metrics, host status, audit and credential metadata' },
    { id: 'host.inspect', tier: 'T0', scopeKind: 'host', title: 'Inspect a host' },
    { id: 'card.requeue', tier: 'T1', scopeKind: 'project', title: 'Requeue a card' },
    { id: 'card.steer', tier: 'T1', scopeKind: 'project', title: "Steer a card's run" },
    { id: 'service.restart', tier: 'T1', scopeKind: 'host', title: 'Restart an allowlisted service' },
    { id: 'host.drain', tier: 'T1', scopeKind: 'host', title: 'Drain a host' },
    { id: 'gate.rerun', tier: 'T1', scopeKind: 'project', title: 'Re-run a gate' },
    { id: 'stage.deploy', tier: 'T1', scopeKind: 'environment', title: 'Deploy a gate-verified commit to stage' },
    { id: 'production.promote', tier: 'T1', scopeKind: 'environment', title: 'Promote a commit with full gate evidence' },
    { id: 'secrets.change', tier: 'T2', scopeKind: 'project', title: 'Change secrets' },
    { id: 'gate.bypass', tier: 'T2', scopeKind: 'project', title: 'Bypass a gate' },
    { id: 'access.change', tier: 'T2', scopeKind: 'workspace', title: 'Change access' },
    { id: 'grant.change', tier: 'T2', scopeKind: 'workspace', title: 'Change standing grants' },
  ],
  defaultGrantDays: 7, maxGrantDays: 30, defaultRatePerHour: 10, maxRatePerHour: 120,
};

async function stubGrantRoutes(page: Page) {
  const state = grantState();
  await page.route(url => url.pathname === '/api/auth/iam/operator-catalogue', route => json(route, catalogue));
  await page.route(url => url.pathname === '/api/auth/step-up', route => {
    state.stepUps.push(route.request().postDataJSON());
    return json(route, {});
  });
  await page.route(url => url.pathname.startsWith('/api/auth/iam/standing-grants'), route => {
    const request = route.request();
    const path = new URL(request.url()).pathname.split('/').slice(5);
    if (request.method() === 'GET') return json(route, state.grants);
    if (path.length === 0) {
      const body = request.postDataJSON() as Record<string, unknown>;
      state.created.push(body);
      const created = state.grant('sgr_new', {
        operations: body['operations'], scope: body['scope'], ratePerHour: body['ratePerHour'], window: body['window'],
        reason: body['reason'], issuedAt: '2026-11-02T09:10:00Z', expiresAt: '2026-11-16T09:10:00Z', usesLastHour: 0,
      });
      state.grants.unshift(created);
      return json(route, created);
    }
    const target = state.grants.find(candidate => candidate.id === path[0])!;
    if (path[1] === 'renew') {
      const days = (request.postDataJSON() as { expiresInDays: number }).expiresInDays;
      state.renewed.push({ id: target.id, days });
      Object.assign(target, { renewedAt: '2026-11-02T09:12:00Z', expiresAt: '2026-11-09T09:12:00Z' });
      return json(route, target);
    }
    Object.assign(target, { state: 'revoked', revokedAt: '2026-11-02T09:15:00Z', revokedBy: 'usr_owner' });
    return json(route, target);
  });
  return state;
}

test('owner issues, renews, and revokes a standing grant with a fresh second factor', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1600 });
  await stubNetworkedOwner(page);
  const grants = await stubGrantRoutes(page);
  const access = await openAccess(page);
  /** Built at runtime: a one-time code is never a source literal. */
  const code = String(100_000 + Math.floor(Math.random() * 899_999));

  const section = access.getByTestId('access-standing-grants');
  await expect(access.getByRole('heading', { name: 'Standing grants' })).toBeVisible();
  await expect(section.getByTestId('access-catalogue-T0')).toContainText('Inspect a host');
  await expect(section.getByTestId('access-catalogue-T1')).toContainText('Requeue a card');
  await expect(section.getByTestId('access-catalogue-T2')).toContainText('Change standing grants');
  await expect(section.getByTestId('access-grant-operation-secrets.change')).toHaveCount(0);
  await expect(section.getByTestId('access-grant-sgr_live')).toContainText('2 / 6 per hour');
  await expect(section.getByTestId('access-grant-sgr_old')).toContainText('Expired');
  await expect(section.getByTestId('access-grant-renew-sgr_old')).toHaveCount(0);

  await section.getByTestId('access-grant-operation-card.requeue').check();
  await section.getByTestId('access-grant-operation-service.restart').check();
  await section.getByTestId('access-grant-project').fill('PROJ-001');
  await expect(section.getByTestId('access-grant-uncovered')).toContainText('Restart an allowlisted service');
  await expect(section.getByTestId('access-grant-issue')).toBeDisabled();
  await section.getByTestId('access-grant-operation-service.restart').uncheck();
  await section.getByTestId('access-grant-rate').fill('4');
  await section.getByTestId('access-grant-window-start').fill('22:00');
  await section.getByTestId('access-grant-window-end').fill('06:00');
  await section.getByTestId('access-grant-lifetime').selectOption('14');
  await section.getByTestId('access-grant-reason').fill('Requeue stalled cards overnight');
  await section.getByTestId('access-grant-code').fill(code);
  await section.getByTestId('access-grant-issue').click();

  const issued = section.getByTestId('access-grant-sgr_new');
  await expect(issued).toContainText('22:00 to 06:00 UTC');
  await expect(issued).toContainText('0 / 4 per hour');
  expect(grants.stepUps).toEqual([{ code }]);
  expect(grants.created).toEqual([{
    principalId: 'opa_release', operations: ['card.requeue'], scope: { kind: 'project', project: 'PROJ-001' },
    ratePerHour: 4, window: { startUtc: '22:00', endUtc: '06:00' }, expiresInDays: 14, reason: 'Requeue stalled cards overnight',
  }]);
  await expect(section.getByTestId('access-grant-code')).toHaveValue('');
  await shoot(page, 'section[aria-labelledby="access-grants-title"]', 'standing-grants');

  await section.getByTestId('access-grant-code').fill(code);
  await section.getByTestId('access-grant-renew-sgr_live').click();
  await expect.poll(() => grants.renewed).toEqual([{ id: 'sgr_live', days: 14 }]);
  expect(grants.stepUps).toHaveLength(2);

  await section.getByTestId('access-grant-revoke-sgr_new').click();
  await expect(page.getByTestId('confirm-dialog-detail')).toContainText('release-operator: Requeue a card');
  await page.getByTestId('confirm-dialog-confirm').click();
  await expect(issued).toHaveAttribute('data-state', 'revoked');
  await expect(section.getByTestId('access-grant-revoke-sgr_new')).toHaveCount(0);
  expect(grants.stepUps).toHaveLength(2);
});
