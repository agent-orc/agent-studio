import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import type { Page, Route } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { setTheme } from '../helpers/theme';

// AGT-3408: Settings > GitHub > Repositories shows the visibility GitHub reports per repository.
const shots = resolve(process.env.JOB_RESULTS_DIR ?? '../results', 'github-app-repositories');
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
  await page.route('**/api/v1/management/github-app*', json({
    connected: true,
    appId: 7,
    slug: 'agent-studio-x',
    name: 'agent-studio-x',
    ownerLogin: 'owner',
    htmlUrl: 'https://github.com/apps/agent-studio-x',
    installUrl: 'https://github.com/apps/agent-studio-x/installations/new',
    connectedAtUtc: '2026-11-01T10:00:00Z',
    installations: [{ id: 1, account: 'owner', accountType: 'User', adoptedAtUtc: '2026-11-01T10:01:00Z', adoptedBy: 'local-operator' }],
    repositories: [
      { project: 'Website', repositoryUrl: 'https://github.com/owner/website.git', state: 'covered', installationId: 1, detail: null, visibility: 'private' },
      { project: 'Docs', repositoryUrl: 'https://github.com/owner/docs.git', state: 'covered', installationId: 1, detail: null, visibility: 'public' },
      { project: 'Legacy', repositoryUrl: 'https://github.com/owner/legacy.git', state: 'not-installed', installationId: null, detail: 'The App is not installed on owner/legacy.', visibility: null },
    ],
    hosts: [{ runnerId: 'review-a', access: 'read', repositories: 2, lastSyncUtc: '2026-11-01T10:02:00Z' }],
    problem: null,
  }));
}

test('the repository list names the visibility GitHub reports', async ({ page }) => {
  mkdirSync(shots, { recursive: true });
  await stub(page);
  await page.goto('/#/workspace/settings/github', { waitUntil: 'domcontentloaded' });
  const table = page.getByTestId('github-app-repositories');
  await expect(table).toBeVisible();
  await expect(table.locator('thead')).toContainText('Visibility');
  await expect(page.getByTestId('github-app-repository-visibility')).toHaveText(['Private', 'Public', '-']);
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await table.scrollIntoViewIfNeeded();
    await page.screenshot({ path: join(shots, `repositories-${theme}--mocked.png`) });
  }
});
