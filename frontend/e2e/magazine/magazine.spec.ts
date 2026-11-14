import { expect, test } from '@playwright/test';
import { join } from 'node:path';
import { mkdirSync } from 'node:fs';
import { setTheme } from '../helpers/theme';

const resultDir = process.env.JOB_RESULTS_DIR || 'test-results/magazine';
const project = {
  sourceType: 'local-folder', id: 'PROJ-002', displayName: 'Demo Project', shortCode: 'DEMO',
  workspaceId: 'ws-demo', color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
  storageLocation: '/tmp/demo-project', repositoryPath: null, rootPath: null, repositoryUrl: null,
  urls: [], archived: false, createdAt: '2026-11-10T00:00:00Z',
};
const workspace = {
  id: 'ws-demo', displayName: 'Demo workspace', sortOrder: 0, isDefault: true,
  color: null, createdAt: '2026-11-10T00:00:00Z', projects: [project],
};
const author = { principal: 'agent:orchestrator', persona: 'Aster', personaId: 'aster', roleId: 'editor' };
const oldAuthor = { principal: 'agent:orchestrator', persona: 'Orchestrator', personaId: null, roleId: null };
const updatedAt = new Date(Date.now() - 3 * 60_000).toISOString();
const visibleCard = {
  id: 'AGT-3715', key: 'AGT-3715', taskKey: '/tmp/demo-project::AGT-3715',
  title: 'Visible magazine card', state: '2-ready', order: 0, agent: 'codex', createdAt: updatedAt,
  watchPath: '/tmp/demo-project', projectName: 'Demo Project', folderPath: '/tmp/demo-project/2-ready/AGT-3715',
  lastActivity: updatedAt, sessionName: null, model: null, cliType: 'codex', useOwnSession: null,
  lastUsage: null, execution: null, commit: null,
};
const baseArticle = (scope: string, slug: string, revision: number) => ({
  scope, slug, revision, title: slug === 'decisions' ? '29 cards wait for you' : 'Project pulse',
  summary: 'A short account of what needs attention today.', kind: 'digest',
  authors: [author, oldAuthor], sources: [{ kind: 'card', ref: 'AGT-3715' }],
  createdAt: updatedAt, updatedAt, pinned: slug === 'decisions', archivedAt: null, bodyBytes: 100,
});

async function setup(page: import('@playwright/test').Page): Promise<{ revise: () => void }> {
  let revision = 1;
  await page.route('**/api/v1/workspaces*', route => route.fulfill({ json: [workspace] }));
  await page.route('**/api/v1/projects*', route => route.fulfill({ json: [project] }));
  await page.route('**/api/v1/studio/board*', route => route.fulfill({ json: {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [visibleCard], progress: [], failedPickup: [],
    codeNotComplete: [], review: [], autoReview: [], humanReview: [], escalated: [], completed: [], archive: [],
    gitStateAt: null, stale: false,
  } }));
  await page.route('**/api/magazine**', route => {
    const url = new URL(route.request().url());
    const parts = url.pathname.split('/');
    const scope = parts[3] ? decodeURIComponent(parts[3]) : url.searchParams.get('scope') || 'installation';
    const slug = parts[4] || 'decisions';
    if (parts.length === 3) return route.fulfill({ json: { scope, articles: [baseArticle(scope, scope === 'installation' ? 'decisions' : 'pulse', revision)] } });
    if (parts[5] === 'history') return route.fulfill({ json: { scope, slug, currentRevision: revision, revisions: Array.from({ length: revision }, (_, index) => ({
      revision: revision - index, at: updatedAt, author: index ? oldAuthor : author, title: '29 cards wait for you',
    })) } });
    const shown = Number(url.searchParams.get('revision')) || revision;
    return route.fulfill({ json: { ...baseArticle(scope, slug, shown), currentRevision: revision, author: shown === 1 ? oldAuthor : author,
      body: '## Today\n\nAGT-3715, QS-144 and UNKNOWN-999 are mentioned. Demo Project is underway.\n\n<script>alert(1)</script>\n\nThe decision lives in docs/operations/decision-cards/.',
    } });
  });
  await page.goto('/');
  await expect(page.getByTestId('studio-titlebar')).toBeVisible();
  return { revise: () => { revision = 2; } };
}

async function openInstallation(page: import('@playwright/test').Page): Promise<void> {
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.getByTestId('studio-titlebar-magazine').click();
  await expect(page.getByRole('heading', { name: 'Installation magazine' })).toBeVisible();
}

test('installation, workspace and project magazines expose their articles and revisions', async ({ page }) => {
  const fixture = await setup(page);
  await openInstallation(page);
  await expect(page.getByTestId('magazine-entry-decisions')).toContainText('Aster · Editor');
  await page.getByTestId('magazine-view').getByRole('button', { name: 'Demo workspace' }).click();
  await expect(page.getByRole('heading', { name: 'Demo workspace magazine' })).toBeVisible();
  await expect(page.getByTestId('magazine-entry-pulse')).toBeVisible();
  await page.getByTestId('magazine-view').getByRole('button', { name: 'Demo Project' }).click();
  await expect(page.getByRole('heading', { name: 'Demo Project magazine' })).toBeVisible();
  await page.getByTestId('magazine-entry-pulse').click();
  await expect(page.getByTestId('magazine-body')).toContainText('AGT-3715');
  await expect(page.getByTestId('magazine-body').locator('[data-task-ref="true"]')).toContainText('AGT-3715');
  await expect(page.getByTestId('magazine-body')).toContainText('UNKNOWN-999');
  await expect(page.getByTestId('magazine-body').locator('a', { hasText: 'UNKNOWN-999' })).toHaveCount(0);
  await expect(page.getByTestId('magazine-body').locator('a', { hasText: 'QS-144' })).toHaveCount(0);
  await expect(page.getByTestId('magazine-body')).not.toContainText('Hidden security card');
  await expect(page.getByTestId('magazine-body').locator('script')).toHaveCount(0);
  await expect(page.getByTestId('magazine-history')).toContainText('Revision 1');
  await page.getByTestId('magazine-view').getByRole('button', { name: /Demo Project magazine/ }).click();
  fixture.revise();
  await page.getByTestId('studio-titlebar-magazine').click();
  await page.getByTestId('magazine-view').getByRole('button', { name: 'Demo workspace' }).click();
  await page.getByTestId('magazine-view').getByRole('button', { name: 'Demo Project' }).click();
  await expect(page.getByTestId('magazine-entry-pulse')).toContainText('Updated');
  await page.getByTestId('magazine-entry-pulse').click();
  await expect(page.getByTestId('magazine-history')).toContainText('Revision 1');
  await page.getByRole('button', { name: 'Revision 1' }).click();
  await expect(page.getByText('Earlier revision, read only.')).toBeVisible();
});

test('magazine screenshots in both themes and widths', async ({ page }) => {
  mkdirSync(resultDir, { recursive: true });
  await setup(page);
  await openInstallation(page);
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 800 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: join(resultDir, `magazine-installation-${width}-${theme}.png`), fullPage: true });
    }
  }
  await page.getByTestId('magazine-view').getByRole('button', { name: 'Demo workspace' }).click();
  await page.getByTestId('magazine-view').getByRole('button', { name: 'Demo Project' }).click();
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 800 });
    if (width === 390) await page.getByTestId('magazine-entry-pulse').scrollIntoViewIfNeeded();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: join(resultDir, `magazine-project-${width}-${theme}.png`), fullPage: true });
    }
  }
  await page.getByTestId('magazine-entry-pulse').click();
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 800 });
    if (width === 390) await page.getByTestId('magazine-body').scrollIntoViewIfNeeded();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: join(resultDir, `magazine-article-${width}-${theme}.png`), fullPage: true });
    }
  }
});
