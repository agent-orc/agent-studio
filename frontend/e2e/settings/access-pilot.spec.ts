import { test, expect, type Locator, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { contrastRatio } from '../helpers/contrast';
import { sampleColours, setTheme } from '../helpers/theme';
import { expandToFullPage, pilotEnrollmentCode, pilotRunners, pilotToken, stubPilotOwner } from './access-pilot.fixture';

/**
 * AGT-4029: Settings > Access as the pilot page of the UI standard. Every
 * route is mocked (access-pilot.fixture.ts), so the flows are deterministic
 * and the captures show the same installation as the "before" set. Captures
 * land in JOB_RESULTS_DIR: `after/` for the page, `patterns/` for the pattern
 * shots of docs/system/ui-standard/access-page-pilot.md.
 */
const results = process.env.JOB_RESULTS_DIR ?? 'test-results';
const DESKTOP = { width: 1440, height: 900 } as const;
const PHONE = { width: 390, height: 844 } as const;

async function openAccess(page: Page, viewport: { width: number; height: number } = DESKTOP): Promise<Locator> {
  await page.setViewportSize(viewport);
  await page.goto('/#/workspace/settings/access', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  const access = page.getByTestId('access-area');
  await expect(access.getByTestId('access-runners')).toBeVisible({ timeout: 30_000 });
  return access;
}

/** Switches the theme and lets colour transitions finish, so a capture never shows a half-way state. */
async function theme(page: Page, value: 'light' | 'dark'): Promise<void> {
  await setTheme(page, value);
  await page.waitForTimeout(400);
}

const rowOpen = (access: Locator, testId: string) => access.getByTestId(testId).getByRole('button', { name: /^Open details of/ });

test('runner renewal health and refused reuse are visible and the owner can turn renewal off', async ({ page }) => {
  test.setTimeout(150_000);
  await stubPilotOwner(page);
  const shots = join(results, 'runner-renewal');
  mkdirSync(shots, { recursive: true });
  const runner = pilotRunners().runners[0];
  const renewal = {
    enabled: true, installationEnabled: true, runnerEnabled: true,
    currentExpiresAt: '2026-12-02T09:00:00Z', lastRenewalAt: '2026-11-02T09:00:00Z',
    nextRenewalAt: '2026-11-22T09:00:00Z', failingSince: '2026-11-22T09:15:00Z',
    failureReason: 'The runner could not write its credential file.', lastReplayAt: '2026-11-02T09:01:00Z',
  };
  let enabled = true;
  await page.route('**/api/auth/runners', route => route.fulfill({ json: [{
    ...runner,
    renewal: { ...renewal, enabled, runnerEnabled: enabled, failingSince: enabled ? renewal.failingSince : null,
      nextRenewalAt: enabled ? renewal.nextRenewalAt : null },
  }] }));
  await page.route('**/api/auth/runners/runner_coding/renewal', route => {
    expect(route.request().method()).toBe('PUT');
    enabled = route.request().postDataJSON().enabled;
    return route.fulfill({ json: {} });
  });
  await page.route('**/api/v1/management/credential-inventory', route => route.fulfill({ json: {
    generatedAt: '2026-11-24T09:00:00Z',
    items: [{ id: 'runner-credential', category: 'service', hostId: runner.id, kind: 'service_credential',
      label: 'Coding runner credential', scopes: ['runner.claim'], state: 'expiring', required: false,
      expiresAt: renewal.currentExpiresAt, detail: 'Enrolled identity', affectedCards: [],
      lifetimeClass: 'long-lived', renewal }],
  } }));

  const access = await openAccess(page);
  await rowOpen(access, 'access-runner-runner_coding').click();
  const detail = page.getByTestId('access-runner-renewal');
  const section = page.getByRole('region', { name: 'Credential', exact: true });
  await expect(detail).toContainText('Current credential expires');
  await expect(detail).toContainText('Last renewal');
  await expect(detail).toContainText('Next renewal');
  await expect(detail).toContainText('Renewal failing since');
  await expect(detail).toContainText(renewal.failureReason);
  await expect(detail).toContainText('Old credential reused');
  for (const [viewport, size] of [[DESKTOP, 'desktop'], [PHONE, 'phone']] as const) {
    await page.setViewportSize(viewport);
    for (const mode of ['light', 'dark'] as const) {
      await theme(page, mode);
      await section.screenshot({ path: join(shots, `access-${size}-${mode}.png`) });
    }
  }
  await page.getByTestId('access-runner-renewal-toggle').click();
  await expect(page.getByTestId('access-runner-renewal-toggle')).toHaveText('Turn automatic renewal on');
  await expect(detail).toContainText('Unscheduled');

  await page.goto('/#/workspace/settings/credentials', { waitUntil: 'domcontentloaded' });
  const item = page.getByRole('article').filter({ hasText: 'Coding runner credential' });
  await expect(item).toContainText('Last renewal 2026-11-02');
  await expect(item).toContainText('Next renewal 2026-11-22');
  await expect(item).toContainText('Renewal failing since');
  await expect(item).toContainText('Studio refused a renewal from a retired credential');
  await expect(item.getByRole('button', { name: 'Open Access' })).toBeVisible();
  for (const [viewport, size] of [[DESKTOP, 'desktop'], [PHONE, 'phone']] as const) {
    await page.setViewportSize(viewport);
    for (const mode of ['light', 'dark'] as const) {
      await theme(page, mode);
      await item.screenshot({ path: join(shots, `inventory-${size}-${mode}.png`) });
    }
  }
});

test('a service account shows its bindings and tokens in one detail; a new token is shown once; a revoke is confirmed in place', async ({ page }) => {
  const { calls } = await stubPilotOwner(page);
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  const access = await openAccess(page);

  // The grid row is one line: who, what it may reach, state, its tokens.
  const row = access.getByTestId('access-service-account-sa_deploy');
  await expect(row).toContainText('deploy-bot');
  await expect(row).toContainText('Developer');
  await expect(row).toContainText('Website · stage');
  await expect(row).toContainText('2 tokens, next expires 1 Dec 2026');
  await expect(access.getByTestId('access-detail-panel')).toHaveCount(0);

  await rowOpen(access, 'access-service-account-sa_deploy').click();
  const panel = access.getByTestId('access-detail-panel');
  await expect(panel).toHaveAccessibleName('Service account: deploy-bot');
  await expect(row).toHaveAttribute('data-selected', 'true');
  // Bindings and tokens of the selected account, side by side in the one detail area.
  await expect(panel.getByTestId('access-binding-rb_bot_web')).toContainText('Website · stage');
  await expect(panel.getByTestId('access-binding-rb_bot_app')).toContainText('Mobile App · stage');
  await expect(panel.getByTestId('access-token-tok_ci')).toContainText('CI pipeline');
  await expect(panel.getByTestId('access-token-tok_hotfix')).toContainText('Hotfix runner');
  await expect(panel.getByTestId('access-token-tok_old')).toHaveCount(0);

  // Create: the secret appears once, in the marked place, with its own copy control.
  await panel.getByTestId('access-token-lifetime').selectOption('30');
  await panel.getByTestId('access-token-create-sa_deploy').click();
  const issued = panel.getByTestId('access-token-issued');
  await expect(issued).toContainText('Shown once');
  await expect(issued).toContainText('New token for deploy-bot');
  await expect(issued.getByTestId('access-token-issued-value')).toHaveText(pilotToken);
  await expect.poll(() => calls.at(-1)).toEqual({
    method: 'POST', path: '/api/auth/iam/service-accounts/sa_deploy/tokens',
    body: { name: 'deploy-bot token', expiresAt: '2026-12-24T09:00:00.000Z' },
  });
  await issued.getByTestId('access-token-issued-copy').click();
  await expect(issued.getByTestId('access-token-issued-copy')).toHaveText('Copied');
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(pilotToken);
  await expect(panel.getByTestId('access-token-tok_pilot')).toContainText('deploy-bot token');
  await issued.getByTestId('access-token-issued-done').click();
  await expect(access).not.toContainText(pilotToken);
  await expect(row).toContainText('3 tokens');

  // Revoke: the first press only asks; Cancel leaves the token alone.
  const before = calls.length;
  await panel.getByTestId('access-token-revoke-tok_ci').click();
  const confirm = panel.getByTestId('access-token-revoke-tok_ci-panel');
  await expect(confirm).toContainText('Anything that uses CI pipeline stops working at once');
  await expect(panel.getByTestId('access-token-revoke-tok_ci-cancel')).toBeFocused();
  await panel.getByTestId('access-token-revoke-tok_ci-cancel').click();
  await expect(confirm).toHaveCount(0);
  expect(calls.length).toBe(before);

  await panel.getByTestId('access-token-revoke-tok_ci').click();
  await panel.getByTestId('access-token-revoke-tok_ci-confirm').click();
  await expect(panel.getByTestId('access-token-tok_ci')).toHaveCount(0);
  await expect.poll(() => calls.at(-1)).toMatchObject({ method: 'DELETE', path: '/api/auth/iam/service-accounts/sa_deploy/tokens/tok_ci' });
  await expect(panel.getByTestId('access-token-tok_hotfix')).toBeVisible();
  await expect(row).toContainText('2 tokens');
});

test('a destructive row action leads to the confirmation in the detail, and Escape steps back one level at a time', async ({ page }) => {
  const { calls } = await stubPilotOwner(page);
  const access = await openAccess(page);

  await access.getByTestId('access-user-menu-tom.okafor').click();
  await page.getByTestId('access-user-menu-tom.okafor-item-disable').click();
  const panel = access.getByTestId('access-detail-panel');
  await expect(panel).toHaveAccessibleName('Person: Tom Okafor');
  const confirm = panel.getByTestId('access-user-toggle-tom.okafor-panel');
  await expect(confirm).toContainText('Open sessions of Tom Okafor end immediately');
  expect(calls).toEqual([]);

  await page.keyboard.press('Escape');
  await expect(confirm).toHaveCount(0);
  await expect(panel).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(panel).toHaveCount(0);
  await expect(page.getByTestId('workspace-settings-rail-access')).toBeAttached();

  // Keyboard: the row button opens the detail and gets focus back when it closes.
  const open = rowOpen(access, 'access-user-tom.okafor');
  await open.focus();
  await page.keyboard.press('Enter');
  await expect(panel).toBeFocused();
  await panel.getByTestId('access-user-toggle-tom.okafor').click();
  await panel.getByTestId('access-user-toggle-tom.okafor-confirm').click();
  await expect(access.getByTestId('access-user-status-tom.okafor')).toContainText('Disabled');
  await expect.poll(() => calls).toEqual([{ method: 'PUT', path: '/api/auth/users/usr_product', body: { disabled: true } }]);
  await page.keyboard.press('Escape');
  await expect(panel).toHaveCount(0);
  await expect(open).toBeFocused();
});

test('every earlier action stays reachable: invite, group, binding, enrollment code, runner projects, revocations', async ({ page }) => {
  const { calls } = await stubPilotOwner(page);
  await page.route('**/api/auth/invitations', route => {
    calls.push({ method: 'POST', path: '/api/auth/invitations', body: route.request().postDataJSON() });
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      user: { id: 'usr_new', username: 'new.developer', displayName: 'New Developer', role: 'viewer', projects: ['PROJ-002'], disabled: false, mustChangePassword: true },
      signInLink: '/?invite=inv.pilot', expiresAt: '2026-11-25T09:00:00Z',
    }) });
  });
  await page.route('**/api/auth/runners/runner_old/projects', route => {
    calls.push({ method: 'PUT', path: '/api/auth/runners/runner_old/projects', body: route.request().postDataJSON() });
    return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
  });
  const access = await openAccess(page);
  const panel = access.getByTestId('access-detail-panel');
  const last = () => calls.at(-1);

  await access.getByTestId('access-invite-open').click();
  await panel.getByTestId('access-invite-username').fill('new.developer');
  await panel.getByTestId('access-grant-project').selectOption('PROJ-002');
  await panel.getByTestId('access-grant-role').selectOption('developer');
  await panel.getByTestId('access-invite-submit').click();
  await expect(panel.getByTestId('access-invite-link')).toContainText('It works once');
  await expect(panel.getByTestId('access-invite-link-value')).toContainText('/?invite=inv.pilot');
  await expect.poll(last).toMatchObject({ path: '/api/auth/invitations', body: { username: 'new.developer', bindings: [{ role: 'developer', scope: { kind: 'project', project: 'PROJ-002' } }] } });

  await access.getByTestId('access-group-open').click();
  await panel.getByTestId('access-group-name').fill('Reviewers');
  await panel.getByTestId('access-group-create').click();
  await expect(panel).toHaveAccessibleName('Group: Reviewers');
  await panel.getByTestId('access-group-add-grp_new').selectOption('usr_dev');
  await expect.poll(last).toMatchObject({ method: 'PUT', path: '/api/auth/iam/groups/grp_new', body: { members: ['usr_dev'] } });
  await panel.getByRole('button', { name: 'Remove Priya Nair' }).click();
  await expect.poll(last).toMatchObject({ method: 'PUT', path: '/api/auth/iam/groups/grp_new', body: { members: [] } });
  await panel.getByTestId('access-grant-scope').selectOption('workspace');
  await panel.getByTestId('access-grant-role').selectOption('viewer');
  await panel.getByTestId('access-binding-add').click();
  await expect.poll(last).toMatchObject({ method: 'POST', path: '/api/auth/iam/bindings', body: { principalKind: 'group', principalId: 'grp_new', role: 'viewer', scope: { kind: 'workspace' } } });
  await panel.getByTestId('access-group-delete-grp_new').click();
  await panel.getByTestId('access-group-delete-grp_new-confirm').click();
  await expect.poll(last).toMatchObject({ method: 'DELETE', path: '/api/auth/iam/groups/grp_new' });
  await expect(panel).toHaveCount(0);

  // A binding is removed where its principal is shown; the scope view leads there.
  await access.getByTestId('access-binding-menu-rb_former').click();
  await page.getByTestId('access-binding-menu-rb_former-item-remove').click();
  await expect(panel).toHaveAccessibleName('Person: Sam Becker');
  await expect(panel.getByTestId('access-binding-remove-rb_former-panel')).toContainText('Sam Becker loses Viewer in Website.');
  await panel.getByTestId('access-binding-remove-rb_former-confirm').click();
  await expect.poll(last).toMatchObject({ method: 'DELETE', path: '/api/auth/iam/bindings/rb_former' });

  await access.getByTestId('access-binding-open').click();
  await panel.getByTestId('access-binding-principal').selectOption('service-account:sa_unbound');
  await panel.getByTestId('access-grant-project').selectOption('PROJ-001');
  await panel.getByTestId('access-grant-role').selectOption('viewer');
  await panel.getByTestId('access-binding-add').click();
  await expect.poll(last).toMatchObject({ method: 'POST', path: '/api/auth/iam/bindings', body: { principalKind: 'service-account', principalId: 'sa_unbound', role: 'viewer' } });
  await expect(panel).toHaveAccessibleName('Service account: import-job');

  await access.getByTestId('access-service-account-open').click();
  await panel.getByTestId('access-service-account-name').fill('release-bot');
  await panel.getByTestId('access-service-account-create').click();
  await expect.poll(last).toMatchObject({ method: 'POST', path: '/api/auth/iam/service-accounts', body: { name: 'release-bot' } });
  await expect(panel).toHaveAccessibleName('Service account: release-bot');
  await panel.getByTestId('access-service-account-disable-sa_new').click();
  await panel.getByTestId('access-service-account-disable-sa_new-confirm').click();
  await expect.poll(last).toMatchObject({ method: 'DELETE', path: '/api/auth/iam/service-accounts/sa_new' });

  await rowOpen(access, 'access-service-account-sa_tok_laptop').click();
  await expect(panel).toHaveAccessibleName('API token: studio-op on laptop');
  await expect(panel.getByTestId('access-api-token-role-sa_tok_laptop')).toContainText('Role: Operator agent');
  await expect(panel.getByTestId('access-token-create-sa_tok_laptop')).toHaveCount(0);
  await panel.getByTestId('access-token-revoke-tok_laptop').click();
  await panel.getByTestId('access-token-revoke-tok_laptop-confirm').click();
  await expect.poll(last).toMatchObject({ method: 'DELETE', path: '/api/auth/iam/service-accounts/sa_tok_laptop/tokens/tok_laptop' });

  await access.getByTestId('access-enrollment-open').click();
  await panel.getByTestId('access-enrollment-name').fill('build-host-03');
  await panel.getByTestId('access-enrollment-project-PROJ-001').check();
  await panel.getByTestId('access-enrollment-create').click();
  await expect(panel.getByTestId('access-enrollment-code-value')).toHaveText(pilotEnrollmentCode);
  await expect.poll(last).toEqual({ method: 'POST', path: '/api/auth/runner-enrollments', body: { name: 'build-host-03', projects: ['PROJ-001'] } });
  await panel.getByTestId('access-enrollment-code-done').click();
  await panel.getByTestId('access-enrollment-kind').selectOption('deploy');
  await panel.getByTestId('access-enrollment-name').fill('stage-host-02');
  await panel.getByTestId('access-enrollment-create').click();
  await expect(panel.getByTestId('access-run-host-command-text')).toContainText(`--enroll`);
  await expect(panel.getByTestId('access-run-host-command-text')).toContainText(pilotEnrollmentCode);

  await access.getByTestId('access-enrollment-menu-enr_build').click();
  await page.getByTestId('access-enrollment-menu-enr_build-item-revoke').click();
  await panel.getByTestId('access-enrollment-revoke-enr_build-confirm').click();
  await expect.poll(last).toMatchObject({ method: 'DELETE', path: '/api/auth/runner-enrollments/enr_build' });
  await expect(access.getByTestId('access-enrollment-enr_build')).toContainText('Expired or revoked');

  await rowOpen(access, 'access-runner-runner_old').click();
  await expect(panel).toContainText('holds all projects until you choose its projects');
  await panel.getByTestId('access-runner-edit-projects-runner_old').click();
  await panel.getByTestId('access-runner-project-runner_old-PROJ-002').check();
  await panel.getByTestId('access-runner-save-projects-runner_old').click();
  await expect.poll(last).toEqual({ method: 'PUT', path: '/api/auth/runners/runner_old/projects', body: { projects: ['PROJ-002'] } });
  await panel.getByTestId('access-runner-revoke-runner_old').click();
  await panel.getByTestId('access-runner-revoke-runner_old-confirm').click();
  await expect.poll(last).toMatchObject({ method: 'DELETE', path: '/api/auth/runners/runner_old' });
  await expect(access.getByTestId('access-runner-runner_old')).toContainText('Revoked');

  await rowOpen(access, 'access-user-priya.nair').click();
  await panel.getByTestId('access-user-reset-password').click();
  await expect(panel.getByTestId('access-temporary-password')).toContainText('Temporary password for priya.nair');
  await expect.poll(last).toMatchObject({ method: 'POST', path: '/api/auth/users/usr_dev/reset-password' });
  await panel.getByTestId('session-manager-toggle').click();
  await expect(panel.getByTestId('session-manager-panel')).toBeVisible();
  await access.getByTestId('access-user-menu-sam.becker').click();
  await page.getByTestId('access-user-menu-sam.becker-item-enable').click();
  await expect.poll(last).toEqual({ method: 'PUT', path: '/api/auth/users/usr_former', body: { disabled: false } });
});

test('the outline counts what each grid shows and jumps to its section', async ({ page }) => {
  await stubPilotOwner(page);
  const access = await openAccess(page);
  const outline = access.getByTestId('access-outline');
  const sections: [string, string, number][] = [
    ['access-people', 'access-users', 6], ['access-groups', 'access-groups', 3], ['access-accounts', 'access-service-accounts', 4],
    ['access-api-tokens', 'access-api-tokens', 2], ['access-runners', 'access-enrolled-runners', 6],
  ];
  for (const [anchor, grid, count] of sections) {
    await expect(access.getByTestId(grid).locator('tbody tr')).toHaveCount(count);
    await expect(outline.getByTestId(`access-outline-${anchor}`)).toContainText(String(count));
  }
  // Role bindings: the total equals the sum of the rows under every scope.
  await expect(access.getByTestId('access-bindings').locator('tbody tr')).toHaveCount(13);
  await expect(outline.getByTestId('access-outline-access-bindings')).toContainText('13');

  // Scrolling moves the mark; exactly one entry is the current location.
  await access.locator('#access-groups').evaluate(element => element.scrollIntoView({ block: 'start' }));
  await expect(outline.getByTestId('access-outline-access-groups')).toHaveAttribute('aria-current', 'location');
  await expect(outline.locator('[aria-current="location"]')).toHaveCount(1);

  await outline.getByTestId('access-outline-access-runners').click();
  await expect(access.getByRole('heading', { name: /^Runners/ })).toBeInViewport();
  await expect(outline.getByTestId('access-outline-access-runners')).toHaveAttribute('aria-current', 'location');
});

test('an empty installation says what belongs in each section', async ({ page }) => {
  await stubPilotOwner(page, { empty: true });
  const access = await openAccess(page);
  await expect(access.getByTestId('access-single-user')).toBeVisible();
  await expect(access.getByTestId('access-users').locator('tbody tr')).toHaveCount(1);
  await expect(access.getByTestId('access-groups-empty')).toContainText('A group holds a role once for several people');
  await expect(access.getByTestId('access-service-accounts-empty')).toContainText('Scripts and deploy jobs get their own account here');
  await expect(access.getByTestId('access-api-tokens-empty')).toContainText('A token a person created for a tool');
  await expect(access.getByTestId('access-enrolled-runners-empty')).toContainText('once it has used an enrollment code');
  await expect(access.getByTestId('access-enrollments-empty')).toContainText('Create a code');
  await expect(access.getByTestId('access-factor-events-empty')).toContainText('No factor activity recorded');
  mkdirSync(join(results, 'patterns'), { recursive: true });
  for (const value of ['light', 'dark'] as const) {
    await theme(page, value);
    await access.getByTestId('access-groups-empty').scrollIntoViewIfNeeded();
    await access.locator('#access-groups').screenshot({ path: join(results, 'patterns', `empty-state-${value}.png`) });
  }
});

for (const [name, viewport] of [['desktop', DESKTOP], ['phone', PHONE]] as const) {
  test(`after: Access page at ${name} width in both themes, without overflow or errors`, async ({ page }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error' && !/Failed to load resource/.test(message.text())) errors.push(message.text()); });
    await stubPilotOwner(page);
    const access = await openAccess(page, viewport);
    const dir = join(results, 'after');
    mkdirSync(dir, { recursive: true });
    // Scrolls a section to the top and lets the outline follow before the capture.
    const top = async (selector: string) => {
      await access.locator(selector).evaluate(element => element.scrollIntoView({ block: 'start' }));
      await expect(access.getByTestId(`access-outline-${selector.slice(1)}`)).toHaveAttribute('aria-current', 'location');
    };
    for (const value of ['light', 'dark'] as const) {
      await theme(page, value);
      await page.setViewportSize(viewport);
      await access.evaluate(element => element.scrollIntoView({ block: 'start' }));
      await expect(access.getByTestId('access-outline-access-people')).toHaveAttribute('aria-current', 'location');
      await page.screenshot({ path: join(dir, `access-${name}-${value}-top.png`) });
      await top('#access-accounts');
      await page.screenshot({ path: join(dir, `access-${name}-${value}-service-accounts.png`) });
      await top('#access-runners');
      await page.screenshot({ path: join(dir, `access-${name}-${value}-runners.png`) });
      await expandToFullPage(page, viewport.width, viewport.height);
      await page.screenshot({ path: join(dir, `access-${name}-${value}-full.png`) });
    }
    await page.setViewportSize(viewport);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    expect(await access.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);

    // The detail of a service account, with a new token and an open confirmation.
    await top('#access-accounts');
    await rowOpen(access, 'access-service-account-sa_deploy').click();
    const panel = access.getByTestId('access-detail-panel');
    await panel.getByTestId('access-token-create-sa_deploy').click();
    await expect(panel.getByTestId('access-token-issued')).toBeVisible();
    await panel.getByTestId('access-account-detail').evaluate(element => element.scrollIntoView({ block: 'start' }));
    for (const value of ['light', 'dark'] as const) {
      await theme(page, value);
      await page.screenshot({ path: join(dir, `access-${name}-${value}-detail-token.png`) });
    }
    await panel.getByTestId('access-token-issued-done').click();
    await panel.getByTestId('access-token-revoke-tok_ci').click();
    await expect(panel.getByTestId('access-token-revoke-tok_ci-panel')).toBeVisible();
    await panel.getByTestId('access-token-revoke-tok_ci-panel').scrollIntoViewIfNeeded();
    for (const value of ['light', 'dark'] as const) {
      await theme(page, value);
      await page.screenshot({ path: join(dir, `access-${name}-${value}-detail-confirm.png`) });
    }
    if (name === 'phone') {
      // Touch: the controls a finger must hit are at least 44 px tall.
      for (const target of [panel.getByTestId('sidesheet-close'), panel.getByTestId('access-token-revoke-tok_ci-confirm')]) {
        const box = await target.boundingBox();
        expect(box!.x + box!.width).toBeLessThanOrEqual(viewport.width);
      }
    }
    expect(errors).toEqual([]);
  });
}

test('text on the new parts meets WCAG AA contrast in both themes', async ({ page }) => {
  await stubPilotOwner(page);
  const access = await openAccess(page);
  await rowOpen(access, 'access-service-account-sa_deploy').click();
  const panel = access.getByTestId('access-detail-panel');
  await panel.getByTestId('access-token-create-sa_deploy').click();
  await expect(panel.getByTestId('access-token-issued')).toBeVisible();
  await panel.getByTestId('access-token-revoke-tok_hotfix').click();
  await expect(panel.getByTestId('access-token-revoke-tok_hotfix-confirm')).toBeVisible();
  const samples: Record<string, string> = {
    'column heading': '[data-testid="access-users"] th',
    'section purpose': '#access-people p',
    'outline, current entry': '[data-testid="access-outline-access-people"]',
    'outline, other entry': '[data-testid="access-outline-access-groups"]',
    'chip, Owner role': '[data-testid="access-user-mara"] [title="Owner · Workspace"] span:first-child',
    'chip, scope': '[data-testid="access-user-mara"] [title="Owner · Workspace"] span:last-child',
    'chip, role through a group': '[data-testid="access-user-priya.nair"] [title="Viewer · Mobile App"] span:first-child',
    'grid, second line': '[data-testid="access-user-mara"] td[data-role="identity"] span:last-child',
    'grid, meta': '[data-testid="access-user-mara"] td[data-role="meta"]',
    'primary action': '[data-testid="access-invite-open"]',
    'destructive at rest': '[data-testid="access-token-revoke-tok_ci"]',
    'destructive, confirming': '[data-testid="access-token-revoke-tok_hotfix-confirm"]',
    'confirmation sentence': '[data-testid="access-token-revoke-tok_hotfix-panel"] p',
    'secret, eyebrow': '[data-testid="access-token-issued"] p:first-child',
    'secret, value': '[data-testid="access-token-issued-value"]',
    'detail, fact label': '[data-testid="access-account-detail"] [data-testid="app-row-label"]',
  };
  const report: string[] = [];
  for (const value of ['light', 'dark'] as const) {
    await theme(page, value);
    for (const [name, selector] of Object.entries(samples)) {
      const { color, bg } = await sampleColours(page, selector);
      const ratio = contrastRatio(color, bg);
      report.push(`${value} | ${name} | ${ratio.toFixed(2)}`);
      expect(ratio, `${value} ${name}: ${color} on ${bg}`).toBeGreaterThanOrEqual(4.5);
    }
  }
  mkdirSync(results, { recursive: true });
  writeFileSync(join(results, 'contrast.md'), `# Contrast of the new parts (WCAG AA needs 4.5)\n\n| Theme | Text | Ratio |\n|---|---|---|\n${report.map(line => `| ${line} |`).join('\n')}\n`);
});

test('on a wide page the outline is a column beside the sections, also next to an open detail', async ({ page }) => {
  await stubPilotOwner(page);
  const access = await openAccess(page, { width: 1920, height: 1080 });
  const outline = access.getByTestId('access-outline');
  const people = access.locator('#access-people');
  const beside = async () => {
    const [nav, section] = [await outline.boundingBox(), await people.boundingBox()];
    return nav!.x + nav!.width <= section!.x;
  };
  await expect.poll(beside).toBe(true);
  await expect(outline.getByText('Machines')).toBeVisible();
  await expect(outline.getByTestId('access-outline-access-people')).toHaveAttribute('aria-current', 'location');
  mkdirSync(join(results, 'after'), { recursive: true });
  for (const value of ['light', 'dark'] as const) {
    await theme(page, value);
    await page.screenshot({ path: join(results, 'after', `access-wide-${value}-top.png`) });
  }

  await rowOpen(access, 'access-service-account-sa_deploy').click();
  await expect(access.getByTestId('access-detail-panel')).toBeVisible();
  expect(await beside()).toBe(true);
  for (const value of ['light', 'dark'] as const) {
    await theme(page, value);
    await page.screenshot({ path: join(results, 'after', `access-wide-${value}-detail.png`) });
  }
});

test('pattern shots for the pilot document, light and dark', async ({ page }) => {
  test.setTimeout(120_000);
  await stubPilotOwner(page);
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  const access = await openAccess(page);
  const dir = join(results, 'patterns');
  mkdirSync(dir, { recursive: true });
  const panel = access.getByTestId('access-detail-panel');
  const shoot = async (name: string, target: Locator | null, prepare?: () => Promise<void>) => {
    for (const value of ['light', 'dark'] as const) {
      await theme(page, value);
      if (prepare) await prepare();
      if (target) {
        await target.scrollIntoViewIfNeeded();
        await target.screenshot({ path: join(dir, `${name}-${value}.png`) });
      } else {
        await page.screenshot({ path: join(dir, `${name}-${value}.png`) });
      }
    }
  };

  await shoot('outline', access.getByTestId('access-outline'));
  await shoot('section', access.locator('#access-groups'));
  await shoot('grid', access.locator('#access-people'));
  await shoot('chips', access.getByTestId('access-user-priya.nair'));
  await shoot('action-placement', access.locator('#access-accounts'));

  await rowOpen(access, 'access-service-account-sa_deploy').click();
  await expect(panel).toBeVisible();
  await shoot('row-detail', null, () => access.locator('#access-accounts').evaluate(element => element.scrollIntoView({ block: 'start' })));
  await panel.getByTestId('access-token-create-sa_deploy').click();
  await shoot('secret-reveal', panel.getByTestId('access-token-issued'));
  await panel.getByTestId('access-token-issued-done').click();
  await panel.getByTestId('access-token-revoke-tok_ci').click();
  await shoot('confirm', panel.locator('#access-account-tokens'));
  await page.keyboard.press('Escape');
  await shoot('destructive-set-apart', panel.getByTestId('access-service-account-disable-sa_deploy'), async () => {
    await panel.getByTestId('access-service-account-disable-sa_deploy').scrollIntoViewIfNeeded();
  });
  await page.keyboard.press('Escape');

  await access.getByTestId('access-service-account-menu-sa_deploy').click();
  await expect(page.getByTestId('access-service-account-menu-sa_deploy-panel')).toBeVisible();
  await shoot('context-menu', null);
  await page.keyboard.press('Escape');

  await access.getByTestId('access-enrollment-open').click();
  await shoot('create-form', panel);
});
