import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import type { Page, Route } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { setTheme } from '../helpers/theme';

const shots = resolve(process.env.JOB_RESULTS_DIR ?? '../results', 'credential-inventory');
const json = (body: unknown) => (route: Route) => route.fulfill({
  status: 200, contentType: 'application/json', body: JSON.stringify(body),
});

async function stub(page: Page) {
  const status = { profile: 'local', bootstrapRequired: false, authenticated: true,
    user: { id: 'owner-inventory', username: 'owner', displayName: 'Owner', role: 'owner',
      projects: [], disabled: false, mustChangePassword: false, hasFactor: true } };
  await page.route('**/api/auth/status', json(status));
  await page.route('**/api/v1/studio/auth/status', json(status));
  await page.route('**/api/crash-recovery/pending', json({ pending: [] }));
  await page.route('**/api/projects', json([]));
  await page.route('**/api/tasks', json([]));
  await page.route('**/api/v1/studio/board', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/v1/studio/runner/status', json({ projects: {} }));
  await page.route('**/api/cli/quota', json({ ttlMs: 60_000, snapshots: [] }));
  await page.route('**/api/clients', json([]));
  await page.route('**/api/v1/workspaces*', json([]));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  const seenAt = new Date().toISOString();
  await page.route('**/api/v1/management/remote-hosts', json([{
    runnerId: 'runner-a', name: 'runner-a', hostId: 'runner-a', instanceId: 'coding',
    runnerVersion: '1', protocolVersion: 2, status: 'active',
    registeredAt: seenAt, lastSeenAt: seenAt,
    hostAdmission: { hostId: 'runner-a', admissionState: 'open' },
    capabilities: [{ key: 'provider-auth:codex', category: 'provider-auth',
      advertisedStatus: 'ready', healthState: 'healthy', signal: 'ok', detail: 'Active session confirmed',
      advertisedAt: seenAt, freshUntil: new Date(Date.now() + 180_000).toISOString(),
      isFresh: true, consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [] }],
  }]));
  await page.route('**/api/v1/management/links', json([]));
  await page.route('**/api/v1/management/credential-inventory', json({
    generatedAt: '2026-10-16T09:00:00Z',
    items: [
      { id: 'missing-review', lifetimeClass: 'long-lived', category: 'repository', hostId: 'review-a', kind: 'review_read_file',
        label: 'Private repository read access', scopes: [], repository: 'forge.example.invalid/team/private',
        permission: 'read', expiresAt: null, lastUseAt: null, state: 'missing', required: true,
        detail: 'Add a repository-scoped read credential', affectedCards: ['AGT-1'] },
      { id: 'expiring-token', lifetimeClass: 'long-lived', category: 'repository', hostId: 'runner-a', kind: 'github_https_token',
        label: 'Runner PAT', scopes: ['main'], repository: 'forge.example.invalid/team/product',
        permission: 'write', expiresAt: '2026-10-24T09:00:00Z', lastUseAt: '2026-10-15T09:00:00Z',
        state: 'expiring', required: false, detail: '14-day expiry warning', affectedCards: [] },
      { id: 'codex-login', category: 'provider', hostId: 'runner-a', kind: 'codex_chatgpt_login',
        label: 'Codex account', scopes: [], repository: null, permission: null,
        expiresAt: null, lastUseAt: null, state: 'ok', required: true,
        detail: 'Host-owned login', affectedCards: [], lifetimeClass: 'long-lived', holder: 'runner-a',
        target: 'model-provider', createdAt: null, expiry: 'none', rotationIntervalDays: 90,
        rotationDueAt: '2027-01-14T09:00:00Z' },
      { id: 'attempt:issuance-visible', lifetimeClass: 'short-lived', category: 'repository', hostId: 'runner-a', kind: 'attempt_repository_token',
        label: 'Attempt repository access', scopes: ['coding', 'repository:read'],
        repository: 'forge.example.invalid/team/product', permission: 'read',
        expiresAt: '2026-10-28T11:00:00Z', lastUseAt: '2026-10-28T10:00:00Z',
        state: 'ok', required: false, detail: 'Attempt bound', affectedCards: ['AGT-1'],
        issuer: 'github-app', binding: 'AGT-1 · attempt run-visible · lease lease-visible · fence 7 · coding',
        storageMode: 'linux-tmpfs', revocationReceipt: 'pending · file pending' },
    ],
  }));
}

test.use({ navigationTimeout: 60_000 });

test('Credentials page shows gaps, filters and both theme screenshots', async ({ page }) => {
  test.setTimeout(120_000);
  const code = `${'A'.repeat(4)}-${'B'.repeat(4)}`;
  mkdirSync(shots, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 900 });
  await stub(page);
  await page.route('**/api/v1/management/device-logins', json({
    handle: 'access-session', state: 'queued', sessionKey: 'browser-session',
    verificationUrl: null, userCode: null, expiresAt: new Date(Date.now() + 900_000).toISOString(),
  }));
  await page.route('**/api/v1/management/device-logins/*', json({
    handle: 'access-session', state: 'running', detail: null,
    verificationUrl: 'https://login.example.invalid/device', userCode: code,
    expiresAt: new Date(Date.now() + 900_000).toISOString(),
  }));
  await page.goto('/#/workspace/settings/credentials', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('credential-inventory')).toBeVisible({ timeout: 45_000 });
  await expect(page.getByTestId('workspace-settings-rail-credentials')).toBeVisible();
  await expect(page.getByTestId('credential-inventory')).toContainText('Affected cards: AGT-1');
  await expect(page.getByRole('article')).toHaveCount(4);
  await expect(page.getByTestId('credential-inventory')).toContainText('long-lived');
  await expect(page.getByTestId('credential-inventory')).toContainText('Holder: runner-a');
  await expect(page.getByTestId('credential-inventory')).toContainText('Target: model-provider');
  await expect(page.getByTestId('credential-inventory')).toContainText('Expires: none');
  await expect(page.getByTestId('credential-inventory')).toContainText('Every 90 days');
  await expect(page.getByTestId('credential-inventory')).toContainText('2027-01-14');
  await expect(page.getByRole('article').filter({ hasText: 'Attempt repository access' })).toContainText('Class: short-lived');
  await expect(page.getByTestId('credential-inventory')).toContainText('run-visible · lease lease-visible · fence 7');
  await expect(page.getByTestId('credential-inventory')).toContainText('File storage: linux-tmpfs');
  await expect(page.getByTestId('credential-inventory')).toContainText('Revocation receipt: pending · file pending');
  await page.getByRole('combobox', { name: 'State', exact: true }).selectOption('missing');
  await expect(page.getByRole('article')).toHaveCount(1);
  await page.getByRole('combobox', { name: 'State', exact: true }).selectOption('all');
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await page.getByRole('combobox', { name: 'Kind', exact: true }).selectOption('codex_chatgpt_login');
    await page.screenshot({ path: join(shots, `inventory-${theme}--mocked.png`) });
  }
  await page.getByTestId('access-codex-sign-in').click();
  await expect(page.getByTestId('codex-sign-in-code')).toContainText(code);
  await expect(page.getByTestId('codex-sign-in-countdown')).toBeVisible();
});

test('an open Credentials page shows a new gap after the inventory refreshes', async ({ page }) => {
  test.setTimeout(120_000);
  await page.clock.install();
  await stub(page);
  let missing = false;
  await page.route('**/api/v1/management/credential-inventory', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ generatedAt: '', items: missing ? [{
      id: 'new-gap', category: 'repository', hostId: 'review-a', kind: 'review_read_file',
      label: 'Private repository read access', scopes: [], repository: 'forge.example.invalid/team/private',
      permission: 'read', expiresAt: null, lastUseAt: null, state: 'missing', required: true,
      detail: 'Add a repository-scoped read credential', affectedCards: [],
    }] : [] }),
  }));
  await page.goto('/#/workspace/settings/credentials', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('credential-inventory')).toBeVisible({ timeout: 45_000 });
  // Finish the initial request before advancing the polling clock.
  await expect(page.getByTestId('credential-inventory')).toContainText('No credentials match these filters.', { timeout: 45_000 });
  await expect(page.getByRole('article')).toHaveCount(0);
  missing = true;
  await page.clock.fastForward(60_000);
  await expect(page.getByRole('article')).toHaveCount(1, { timeout: 45_000 });
  await expect(page.getByRole('article')).toContainText('Private repository read access');
});
