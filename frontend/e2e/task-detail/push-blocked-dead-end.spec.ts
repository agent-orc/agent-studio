import { test, expect, type Page, type Route } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3695: a Post Processing card whose integration merged locally but whose
 * publication was refused (the approved merge result is not on the local
 * integration line) names the failure in plain words and offers exactly one
 * owner action, Retry integration, which replays the publication without a
 * new review round. Fully mocked; light, dark and 390 px screenshots.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/push-blocked';
const JOB_ID = 'push-blocked-test';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';
const REASON = 'Publication stopped: the approved merge result c58e247a1 is not on the local develop integration line, '
  + 'so nothing was pushed to origin/develop. The reviewed delivery is unchanged; Retry integration replays it '
  + 'without a new review round.';

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

function detail() {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Concept path dossier',
      state: '4-auto-review',
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-haiku-4-5',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/4-auto-review/${JOB_ID}`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: null,
      commits: [],
      integration: {
        status: 'conflict-skipped', integrationBranch: 'develop', deliveryRef: `task/${JOB_ID}`, sha: null,
        detail: REASON,
        failure: {
          code: 'integration-push-blocked', label: 'Integration push blocked', reason: REASON,
          stage: 'post-merge-into-develop-push', rebaseRecoveryAvailable: false, replayAvailable: true,
        },
      },
      ownerClientId: 'local-default',
      createdAt: '2026-11-09T12:00:00Z',
      sessionChain: [],
      provenance: { branch: `task/${JOB_ID}`, base: 'base0000', transitions: [], merge: null },
    },
    promptMarkdown: '# Concept path\n\nAdd the concept path dossier.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    titleHistory: [],
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installBaseRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', (route) => json(route, []));
  await page.route('**/hubs/v1/studio/negotiate**', (route) => json(route, {
    connectionId: 'mock-jobs-connection', connectionToken: 'mock-jobs-connection',
    negotiateVersion: 1,
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
  }));
  await page.routeWebSocket('**/hubs/v1/studio**', (socket) => {
    socket.onMessage((message) => {
      if (String(message).includes('"protocol"')) socket.send('{}\x1e');
    });
  });
  await page.route('**/api/v1/studio/auth/status', (route) => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/tasks/*/runs**', (route) =>
    json(route, { runs: [], runnerEvents: [], hasActiveRun: false }));
  await page.route('**/api/tasks', (route) => json(route, []));
  await page.route('**/api/v1/studio/board**', (route) => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
    escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', (route) => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', (route) => json(route, {
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/clients', (route) => json(route, []));
  await page.route('**/api/agent-rules**', (route) => json(route, []));
  await page.route('**/api/cli/usage**', (route) => json(route, { items: [] }));
  await page.route('**/api/cli/quota**', (route) => json(route, { at: '2026-06-09T00:00:00Z', snapshots: [] }));
  await page.route(/\/api\/projects\/[^/]+\/workbenches(\?|$)/, (route) => json(route, { items: [] }));
  await page.route(/\/api\/tasks\/[^/]+\/pipeline(\?|$)/, (route) => json(route, null));
  await page.route(/\/api\/tasks\/[^/]+\/session-events(\?|$)/, (route) => json(route, {
    events: [], sessionChain: [], currentSessionId: null,
  }));
  await page.route(/\/api\/tasks\/[^/]+\/runs(\?|$)/, (route) => json(route, {
    runCount: 0, runs: [], promptEntries: [], runnerEvents: [],
  }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) => json(route, {
    projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } },
  }));
}

async function installJobRoutes(page: Page, retries: string[]): Promise<void> {
  await page.route(new RegExp(`/api/tasks/${JOB_ID}/runs(\\?|$)`), (route) =>
    json(route, { runs: [], runnerEvents: [], hasActiveRun: false }));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${JOB_ID}(\\?|$)`), (route) => json(route, detail()));
  await page.route(new RegExp(`/api/tasks/${JOB_ID}/delivery-claim(\\?|$)`), (route) => json(route, {
    taskKey: JOB_ID, jobId: JOB_ID, lane: '4-auto-review', deliveryRef: `task/${JOB_ID}`,
    integrationBranch: 'develop', releaseBranch: 'main', containmentStatus: 'not-integrated',
    integrated: false, released: null, integratedSha: null, mergeCommit: null, mergeSubject: null,
    mergedAtUtc: null, hasIntegrationRecord: false, class: 'delivery-not-integrated', findings: [],
    completionClaim: null, commits: [], detail: null,
  }));
  await page.route(new RegExp(`/api/tasks/${JOB_ID}/failure/continue(\\?|$)`), (route) =>
    json(route, { available: true, reason: null }));
  await page.route(new RegExp(`/api/tasks/${JOB_ID}/integration/retry(\\?|$)`), (route) => {
    retries.push(route.request().method());
    return json(route, { status: 'integrated', reason: 'The delivery integrated without a new review round.' });
  });
}

test.describe('Push blocked dead end', () => {
  test.use({ serviceWorkers: 'block' });

  test('names the refused publication and offers Retry integration as the one action', async ({ page }) => {
    const retries: string[] = [];
    await installBaseRoutes(page);
    await installJobRoutes(page, retries);
    await page.route('**/api/usage/cockpit**', (route) => route.fulfill({ status: 503, body: '{}' }));
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
      { waitUntil: 'domcontentloaded' });

    const deadEnd = page.getByTestId('integration-dead-end');
    await expect(deadEnd).toBeVisible({ timeout: 15_000 });
    await expect(deadEnd).toHaveAttribute('data-kind', 'integration-push-blocked');
    await expect(deadEnd).toContainText('Push blocked');
    await expect(deadEnd).toContainText(REASON);
    await expect(deadEnd).not.toContainText('sha-not-on-branch');
    await expect(deadEnd.locator('.dead-end__actions button')).toHaveCount(1);
    await expect(deadEnd.getByTestId('dead-end-continue')).toHaveCount(0);
    const retry = deadEnd.getByTestId('dead-end-retry');
    await expect(retry).toHaveText('Retry integration');

    for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
      await page.setViewportSize(viewport);
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await dismissDevErrorDialog(page);
        await expect(retry).toBeVisible();
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        if (RESULTS_DIR) {
          fs.mkdirSync(RESULTS_DIR, { recursive: true });
          await deadEnd.screenshot({ path: path.join(RESULTS_DIR, `push-blocked-dead-end-${viewport.width}-${theme}.png`) });
        }
      }
    }

    await retry.click();
    await expect.poll(() => retries).toEqual(['POST']);
  });
});
