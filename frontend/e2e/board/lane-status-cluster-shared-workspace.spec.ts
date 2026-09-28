import { test, expect, type Page } from '@playwright/test';

/**
 * Shared-workspace lane-status cluster.
 *
 * Bug: in a multi-backend setup where two backends (dev + stable) watch the
 * same workspace and backend A picks up a task, backend B's UI used to show
 * only the MANUAL pill on the In-Progress lane. The operator saw a task in
 * 3-progress that was genuinely running but the lane header read MANUAL,
 * with no RUNNING indicator. Cause: the cluster derived RUNNING from
 * `/api/runner/status.activeExecution`, which is per-backend in-memory
 * state. The other backend's runner had `activeJobId=null`.
 *
 * Fix: the lane now derives RUNNING from any 3-progress job whose
 * `execution.status === 'running'` as a fallback. When the local runner
 * does not own the active execution, the pill is flagged with
 * `data-foreign="true"`, the meta carries " · external", and the tooltip
 * names that another backend is driving the run. The mode pill stays
 * accurate (this backend's runner is genuinely manual or auto) but its
 * tooltip is rewritten to acknowledge the running foreign task so the
 * operator does not misread "MANUAL" as "nothing is happening".
 */

const PROJECT = 'fixture-shared-ws';
const WATCH_PATH = 'C:/fixtures/shared-workspace';
const FOREIGN_JOB_ID = 'shared-task-7';

function makeJob(opts: { id: string; state: string; order: number; title: string; running: boolean }) {
  return {
    id: opts.id,
    jobKey: `${WATCH_PATH}::${opts.id}`,
    title: opts.title,
    state: opts.state,
    order: opts.order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-05-28T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${opts.state}/${opts.id}`,
    lastActivity: '2026-05-28T08:30:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    useOwnSession: null,
    lastUsage: null,
    execution: opts.running
      ? {
          executionId: `exec-${opts.id}`,
          jobId: opts.id,
          jobKey: `${WATCH_PATH}::${opts.id}`,
          status: 'running',
          processId: 5796,
          startedAt: '2026-05-28T08:25:00Z',
          finishedAt: null,
          exitCode: null,
          durationSeconds: null,
          model: 'claude-opus-4-7',
          runOutcome: null,
        }
      : null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
  };
}

interface RunnerStatusOverride {
  mode: string;
  activeJobId: string | null;
  activeExecution: unknown;
}

async function installRoutes(
  page: Page,
  groupedPayload: Record<string, unknown[]>,
  runnerOverride: RunnerStatusOverride,
) {
  await page.route('**/api/**', (route) => {
    const url = route.request().url();
    if (url.endsWith('/api/tasks')) {
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    route
      .fulfill({ status: 200, contentType: 'application/json', body: '[]' })
      .catch(() => undefined);
  });

  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(groupedPayload),
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

  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: runnerOverride.mode,
            activeJobId: runnerOverride.activeJobId,
            activeExecution: runnerOverride.activeExecution,
            queuedJobIds: [],
            modeReason: null,
            modeChangedAt: null,
            modeSource: null,
          },
        },
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
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-28T08:00:00Z', sessions: [] }),
    }),
  );
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-28T08:00:00Z', ttlSeconds: 600, snapshots: [] }),
    }),
  );
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  // Suppress the update-failed toast that intercepts pointer events over
  // the lane header in routed tests. UpdateService runs on port 5039 and
  // is unreachable in routed tests; the bridge surfaces an "Update failed"
  // toast over the lane header, which then steals every hover. Routing
  // the bare `/update/**` namespace returns a clean idle snapshot.
  await page.route(/\/update\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        isRunning: false,
        phase: 'idle',
        currentRunId: null,
        lastRunFinishedAt: null,
        message: null,
        verificationFailures: [],
      }),
    }),
  );
  await page.route(/\/update\/history(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
}

async function seedBoardTab(page: Page) {
  await page.addInitScript(() => {
    localStorage.setItem(
      'atp.studio.tabs.v1',
      JSON.stringify({
        v: 1,
        tabs: [{ kind: 'board', projectName: '__all__' }],
        activeKey: 'board:__all__',
      }),
    );
  });
}

async function readTooltipForLocator(page: Page, locator: ReturnType<Page['locator']>): Promise<string> {
  await locator.hover();
  const root = page.getByTestId('cac-tooltip').first();
  await root.waitFor({ state: 'attached', timeout: 4000 });
  return ((await root.textContent()) ?? '').trim();
}

test.describe('Lane status cluster — shared workspace / multi-backend', () => {
  test('foreign backend running on shared workspace: RUNNING pill shows with foreign hint while local mode stays MANUAL', async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1000 });
    const foreignJob = makeJob({
      id: FOREIGN_JOB_ID,
      state: '3-progress',
      order: 1,
      title: 'Foreign-backend run',
      running: true,
    });
    const groupedPayload = {
      backlog: [],
      preparation: [],
      orchestratorPrep: [],
      ready: [],
      progress: [foreignJob],
      failedPickup: [],
      review: [],
      autoReview: [],
      humanReview: [],
      completed: [],
      archive: [],
    };
    await seedBoardTab(page);
    // Local runner is manual + idle: another backend's runner owns this run.
    await installRoutes(page, groupedPayload, {
      mode: 'manual',
      activeJobId: null,
      activeExecution: null,
    });

    await page.goto('/?includeFixtures=true');
    await page.waitForLoadState('domcontentloaded');
    await page.getByTestId('lane-3-progress').first().waitFor({ state: 'visible', timeout: 8000 });

    const runningPill = page.getByTestId('lane-running-pill-3-progress').first();
    await expect(runningPill).toBeVisible({ timeout: 6000 });
    await expect(runningPill).toHaveAttribute('data-job-id', FOREIGN_JOB_ID);
    await expect(runningPill).toHaveAttribute('data-foreign', 'true');
    // The running indicator is the dot ONLY — no visible label to truncate.
    // The job id, elapsed time and foreign-backend note live in the tooltip.
    await expect(runningPill).toHaveText('');
    const runTip = await readTooltipForLocator(page, runningPill);
    expect(runTip).toContain(FOREIGN_JOB_ID);
    expect(runTip.toLowerCase()).toContain('another backend');

    // Mode pill stays MANUAL (this backend's truth) but the tooltip
    // acknowledges the foreign run so the operator does not read MANUAL
    // as "the system is doing nothing".
    const modePill = page.getByTestId('lane-auto-toggle-3-progress').first();
    await expect(modePill).toHaveAttribute('data-mode-kind', 'manual');
    const modeTip = await readTooltipForLocator(page, modePill);
    expect(modeTip.toLowerCase()).toContain('shared workspace');

    // Visual evidence for review. Snapped at the lane level so the cluster
    // sits in context with the lane title + count + foreign-run pill.
    await page.getByTestId('lane-3-progress').first().screenshot({
      path: 'screenshots/lane-status-cluster/foreign-run-manual.png',
    });
  });

  test('foreign backend running while local mode is auto-continuous: RUNNING + AUTO chips both visible with shared-workspace tooltip', async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1000 });
    const foreignJob = makeJob({
      id: FOREIGN_JOB_ID,
      state: '3-progress',
      order: 1,
      title: 'Foreign-backend run',
      running: true,
    });
    const groupedPayload = {
      backlog: [],
      preparation: [],
      orchestratorPrep: [],
      ready: [],
      progress: [foreignJob],
      failedPickup: [],
      review: [],
      autoReview: [],
      humanReview: [],
      completed: [],
      archive: [],
    };
    await seedBoardTab(page);
    await installRoutes(page, groupedPayload, {
      mode: 'auto-continuous',
      activeJobId: null,
      activeExecution: null,
    });

    await page.goto('/?includeFixtures=true');
    await page.waitForLoadState('domcontentloaded');
    await page.getByTestId('lane-3-progress').first().waitFor({ state: 'visible', timeout: 8000 });

    const runningPill = page.getByTestId('lane-running-pill-3-progress').first();
    await expect(runningPill).toBeVisible({ timeout: 6000 });
    await expect(runningPill).toHaveAttribute('data-foreign', 'true');

    const modePill = page.getByTestId('lane-auto-toggle-3-progress').first();
    await expect(modePill).toHaveAttribute('data-mode-kind', 'auto');
    await expect(modePill).toContainText('AUTO');
    const modeTip = await readTooltipForLocator(page, modePill);
    expect(modeTip.toLowerCase()).toContain('shared workspace');

    await page.getByTestId('lane-3-progress').first().screenshot({
      path: 'screenshots/lane-status-cluster/foreign-run-auto.png',
    });
  });

  test('local runner owns the active execution: RUNNING pill renders without foreign flag', async ({ page }) => {
    const job = makeJob({
      id: FOREIGN_JOB_ID,
      state: '3-progress',
      order: 1,
      title: 'Local run',
      running: true,
    });
    const groupedPayload = {
      backlog: [],
      preparation: [],
      orchestratorPrep: [],
      ready: [],
      progress: [job],
      failedPickup: [],
      review: [],
      autoReview: [],
      humanReview: [],
      completed: [],
      archive: [],
    };
    await seedBoardTab(page);
    await installRoutes(page, groupedPayload, {
      mode: 'auto-continuous',
      activeJobId: FOREIGN_JOB_ID,
      activeExecution: job.execution,
    });

    await page.goto('/?includeFixtures=true');
    await page.waitForLoadState('domcontentloaded');
    await page.getByTestId('lane-3-progress').first().waitFor({ state: 'visible', timeout: 8000 });

    const runningPill = page.getByTestId('lane-running-pill-3-progress').first();
    await expect(runningPill).toBeVisible({ timeout: 6000 });
    // The directive emits the attribute only when foreign is true; absence
    // is the signal that the local runner owns the execution.
    const hasForeignAttr = await runningPill.evaluate((el) => el.hasAttribute('data-foreign'));
    expect(hasForeignAttr).toBe(false);
    // Dot-only indicator: no visible label in either the local or foreign case.
    await expect(runningPill).toHaveText('');
    const runTip = await readTooltipForLocator(page, runningPill);
    expect(runTip.toLowerCase()).not.toContain('another backend');
  });
});
