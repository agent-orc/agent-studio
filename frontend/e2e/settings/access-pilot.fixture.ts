import type { Page, Route } from '@playwright/test';

/**
 * AGT-4029: the data set of the Access page pilot. One realistic installation
 * (six people, three groups, service accounts with bindings and tokens, API
 * tokens, enrollment codes, enrolled runners) served from mocked routes, so
 * the before and after captures and the pilot spec show the same facts.
 */
export const PILOT_NOW = new Date('2026-11-24T09:00:00Z');
export const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
/** One-time values are assembled at runtime so no credential-shaped literal lands in source. */
export const pilotToken = ['sat', 'tok_pilot', 'one-time-value-shown-once'].join('.');
export const pilotEnrollmentCode = ['enr', 'pilot', 'one-time-code'].join('.');

const roles = [
  { id: 'owner', displayName: 'Owner', description: 'Break-glass role of the installation. Holds every permission.', permissions: [], bindableScopes: ['workspace'], serviceAccountBindable: false },
  { id: 'admin', displayName: 'Admin', description: 'Runs the workspace or a project: settings, operation, production deploys, secrets.', permissions: [], bindableScopes: ['workspace', 'project'], serviceAccountBindable: true },
  { id: 'developer', displayName: 'Developer', description: 'Works on cards, runs, chats, and documents; deploys outside production.', permissions: [], bindableScopes: ['workspace', 'project', 'environment'], serviceAccountBindable: true },
  { id: 'operator-agent', displayName: 'Operator agent', description: 'Operates the board for a coding agent: reads, lane moves, runs, batch gate, review step models, and stage deploys; no secrets, production approval, access, or installation settings.', permissions: [], bindableScopes: ['workspace', 'project'], serviceAccountBindable: true, userBindable: false },
  { id: 'contributor', displayName: 'Product', description: 'Product role: writes and accepts cards in assigned projects.', permissions: [], bindableScopes: ['project'], serviceAccountBindable: false },
  { id: 'viewer', displayName: 'Viewer', description: 'Reads everything in its scope, secret values excluded.', permissions: [], bindableScopes: ['workspace', 'project', 'environment'], serviceAccountBindable: true },
  { id: 'approver', displayName: 'Approver', description: 'Approves or rejects production releases in its scope.', permissions: [], bindableScopes: ['workspace', 'project', 'environment'], serviceAccountBindable: false },
];

const day = (value: string) => `${value}T09:00:00Z`;

interface PilotToken { id: string; name: string; createdAt: string; expiresAt: string | null; lastUsedAt: string | null; revokedAt: string | null }
interface PilotAccount {
  id: string; name: string; description: string; createdAt: string; disabledAt?: string | null;
  origin?: string; createdBy?: string; tokens: PilotToken[];
}

export function pilotState() {
  const user = (id: string, username: string, displayName: string, role: string, projects: string[], extra: Record<string, unknown> = {}) =>
    ({ id, username, displayName, role, projects, disabled: false, mustChangePassword: false, hasFactor: false, factorChangedAt: null as string | null, ...extra });
  const binding = (id: string, principalKind: string, principalId: string, role: string, scope: Record<string, string>, source = 'manual') =>
    ({ id, principalKind, principalId, role, scope, source, createdAt: day('2026-10-20') });
  const token = (id: string, name: string, createdAt: string, expiresAt: string | null, lastUsedAt: string | null, revokedAt: string | null = null): PilotToken =>
    ({ id, name, createdAt, expiresAt, lastUsedAt, revokedAt });
  const accounts: PilotAccount[] = [
    { id: 'sa_deploy', name: 'deploy-bot', description: 'Stage deploys from CI', createdAt: day('2026-09-02'), tokens: [
      token('tok_ci', 'CI pipeline', day('2026-09-02'), day('2026-12-01'), '2026-11-24T07:42:00Z'),
      token('tok_hotfix', 'Hotfix runner', day('2026-11-03'), day('2027-02-01'), null),
      token('tok_old', 'First token', day('2026-06-01'), day('2026-08-30'), day('2026-08-29'), day('2026-09-02')),
    ] },
    { id: 'sa_reports', name: 'nightly-reports', description: 'Reads cards and runs for the nightly report', createdAt: day('2026-10-01'), tokens: [
      token('tok_reports', 'Report job', day('2026-10-01'), day('2026-12-30'), '2026-11-24T02:00:00Z'),
    ] },
    { id: 'sa_unbound', name: 'import-job', description: 'Planned data import', createdAt: day('2026-11-18'), tokens: [] },
    { id: 'sa_legacy', name: 'legacy-sync', description: 'Replaced by deploy-bot', createdAt: day('2026-03-11'), disabledAt: day('2026-09-02'), tokens: [] },
    { id: 'sa_tok_build', name: 'build bot', description: 'Migrated API token.', createdAt: day('2026-08-14'), origin: 'api-token', createdBy: 'usr_owner', tokens: [
      token('tok_build', 'build bot', day('2026-08-14'), day('2026-12-12'), '2026-11-23T22:15:00Z'),
    ] },
    { id: 'sa_tok_laptop', name: 'studio-op on laptop', description: 'Operator CLI of Jonas.', createdAt: day('2026-11-10'), origin: 'api-token', createdBy: 'usr_admin', tokens: [
      token('tok_laptop', 'studio-op on laptop', day('2026-11-10'), day('2026-12-10'), null),
    ] },
  ];
  return {
    roles,
    permissions: [],
    iamVersion: 7,
    users: [
      user('usr_owner', 'mara', 'Mara Lindqvist', 'owner', [], { hasFactor: true, factorChangedAt: day('2026-10-20') }),
      user('usr_admin', 'jonas.weber', 'Jonas Weber', 'operator', [], { hasFactor: true, factorChangedAt: day('2026-11-02') }),
      user('usr_dev', 'priya.nair', 'Priya Nair', 'operator', ['PROJ-001'], { hasFactor: true, factorChangedAt: day('2026-10-28') }),
      user('usr_product', 'tom.okafor', 'Tom Okafor', 'contributor', ['PROJ-002']),
      user('usr_invited', 'elena.rossi', 'Elena Rossi', 'operator', ['PROJ-002'], { mustChangePassword: true }),
      user('usr_former', 'sam.becker', 'Sam Becker', 'viewer', ['PROJ-001'], { disabled: true }),
    ],
    groups: [
      { id: 'grp_web', name: 'Team Website', description: 'Everyone on the website', members: ['usr_dev', 'usr_product'], createdAt: day('2026-10-20') },
      { id: 'grp_release', name: 'Release approvers', description: 'May approve production releases', members: ['usr_admin', 'usr_dev'], createdAt: day('2026-10-22') },
      { id: 'grp_contract', name: 'Contractors', description: '', members: [] as string[], createdAt: day('2026-11-15') },
    ],
    serviceAccounts: accounts,
    bindings: [
      binding('rb_owner', 'user', 'usr_owner', 'owner', { kind: 'workspace' }, 'bootstrap'),
      binding('rb_admin', 'user', 'usr_admin', 'admin', { kind: 'workspace' }),
      binding('rb_dev', 'user', 'usr_dev', 'developer', { kind: 'project', project: 'PROJ-001' }),
      binding('rb_product', 'user', 'usr_product', 'contributor', { kind: 'project', project: 'PROJ-002' }),
      binding('rb_invited', 'user', 'usr_invited', 'developer', { kind: 'project', project: 'PROJ-002' }, 'role-assignment'),
      binding('rb_former', 'user', 'usr_former', 'viewer', { kind: 'project', project: 'PROJ-001' }),
      binding('rb_group_web', 'group', 'grp_web', 'viewer', { kind: 'project', project: 'PROJ-002' }),
      binding('rb_group_release', 'group', 'grp_release', 'approver', { kind: 'environment', project: 'PROJ-001', environment: 'production' }),
      binding('rb_bot_web', 'service-account', 'sa_deploy', 'developer', { kind: 'environment', project: 'PROJ-001', environment: 'stage' }),
      binding('rb_bot_app', 'service-account', 'sa_deploy', 'developer', { kind: 'environment', project: 'PROJ-002', environment: 'stage' }),
      binding('rb_reports', 'service-account', 'sa_reports', 'viewer', { kind: 'workspace' }),
      binding('rb_build', 'service-account', 'sa_tok_build', 'operator-agent', { kind: 'workspace' }, 'migration'),
      binding('rb_laptop', 'service-account', 'sa_tok_laptop', 'operator-agent', { kind: 'project', project: 'PROJ-001' }, 'migration'),
    ],
    invitations: [{ id: 'inv_elena', userId: 'usr_invited', expiresAt: '2026-11-25T08:10:00Z', used: false }],
  };
}

export type PilotState = ReturnType<typeof pilotState>;

export function pilotRunners() {
  const credential = (id: string, scopes: string[], expiresAt: string | null, extra: Record<string, unknown> = {}) =>
    ({ id, scopes, createdAt: day('2026-11-01'), expiresAt, lastUsedAt: '2026-11-24T08:55:00Z', revokedAt: null as string | null, ...extra });
  return {
    enrollments: [
      { id: 'enr_build', name: 'build-host-02', scopes: ['runner.claim'], createdAt: '2026-11-24T08:50:00Z', expiresAt: '2026-11-24T09:05:00Z', usedAt: null as string | null, projects: ['PROJ-001'] },
      { id: 'enr_review', name: 'review-host', scopes: ['runner.claim'], createdAt: '2026-11-20T10:00:00Z', expiresAt: '2026-11-20T10:15:00Z', usedAt: '2026-11-20T10:04:00Z', projects: ['*'] },
      { id: 'enr_stale', name: 'spare-host', scopes: ['runner.claim'], createdAt: '2026-11-12T14:00:00Z', expiresAt: '2026-11-12T14:15:00Z', usedAt: null, projects: ['PROJ-002'] },
    ],
    runners: [
      { id: 'runner_coding', name: 'coding-host-01', createdAt: day('2026-10-21'), revokedAt: null as string | null, lastSeenAt: '2026-11-24T08:59:00Z',
        projects: ['PROJ-001', 'PROJ-002'], credentials: [credential('cred_coding', ['runner.claim'], day('2026-12-20'))] },
      { id: 'runner_review', name: 'review-host', createdAt: '2026-11-20T10:04:00Z', revokedAt: null, lastSeenAt: '2026-11-24T08:58:00Z',
        projects: ['*'], credentials: [credential('cred_review', ['runner.claim'], day('2026-11-28'))] },
      { id: 'runner_deploy', name: 'stage-run-host', createdAt: day('2026-11-05'), revokedAt: null, lastSeenAt: '2026-11-24T08:57:00Z',
        projects: ['PROJ-001'], credentials: [credential('cred_deploy', ['runner.deploy'], day('2026-12-05'))] },
      { id: 'engine_main', name: 'orchestration-engine', createdAt: day('2026-10-21'), revokedAt: null, lastSeenAt: '2026-11-24T08:59:30Z',
        projects: [] as string[], credentials: [credential('cred_engine', ['orchestration.write'], day('2026-12-21'))] },
      { id: 'runner_old', name: 'old-host', createdAt: day('2026-05-02'), revokedAt: null, lastSeenAt: '2026-11-21T17:20:00Z', projects: ['*'],
        projectsNotice: 'Enrolled before project binding: holds all projects until you choose its projects.',
        credentials: [credential('cred_old', ['runner.claim'], null, { expiryNotice: 'No expiry: issued before the 30-day default. Rotate it to adopt the default.' })] },
      { id: 'runner_retired', name: 'retired-host', createdAt: day('2026-04-10'), revokedAt: day('2026-10-30'), lastSeenAt: day('2026-10-29'),
        projects: ['PROJ-002'], credentials: [credential('cred_retired', ['runner.claim'], day('2026-11-10'), { revokedAt: day('2026-10-30') })] },
    ],
  };
}

const factorEvents = [
  { sequence: 3, at: day('2026-11-02'), actor: 'human:usr_admin', session: 'session-jonas', operation: 'factor.bound', target: 'user:usr_admin' },
  { sequence: 2, at: day('2026-10-28'), actor: 'human:usr_dev', session: 'session-priya', operation: 'factor.bound', target: 'user:usr_dev' },
  { sequence: 1, at: day('2026-10-20'), actor: 'human:usr_owner', session: 'session-mara', operation: 'factor.bound', target: 'user:usr_owner' },
];

export interface PilotCalls { method: string; path: string; body: unknown }

/**
 * Serves the pilot installation to a networked Owner. Mutations change the
 * served state, so the page reloads what a real server would return, and
 * every mutation is recorded in `calls`.
 */
export async function stubPilotOwner(page: Page, options: { empty?: boolean } = {}) {
  const state = pilotState();
  const runners = pilotRunners();
  if (options.empty) {
    state.users = state.users.slice(0, 1);
    state.groups = [];
    state.serviceAccounts = [];
    state.bindings = state.bindings.slice(0, 1);
    state.invitations = [];
    runners.enrollments = [];
    runners.runners = [];
  }
  const calls: PilotCalls[] = [];
  const record = (route: Route) => {
    const request = route.request();
    const body = (): unknown => {
      try { return request.postDataJSON(); } catch { return null; }
    };
    calls.push({ method: request.method(), path: new URL(request.url()).pathname, body: body() });
  };
  const owner = state.users[0];
  const status = { profile: 'networked', bootstrapRequired: false, authenticated: true, user: owner };
  await page.clock.setFixedTime(PILOT_NOW);
  await page.route('**/hubs/v1/studio/negotiate?*', route => json(route, {
    negotiateVersion: 1, connectionId: 'access-pilot', connectionToken: 'access-pilot',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  }));
  await page.routeWebSocket('**/hubs/v1/studio*', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  await page.route('**/api/auth/status', route => json(route, status));
  await page.route('**/api/v1/studio/auth/status', route => json(route, status));
  await page.route('**/api/auth/sessions', route => json(route, [
    { id: 'session-mara', device: 'Firefox on Linux', address: 'Office network', createdAt: day('2026-11-24'), lastUseAt: day('2026-11-24'), current: true },
  ]));
  await page.route('**/api/projects', route => route.request().method() === 'GET'
    ? json(route, [
      { id: 'PROJ-001', displayName: 'Website', shortCode: 'WEB', workspaceId: 'ws_main', storageLocation: '/w/website', sortOrder: 0 },
      { id: 'PROJ-002', displayName: 'Mobile App', shortCode: 'APP', workspaceId: 'ws_main', storageLocation: '/w/app', sortOrder: 1 },
    ])
    : route.continue());
  await page.route('**/api/auth/iam', route => json(route, state));
  await page.route('**/api/auth/users', route => json(route, state.users));
  // Operator agents, approvals, standing grants, and their audit views have their own spec (operator-agents.spec.ts).
  await page.route(url => url.pathname.startsWith('/api/auth/iam/operator-agents') || url.pathname.startsWith('/api/auth/iam/operator-approvals')
    || url.pathname.startsWith('/api/auth/audit/') || url.pathname === '/api/auth/iam/operator-catalogue'
    || url.pathname.startsWith('/api/auth/iam/standing-grants'),
    route => json(route, { message: 'Not found' }, 404));
  await page.route('**/api/auth/audit/factors', route => json(route, options.empty ? [] : factorEvents));

  await page.route('**/api/auth/runner-enrollments', route => {
    if (route.request().method() !== 'POST') return json(route, runners.enrollments);
    record(route);
    return json(route, { enrollmentCode: pilotEnrollmentCode, name: 'new-host', scopes: [], expiresAt: '2026-11-24T09:15:00Z' });
  });
  await page.route('**/api/auth/runner-enrollments/*', route => {
    record(route);
    const id = new URL(route.request().url()).pathname.split('/').pop();
    // The server keeps a revoked code on record and ends it now.
    const enrollment = runners.enrollments.find(candidate => candidate.id === id);
    if (enrollment) enrollment.expiresAt = PILOT_NOW.toISOString();
    return json(route, {});
  });
  await page.route('**/api/auth/runners', route => json(route, runners.runners));
  await page.route('**/api/auth/runners/*', route => {
    record(route);
    const id = new URL(route.request().url()).pathname.split('/').pop();
    const runner = runners.runners.find(candidate => candidate.id === id);
    if (runner) runner.revokedAt = PILOT_NOW.toISOString();
    return json(route, {});
  });

  await page.route('**/api/auth/iam/service-accounts', route => {
    record(route);
    const body = route.request().postDataJSON() as { name: string; description: string };
    const account: PilotAccount = { id: 'sa_new', name: body.name, description: body.description, createdAt: PILOT_NOW.toISOString(), tokens: [] };
    state.serviceAccounts.push(account);
    return json(route, account);
  });
  await page.route('**/api/auth/iam/service-accounts/*', route => {
    record(route);
    const id = new URL(route.request().url()).pathname.split('/').pop();
    const account = state.serviceAccounts.find(candidate => candidate.id === id);
    if (account) account.disabledAt = PILOT_NOW.toISOString();
    return json(route, {});
  });
  await page.route('**/api/auth/iam/service-accounts/*/tokens', route => {
    record(route);
    const accountId = new URL(route.request().url()).pathname.split('/').at(-2)!;
    const body = route.request().postDataJSON() as { name: string; expiresAt: string | null };
    const account = state.serviceAccounts.find(candidate => candidate.id === accountId);
    account?.tokens.push({ id: 'tok_pilot', name: body.name, createdAt: PILOT_NOW.toISOString(), expiresAt: body.expiresAt, lastUsedAt: null, revokedAt: null });
    return json(route, { serviceAccountId: accountId, tokenId: 'tok_pilot', token: pilotToken, expiresAt: body.expiresAt });
  });
  await page.route('**/api/auth/iam/service-accounts/*/tokens/*', route => {
    record(route);
    const parts = new URL(route.request().url()).pathname.split('/');
    const account = state.serviceAccounts.find(candidate => candidate.id === parts.at(-3));
    const found = account?.tokens.find(candidate => candidate.id === parts.at(-1));
    if (found) found.revokedAt = PILOT_NOW.toISOString();
    return json(route, {});
  });
  await page.route('**/api/auth/iam/bindings', route => {
    record(route);
    const body = route.request().postDataJSON() as { principalKind: string; principalId: string; role: string; scope: Record<string, string> };
    const created = { id: `rb_new_${state.bindings.length}`, ...body, source: 'manual', createdAt: PILOT_NOW.toISOString() };
    state.bindings.push(created);
    return json(route, created);
  });
  await page.route('**/api/auth/iam/bindings/*', route => {
    record(route);
    const id = new URL(route.request().url()).pathname.split('/').pop();
    state.bindings = state.bindings.filter(candidate => candidate.id !== id);
    return json(route, {});
  });
  await page.route('**/api/auth/iam/groups', route => {
    record(route);
    const body = route.request().postDataJSON() as { name: string; description: string; members: string[] };
    const group = { id: 'grp_new', name: body.name, description: body.description, members: body.members, createdAt: PILOT_NOW.toISOString() };
    state.groups.push(group);
    return json(route, group);
  });
  await page.route('**/api/auth/iam/groups/*', route => {
    record(route);
    const id = new URL(route.request().url()).pathname.split('/').pop();
    if (route.request().method() === 'DELETE') state.groups = state.groups.filter(group => group.id !== id);
    else {
      const group = state.groups.find(candidate => candidate.id === id);
      if (group) group.members = (route.request().postDataJSON() as { members: string[] }).members;
    }
    return json(route, {});
  });
  await page.route('**/api/auth/users/*', route => {
    record(route);
    const id = new URL(route.request().url()).pathname.split('/').pop();
    const user = state.users.find(candidate => candidate.id === id);
    const body = route.request().postDataJSON() as { disabled?: boolean } | null;
    if (user && body && typeof body.disabled === 'boolean') user.disabled = body.disabled;
    return json(route, user ?? {});
  });
  await page.route('**/api/auth/users/*/reset-password', route => {
    record(route);
    return json(route, { temporaryPassword: ['Tmp', 'pilot', 'once'].join('-') });
  });
  await page.route('**/api/auth/users/*/sessions', route => json(route, []));
  return { state, runners, calls };
}

/** Grows the viewport to the page's full scroll height, so one screenshot holds the whole Access page. */
export async function expandToFullPage(page: Page, width: number, height: number): Promise<void> {
  await page.setViewportSize({ width, height });
  const extra = await page.getByTestId('access-area').evaluate(element => {
    let grow = 0;
    for (let node: Element | null = element; node; node = node.parentElement) {
      const overflow = getComputedStyle(node).overflowY;
      if ((overflow === 'auto' || overflow === 'scroll') && node.scrollHeight > node.clientHeight) grow += node.scrollHeight - node.clientHeight;
    }
    return grow + Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
  });
  if (extra > 0) await page.setViewportSize({ width, height: height + extra + 8 });
}
