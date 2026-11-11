import { test, expect, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const resultDir = process.env.JOB_RESULTS_DIR ?? 'test-results';
const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
const oneTimeToken = ['sat', 'tok_e2e', 'one-time-value'].join('.');
const enrollmentCode = ['enr', 'e2e', 'one-time-code'].join('.');
const created = '2026-10-20T09:00:00Z';

const roles = [
  { id: 'owner', displayName: 'Owner', description: 'Break-glass role of the installation. Holds every permission.', permissions: [], bindableScopes: ['workspace'], serviceAccountBindable: false },
  { id: 'admin', displayName: 'Admin', description: 'Runs the workspace or a project: settings, operation, production deploys, secrets.', permissions: [], bindableScopes: ['workspace', 'project'], serviceAccountBindable: true },
  { id: 'developer', displayName: 'Developer', description: 'Works on cards, runs, chats, and documents; deploys outside production.', permissions: [], bindableScopes: ['workspace', 'project', 'environment'], serviceAccountBindable: true },
  { id: 'operator-agent', displayName: 'Operator agent', description: 'Operates the board for a coding agent: reads, lane moves, runs, batch gate, review step models, and stage deploys; no secrets, production approval, access, or installation settings.', permissions: [], bindableScopes: ['workspace', 'project'], serviceAccountBindable: true, userBindable: false },
  { id: 'contributor', displayName: 'Product', description: 'Product role: writes and accepts cards in assigned projects.', permissions: [], bindableScopes: ['project'], serviceAccountBindable: false },
  { id: 'viewer', displayName: 'Viewer', description: 'Reads everything in its scope, secret values excluded.', permissions: [], bindableScopes: ['workspace', 'project', 'environment'], serviceAccountBindable: true },
  { id: 'approver', displayName: 'Approver', description: 'Approves or rejects production releases in its scope.', permissions: [], bindableScopes: ['workspace', 'project', 'environment'], serviceAccountBindable: false },
];

function accessState() {
  return {
    roles,
    permissions: [],
    iamVersion: 1,
    users: [
      { id: 'usr_owner', username: 'robert', displayName: 'Robert', role: 'owner', projects: [], disabled: false, mustChangePassword: false,
        hasFactor: true, factorChangedAt: created },
      { id: 'usr_product', username: 'product.lead', displayName: 'Product Lead', role: 'contributor', projects: ['PROJ-001'], disabled: false, mustChangePassword: false },
    ],
    groups: [{ id: 'grp_web', name: 'Team Website', description: 'Everyone on the website', members: ['usr_product'], createdAt: created }],
    serviceAccounts: [
      { id: 'sa_deploy', name: 'deploy-bot', description: 'Stage deploys from CI', createdAt: created, tokens: [] },
      { id: 'sa_tok_build', name: 'build bot', description: 'Migrated API token.', createdAt: created, origin: 'api-token', createdBy: 'usr_owner', tokens: [
        { id: 'tok_build', name: 'build bot', createdAt: created, expiresAt: '2026-11-19T09:00:00Z', lastUsedAt: null, revokedAt: null },
      ] },
    ],
    bindings: [
      { id: 'rb_owner', principalKind: 'user', principalId: 'usr_owner', role: 'owner', scope: { kind: 'workspace' }, source: 'migration', createdAt: created },
      { id: 'rb_product', principalKind: 'user', principalId: 'usr_product', role: 'contributor', scope: { kind: 'project', project: 'PROJ-001' }, source: 'migration', createdAt: created },
      { id: 'rb_approver', principalKind: 'user', principalId: 'usr_product', role: 'approver', scope: { kind: 'environment', project: 'PROJ-001', environment: 'production' }, source: 'migration', createdAt: created },
      { id: 'rb_group', principalKind: 'group', principalId: 'grp_web', role: 'viewer', scope: { kind: 'project', project: 'PROJ-002' }, source: 'manual', createdAt: created },
      { id: 'rb_bot', principalKind: 'service-account', principalId: 'sa_deploy', role: 'developer', scope: { kind: 'environment', project: 'PROJ-001', environment: 'stage' }, source: 'manual', createdAt: created },
      { id: 'rb_build', principalKind: 'service-account', principalId: 'sa_tok_build', role: 'operator-agent', scope: { kind: 'workspace' }, source: 'migration', createdAt: created },
    ],
    invitations: [] as Array<{ id: string; userId: string; expiresAt: string; used: boolean }>,
  };
}

async function stubNetworkedOwner(page: Page): Promise<ReturnType<typeof accessState>> {
  const state = accessState();
  const owner = { id: 'usr_owner', username: 'robert', displayName: 'Robert', role: 'owner', projects: [], disabled: false, mustChangePassword: false, hasFactor: true };
  const status = { profile: 'networked', bootstrapRequired: false, authenticated: true, user: owner };
  await page.route('**/hubs/v1/studio/negotiate?*', route => json(route, {
    negotiateVersion: 1, connectionId: 'access-e2e', connectionToken: 'access-e2e',
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
    ? json(route, [
      { id: 'PROJ-001', displayName: 'Website', shortCode: 'WEB', workspaceId: 'ws_main', storageLocation: '/w/website', sortOrder: 0 },
      { id: 'PROJ-002', displayName: 'Mobile App', shortCode: 'APP', workspaceId: 'ws_main', storageLocation: '/w/app', sortOrder: 1 },
    ])
    : route.continue());
  await page.route('**/api/auth/iam', route => json(route, state));
  await page.route('**/api/auth/iam/operator-approvals', route => json(route, []));
  // Operator agents and standing grants have their own spec (operator-agents.spec.ts); this server answers without them.
  await page.route(url => url.pathname.startsWith('/api/auth/iam/operator-agents') || url.pathname.startsWith('/api/auth/audit/')
    || url.pathname === '/api/auth/iam/operator-catalogue' || url.pathname.startsWith('/api/auth/iam/standing-grants'),
    route => json(route, { message: 'Not found' }, 404));
  await page.route('**/api/auth/runner-enrollments', route => route.request().method() === 'POST'
    ? json(route, { enrollmentCode, name: 'build-host', scopes: [], expiresAt: '2026-10-20T09:15:00Z' })
    : json(route, []));
  await page.route('**/api/auth/runners', route => json(route, [
    { id: 'runner_coding', name: 'coding-host', createdAt: created, revokedAt: null, credentials: [{ id: 'cred_1', scopes: ['runner.claim'] }] },
  ]));
  await page.route('**/api/auth/invitations', async route => {
    const body = route.request().postDataJSON() as { username: string; displayName: string; bindings: Array<{ role: string; scope: object }> };
    const user = { id: 'usr_new', username: body.username, displayName: body.displayName || body.username, role: 'operator', projects: ['PROJ-002'], disabled: false, mustChangePassword: true };
    state.users.push(user);
    state.invitations.push({ id: 'inv_new', userId: user.id, expiresAt: '2026-10-21T09:00:00Z', used: false });
    body.bindings.forEach((grant, index) => state.bindings.push({
      id: `rb_new_${index}`, principalKind: 'user', principalId: user.id, role: grant.role, scope: grant.scope as never, source: 'role-assignment', createdAt: created,
    }));
    await json(route, { user, signInLink: '/?invite=inv.e2e-invite', expiresAt: '2026-10-21T09:00:00Z' });
  });
  await page.route('**/api/auth/iam/service-accounts/sa_deploy/tokens', route => {
    state.serviceAccounts[0].tokens = [{ id: 'tok_e2e', name: 'deploy-bot token', createdAt: created, expiresAt: '2027-01-18T09:00:00Z', lastUsedAt: null, revokedAt: null }] as never;
    return json(route, { serviceAccountId: 'sa_deploy', tokenId: 'tok_e2e', token: oneTimeToken, expiresAt: '2027-01-18T09:00:00Z' });
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

test('Owner sees factor changes and verifies the current factor before replacement', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await stubNetworkedOwner(page);
  await page.route('**/api/auth/audit/factors', route => json(route, [{
    sequence: 1, at: created, actor: 'human:usr_owner', session: 'session-1',
    operation: 'factor.bound', target: 'user:usr_owner',
  }]));
  await page.route('**/api/auth/step-up', route => json(route, {}));
  await page.route('**/api/auth/totp/start', route => json(route, { secret: 'TESTSECRET' }));
  const access = await openAccess(page);
  await expect(access.getByTestId('access-user-robert')).toContainText('Registered');
  await expect(access.getByTestId('factor-activity')).toContainText('factor.bound');
  mkdirSync(resultDir, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await access.getByTestId('access-user-robert').scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(resultDir, `owner-factor-access-${theme}.png`) });
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await access.getByTestId('access-user-robert').scrollIntoViewIfNeeded();
  await page.screenshot({ path: join(resultDir, 'owner-factor-access-390px.png') });
  await page.setViewportSize({ width: 1440, height: 900 });
  await access.getByTestId('session-manager-toggle').last().click();
  await access.getByRole('button', { name: 'Add authenticator' }).click();
  const challenge = access.getByTestId('enrollment-step-up');
  await expect(challenge).toBeVisible();
  await page.screenshot({ path: join(resultDir, 'owner-factor-step-up.png') });
  await expect(access.getByTestId('session-totp-secret')).toHaveCount(0);
  await challenge.getByLabel('Authenticator or recovery code').fill('123456');
  await challenge.getByRole('button', { name: 'Verify', exact: true }).click();
  await expect(access.getByTestId('session-totp-secret')).toHaveText('TESTSECRET');
});

test('owner sees users, groups, service accounts with API tokens, runners, and role bindings per scope', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await stubNetworkedOwner(page);
  const access = await openAccess(page);

  await expect(access.getByTestId('access-summary')).toContainText('6 role bindings');
  await expect(access.getByTestId('access-user-product.lead')).toContainText('Product · Website');
  await expect(access.getByTestId('access-user-product.lead')).toContainText('Viewer · Mobile App (group)');
  await expect(access.getByTestId('access-scope-workspace')).toContainText('Owner');
  await expect(access.getByTestId('access-scope-environment:proj-001/production')).toContainText('Approver');
  await expect(access.getByTestId('access-scope-environment:proj-001/stage')).toContainText('deploy-bot');
  await expect(access.getByTestId('access-group-grp_web')).toContainText('Product Lead');
  await expect(access.getByTestId('access-service-account-sa_tok_build')).toContainText('API token');
  await expect(access.getByTestId('access-runner-runner_coding')).toContainText('coding-host');

  mkdirSync(resultDir, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await access.getByTestId('access-summary').scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(resultDir, `access-area-1440-${theme}.png`) });
    await access.getByTestId('access-bindings').scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(resultDir, `access-area-1440-${theme}-bindings.png`) });
  }
});

test('an operator API token shows its operator-agent role in both themes', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await stubNetworkedOwner(page);
  const access = await openAccess(page);

  const token = access.getByTestId('access-service-account-sa_tok_build');
  await expect(token).toContainText('API token');
  await expect(access.getByTestId('access-api-token-role-sa_tok_build')).toContainText('Role: Operator agent');
  await expect(token).toContainText('Operator agent · Workspace');
  await expect(token).not.toContainText('Admin');

  mkdirSync(resultDir, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await token.scrollIntoViewIfNeeded();
    await token.screenshot({ path: join(resultDir, `access-operator-token-role-${theme}.png`) });
    await page.screenshot({ path: join(resultDir, `access-operator-token-role-1440-${theme}.png`) });
  }
});

test('owner verifies a second factor before revoking another user session', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await stubNetworkedOwner(page);
  let stepUpCount = 0;
  let revokeCount = 0;
  await page.route('**/api/auth/users/usr_product/sessions', route => json(route, [{
    id: 'session-product', device: 'Product browser', address: 'Example address',
    createdAt: created, lastUseAt: created, current: false,
  }]));
  await page.route('**/api/auth/step-up', route => {
    expect(route.request().postDataJSON()).toEqual({ code: '123456' });
    stepUpCount++;
    return json(route, { verified: true });
  });
  await page.route('**/api/auth/users/usr_product/sessions/revoke-others', route => {
    revokeCount++;
    return json(route, { revoked: true });
  });
  const access = await openAccess(page);
  await access.getByTestId('access-user-product.lead').getByTestId('session-manager-toggle').click();
  await access.getByTestId('access-user-product.lead').getByRole('button', { name: 'Revoke all other sessions' }).click();
  const challenge = access.getByTestId('session-step-up');
  await expect(challenge).toBeVisible();
  expect(revokeCount).toBe(0);
  mkdirSync(resultDir, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: join(resultDir, `session-step-up-${theme}--mocked.png`) });
  }
  await challenge.getByLabel('Authenticator or recovery code').fill('123456');
  await challenge.getByRole('button', { name: 'Verify and revoke' }).click();
  await expect(challenge).toHaveCount(0);
  expect(stepUpCount).toBe(1);
  expect(revokeCount).toBe(1);
});

test('owner invites a developer through the invitation flow and issues a service-account token once', async ({ page }) => {
  await page.setViewportSize({ width: 1920, height: 1080 });
  await stubNetworkedOwner(page);
  const access = await openAccess(page);

  const invite = access.getByTestId('access-invite-form');
  await invite.getByTestId('access-invite-username').fill('new.developer');
  await invite.getByTestId('access-invite-display-name').fill('New Developer');
  await invite.getByTestId('access-grant-project').selectOption('PROJ-002');
  await invite.getByTestId('access-grant-role').selectOption('developer');
  await invite.getByTestId('access-invite-submit').click();
  await expect(invite.getByTestId('access-invite-link')).toContainText('/?invite=inv.e2e-invite');
  await expect(access.getByTestId('access-user-new.developer')).toContainText('Developer · Mobile App');
  await expect(access.getByTestId('access-user-new.developer')).toContainText('Invited');

  await access.getByTestId('access-token-create-sa_deploy').click();
  await expect(access.getByTestId('access-token-issued')).toContainText(oneTimeToken);
  await setTheme(page, 'dark');
  await page.screenshot({ path: join(resultDir, 'access-area-1920-dark-invite-and-token.png'), fullPage: true });
  await access.getByRole('button', { name: 'Done' }).click();
  await expect(access).not.toContainText(oneTimeToken);
  await expect(access.getByTestId('access-service-account-sa_deploy')).toContainText('deploy-bot token');

  await access.getByTestId('access-enrollment-name').fill('build-host');
  await access.getByTestId('access-enrollment-create').click();
  await expect(access.getByTestId('access-enrollment-code')).toContainText(enrollmentCode);
  await access.getByTestId('access-runners').scrollIntoViewIfNeeded();
  await setTheme(page, 'light');
  await page.screenshot({ path: join(resultDir, 'access-area-1920-light-runners.png'), fullPage: true });
});

test('a denied reload clears cached access data and the issued token', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await stubNetworkedOwner(page);
  const access = await openAccess(page);
  await access.getByTestId('access-token-create-sa_deploy').click();
  await expect(access.getByTestId('access-token-issued')).toContainText(oneTimeToken);

  await page.route('**/api/auth/iam', route => json(route, { message: 'Denied Access' }, 403));
  await access.getByTestId('access-reload').click();
  await expect(access.getByTestId('access-error')).toContainText('cannot view access settings');
  await expect(access.getByTestId('access-users')).toHaveCount(0);
  await expect(access.getByTestId('access-token-issued')).toHaveCount(0);
  await expect(access).not.toContainText(oneTimeToken);

  mkdirSync(resultDir, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: join(resultDir, `access-area-denied-1440-${theme}.png`) });
  }
});

test('owner binds runners to projects and sees credential expiry and pre-binding notices', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.clock.setFixedTime(new Date('2026-11-02T09:00:00Z'));
  await stubNetworkedOwner(page);
  const runners = [
    { id: 'runner_web', name: 'web-host', createdAt: created, revokedAt: null, projects: ['PROJ-001'],
      credentials: [{ id: 'cred_web', scopes: ['runner.claim'], expiresAt: '2026-11-30T09:00:00Z' }] },
    { id: 'runner_soon', name: 'review-host', createdAt: created, revokedAt: null, projects: ['PROJ-002'],
      credentials: [{ id: 'cred_soon', scopes: ['runner.claim'], expiresAt: '2026-11-06T09:00:00Z' }] },
    { id: 'runner_old', name: 'old-host', createdAt: created, revokedAt: null, projects: ['*'],
      projectsNotice: 'Enrolled before project binding: holds all projects until you choose its projects.',
      credentials: [{ id: 'cred_old', scopes: ['runner.claim'], expiresAt: null,
        expiryNotice: 'No expiry: issued before the 30-day default. Rotate it to adopt the default.' }] },
  ];
  const enrollmentBodies: unknown[] = [];
  const projectUpdates: unknown[] = [];
  await page.route('**/api/auth/runners', route => json(route, runners));
  await page.route('**/api/auth/runners/runner_old/projects', async route => {
    projectUpdates.push(route.request().postDataJSON());
    runners[2] = { ...runners[2], projects: ['PROJ-002'], projectsNotice: undefined as never };
    await json(route, runners[2]);
  });
  await page.route('**/api/auth/runner-enrollments', route => {
    if (route.request().method() !== 'POST') return json(route, []);
    enrollmentBodies.push(route.request().postDataJSON());
    return json(route, { enrollmentCode, name: 'build-host', scopes: [], expiresAt: '2026-11-02T09:15:00Z', projects: ['PROJ-001'] });
  });
  const access = await openAccess(page);
  const list = access.getByTestId('access-runners');

  await expect(list.getByTestId('access-runner-projects-runner_web')).toContainText('Website');
  await expect(list.getByTestId('access-runner-expiry-runner_web')).toContainText('Expires 30 Nov 2026');
  await expect(list.getByTestId('access-runner-expiry-runner_soon')).toContainText('Expires in 4 days');
  await expect(list.getByTestId('access-runner-projects-runner_old')).toContainText('Before binding');
  await expect(list.getByTestId('access-runner-expiry-runner_old')).toContainText('No expiry');
  await expect(list.getByTestId('access-enrollment-no-project')).toContainText('claims and reads nothing');

  mkdirSync(resultDir, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await list.scrollIntoViewIfNeeded();
    await list.screenshot({ path: join(resultDir, `access-runner-binding-1440-${theme}.png`) });
  }

  await list.getByTestId('access-enrollment-name').fill('build-host');
  await list.getByTestId('access-enrollment-project-PROJ-001').check();
  await list.getByTestId('access-enrollment-create').click();
  await expect(list.getByTestId('access-enrollment-code')).toContainText('expires 30 days after enrollment');
  expect(enrollmentBodies).toEqual([{ name: 'build-host', projects: ['PROJ-001'] }]);

  await list.getByTestId('access-runner-edit-projects-runner_old').click();
  await list.getByTestId('access-runner-project-runner_old-PROJ-002').check();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await list.getByTestId('access-runner-project-editor-runner_old').scrollIntoViewIfNeeded();
    await list.screenshot({ path: join(resultDir, `access-runner-project-editor-1440-${theme}.png`) });
  }
  await list.getByTestId('access-runner-save-projects-runner_old').click();
  await expect(list.getByTestId('access-runner-project-editor-runner_old')).toHaveCount(0);
  expect(projectUpdates).toEqual([{ projects: ['PROJ-002'] }]);
  await expect(list.getByTestId('access-runner-projects-runner_old')).toContainText('Mobile App');
  await expect(list.getByTestId('access-runner-projects-runner_old')).not.toContainText('Before binding');
});
