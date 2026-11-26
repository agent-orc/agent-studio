import { test, expect, type Page, type Route } from '@playwright/test';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

// AGT-4030 Dossier capture (docs/look-and-feel-overhaul). Runs from a copy of this
// folder outside the repository through run.sh, against the isolated prebuilt
// stack of frontend/playwright.config.ts (profile standard). No spec is added to
// frontend/e2e. Readiness follows scripts/stable-frontend-boot-probe.mjs: page
// errors and console errors are recorded and written next to the images.
//
// The Access page reads its state from the fixture of
// frontend/e2e/settings/access-area.spec.ts (copied below, unchanged values), so
// it shows the same users, groups, service accounts and bindings that spec
// asserts. Every other settings page shows what the isolated backend returns.

const OUT = process.env.CAPTURE_DIR!;
const report: Record<string, { pageErrors: string[]; consoleErrors: string[]; url: string }> = {};
const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
const created = '2026-10-20T09:00:00Z';

function watch(page: Page, name: string) {
  const entry = { pageErrors: [] as string[], consoleErrors: [] as string[], url: '' };
  report[name] = entry;
  page.on('pageerror', e => entry.pageErrors.push(String(e.message ?? e)));
  page.on('console', m => { if (m.type() === 'error') entry.consoleErrors.push(m.text().slice(0, 300)); });
  return entry;
}

async function theme(page: Page, t: 'light' | 'dark') {
  await page.evaluate(v => {
    document.documentElement.dataset['studioTheme'] = v;
    try { localStorage.setItem('atp.studio.theme', v); } catch { /* ignore */ }
  }, t);
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', t);
  await page.waitForTimeout(400);
}

async function settle(page: Page) {
  await page.waitForLoadState('networkidle', { timeout: 15_000 }).catch(() => undefined);
  await expect(page.getByTestId('offline-banner')).toBeHidden({ timeout: 30_000 }).catch(() => undefined);
  await page.waitForTimeout(800);
}

const roles = [
  { id: 'owner', displayName: 'Owner', description: 'Break-glass role of the installation. Holds every permission.', permissions: [], bindableScopes: ['workspace'], serviceAccountBindable: false },
  { id: 'admin', displayName: 'Admin', description: 'Runs the workspace or a project: settings, operation, production deploys, secrets.', permissions: [], bindableScopes: ['workspace', 'project'], serviceAccountBindable: true },
  { id: 'developer', displayName: 'Developer', description: 'Works on cards, runs, chats, and documents; deploys outside production.', permissions: [], bindableScopes: ['workspace', 'project', 'environment'], serviceAccountBindable: true },
  { id: 'operator-agent', displayName: 'Operator agent', description: 'Operates the board for a coding agent: reads, lane moves, runs, batch gate, review step models, and stage deploys; no secrets, production approval, access, or installation settings.', permissions: [], bindableScopes: ['workspace', 'project'], serviceAccountBindable: true, userBindable: false },
  { id: 'contributor', displayName: 'Product', description: 'Product role: writes and accepts cards in assigned projects.', permissions: [], bindableScopes: ['project'], serviceAccountBindable: false },
  { id: 'viewer', displayName: 'Viewer', description: 'Reads everything in its scope, secret values excluded.', permissions: [], bindableScopes: ['workspace', 'project', 'environment'], serviceAccountBindable: true },
  { id: 'approver', displayName: 'Approver', description: 'Approves or rejects production releases in its scope.', permissions: [], bindableScopes: ['workspace', 'project', 'environment'], serviceAccountBindable: false },
];

async function stubAccess(page: Page) {
  const state = {
    roles, permissions: [], iamVersion: 1,
    users: [
      { id: 'usr_owner', username: 'robert', displayName: 'Robert', role: 'owner', projects: [], disabled: false, mustChangePassword: false, hasFactor: true, factorChangedAt: created },
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
    invitations: [],
  };
  const owner = { id: 'usr_owner', username: 'robert', displayName: 'Robert', role: 'owner', projects: [], disabled: false, mustChangePassword: false, hasFactor: true };
  const status = { profile: 'networked', bootstrapRequired: false, authenticated: true, user: owner };
  await page.route('**/hubs/v1/studio/negotiate?*', route => json(route, {
    negotiateVersion: 1, connectionId: 'capture', connectionToken: 'capture',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  }));
  await page.routeWebSocket('**/hubs/v1/studio*', socket => {
    socket.onMessage(message => { if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send('{}\u001e'); });
  });
  await page.route('**/api/auth/status', route => json(route, status));
  await page.route('**/api/auth/sessions', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, status));
  await page.route('**/api/projects', route => route.request().method() === 'GET'
    ? json(route, [
      { id: 'PROJ-001', displayName: 'Website', shortCode: 'WEB', workspaceId: 'ws_main', storageLocation: '/w/website', sortOrder: 0 },
      { id: 'PROJ-002', displayName: 'Mobile App', shortCode: 'APP', workspaceId: 'ws_main', storageLocation: '/w/app', sortOrder: 1 },
    ]) : route.continue());
  await page.route('**/api/auth/iam', route => json(route, state));
  await page.route('**/api/auth/iam/operator-approvals', route => json(route, []));
  await page.route('**/api/auth/users', route => json(route, state.users));
  await page.route(url => url.pathname.startsWith('/api/auth/iam/operator-agents') || url.pathname.startsWith('/api/auth/iam/operator-approvals')
    || url.pathname.startsWith('/api/auth/audit/')
    || url.pathname === '/api/auth/iam/operator-catalogue' || url.pathname.startsWith('/api/auth/iam/standing-grants'),
  route => json(route, { message: 'Not found' }, 404));
  await page.route('**/api/auth/runner-enrollments', route => json(route, []));
  await page.route('**/api/auth/runners', route => json(route, [
    { id: 'runner_coding', name: 'coding-host', createdAt: created, revokedAt: null, credentials: [{ id: 'cred_1', scopes: ['runner.claim'] }] },
  ]));
}

test.describe.configure({ mode: 'serial' });
test.afterAll(() => writeFileSync(join(OUT, 'capture-report.json'), JSON.stringify(report, null, 1)));

test('access page, first screen and full length, both themes', async ({ page }) => {
  mkdirSync(OUT, { recursive: true });
  const entry = watch(page, 'access');
  await page.setViewportSize({ width: 1440, height: 900 });
  await stubAccess(page);
  await page.goto('/#/workspace/settings/access', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  await expect(page.getByTestId('workspace-settings-rail-access')).toBeAttached({ timeout: 30_000 });
  const access = page.getByTestId('access-area');
  await expect(access.getByRole('heading', { name: 'Users' })).toBeVisible();
  await settle(page);
  entry.url = page.url();
  for (const t of ['light', 'dark'] as const) {
    await theme(page, t);
    await page.screenshot({ path: join(OUT, `access-first-screen-${t}.png`) });
  }
  // The whole page at once: a tall viewport so the scroll owner shows every section.
  await page.setViewportSize({ width: 1440, height: 4200 });
  await page.waitForTimeout(600);
  for (const t of ['light', 'dark'] as const) {
    await theme(page, t);
    const box = await access.boundingBox();
    await page.screenshot({ path: join(OUT, `access-full-${t}.png`), clip: { x: 0, y: 0, width: 1440, height: Math.min(4200, Math.ceil(box!.y + box!.height + 24)) } });
  }
  const headings = await access.locator('h2, h3, h4').allInnerTexts();
  const buttons = await access.locator('button').count();
  const tables = await access.locator('table').count();
  const selects = await access.locator('select').count();
  const inputs = await access.locator('input').count();
  // Which font the browser actually rendered (the Studio stack is 'Segoe UI', system-ui).
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('DOM.enable'); await cdp.send('CSS.enable');
  const { root } = await cdp.send('DOM.getDocument', { depth: -1 });
  const { nodeId } = await cdp.send('DOM.querySelector', { nodeId: root.nodeId, selector: '[data-testid="access-area"] h3' });
  const rendered = await cdp.send('CSS.getPlatformFontsForNode', { nodeId });
  const declared = await access.locator('h3').first().evaluate(el => getComputedStyle(el).fontFamily);
  writeFileSync(join(OUT, 'access-facts.json'), JSON.stringify({ headings, buttons, tables, selects, inputs, font: { declared, rendered: rendered.fonts } }, null, 1));
});

const SECTIONS = ['task-server', 'remote-hosts', 'caps', 'prompts', 'retention', 'operations', 'workspaces', 'credentials', 'secrets', 'github', 'orchestrator', 'updates', 'first-run', 'appearance'];
for (const key of SECTIONS) {
  test(`settings ${key}, first screen, both themes`, async ({ page }) => {
    const entry = watch(page, key);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`/#/workspace/settings/${key}`, { waitUntil: 'domcontentloaded', timeout: 30_000 });
    await expect(page.getByTestId(`workspace-settings-rail-${key}`)).toBeAttached({ timeout: 30_000 });
    await settle(page);
    await page.waitForTimeout(1200);
    entry.url = page.url();
    for (const t of ['light', 'dark'] as const) {
      await theme(page, t);
      await page.screenshot({ path: join(OUT, `settings-${key}-${t}.png`) });
    }
  });
}
