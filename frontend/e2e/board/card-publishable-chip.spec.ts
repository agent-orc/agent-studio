import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * PUB-1: an accepted (6-completed) card whose merged work touches a publish
 * target shows a "publishable: npm, website" chip, listing the targets. A
 * completed card that touched no target renders no chip (Ruhe). Fully mocked
 * via route interception, so it runs against any served frontend with no
 * backend; the screenshot is labelled `--mocked`.
 */

const PROJECT = 'fixture-publishable';
const WATCH_PATH = 'C:/fixtures/publishable-repo';

function makeTask(id: string, state: string, title: string, publishSignal: { targetIds: string[]; labels: string[] } | null) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    key: id.toUpperCase(),
    title,
    state,
    order: 1,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-07-10T09:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/${state}/${id}`,
    lastActivity: '2026-07-10T11:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-8',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: { sha: 'a'.repeat(40), shortSha: 'aaaaaaa', message: 'work', filesChanged: 1, files: [], at: '2026-07-10T10:00:00Z' },
    commits: [{ sha: 'a'.repeat(40), shortSha: 'aaaaaaa', message: 'work', filesChanged: 1, files: [], at: '2026-07-10T10:00:00Z' }],
    ownerClientId: 'local-default',
    tags: [],
    publishSignal,
  };
}

// Two accepted cards touch targets; one accepted card touches nothing (no chip).
const PKG_AND_WEB = makeTask('pub-both', '6-completed', 'Adds runner feature and site copy',
  { targetIds: ['package:nuget', 'website'], labels: ['NuGet', 'Website'] });
const NPM_ONLY = makeTask('pub-npm', '6-completed', 'Publishes the chat package change',
  { targetIds: ['package:npm'], labels: ['npm'] });
const NO_TARGET = makeTask('pub-none', '6-completed', 'Docs-only cleanup, nothing publishable', null);

const GROUPED_PAYLOAD = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], review: [], autoReview: [], humanReview: [],
  completed: [PKG_AND_WEB, NPM_ONLY, NO_TARGET],
  archive: [],
};

async function installRoutes(page: Page) {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/**', r => r.fulfill(json([])).catch(() => undefined));
  await page.route('**/api/v1/studio/board**', r => r.fulfill(json(GROUPED_PAYLOAD)));
  await page.route(/\/api\/tasks(\?|$)/, r => r.fulfill(json([])));
  await page.route('**/api/watch-paths**', r => r.fulfill(json([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }])));
  await page.route('**/api/git/summary**', r => r.fulfill(json([])));
  await page.route(/\/api\/git\/hygiene(\?|$)/, r => r.fulfill(json({})));
  await page.route('**/api/environment**', r => r.fulfill(json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } })));
  await page.route('**/api/agent-rules**', r => r.fulfill(json([])));
  await page.route('**/api/clients', r => r.fulfill(json([])));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, r => r.fulfill(json({ projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } })));
  await page.route('**/api/tags', r => r.fulfill(json([])));
}

async function gotoBoard(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
    .toBeVisible({ timeout: 15_000 });
  await expect(page.locator('[data-testid="task-card"]').first()).toBeVisible({ timeout: 15_000 });
}

function cardByTitle(page: Page, title: string) {
  return page.locator('[data-testid="task-card"]', { hasText: title });
}

function resultsDir(): string {
  const fromEnv = process.env.PUB_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pub-1');
}

test.describe('PUB-1 · accepted-task publishable chip (mocked)', () => {
  test('lists touched targets on accepted cards and stays quiet otherwise', async ({ page }, testInfo) => {
    await gotoBoard(page);

    // Package + website card lists both labels.
    const both = cardByTitle(page, PKG_AND_WEB.title);
    await expect(both).toHaveCount(1);
    const bothChip = both.getByTestId('task-card-publishable');
    await expect(bothChip).toBeVisible();
    await expect(bothChip).toContainText('publishable:');
    await expect(bothChip).toContainText('NuGet');
    await expect(bothChip).toContainText('Website');

    // npm-only card lists just npm.
    const npm = cardByTitle(page, NPM_ONLY.title);
    const npmChip = npm.getByTestId('task-card-publishable');
    await expect(npmChip).toBeVisible();
    await expect(npmChip).toContainText('npm');
    await expect(npmChip).not.toContainText('Website');

    // An accepted card that touched no target renders no chip (Ruhe).
    await expect(cardByTitle(page, NO_TARGET.title).getByTestId('task-card-publishable')).toHaveCount(0);

    // Strip the global error dialog / dev overlays before the evidence frame -
    // harness noise from a sibling component hitting an empty mocked response,
    // unrelated to the chip (already asserted). Same as card-merge-signal.spec.ts.
    await page.evaluate(() => {
      document.querySelectorAll('vite-error-overlay').forEach(n => n.remove());
      document.querySelectorAll('.overlay--error, app-error-dialog, [data-testid="error-dialog"]')
        .forEach(n => ((n as HTMLElement).style.display = 'none'));
    });
    await both.scrollIntoViewIfNeeded();
    await page.waitForTimeout(150);

    // Evidence screenshot (mocked API).
    fs.mkdirSync(resultsDir(), { recursive: true });
    const shotPath = path.join(resultsDir(), 'card-publishable-chip--mocked.png');
    await page.screenshot({ path: shotPath, fullPage: false });
    await testInfo.attach('card-publishable-chip--mocked.png', { path: shotPath, contentType: 'image/png' });
  });
});
