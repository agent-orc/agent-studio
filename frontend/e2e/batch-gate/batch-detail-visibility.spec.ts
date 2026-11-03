import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'fs';
import * as path from 'path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

// AGT-3463: the task-detail "Batch verification" section exists only for a
// batch member. A 2-ready card held by a dependency gate shows nothing.
// Unrouted calls reach the isolated e2e backend; only the board, the two
// cards, and the batch gate state are fixtures.
test.use({ serviceWorkers: 'block', reducedMotion: 'reduce' });

const PROJECT = 'batch-visibility-fixture';
const WATCH_PATH = '/tmp/batch-visibility-fixture';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve('test-results', 'batch-detail-visibility');

const json = (route: Route, body: unknown) =>
  route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

function taskInfo(id: string, state: string, title: string, extra: Record<string, unknown> = {}) {
  return {
    id, key: id, taskKey: `${WATCH_PATH}::${id}`, title, state, order: 1,
    agent: 'codex', cliType: 'codex', createdAt: '2026-11-03T08:00:00Z', lastActivity: '2026-11-03T09:00:00Z',
    watchPath: WATCH_PATH, projectName: PROJECT, folderPath: `${WATCH_PATH}/${state}/${id}`,
    commits: [], ownerClientId: 'local-default', tags: [], ...extra,
  };
}

const heldCard = taskInfo('AGT-3403', '2-ready', 'Held by a dependency gate', {
  references: { dependsOn: ['AGT-3402'], relatedTo: [], blockedBy: [], supersedes: [] },
});
const memberCard = taskInfo('AGT-3410', '4-auto-review', 'Batch member in auto review');

const batchState = {
  gateSlots: { busy: 1, total: 3 },
  plan: { decision: 'wait', reason: 'Waiting for a safe group', targetSize: 2, closeAtUtc: null,
    proposedMembers: [], upcoming: [], advisor: null },
  pending: [],
  batches: [{ batchId: 'batch-12', phase: 'running', baseSha: 'b'.repeat(40), candidateSha: 'c'.repeat(40),
    members: [{ taskKey: 'AGT-3410', replay: 'applied', released: false }],
    runs: [{ batchRunId: 'run-12', host: 'agent-runner-01', startedAtUtc: '2026-11-03T09:00:00Z' }] }],
  members: { 'AGT-3410': { status: 'in-batch', batchId: 'batch-12', phase: 'running', position: 1, size: 2 } },
};

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [heldCard], progress: [], failedPickup: [],
    codeNotComplete: [], review: [], autoReview: [memberCard], humanReview: [], escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/projects/settings**', route => json(route, {}));
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, route => json(route, []));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-11-03T09:00:00Z', ttlSeconds: 600, snapshots: [] }));
  await page.route('**/api/runner/orchestrator-feed**', route => json(route, { entries: [], generatedAtUtc: '2026-11-03T09:00:00Z' }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/projects/*/batch-gate/state?history=10', route => json(route, batchState));
  for (const card of [heldCard, memberCard]) {
    await page.route(new RegExp(`/api/tasks/${card.id}/runs(\\?|$)`), route => json(route, { runs: [] }));
    await page.route(new RegExp(`/api/tasks/${card.id}/session-events(\\?|$)`), route => json(route, { events: [], sessionChain: [] }));
    await page.route(new RegExp(`/api/tasks/${card.id}/pipeline(\\?|$)`), route => json(route, {
      pipeline: { id: 'task', displayName: 'Task pipeline', version: 1, pre: [], core: [], post: [], allSteps: [] },
      execution: null, cost: null, config: {},
    }));
    await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${card.id}(\\?|$)`), route => json(route, {
      info: card, promptMarkdown: `# ${card.title}`, statusMarkdown: null, log: [], promptHistory: [],
      contextUsage: null, reviewEvidence: [], summaryState: null,
    }));
  }
}

async function openDetail(page: Page, id: string): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await dismissDevErrorDialog(page);
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
}

async function captureThemes(page: Page, name: string): Promise<void> {
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.waitForTimeout(300);
    await page.screenshot({ path: path.join(RESULTS_DIR, `${name}-${theme}.png`) });
  }
}

test.beforeEach(() => { mkdirSync(RESULTS_DIR, { recursive: true }); });

test('a 2-ready card held by a dependency shows no batch verification section', async ({ page }) => {
  let stateServed = false;
  page.on('response', response => { if (response.url().includes('/batch-gate/state')) stateServed = true; });
  await installRoutes(page);
  await openDetail(page, heldCard.id);
  await expect(page.getByText('Held by a dependency gate').first()).toBeVisible();
  // The state has arrived and lists a member, so the absence is the visibility rule, not loading.
  await expect.poll(() => stateServed).toBe(true);
  await expect(page.getByTestId('task-batch-verification')).toHaveCount(0);
  await expect(page.getByText('This card is not in a batch.')).toHaveCount(0);
  await expect(page.getByText('Loading batch verification')).toHaveCount(0);
  await captureThemes(page, 'batch-detail-held-2-ready');
});

test('a batch member in 4-auto-review shows its batch verification section', async ({ page }) => {
  await installRoutes(page);
  await openDetail(page, memberCard.id);
  const section = page.getByTestId('task-batch-verification');
  await expect(section).toBeVisible();
  await expect(section.getByTestId('task-batch-summary')).toContainText('in-batch');
  await expect(section.getByTestId('task-batch-summary')).toContainText('Batch batch-12');
  await expect(section.getByTestId('task-batch-summary')).toContainText('1 of 2');
  await expect(section.getByRole('button', { name: 'Open batch panel' })).toHaveClass(/\bbtn--ghost\b/);
  await captureThemes(page, 'batch-detail-member-4-auto-review');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.waitForTimeout(300);
    await section.screenshot({ path: path.join(RESULTS_DIR, `batch-detail-member-section-${theme}.png`) });
  }
});
