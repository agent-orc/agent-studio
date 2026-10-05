import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Per-run passed-context expander.
 *
 * Each run card in the run timeline can reveal the exact context string that
 * was handed to the agent for that run (prompt + prepended open-items +
 * resume/intent framing). The text is multi-KB so it is fetched lazily from
 * GET /api/tasks/{id}/runs/{index}/context only when the user clicks
 * "Show passed context"; the polled /runs list never carries it.
 *
 * Acceptance: (a) every run entry can show the context it was started with;
 * (b) works for the first run AND a resume/continue run. A run with no
 * captured context (older run) shows an explicit "Not captured" note instead
 * of a dead toggle.
 *
 * Runs against fully-mocked API routes - no backend or git repo required.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/run-context';
const JOB_ID = 'run-context-test';

const CONTEXT_RUN_1 = [
  '# Task prompt',
  '',
  'Implement the per-run passed-context view.',
  '',
  '## Open items carried forward',
  '- none (first run)',
].join('\n');

const CONTEXT_RUN_2 = [
  '# Resume context (continue)',
  '',
  '## Open items carried forward',
  '- finish the Playwright coverage',
  '',
  '## Follow-up intent',
  'please continue',
].join('\n');

interface RunFixture {
  index: number;
  intent: string;
  status: string;
  contextRef: string | null;
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
    resumed: f.intent === 'continue',
    reason: null,
    userFollowup: f.index === 1 ? 'initial prompt' : 'please continue',
    lineStart: (f.index - 1) * 40 + 1,
    lineEnd: f.index * 40,
    headShaBefore: sha,
    headShaAfter: sha,
    contextRef: f.contextRef,
  };
}

// Run 1: fresh start, context captured. Run 2: resume/continue, context
// captured. Run 3: legacy run with no captured context.
const RUNS = [
  makeRun({ index: 1, intent: 'start', status: 'completed', contextRef: 'logs/run-context/run-1.md' }),
  makeRun({ index: 2, intent: 'continue', status: 'completed', contextRef: 'logs/run-context/run-2.md' }),
  makeRun({ index: 3, intent: 'continue', status: 'failed', contextRef: null }),
];

const PROMPT_ENTRIES = [
  {
    index: 1,
    runIndex: 1,
    intent: 'start',
    at: '2026-05-29T10:00:00Z',
    label: 'Prompt #1',
    fileName: 'prompt.md',
    promptTokenSource: 'task-prompt',
    promptPreview: 'Implement the per-run passed-context view.',
    promptTokenEstimate: 18,
    contextTokenEstimate: 36,
    contextRef: 'logs/run-context/run-1.md',
    contextSnapshot: {
      source: 'captured-context',
      ref: 'logs/run-context/run-1.md',
      at: null,
      status: 'captured',
      tokenEstimate: 36,
      metrics: [],
    },
  },
  {
    index: 2,
    runIndex: 2,
    intent: 'continue',
    at: '2026-05-29T10:05:00Z',
    label: 'Prompt #2',
    fileName: 'prompt-1.md',
    promptTokenSource: 'prompt-history',
    promptPreview: 'please continue',
    promptTokenEstimate: 4,
    contextTokenEstimate: 31,
    contextRef: 'logs/run-context/run-2.md',
    contextSnapshot: {
      source: 'captured-context',
      ref: 'logs/run-context/run-2.md',
      at: null,
      status: 'captured',
      tokenEstimate: 31,
      metrics: [],
    },
  },
  {
    index: 3,
    runIndex: 3,
    intent: 'continue',
    at: '2026-05-29T10:10:00Z',
    label: 'Prompt #3',
    fileName: 'user-followup',
    promptTokenSource: 'user-followup',
    promptPreview: 'please continue',
    promptTokenEstimate: 4,
    contextTokenEstimate: null,
    contextRef: null,
    contextSnapshot: null,
  },
];

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Run context fixture',
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
    promptHistory: [
      {
        index: 1,
        fileName: 'prompt-1.md',
        markdown: 'please continue\n\nCapture the prompt and context token snapshots.',
        writtenAt: '2026-05-29T10:04:00Z',
      },
    ],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installRoutes(page: Page): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail('3-progress');

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {});
  });
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
  // The lazy per-run context endpoint. Echoes context keyed by the run index
  // parsed out of the URL so the two captured runs return distinct text.
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs/(\\d+)/context(\\?|$)`), (route) => {
    const m = route.request().url().match(/\/runs\/(\d+)\/context/);
    const idx = m ? Number(m[1]) : 0;
    const context = idx === 1 ? CONTEXT_RUN_1 : idx === 2 ? CONTEXT_RUN_2 : null;
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ runIndex: idx, context }),
    });
  });
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        runCount: RUNS.length,
        firstStartedAt: '2026-05-29T10:00:00Z',
        lastActivityAt: '2026-05-29T10:05:00Z',
        hasActiveRun: false,
        runs: RUNS,
        promptEntries: PROMPT_ENTRIES,
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
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {});
  }
}

async function openRunsModal(page: Page): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await dismissErrorDialog(page);
  await expect(page.getByTestId('activity-runs')).toBeVisible({ timeout: 10_000 });
  await page.getByTestId('activity-runs-open').click();
  await expect(page.getByTestId('runs-modal')).toBeVisible();
}

test.describe('Run timeline: per-run passed context', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: false, protocol: true, git: false }),
        );
        localStorage.setItem('taskboard.activeInspectorTab', '"activity"');
      } catch {
        /* private mode */
      }
    });
  });

  test('first run reveals the captured context lazily on click', async ({ page }) => {
    await installRoutes(page);
    await openRunsModal(page);

    await page.getByTestId('run-icon-1').click();
    await expect(page.getByTestId('run-popover-1')).toBeVisible();

    // Context is not fetched/rendered until the toggle is clicked.
    await expect(page.getByTestId('run-context-pre-1')).toHaveCount(0);

    const toggle = page.getByTestId('run-context-toggle-1');
    await expect(toggle).toHaveText('Show passed context');
    await toggle.click();

    const pre = page.getByTestId('run-context-pre-1');
    await expect(pre).toBeVisible();
    await expect(pre).toContainText('Implement the per-run passed-context view.');
    await expect(pre).toContainText('Open items carried forward');
    await expect(toggle).toHaveText('Hide passed context');

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'run-context-first-run-expanded.png') });
    }
  });

  test('prompt entries show prompt and context token snapshots', async ({ page }) => {
    await installRoutes(page);
    await openRunsModal(page);

    const rail = page.getByTestId('runs-icon-row');
    await expect(rail).toContainText('Prompt #1');
    await expect(rail).toContainText('Prompt #2');
    await expect(rail).toContainText('18 tokens');

    await page.getByTestId('run-icon-2').click();
    const detail = page.getByTestId('run-popover-2');
    await expect(detail).toBeVisible();
    await expect(detail).toContainText('Prompt tokens');
    await expect(detail).toContainText('4 tokens');
    await expect(detail).toContainText('Context size');
    await expect(detail).toContainText('31 tokens');
    await expect(detail).toContainText('captured at run start');
    await expect(page.getByTestId('run-prompt-pre-2')).toContainText('Capture the prompt and context token snapshots.');
    await expect(page.getByTestId('run-context-pre-2')).toHaveCount(0);

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'run-prompts-token-snapshots.png') });
    }
  });

  test('resume/continue run shows its own distinct resume context', async ({ page }) => {
    await installRoutes(page);
    await openRunsModal(page);

    await page.getByTestId('run-icon-2').click();
    await expect(page.getByTestId('run-popover-2')).toBeVisible();
    await page.getByTestId('run-context-toggle-2').click();

    const pre = page.getByTestId('run-context-pre-2');
    await expect(pre).toBeVisible();
    await expect(pre).toContainText('Resume context (continue)');
    await expect(pre).toContainText('finish the Playwright coverage');

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'run-context-resume-run-expanded.png') });
    }
  });

  test('toggle hides the context again without refetching', async ({ page }) => {
    await installRoutes(page);
    await openRunsModal(page);

    await page.getByTestId('run-icon-1').click();
    const toggle = page.getByTestId('run-context-toggle-1');
    await toggle.click();
    await expect(page.getByTestId('run-context-pre-1')).toBeVisible();

    await toggle.click();
    await expect(page.getByTestId('run-context-pre-1')).toHaveCount(0);
    await expect(toggle).toHaveText('Show passed context');
  });

  test('a run without captured context shows an explicit note, no toggle', async ({ page }) => {
    await installRoutes(page);
    await openRunsModal(page);

    await page.getByTestId('run-icon-3').click();
    await expect(page.getByTestId('run-popover-3')).toBeVisible();

    await expect(page.getByTestId('run-context-toggle-3')).toHaveCount(0);
    await expect(page.getByTestId('run-popover-3')).toContainText('Not captured for this run.');

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'run-context-not-captured.png') });
    }
  });
});
