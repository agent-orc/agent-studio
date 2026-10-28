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
  await page.route('**/api/auth/status', json({ profile: 'networked', bootstrapRequired: false, authenticated: true }));
  await page.route('**/api/tasks', json([]));
  await page.route('**/api/v1/studio/board', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/v1/studio/runner/status', json({ projects: {} }));
  await page.route('**/api/cli/quota', json({ ttlMs: 60_000, snapshots: [] }));
  await page.route('**/api/clients', json([]));
  await page.route('**/api/v1/workspaces*', json([]));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/v1/management/credential-inventory', json({
    generatedAt: '2026-10-16T09:00:00Z',
    items: [
      { id: 'missing-review', category: 'repository', hostId: 'review-a', kind: 'review_read_file',
        label: 'Private repository read access', scopes: [], repository: 'github.com/team/private',
        permission: 'read', expiresAt: null, lastUseAt: null, state: 'missing', required: true,
        detail: 'Add a repository-scoped read credential', affectedCards: ['AGT-1'] },
      { id: 'expiring-token', category: 'repository', hostId: 'runner-a', kind: 'github_https_token',
        label: 'Runner PAT', scopes: ['main'], repository: 'github.com/team/product',
        permission: 'write', expiresAt: '2026-10-24T09:00:00Z', lastUseAt: '2026-10-15T09:00:00Z',
        state: 'expiring', required: false, detail: '14-day expiry warning', affectedCards: [] },
      { id: 'codex-login', category: 'provider', hostId: 'runner-a', kind: 'codex_chatgpt_login',
        label: 'Codex account', scopes: [], repository: null, permission: null,
        expiresAt: null, lastUseAt: null, state: 'ok', required: true,
        detail: 'Host-owned login', affectedCards: [] },
    ],
  }));
}

test('Credentials page shows gaps, filters and both theme screenshots', async ({ page }) => {
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
    verificationUrl: 'https://auth.openai.com/codex/device', userCode: code,
    expiresAt: new Date(Date.now() + 900_000).toISOString(),
  }));
  await page.goto('/#/workspace/settings/credentials', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('credential-inventory')).toBeVisible();
  await expect(page.getByTestId('workspace-settings-rail-credentials')).toBeVisible();
  await expect(page.getByTestId('credential-inventory')).toContainText('Affected cards: AGT-1');
  await expect(page.locator('.credentials__row')).toHaveCount(3);
  await page.getByRole('combobox', { name: 'State', exact: true }).selectOption('missing');
  await expect(page.locator('.credentials__row')).toHaveCount(1);
  await page.getByRole('combobox', { name: 'State', exact: true }).selectOption('all');
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: join(shots, `inventory-${theme}--mocked.png`) });
  }
  await page.getByTestId('access-codex-sign-in').click();
  await expect(page.getByTestId('codex-sign-in-code')).toContainText(code);
  await expect(page.getByTestId('codex-sign-in-countdown')).toBeVisible();
});

test('an open Credentials page shows a new gap after the inventory refreshes', async ({ page }) => {
  await page.clock.install();
  await stub(page);
  let missing = false;
  await page.route('**/api/v1/management/credential-inventory', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ generatedAt: '', items: missing ? [{
      id: 'new-gap', category: 'repository', hostId: 'review-a', kind: 'review_read_file',
      label: 'Private repository read access', scopes: [], repository: 'github.com/team/private',
      permission: 'read', expiresAt: null, lastUseAt: null, state: 'missing', required: true,
      detail: 'Add a repository-scoped read credential', affectedCards: [],
    }] : [] }),
  }));
  await page.goto('/#/workspace/settings/credentials', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('credential-inventory')).toBeVisible();
  await expect(page.locator('.credentials__row')).toHaveCount(0);
  missing = true;
  await page.clock.fastForward(60_000);
  await expect(page.locator('.credentials__row')).toHaveCount(1);
  await expect(page.locator('.credentials__row')).toContainText('Private repository read access');
});
