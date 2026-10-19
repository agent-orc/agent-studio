import { test, expect, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const resultDir = process.env.JOB_RESULTS_DIR ?? 'test-results';
const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
const oneTimeToken = ['sat', 'tok_e2e', 'one-time-value'].join('.');
const created = '2026-10-20T09:00:00Z';

const roles = [
  { id: 'owner', displayName: 'Owner', description: 'Break-glass role of the installation. Holds every permission.', permissions: [], bindableScopes: ['workspace'], serviceAccountBindable: false },
  { id: 'admin', displayName: 'Admin', description: 'Runs the workspace or a project: settings, operation, production deploys, secrets.', permissions: [], bindableScopes: ['workspace', 'project'], serviceAccountBindable: true },
  { id: 'developer', displayName: 'Developer', description: 'Works on cards, runs, chats, and documents; deploys outside production.', permissions: [], bindableScopes: ['workspace', 'project', 'environment'], serviceAccountBindable: true },
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
      { id: 'usr_owner', username: 'robert', displayName: 'Robert', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
      { id: 'usr_product', username: 'product.lead', displayName: 'Product Lead', role: 'contributor', projects: ['PROJ-001'], disabled: false, mustChangePassword: false },
    ],
    groups: [{ id: 'grp_web', name: 'Team Website', description: 'Everyone on the website', members: ['usr_product'], createdAt: created }],
    serviceAccounts: [{ id: 'sa_deploy', name: 'deploy-bot', description: 'Stage deploys from CI', createdAt: created, tokens: [] }],
    bindings: [
      { id: 'rb_owner', principalKind: 'user', principalId: 'usr_owner', role: 'owner', scope: { kind: 'workspace' }, source: 'migration', createdAt: created },
      { id: 'rb_product', principalKind: 'user', principalId: 'usr_product', role: 'contributor', scope: { kind: 'project', project: 'PROJ-001' }, source: 'migration', createdAt: created },
      { id: 'rb_approver', principalKind: 'user', principalId: 'usr_product', role: 'approver', scope: { kind: 'environment', project: 'PROJ-001', environment: 'production' }, source: 'migration', createdAt: created },
      { id: 'rb_group', principalKind: 'group', principalId: 'grp_web', role: 'viewer', scope: { kind: 'project', project: 'PROJ-002' }, source: 'manual', createdAt: created },
      { id: 'rb_bot', principalKind: 'service-account', principalId: 'sa_deploy', role: 'developer', scope: { kind: 'environment', project: 'PROJ-001', environment: 'stage' }, source: 'manual', createdAt: created },
    ],
    invitations: [] as Array<{ id: string; userId: string; expiresAt: string; used: boolean }>,
  };
}

async function stubNetworkedOwner(page: Page): Promise<ReturnType<typeof accessState>> {
  const state = accessState();
  const owner = { id: 'usr_owner', username: 'robert', displayName: 'Robert', role: 'owner', projects: [], disabled: false, mustChangePassword: false };
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
  await page.route('**/api/v1/studio/auth/status', route => json(route, status));
  await page.route('**/api/projects', route => route.request().method() === 'GET'
    ? json(route, [
      { id: 'PROJ-001', displayName: 'Website', shortCode: 'WEB', workspaceId: 'ws_main', storageLocation: '/w/website', sortOrder: 0 },
      { id: 'PROJ-002', displayName: 'Mobile App', shortCode: 'APP', workspaceId: 'ws_main', storageLocation: '/w/app', sortOrder: 1 },
    ])
    : route.continue());
  await page.route('**/api/auth/iam', route => json(route, state));
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

test('owner sees users, groups, service accounts, and role bindings per scope', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await stubNetworkedOwner(page);
  const access = await openAccess(page);

  await expect(access.getByTestId('access-summary')).toContainText('5 role bindings');
  await expect(access.getByTestId('access-user-product.lead')).toContainText('Product · Website');
  await expect(access.getByTestId('access-user-product.lead')).toContainText('Viewer · Mobile App (group)');
  await expect(access.getByTestId('access-scope-workspace')).toContainText('Owner');
  await expect(access.getByTestId('access-scope-environment:proj-001/production')).toContainText('Approver');
  await expect(access.getByTestId('access-scope-environment:proj-001/stage')).toContainText('deploy-bot');
  await expect(access.getByTestId('access-group-grp_web')).toContainText('Product Lead');

  mkdirSync(resultDir, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await access.getByTestId('access-summary').scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(resultDir, `access-area-1440-${theme}.png`) });
    await access.getByTestId('access-bindings').scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(resultDir, `access-area-1440-${theme}-bindings.png`) });
  }
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
});
