import { test, expect, type Page, type Route } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * Human Review acceptance reads "Accept" in every integration state.
 * Live Git membership supplies the landing evidence and action gate.
 *
 * UI-feedback 2026-07-09: the redundant landed-status pill that used to sit
 * beside the primary (`studio-triage-merge-status`) was removed. The task's
 * on-develop state is shown once, at the task commit (git pane landed ladder),
 * not repeated in the top-bar action cluster. These tests therefore assert the
 * primary relabel AND that the pill is no longer rendered.
 *
 * Fully mocked (no backend): the studio resolves `selectedJob().info` from the
 * `GET /api/tasks/{id}` detail endpoint, so the persisted provenance/merge fact
 * rides on `detail.info.provenance`. The live `GET /api/tasks/{id}/provenance`
 * view drives the "Released to main" upgrade. See file-source-history.spec.ts
 * for the same mock harness.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/triage-merge-status';
const JOB_ID = 'triage-merge-status-test';
const MERGE_SHA = 'ddddddd9abc1234ef5678901234567890abcdef0';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

interface MergeFact { mergeCommit: string | null }

function detail(merge: MergeFact | null, hasDeliverable = true) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Triage merge-status fixture',
      state: '5-human-review',
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-haiku-4-5',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/5-human-review/${JOB_ID}`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: null,
      commits: hasDeliverable ? [{
        sha: 'abc1234abc1234abc1234abc1234abc1234abc1', shortSha: 'abc1234',
        message: 'feat: task deliverable', filesChanged: 1, files: ['src/task.ts'], at: '2026-06-09T12:20:00Z',
      }] : [],
      ownerClientId: 'local-default',
      createdAt: '2026-06-09T12:00:00Z',
      sessionChain: [],
      provenance: {
        branch: `task/${JOB_ID}`,
        base: 'base0000',
        transitions: [],
        merge: merge
          ? {
              mergeCommit: merge.mergeCommit,
              workBranchHeadBefore: 'dev00000',
              workBranchHeadAfter: merge.mergeCommit,
              atUtc: '2026-06-09T12:30:00Z',
            }
          : null,
      },
    },
    promptMarkdown: '# Current prompt\n\nThe current task prompt.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    titleHistory: [],
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

function provenanceView(landedState: 'on-branch-only' | 'merged-to-develop' | 'released-to-main', merge: MergeFact | null) {
  return {
    branch: `task/${JOB_ID}`,
    base: 'base0000',
    transitions: [],
    merge: merge
      ? { mergeCommit: merge.mergeCommit, workBranchHeadBefore: 'dev00000', workBranchHeadAfter: merge.mergeCommit, atUtc: '2026-06-09T12:30:00Z' }
      : null,
    landedState,
    ladder: {
      branch: `task/${JOB_ID}`,
      branchTip: 'tip00000',
      integrationBranch: 'develop',
      integrationHead: 'devhead0',
      mergedToIntegration: landedState !== 'on-branch-only',
      releaseBranch: 'main',
      releaseHead: 'mainhead',
      releasedToRelease: landedState === 'released-to-main',
    },
    commits: [],
  };
}

async function installBaseRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', (route) => json(route, []));
  await page.route('**/hubs/jobs/negotiate**', (route) => json(route, {
    connectionId: 'mock-jobs-connection', connectionToken: 'mock-jobs-connection',
    negotiateVersion: 1,
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
  }));
  await page.routeWebSocket('**/hubs/jobs**', (socket) => {
    socket.onMessage((message) => {
      if (String(message).includes('"protocol"')) socket.send('{}\x1e');
    });
  });
  await page.route('**/api/auth/status', (route) => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/tasks', (route) => json(route, []));
  await page.route('**/api/tasks/grouped**', (route) => json(route, {
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
  await page.route(/\/api\/runner\/status(\?|$)/, (route) => json(route, {
    projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } },
  }));
}

/**
 * Install the detail + provenance routes for one landed-state scenario. Added
 * after the base routes so the job-specific handlers win (Playwright matches
 * the most recently registered route first).
 */
async function installJobRoutes(
  page: Page,
  opts: { detailMerge: MergeFact | null; landedState: 'on-branch-only' | 'merged-to-develop' | 'released-to-main'; viewMerge?: MergeFact | null; hasDeliverable?: boolean },
): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route(new RegExp(`/api/tasks/${idEsc}/provenance(\\?|$)`), (route) =>
    json(route, provenanceView(opts.landedState, opts.viewMerge ?? opts.detailMerge)));
  await page.route(new RegExp(`/api/tasks/${idEsc}(\\?|$)`), (route) => json(route, detail(opts.detailMerge, opts.hasDeliverable ?? true)));
}

async function saveEvidence(page: Page, fileName: string): Promise<void> {
  if (!RESULTS_DIR) return;
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  await page.getByTestId('studio-triage-panel').screenshot({ path: path.join(RESULTS_DIR, fileName) });
}

async function openJob(page: Page): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await expect(page.getByTestId('studio-triage-panel')).toBeVisible({ timeout: 10_000 });
}

test.describe('Human Review acceptance primary is landed-state aware', () => {
  test.use({ serviceWorkers: 'block' });
  for (const theme of ['light', 'dark'] as const) {
    test(`acceptance dead end offers the contextual continuation in ${theme} theme`, async ({ page }) => {
      await installBaseRoutes(page);
      await installJobRoutes(page, { detailMerge: null, landedState: 'on-branch-only' });
      const pending = {
        status: 'pending', deliveryRef: `task/${JOB_ID}`, sha: null,
        integrationBranch: 'develop', detail: 'Delivery ref is not integrated into develop.',
        failure: null,
      };
      const card = { ...detail(null).info, integration: pending };
      await page.route('**/api/tasks/grouped**', (route) => json(route, {
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [card],
        escalated: [], completed: [], archive: [],
      }));
      await page.route(new RegExp(`/api/tasks/${JOB_ID}(\\?|$)`), (route) =>
        json(route, { ...detail(null), info: card }));
      await page.route(`**/api/tasks/${JOB_ID}/delivery-claim**`, (route) => json(route, {
        taskKey: `${WATCH_PATH}::${JOB_ID}`, jobId: JOB_ID, lane: '5-human-review',
        deliveryRef: `task/${JOB_ID}`, integrationBranch: 'develop', releaseBranch: 'main',
        containmentStatus: 'pending', integrated: false, released: false,
        commits: [], detail: 'Delivery ref is not integrated into develop.',
      }));
      let queued = false;
      await page.route(`**/api/tasks/${JOB_ID}/move**`, (route) => route.fulfill({
        status: 409, contentType: 'application/json',
        body: JSON.stringify({ code: 'integration-dead-end', error: 'Acceptance does not integrate deliveries. Delivery is pending on develop.' }),
      }));
      await page.route(`**/api/tasks/${JOB_ID}/failure/continue**`, (route) => {
        queued = true;
        return route.fulfill({ status: 202, contentType: 'application/json',
          body: JSON.stringify({ status: 'queued', stage: 'delivery-pending', taskKey: JOB_ID }) });
      });
      await page.route(`**/api/tasks/${JOB_ID}/timeline**`, (route) => json(route, queued ? [{
        ts: '2026-09-25T12:00:00Z', kind: 'integration_recovery_queued', actor: 'system',
        summary: 'Continuation queued from delivery-pending: delivery is not on develop.',
        details: { failureStage: 'delivery-pending', source: 'operator-failure-panel' },
      }] : []));

      await page.addInitScript((value) => localStorage.setItem('atp.studio.theme', value), theme);
      await openJob(page);
      await page.getByTestId('studio-triage-action-mark-done').click();
      const panel = page.getByTestId('integration-dead-end');
      await expect(panel).toBeVisible();
      await expect(panel).toContainText('Delivery pending');
      await expect(panel).toContainText('Stage: integration reach');
      await expect(page.getByText('Failed to move task')).toHaveCount(0);
      if (RESULTS_DIR) {
        fs.mkdirSync(RESULTS_DIR, { recursive: true });
        await panel.screenshot({ path: path.join(RESULTS_DIR, `acceptance-dead-end-${theme}.png`) });
      }
      await panel.getByRole('button', { name: 'Continue the task with this context' }).click();
      await expect.poll(() => queued).toBe(true);
      await page.reload();
      await page.getByRole('tab', { name: 'Timeline' }).click();
      await expect(page.getByTestId('timeline-event').filter({ hasText: 'Continuation queued' }))
        .toBeVisible({ timeout: 15_000 });
    });
  }

  test('not landed: keeps the Accept label and shows no status pill', async ({ page }) => {
    await installBaseRoutes(page);
    await installJobRoutes(page, { detailMerge: null, landedState: 'on-branch-only' });
    await openJob(page);

    const primary = page.getByTestId('studio-triage-action-mark-done');
    await expect(primary).toBeVisible();
    await expect(primary).toHaveText(/Accept/);
    await expect(page.getByTestId('studio-triage-merge-status')).toHaveCount(0);
    await saveEvidence(page, 'merge-action-with-deliverable.png');
  });

  test('no task diff: offers Accept instead of Merge into Develop', async ({ page }) => {
    await installBaseRoutes(page);
    await installJobRoutes(page, { detailMerge: null, landedState: 'on-branch-only', hasDeliverable: false });
    await openJob(page);

    const primary = page.getByTestId('studio-triage-action-mark-done');
    await expect(primary).toContainText('Accept');
    await expect(primary).not.toContainText('Merge');
    await saveEvidence(page, 'accept-action-without-deliverable.png');
  });

  test('merged to develop: relabels the primary to "Accept" without a redundant pill', async ({ page }) => {
    await installBaseRoutes(page);
    await installJobRoutes(page, { detailMerge: { mergeCommit: MERGE_SHA }, landedState: 'merged-to-develop' });
    await openJob(page);

    const primary = page.getByTestId('studio-triage-action-mark-done');
    await expect(primary).toBeVisible();
    await expect(primary).toHaveText(/Accept/);

    // The former "Merged to develop" pill is gone; the on-develop state lives
    // once at the task commit (git pane landed ladder), not in this cluster.
    await expect(page.getByTestId('studio-triage-merge-status')).toHaveCount(0);
    await saveEvidence(page, 'accept-action-already-merged.png');
  });

  test('released to main: still relabels the primary to "Accept" without a pill', async ({ page }) => {
    await installBaseRoutes(page);
    await installJobRoutes(page, { detailMerge: { mergeCommit: MERGE_SHA }, landedState: 'released-to-main' });
    await openJob(page);

    await expect(page.getByTestId('studio-triage-action-mark-done')).toHaveText(/Accept/);
    await expect(page.getByTestId('studio-triage-merge-status')).toHaveCount(0);
  });

  test('landed-state evidence — merged vs not-landed primary', async ({ page }) => {
    await installBaseRoutes(page);
    await installJobRoutes(page, { detailMerge: { mergeCommit: MERGE_SHA }, landedState: 'merged-to-develop' });
    await openJob(page);
    await expect(page.getByTestId('studio-triage-action-mark-done')).toHaveText(/Accept/);
    await test.info().attach('human-review-merged--mocked', {
      body: await page.getByTestId('studio-triage-panel').screenshot(),
      contentType: 'image/png',
    });
  });

  // AGT-2006: the git-dependent acceptance primary must not be actionable (nor
  // show a guessed label) while the live git status is still loading. Detail
  // says "not landed"; the delayed provenance resolves to merged-to-develop.
  // Pre-fix the button rendered an actionable "Merge into Develop" during the
  // load and then flipped to "Accept" — the race Robert reported. Post-fix it
  // is disabled + skeletoned until provenance settles, then switches atomically.
  test('holds the acceptance primary until git status loads, then switches atomically', async ({ page }) => {
    await installBaseRoutes(page);

    let releaseProvenance!: () => void;
    const provenanceGate = new Promise<void>((resolve) => { releaseProvenance = resolve; });
    const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    await page.route(new RegExp(`/api/tasks/${idEsc}/provenance(\\?|$)`), async (route) => {
      await provenanceGate;
      await json(route, provenanceView('merged-to-develop', { mergeCommit: MERGE_SHA }));
    });
    await page.route(new RegExp(`/api/tasks/${idEsc}(\\?|$)`), (route) => json(route, detail(null)));

    await openJob(page);

    const primary = page.getByTestId('studio-triage-action-mark-done');
    await expect(primary).toBeVisible();
    // Held while the branch/merge status is unknown.
    await expect(primary).toBeDisabled();
    await expect(primary).toHaveAttribute('data-git-loading', 'true');
    await expect(primary.locator('.studio-tab-action__skeleton')).toBeVisible();

    await test.info().attach('git-action-loading-gate--mocked', {
      body: await page.getByTestId('studio-triage-panel').screenshot(),
      contentType: 'image/png',
    });

    // Resolve the git status -> the true "Accept" label appears and the button
    // becomes actionable, with no intermediate wrong "Merge into Develop" click.
    releaseProvenance();
    await expect(primary).toHaveText(/Accept/);
    await expect(primary).toBeEnabled();
    await expect(primary).not.toHaveAttribute('data-git-loading', 'true');
  });
});
