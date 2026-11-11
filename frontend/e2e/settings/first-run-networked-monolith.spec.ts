import { mkdirSync } from 'node:fs';
import { basename, join } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const resultDir = process.env.JOB_RESULTS_DIR ?? 'test-results';

test('networked monolith shows Runner setup before chat can send', async ({ page }) => {
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace).toBeTruthy();
  const before = await page.request.get('/api/auth/status');
  expect(before.ok()).toBeTruthy();
  const status = await before.json() as { profile: string; bootstrapRequired: boolean };
  expect(status.profile).toBe('networked');
  const password = ['first-run', 'password', 'long-enough'].join('-');
  const session = status.bootstrapRequired
    ? await page.request.post('/api/auth/bootstrap', { headers: { 'Sec-Fetch-Site': 'same-origin' }, data: {
      username: 'first-run-owner', displayName: 'Owner', password,
      bootstrapCode: `${basename(workspace!)}-owner-code`,
    } })
    : await page.request.post('/api/auth/login', { headers: { 'Sec-Fetch-Site': 'same-origin' },
      data: { username: 'first-run-owner', password } });
  expect(session.ok(), `${session.status()}: ${await session.text()}`).toBeTruthy();
  // The test proxy uses HTTP, so verify the Secure session through the API
  // client and supply browser read fixtures for the chat surface.
  const token = session.headers()['set-cookie']?.match(/__Host-agentstudio-session=([^;,\s]+)/)?.[1];
  expect(token).toBeTruthy();
  const cookie = `__Host-agentstudio-session=${token}`;
  const authenticated = await page.request.get('/api/auth/status', { headers: { Cookie: cookie } });
  const authenticatedStatus = await authenticated.json();
  expect(authenticatedStatus).toMatchObject({ profile: 'networked', authenticated: true,
    user: { role: 'owner' } });
  await page.route('**/api/**', route => route.continue({
    headers: { ...route.request().headers(), Cookie: cookie },
  }));
  await page.route('**/api/v1/studio/board**', route => route.fulfill({ json: {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
    humanReview: [], escalated: [], completed: [], archive: [],
    gitStateAt: null, stale: false,
  } }));
  // The first-login factor enrollment is a separate flow. This fixture keeps
  // the real networked profile and session while opening the chat surface.
  await page.route('**/api/auth/status', route => route.fulfill({ json: {
    ...authenticatedStatus, secondFactorEnrollmentRequired: false,
  } }));

  mkdirSync(resultDir, { recursive: true });

  await page.route('**/api/watch-paths', route => route.fulfill({ json: [
    { name: 'chat-fixture', path: '/tmp/chat-fixture', rootPath: '/tmp/chat-fixture' },
  ] }));
  await page.route('**/api/v1/workspaces', route => route.fulfill({ json: [{
    id: 'workspace-chat-fixture', displayName: 'Chat fixture', sortOrder: 0, isDefault: true,
    projects: [{ id: 'chat-fixture', displayName: 'chat-fixture', shortCode: 'CF',
      workspaceId: 'workspace-chat-fixture', storageLocation: '/tmp/chat-fixture', archived: false, urls: [] }],
  }] }));
  await page.route('**/api/v1/studio/runner/status', route => route.fulfill({ json: { projects: {} } }));
  let ready = false;
  await page.route('**/api/runner/*/orchestrator-chat', route => {
    if (route.request().method() !== 'GET') return route.fallback();
    return route.fulfill({ json: { project: 'chat-fixture', turns: [],
      executionReadiness: ready
        ? { ready: true, code: 'runner-ready', reason: null }
        : { ready: false, code: 'runner-not-configured', reason: 'No Runner is configured for this project.' },
    } });
  });
  await page.addInitScript(() => sessionStorage.setItem('atp.studio.orchestratorOpen.v1', '0'));
  for (const theme of ['light', 'dark'] as const) {
    await page.goto('/');
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    await page.getByTestId('studio-titlebar-chat').click();
    await expect(page.locator('app-orchestrator-side-sheet.is-open')).toBeVisible();
    await expect.poll(() => page.getByTestId('orch-side-sheet').evaluate(el => el.getBoundingClientRect().width))
      .toBeGreaterThan(300);
    const setup = page.getByTestId('orch-chat-runner-setup');
    await expect(setup).toContainText('No Runner is configured');
    await expect(setup.getByRole('link', { name: 'Runner setup guide' })).toHaveAttribute('href', /linux-runner-host\.md$/);
    await expect(page.getByTestId('orch-side-sheet').locator('textarea').last()).toBeDisabled();
    await page.screenshot({ path: join(resultDir, `chat-runner-setup-${theme}.png`) });
  }
  ready = true;
  await page.reload();
  await dismissDevErrorDialog(page);
  await page.getByTestId('studio-titlebar-chat').click();
  await expect(page.locator('app-orchestrator-side-sheet.is-open')).toBeVisible();
  await expect.poll(() => page.getByTestId('orch-side-sheet').evaluate(el => el.getBoundingClientRect().width))
    .toBeGreaterThan(300);
  await expect(page.getByTestId('orch-chat-runner-setup')).toHaveCount(0);
  await expect(page.getByTestId('orch-side-sheet').locator('textarea').last()).toBeEnabled();
});

test('networked monolith verifies the owner and explains the Task Server topology', async ({ page }) => {
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace).toBeTruthy();
  const before = await page.request.get('/api/auth/status');
  expect(before.ok()).toBeTruthy();
  const status = await before.json() as { profile: string; bootstrapRequired: boolean };
  expect(status.profile).toBe('networked');
  const password = ['first-run', 'password', 'long-enough'].join('-');
  const session = status.bootstrapRequired
    ? await page.request.post('/api/auth/bootstrap', { headers: { 'Sec-Fetch-Site': 'same-origin' }, data: {
      username: 'first-run-owner', displayName: 'Owner', password,
      bootstrapCode: `${basename(workspace!)}-owner-code`,
    } })
    : await page.request.post('/api/auth/login', { headers: { 'Sec-Fetch-Site': 'same-origin' },
      data: { username: 'first-run-owner', password } });
  expect(session.ok(), `${session.status()}: ${await session.text()}`).toBeTruthy();
  const token = session.headers()['set-cookie']?.match(/__Host-agentstudio-session=([^;,\s]+)/)?.[1];
  expect(token).toBeTruthy();
  const cookie = `__Host-agentstudio-session=${token}`;
  const authenticated = await page.request.get('/api/auth/status', { headers: { Cookie: cookie } });
  expect(authenticated.ok()).toBeTruthy();
  const authenticatedStatus = await authenticated.json();
  expect(authenticatedStatus).toMatchObject({ profile: 'networked', authenticated: true,
    user: { role: 'owner' } });
  await page.route('**/api/**', route => route.continue({
    headers: { ...route.request().headers(), Cookie: cookie },
  }));
  await page.route('**/api/auth/status', route => route.fulfill({ json: {
    ...authenticatedStatus, secondFactorEnrollmentRequired: false,
  } }));
  // The fixture bypasses factor enrollment in the browser; model the 404 that
  // the monolith returns for this route after enrollment is complete.
  await page.route('**/api/v1/installation', route => route.fulfill({ status: 404 }));
  await page.route('**/api/v1/studio/runner/status', route => route.fulfill({ json: { projects: {} } }));
  await page.goto('/#/workspace/settings/first-run');
  await expect(page.getByTestId('first-run-owner')).toHaveAttribute('data-state', 'ready');
  const banner = page.getByTestId('first-run-task-server-unavailable');
  await expect(banner).toContainText('supports owner sign-in and second-factor enrollment');
  await expect(banner.getByRole('link', { name: 'Read the Task Server topology' }))
    .toHaveAttribute('href', /task-server-topology\.md$/);
  await dismissDevErrorDialog(page);
  // Factor enrollment is bypassed in this fixture, so its SignalR connection
  // shows an unrelated offline strip. Keep the review capture on the checklist.
  await page.locator('app-offline-banner').evaluate(element => (element as HTMLElement).style.display = 'none');
  mkdirSync(resultDir, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: join(resultDir, `first-run-monolith-${theme}.png`), fullPage: true });
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  await page.screenshot({ path: join(resultDir, 'first-run-monolith-390.png'), fullPage: true });
});
