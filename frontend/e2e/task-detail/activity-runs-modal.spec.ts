import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Activity-pane slim-down: the RUNS bar moved out of the activity log.
 *
 * The activity-panel header now carries a compact `N Runs` chip plus a
 * small info `(i)` button. Clicking the chip opens a modal that holds the
 * full run list / run picker (the run-timeline that used to sit inline
 * below the plan strip). The info button explains what a "run" is via an
 * instant tooltip.
 *
 * These scenarios run against fully-mocked API routes so there is no
 * dependency on a running backend or a real git repository.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/activity-runs';
const JOB_ID = 'activity-runs-test';

interface RunFixture {
  index: number;
  intent: string;
  status: string;
  lineStart: number | null;
  lineEnd: number | null;
}

function makeRun(f: RunFixture) {
  const sha = `${f.index}`.padStart(40, `${f.index}`).slice(0, 40);
  return {
    index: f.index,
    intent: f.intent,
    startedAt: '2026-05-29T10:00:00Z',
    endedAt: '2026-05-29T10:05:00Z',
    status: f.status,
    cli: 'claude',
    exitCode: f.status === 'failed' ? 1 : 0,
    durationSeconds: 300,
    inputSessionId: null,
    capturedSessionId: null,
    resumed: false,
    reason: null,
    userFollowup: f.index === 1 ? 'initial prompt' : 'please continue',
    lineStart: f.lineStart,
    lineEnd: f.lineEnd,
    headShaBefore: sha,
    headShaAfter: sha,
  };
}

const RUNS = [
  makeRun({ index: 1, intent: 'start', status: 'completed', lineStart: 1, lineEnd: 40 }),
  makeRun({ index: 2, intent: 'continue', status: 'failed', lineStart: 41, lineEnd: 80 }),
];

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Activity runs fixture',
      state,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${JOB_ID}`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: null,
      commits: [],
      ownerClientId: 'local-default',
    },
    promptMarkdown: 'Test prompt.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installRoutes(page: Page, runs: object[]): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail('3-progress');

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      }),
    }),
  );
  await page.route('**/api/tasks', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        preparation: [],
        orchestratorPrep: [],
        ready: [],
        progress: [],
        failedPickup: [],
        autoReview: [],
        humanReview: [],
        completed: [],
        archive: [],
      }),
    }),
  );
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
      ]),
    }),
  );
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(/\/api\/(?:projects|v1\/projects(?:\?|$))/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }),
  );
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        isDev: false,
        devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
      }),
    }),
  );
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }),
  );
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-29T00:00:00Z', snapshots: [] }),
    }),
  );
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'manual',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    }),
  );

  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs/\\d+/commits(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        runIndex: 1,
        startedAt: '2026-05-29T10:00:00Z',
        endedAt: '2026-05-29T10:05:00Z',
        headShaBefore: null,
        headShaAfter: null,
        source: 'wall-clock',
        commits: [],
      }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        runCount: runs.length,
        firstStartedAt: '2026-05-29T10:00:00Z',
        lastActivityAt: '2026-05-29T10:05:00Z',
        hasActiveRun: false,
        runs,
        reviewAttemptEpoch: 1,
        reviewAttemptCycles: [
          {
            epoch: 1,
            isCurrent: true,
            startedAt: '2026-05-29T09:45:00Z',
            endedAt: null,
            actor: 'human:operator@example.com',
            reason: 'Runner repaired; assess the card from fresh evidence.',
            fromState: '5e-escalated',
            toState: '4-auto-review',
            rotatedArtifacts: 3,
          },
          {
            epoch: 0,
            isCurrent: false,
            startedAt: '2026-05-28T20:00:00Z',
            endedAt: '2026-05-29T09:45:00Z',
            actor: null,
            reason: 'Initial review cycle.',
            fromState: null,
            toState: null,
            rotatedArtifacts: 0,
          },
        ],
      }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ events: [], sessionChain: [] }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/claude-session(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/status(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        isRepo: true,
        branch: 'main',
        filesChanged: 0,
        totalAdded: 0,
        totalRemoved: 0,
        files: [],
        error: null,
      }),
    }),
  );
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }),
  );
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

async function dismissErrorDialog(page: Page): Promise<void> {
  const overlay = page.getByTestId('error-dialog-overlay');
  if (await overlay.isVisible().catch(() => false)) {
    await page.evaluate(() => {
      const el = document.querySelector<HTMLElement>('[data-testid="error-dialog-overlay"]');
      el?.click();
    });
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => undefined);
  }
}

async function openDetail(page: Page): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await dismissErrorDialog(page);
  await expect(page.getByTestId('activity-runs')).toBeVisible({ timeout: 10_000 });
}

test.describe('Activity-pane: compact N Runs chip + modal', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: false, protocol: true, git: false }),
        );
        localStorage.setItem('taskboard.activeInspectorTab', '"activity"');
      } catch {
        return;
      }
    });
  });

  test('header shows a compact "N Runs" chip and an info button', async ({ page }) => {
    await installRoutes(page, RUNS);
    await openDetail(page);

    const chip = page.getByTestId('activity-runs-open');
    await expect(chip).toBeVisible();
    await expect(chip).toHaveText('2 Runs');
    await expect(page.getByTestId('activity-runs-info')).toBeVisible();

    // The inline run-timeline must NOT be on the page until the modal opens.
    await expect(page.getByTestId('runs-modal')).toHaveCount(0);
    await expect(page.getByTestId('runs-icon-row')).toHaveCount(0);

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'activity-runs-chip.png') });
    }
  });

  test('chip uses the singular "1 Run" for a single run', async ({ page }) => {
    await installRoutes(page, [RUNS[0]]);
    await openDetail(page);
    await expect(page.getByTestId('activity-runs-open')).toHaveText('1 Run');
  });

  test('clicking the chip opens the run-list modal with the full picker', async ({ page }) => {
    await installRoutes(page, RUNS);
    await openDetail(page);

    await page.getByTestId('activity-runs-open').click();

    const modal = page.getByTestId('runs-modal');
    await expect(modal).toBeVisible();
    await expect(page.getByTestId('runs-icon-row')).toBeVisible();
    await expect(page.getByTestId('run-icon-1')).toBeVisible();
    await expect(page.getByTestId('run-icon-2')).toBeVisible();
    await expect(page.getByTestId('runs-aggregate')).toContainText('2 runs');

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'activity-runs-modal-open.png') });
    }
  });

  test('the modal shows the current review epoch and closed cycle history', async ({ page }) => {
    await installRoutes(page, RUNS);
    await openDetail(page);

    await page.getByTestId('activity-runs-open').click();

    const history = page.getByTestId('review-attempt-history');
    await expect(history).toBeVisible();
    await expect(page.getByTestId('review-attempt-current')).toContainText('Epoch 1');
    await expect(page.getByTestId('review-attempt-cycle-1')).toContainText(
      'Runner repaired; assess the card from fresh evidence.',
    );
    await expect(page.getByTestId('review-attempt-cycle-1')).toContainText('3 artifacts archived');
    await expect(page.getByTestId('review-attempt-cycle-0')).toContainText('Initial review cycle.');

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'activity-runs-review-epochs.png') });
    }
  });

  test('review epoch history remains legible in dark theme', async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem('atp.studio.theme', 'dark');
      } catch {
        return;
      }
    });
    await installRoutes(page, RUNS);
    await openDetail(page);

    await page.getByTestId('activity-runs-open').click();

    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
    await expect(page.getByTestId('review-attempt-cycle-1')).toBeVisible();
    await expect(page.getByTestId('review-attempt-cycle-0')).toContainText('Closed');

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'activity-runs-review-epochs-dark.png') });
    }
  });

  test('the close button, backdrop, and Escape all dismiss the modal', async ({ page }) => {
    await installRoutes(page, RUNS);
    await openDetail(page);

    const open = page.getByTestId('activity-runs-open');
    const modal = page.getByTestId('runs-modal');

    // Close button.
    await open.click();
    await expect(modal).toBeVisible();
    await page.getByTestId('runs-modal-close').click();
    await expect(modal).toHaveCount(0);

    // Backdrop click (the backdrop is the test-id element; clicking it at a
    // corner hits the backdrop, not the centered dialog).
    await open.click();
    await expect(modal).toBeVisible();
    await modal.click({ position: { x: 5, y: 5 } });
    await expect(modal).toHaveCount(0);

    // Escape key.
    await open.click();
    await expect(modal).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(modal).toHaveCount(0);
  });

  test('run selection still works inside the modal (no regression)', async ({ page }) => {
    await installRoutes(page, RUNS);
    await openDetail(page);

    await page.getByTestId('activity-runs-open').click();
    await expect(page.getByTestId('runs-modal')).toBeVisible();

    // Clicking a run icon expands that run's popover card — the run-picker
    // behavior that used to live inline still works in the modal.
    await page.getByTestId('run-icon-1').click();
    await expect(page.getByTestId('run-popover-1')).toBeVisible();
  });

  test('filtering the log to a run closes the modal', async ({ page }) => {
    await installRoutes(page, RUNS);
    await openDetail(page);

    await page.getByTestId('activity-runs-open').click();
    await page.getByTestId('run-icon-1').click();
    await expect(page.getByTestId('run-popover-1')).toBeVisible();

    await page
      .getByRole('button', { name: 'Filter activity log to this run' })
      .first()
      .click();

    // onRunFilter applies the range and closes the modal so the user can
    // see the filtered log underneath.
    await expect(page.getByTestId('runs-modal')).toHaveCount(0);
  });
});
