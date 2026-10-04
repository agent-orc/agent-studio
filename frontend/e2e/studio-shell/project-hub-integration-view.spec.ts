import { expect, test, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'integration-demo';
const HUB_KEY = `hub:${PROJECT}`;

const INTEGRATION_VIEW = {
  project: PROJECT,
  isRepo: true,
  integrationRef: 'origin/develop',
  releaseRef: 'origin/main',
  integrationHeadSha: 'd'.repeat(40),
  releaseHeadSha: 'a'.repeat(40),
  capturedAt: '2026-07-22T12:00:00Z',
  queue: [
    { taskId: 'merged', taskKey: 'AGT-2198', title: 'Publisher durability', lane: '6-completed', stateSince: '2026-07-22T09:10:00Z', status: 'merged', mergeSha: 'd'.repeat(40), reason: null },
    { taskId: 'waiting', taskKey: 'AGT-2202', title: 'Merge queue visibility', lane: '6-completed', stateSince: '2026-07-22T10:00:00Z', status: 'waiting', mergeSha: null, reason: 'Accepted change is not present in origin/develop.' },
    { taskId: 'conflict', taskKey: 'AGT-2203', title: 'Resolve integration conflict', lane: '6-completed', stateSince: '2026-07-22T10:20:00Z', status: 'conflict', mergeSha: null, reason: 'Conflict in project-shell.config.ts.' },
    { taskId: 'skipped', taskKey: 'AGT-2204', title: 'Research note', lane: '6-completed', stateSince: '2026-07-22T10:30:00Z', status: 'skipped', mergeSha: null, reason: 'No task branch or attributed commit to integrate.' },
    { taskId: 'legacy', taskKey: 'AGT-2205', title: 'Legacy review subject', lane: '7-archive', stateSince: '2026-07-22T10:40:00Z', status: 'legacy-unverifiable', mergeSha: null, reason: 'The archived review subject predates RunAttempt authority.' },
    { taskId: 'superseded', taskKey: 'AGT-2206', title: 'Retired conflict', lane: '7-archive', stateSince: '2026-07-22T10:50:00Z', status: 'superseded', mergeSha: null, reason: 'The conflicting delivery belongs to an archived card.' },
  ],
  publisherMerges: [
    { taskKey: 'AGT-2198', title: 'Publisher durability', sha: 'd'.repeat(40), shortSha: 'd2198aa', integratedAt: '2026-07-22T11:15:00Z', publisher: 'auto-publisher', subject: 'merge(AGT-2198): publisher durability' },
  ],
  promotion: {
    fromRef: 'origin/develop', toRef: 'origin/main', fromSha: 'd'.repeat(40), toSha: 'a'.repeat(40),
    tasks: [
      { taskKey: 'AGT-2198', title: 'Publisher durability', sha: 'd'.repeat(40), shortSha: 'd2198aa', subject: 'merge(AGT-2198): publisher durability' },
    ],
    files: [
      { status: 'M', path: 'backend/Features/Publishing/Publisher.cs', added: 42, removed: 8 },
      { status: 'A', path: 'backend.Tests/PublisherTests.cs', added: 96, removed: 0 },
      { status: 'M', path: 'docs/concepts/release-semantics.md', added: 12, removed: 3 },
    ],
    filesChanged: 3, added: 150, removed: 11,
  },
  error: null,
};

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

async function installRoutes(page: Page): Promise<void> {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/**', route => route.fulfill(json([])).catch(() => undefined));
  await page.route(/\/api\/v1\/studio\/board/, route => route.fulfill(json(EMPTY_GROUPED)));
  await page.route(/\/api\/(?:jobs|tasks)(\?|$)/, route => route.fulfill(json([])));
  await page.route('**/api/watch-paths**', route => route.fulfill(json([{ name: PROJECT, path: '/tasks', rootPath: '/repo', repositoryPath: '/repo' }])));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill(json({ profile: 'local', bootstrapRequired: false, authenticated: false, user: null })));
  await page.route('**/api/environment**', route => route.fulfill(json({ isDev: true, devTools: {} })));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => route.fulfill(json({ projects: {} })));
  await page.route('**/api/clients', route => route.fulfill(json([])));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ items: [] })));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({ ttlSeconds: 600, snapshots: [] })));
  await page.route('**/api/git/summary**', route => route.fulfill(json([])));
  await page.route('**/api/git/integration**', route => route.fulfill(json(INTEGRATION_VIEW)));
}

async function openIntegrationRail(page: Page): Promise<void> {
  await page.addInitScript(({ key, project }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName: project, section: 'integration' }],
      activeKey: key,
    }));
  }, { key: HUB_KEY, project: PROJECT });
}

test('Project Hub Integration shows queue, publisher merges, and promotion file stat', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1600, height: 1050 });
  await openIntegrationRail(page);
  await installRoutes(page);
  await page.goto('/');

  await expect(page.getByTestId('project-shell-rail-integration')).toBeVisible();
  const panel = page.getByTestId('project-integration-panel');
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('origin/develop');
  await expect(page.getByTestId('integration-queue-row')).toHaveCount(6);
  await expect(page.getByTestId('integration-queue')).toContainText('Conflict in project-shell.config.ts');
  await expect(page.getByTestId('integration-filter-legacy-unverifiable')).toContainText('1 legacy unverifiable');
  await expect(page.getByTestId('integration-filter-superseded')).toContainText('1 superseded');
  await page.getByTestId('integration-filter-conflict').click();
  await expect(page.getByTestId('integration-queue-row')).toHaveCount(1);
  await expect(page.getByTestId('integration-queue')).toContainText('AGT-2203');
  await page.getByTestId('integration-filter-all').click();
  await expect(page.getByTestId('integration-queue-row')).toHaveCount(6);
  await expect(page.getByTestId('promotion-diff')).toContainText('3 files');
  await expect(page.getByTestId('promotion-diff')).toContainText('PublisherTests.cs');
  await expect(page.getByTestId('publisher-merges')).toContainText('AGT-2198');
  await expect(page.getByTestId('publisher-merges')).toContainText('auto-publisher');
  await expect(page.getByTestId('integration-queue').locator('code[title]')).toHaveAttribute('title', 'd'.repeat(40));
  await expect(page.getByText('Unexpected application error')).toHaveCount(0);

  const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-hub-integration');
  fs.mkdirSync(results, { recursive: true });
  const screenshot = path.join(results, 'project-hub-integration--mocked.png');
  await page.screenshot({ path: screenshot, fullPage: true });
  await testInfo.attach('project-hub-integration--mocked.png', { path: screenshot, contentType: 'image/png' });

  await setTheme(page, 'dark');
  await expect(page.getByText('Unexpected application error')).toHaveCount(0);
  const darkScreenshot = path.join(results, 'project-hub-integration--dark--mocked.png');
  await page.screenshot({ path: darkScreenshot, fullPage: true });
  await testInfo.attach('project-hub-integration--dark--mocked.png', { path: darkScreenshot, contentType: 'image/png' });
});
