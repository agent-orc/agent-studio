import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import type { Page, Route } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3402: Settings surfaces show state through the shared status indicator,
 * a tone icon in front of the text, in both themes. GitHub > Repositories was
 * the reported case: Connected and No repository used to look the same.
 */
const shots = resolve(process.env.JOB_RESULTS_DIR ?? '../results', 'status-indicator');
const json = (body: unknown) => (route: Route) => route.fulfill({
  status: 200, contentType: 'application/json', body: JSON.stringify(body),
});

async function stub(page: Page) {
  const now = new Date().toISOString();
  await page.route('**/api/auth/status', json({ profile: 'networked', bootstrapRequired: false, authenticated: true }));
  await page.route('**/api/auth/sessions', json([]));
  await page.route('**/api/tasks', json([]));
  await page.route('**/api/v1/studio/board*', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/v1/studio/runner/status', json({ projects: {} }));
  await page.route('**/api/cli/quota', json({ ttlMs: 60_000, snapshots: [] }));
  await page.route('**/api/clients', json([]));
  await page.route('**/api/v1/workspaces*', json([]));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/v1/management/remote-hosts', json([]));
  await page.route('**/api/v1/management/links', json([]));
  await page.route('**/api/v1/management/github-app', json({
    connected: true, appId: 7, slug: 'agent-studio', name: 'agent-studio', ownerLogin: 'team',
    htmlUrl: 'https://github.com/apps/agent-studio', installUrl: 'https://github.com/apps/agent-studio/installations/new',
    connectedAtUtc: '2026-11-01T10:00:00Z',
    installations: [{ id: 1, account: 'team', accountType: 'Organization', adoptedAtUtc: '2026-11-01T10:01:00Z', adoptedBy: 'robert' }],
    repositories: [
      { project: 'Agent Studio', repositoryUrl: 'https://github.com/team/agent-studio.git', state: 'covered', installationId: 1, detail: null },
      { project: 'Website', repositoryUrl: 'https://github.com/other/website.git', state: 'not-installed', installationId: null,
        detail: 'The App is not installed on the account that owns this repository.' },
      { project: 'Docs', repositoryUrl: 'https://github.com/team/docs.git', state: 'not-adopted', installationId: null,
        detail: 'Confirm the installation with your GitHub account.' },
      { project: 'Scratch', repositoryUrl: null, state: 'no-repository-url', installationId: null, detail: null },
      { project: 'Mirror', repositoryUrl: 'https://gitlab.example.com/team/mirror.git', state: 'not-github', installationId: null, detail: null },
    ],
    hosts: [{ runnerId: 'runner-a', access: 'write', repositories: 1, lastSyncUtc: now }],
    problem: null,
  }));
  await page.route('**/api/v1/management/credential-inventory', json({
    generatedAt: now,
    items: [
      { id: 'missing-review', category: 'repository', hostId: 'review-a', kind: 'review_read_file',
        label: 'Private repository read access', scopes: [], repository: 'github.com/team/private',
        permission: 'read', expiresAt: null, lastUseAt: null, state: 'missing', required: true,
        detail: 'Add a repository-scoped read credential', affectedCards: [] },
      { id: 'expiring-token', category: 'repository', hostId: 'runner-a', kind: 'github_https_token',
        label: 'Runner token', scopes: ['main'], repository: 'github.com/team/product',
        permission: 'write', expiresAt: '2026-11-14T09:00:00Z', lastUseAt: now,
        state: 'expiring', required: false, detail: '14-day expiry warning', affectedCards: [] },
      { id: 'deploy-key', category: 'repository', hostId: 'runner-a', kind: 'host_key',
        label: 'Deploy key', scopes: [], repository: 'github.com/team/product', permission: 'write',
        expiresAt: null, lastUseAt: now, state: 'ok', required: true, detail: 'Verified push', affectedCards: [] },
      { id: 'old-token', category: 'repository', hostId: 'runner-a', kind: 'github_https_token',
        label: 'Old token', scopes: [], repository: null, permission: null,
        expiresAt: null, lastUseAt: null, state: 'unused', required: false, detail: 'Never used', affectedCards: [] },
    ],
  }));
}

const indicator = (page: Page, testId: string) => page.getByTestId(testId);

test('GitHub repositories and Credentials show tone icons in both themes', async ({ page }) => {
  mkdirSync(shots, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 900 });
  await stub(page);

  await page.goto('/#/workspace/settings/github', { waitUntil: 'domcontentloaded' });
  const panel = page.getByTestId('github-app-panel');
  await expect(panel).toBeVisible();
  const expected = [
    ['Agent Studio', 'success', 'check', 'Connected'],
    ['Website', 'warning', 'warn', 'App not installed'],
    ['Docs', 'warning', 'warn', 'Needs confirmation'],
    ['Scratch', 'neutral', 'circle', 'No repository'],
    ['Mirror', 'neutral', 'circle', 'Not on GitHub'],
  ] as const;
  for (const [project, tone, icon, label] of expected) {
    const cell = indicator(page, `github-app-coverage-${project}`);
    await expect(cell).toHaveAttribute('data-tone', tone);
    await expect(cell).toHaveAttribute('data-icon', icon);
    await expect(cell).toHaveText(label);
    await expect(cell.locator('.status-indicator__icon')).toHaveAttribute('aria-hidden', 'true');
    await expect(cell.locator('svg')).toBeVisible();
  }
  await expect(indicator(page, 'github-app-connection-status')).toHaveAttribute('data-tone', 'success');

  // The detail is reachable by keyboard and announced as a description.
  await indicator(page, 'github-app-coverage-Docs').focus();
  await expect(page.getByRole('tooltip')).toContainText('Confirm the installation');
  await indicator(page, 'github-app-coverage-Docs').blur();
  await expect(page.getByRole('tooltip')).toHaveCount(0);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    const success = await indicator(page, 'github-app-coverage-Agent Studio').evaluate(node => getComputedStyle(node).color);
    const warning = await indicator(page, 'github-app-coverage-Website').evaluate(node => getComputedStyle(node).color);
    const neutral = await indicator(page, 'github-app-coverage-Scratch').evaluate(node => getComputedStyle(node).color);
    expect(new Set([success, warning, neutral]).size, `${theme}: tones resolve to distinct colours`).toBe(3);
    await page.getByTestId('github-app-repositories').scrollIntoViewIfNeeded();
    await panel.screenshot({ path: join(shots, `github-panel-${theme}--mocked.png`) });
  }

  await page.goto('/#/workspace/settings/credentials', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('credential-inventory')).toBeVisible();
  await expect(indicator(page, 'credential-state-missing-review')).toHaveAttribute('data-icon', 'close');
  await expect(indicator(page, 'credential-state-expiring-token')).toHaveAttribute('data-icon', 'warn');
  await expect(indicator(page, 'credential-state-deploy-key')).toHaveAttribute('data-icon', 'check');
  await expect(indicator(page, 'credential-state-old-token')).toHaveAttribute('data-icon', 'circle');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: join(shots, `credentials-${theme}--mocked.png`) });
  }
});

test('the running tone stops spinning under reduced motion', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await stub(page);
  await page.route('**/api/v1/management/status', () => { /* keep the Task Server query pending */ });
  await page.goto('/#/workspace/settings/task-server', { waitUntil: 'domcontentloaded' });
  const querying = page.getByTestId('task-server-querying').getByTestId('status-indicator');
  await expect(querying).toHaveAttribute('data-tone', 'running');
  await expect(querying).toHaveAttribute('data-icon', 'spinner');
  const animation = await querying.locator('.status-indicator__icon').evaluate(node => getComputedStyle(node).animationName);
  expect(animation).toBe('none');
});
