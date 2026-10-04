import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Regression Radar info-button.
 *
 * Asserts the ⓘ trigger sits in the radar header and opens the shared
 * concept-doc modal explaining how the radar works. Fully mocked: the
 * radar result and the concept-doc payload are stubbed, so no backend or
 * git repository is needed.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/radar-info';
const JOB_ID = 'radar-info-test';

const CONCEPT_DOC = {
  topic: 'regression-radar',
  title: 'Regression Radar',
  body:
    'Regression Radar watches the **spec and test files** a task touched and ' +
    'flags the changes most likely to hide a regression.\n\n' +
    '## How changes are classified\n\n' +
    '- **Intended** (green) — the change reads as healthy.\n' +
    '- **At Risk** (amber) — a spec was modified but its companion did not.\n' +
    '- **Drift** (red) — a spec was deleted with no replacement.\n',
};

function makeRadarResult() {
  return {
    overallStatus: 'Drift',
    intendedCount: 1,
    atRiskCount: 0,
    driftCount: 1,
    totalSpecChanges: 2,
    baselineSha: 'aaaa111',
    headSha: 'bbbb222',
    error: null,
    generatedAt: '2026-06-03T09:00:00Z',
    durationMs: 420,
    taskGroups: [],
    entries: [
      {
        jobId: null,
        jobTitle: null,
        path: 'frontend/src/app/services/task.service.spec.ts',
        fileName: 'task.service.spec.ts',
        gitStatus: 'M',
        category: 'Intended',
        reason: 'Spec changed alongside implementation (task.service.ts)',
        companionPath: 'frontend/src/app/services/task.service.ts',
        companionChanged: true,
        linesAdded: 12,
        linesRemoved: 3,
        overrideCategory: null,
        overrideReason: null,
      },
      {
        jobId: null,
        jobTitle: null,
        path: 'backend.Tests/LegacyFlowTests.cs',
        fileName: 'LegacyFlowTests.cs',
        gitStatus: 'D',
        category: 'Drift',
        reason: "Spec deleted without replacement in this task's attributed commits",
        companionPath: 'LegacyFlow.cs',
        companionChanged: false,
        linesAdded: 0,
        linesRemoved: 88,
        overrideCategory: null,
        overrideReason: null,
      },
    ],
  };
}

function makeNoAttributedCommitsResult() {
  return {
    overallStatus: 'Intended',
    intendedCount: 0,
    atRiskCount: 0,
    driftCount: 0,
    totalSpecChanges: 0,
    baselineSha: null,
    headSha: null,
    error: null,
    generatedAt: '2026-06-03T09:00:00Z',
    durationMs: 0,
    taskGroups: [],
    entries: [],
  };
}

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Regression radar info-button fixture',
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

async function installRoutes(page: Page, state: string, radarResult = makeRadarResult()) {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail(state);

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
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }),
  );
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-06-02T00:00:00Z', snapshots: [] }),
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

  await page.route(new RegExp(`/api/tasks/${idEsc}/regression-radar(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(radarResult),
    }),
  );
  await page.route('**/api/concept-docs/regression-radar', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(CONCEPT_DOC),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ events: [], sessionChain: [] }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/pipeline(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        pipeline: {
          id: 'standard-task-pipeline',
          displayName: 'Standard task pipeline',
          version: 1,
          pre: [],
          core: [],
          post: [],
          allSteps: [],
        },
        execution: null,
        cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
        config: {},
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

test.describe('Regression radar info-button', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: true, protocol: false, git: false }),
        );
      } catch {
        /* private mode */
      }
    });
  });

  test('opens the concept-doc modal from the radar header', async ({ page }) => {
    await installRoutes(page, '5-human-review');
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

    await dismissErrorDialog(page);

    const radar = page.getByTestId('regression-radar');
    await expect(radar).toBeVisible({ timeout: 10_000 });

    const trigger = radar.getByTestId('info-button-regression-radar');
    await expect(trigger).toBeVisible();
    await expect(trigger).toHaveAttribute('aria-label', 'How does Regression Radar work?');

    if (RESULTS_DIR) {
      await radar.scrollIntoViewIfNeeded();
      await radar.screenshot({ path: path.join(RESULTS_DIR, 'radar-header-info-trigger.png') });
    }

    await trigger.click();
    await expect(trigger).toHaveAttribute('aria-expanded', 'true');

    const modal = page.getByTestId('info-button-modal-regression-radar');
    await expect(modal).toBeVisible();
    await expect(modal).toContainText('Regression Radar');
    await expect(modal).toContainText('Drift');

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'radar-info-modal.png'), fullPage: true });
    }
  });

  test('concept-doc modal is responsive: grows on a wide viewport, shrinks on a narrow one', async ({ page }) => {
    await installRoutes(page, '5-human-review');

    // --- Wide viewport: the modal should claim much more than the old fixed
    //     520px column (token cap is min(90vw, 880px) -> ~880px at 1600px). ---
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const radar = page.getByTestId('regression-radar');
    await expect(radar).toBeVisible({ timeout: 10_000 });
    await radar.getByTestId('info-button-regression-radar').click();

    const modal = page.getByTestId('info-button-modal-regression-radar');
    await expect(modal).toBeVisible();

    const wideBox = await modal.boundingBox();
    expect(wideBox).not.toBeNull();
    // Far wider than the legacy 520px panel, and near the 880px cap.
    expect(wideBox!.width).toBeGreaterThan(820);
    expect(wideBox!.width).toBeLessThanOrEqual(890);

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'radar-info-modal-wide-1600.png'), fullPage: false });
    }

    // --- Narrow viewport: the same modal collapses toward ~90vw instead of
    //     overflowing, proving the width is responsive rather than fixed. ---
    await page.setViewportSize({ width: 720, height: 900 });
    const narrowBox = await modal.boundingBox();
    expect(narrowBox).not.toBeNull();
    // ~90vw of 720 = 648; comfortably under the wide-viewport width and the cap.
    expect(narrowBox!.width).toBeLessThan(wideBox!.width);
    expect(narrowBox!.width).toBeLessThanOrEqual(0.92 * 720);
    expect(narrowBox!.width).toBeGreaterThan(560);

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'radar-info-modal-narrow-720.png'), fullPage: false });
    }
  });

  test('is compact by default: metadata shown, entries hidden until expanded', async ({ page }) => {
    await installRoutes(page, '5-human-review');
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

    await dismissErrorDialog(page);

    const radar = page.getByTestId('regression-radar');
    await expect(radar).toBeVisible({ timeout: 10_000 });

    // Metadata row: first..last attributed commit label + generation duration are visible in the compact view.
    await expect(radar.getByTestId('regression-radar-sha-range')).toHaveText('aaaa111..bbbb222');
    await expect(radar.getByTestId('regression-radar-duration')).toContainText('generated in 420 ms');

    // Compact default: the per-file entry list is collapsed (not rendered).
    await expect(radar.getByTestId('regression-radar-entries')).toHaveCount(0);

    const toggle = radar.getByTestId('regression-radar-details-toggle');
    await expect(toggle).toContainText('Show 2 spec changes');

    if (RESULTS_DIR) {
      await radar.scrollIntoViewIfNeeded();
      await radar.screenshot({ path: path.join(RESULTS_DIR, 'radar-compact-default.png') });
    }

    // Expand: entries appear, most-severe first (Drift before Intended).
    await toggle.click();
    await expect(toggle).toContainText('Hide 2 spec changes');

    const entries = radar.getByTestId('regression-radar-entries');
    await expect(entries).toBeVisible();
    const rows = radar.getByTestId('radar-entry');
    await expect(rows).toHaveCount(2);
    await expect(rows.first()).toContainText('LegacyFlowTests.cs');

    if (RESULTS_DIR) {
      await radar.scrollIntoViewIfNeeded();
      await radar.screenshot({ path: path.join(RESULTS_DIR, 'radar-expanded.png') });
    }
  });

  test('renders no attributed commits as a quiet inline note', async ({ page }) => {
    await installRoutes(page, '5-human-review', makeNoAttributedCommitsResult());
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

    await dismissErrorDialog(page);

    const note = page.getByTestId('regression-radar-empty');
    await expect(note).toBeVisible({ timeout: 10_000 });
    await expect(note).toContainText('No changes for this task');
    await expect(page.getByTestId('regression-radar')).toHaveCount(0);

    const box = await note.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.height).toBeLessThan(36);

    const borderWidth = await note.evaluate((el) => getComputedStyle(el).borderTopWidth);
    expect(borderWidth).toBe('0px');

    if (RESULTS_DIR) {
      await note.scrollIntoViewIfNeeded();
      await note.screenshot({ path: path.join(RESULTS_DIR, 'radar-no-attributed-commits-inline-note.png') });
    }
  });
});
