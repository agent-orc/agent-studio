import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

/**
 * Task-detail Activity tab: native TODO_LIST snapshots are one living
 * checklist entry in the readable stream. Repeated frames update that entry;
 * Trace, Debug, and Copy remain in the Activity overflow menu.
 *
 * The spec drives the live frontend (proxied to a real backend) but pins the
 * two pieces of evidence the behaviour keys off: the per-job `output` buffer
 * (so the panel is in its "has output" branch) and the per-job `plan` (so the
 * living checklist is deterministically present or absent). Everything else (detail,
 * runs, board) is served by the backend the frontend proxies to.
 *
 * Screenshots land under PLAN_TOGGLE_SHOTS when the orchestrator
 * sets it, else test-results/ (scratch).
 */

const SHOTS_DIR = process.env.PLAN_TOGGLE_SHOTS?.trim()
  || path.resolve(__dirname, '../../test-results/plan-toggle');

const TARGET = { id: 'activity-toolbar-fixture', watchPath: 'C:/fixtures/activity-toolbar' };

interface OutLine { timestamp: string; stream: string; text: string; }

function buildOutputBuffer(): OutLine[] {
  const t0 = Date.now() - 6 * 60 * 1000;
  const t = (s: number) => new Date(t0 + s * 1000).toISOString();
  return [
    { timestamp: t(0), stream: 'user', text: 'Show TODO_LIST as one living checklist in Activity and Orchestrator.' },
    { timestamp: t(3), stream: 'stdout', text: '* Read the Codex TODO_LIST frame' },
    { timestamp: t(5), stream: 'stdout', text: '* Update the plan projection' },
    { timestamp: t(8), stream: 'stdout', text: 'Mapped TODO_LIST snapshots into the live plan projection.' },
    { timestamp: t(10), stream: 'stdout', text: '[[TASK_DONE]]' },
  ];
}

function buildPlan(present: boolean) {
  if (!present) {
    return { hasPlan: false, source: null, snapshotCount: 0, activeItemId: null, softEstimateMedian: null, items: [], unassignedSubActions: [] };
  }
  const ts = new Date(Date.now() - 4 * 60 * 1000).toISOString();
  return {
    hasPlan: true,
    source: 'codex/todo_list',
    snapshotCount: 3,
    activeItemId: 'i2',
    softEstimateMedian: 2,
    items: [
      { id: 'i1', title: 'Render TODO_LIST as one checklist', status: 'done', subActionCount: 2, subActions: [{ ts, tool: 'Edit', label: 'conversation-projection.ts' }] },
      { id: 'i2', title: 'Stream progress into Orchestrator', status: 'active', subActionCount: 1, subActions: [{ ts, tool: 'Edit', label: 'orchestrator-task-plan.store.ts' }] },
      { id: 'i3', title: 'Lock the frame into the corpus', status: 'pending', subActionCount: 0, subActions: [] },
    ],
    unassignedSubActions: [],
  };
}

function detail() {
  return {
    info: {
      id: TARGET.id,
      taskKey: `ASS-E2E-${TARGET.id}`,
      displayKey: 'ASS-E2E',
      title: 'Activity toolbar fixture',
      state: '5-human-review',
      order: 1,
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5',
      watchPath: TARGET.watchPath,
      projectName: 'fixture',
      folderPath: `${TARGET.watchPath}/.orchestrator/jobs/5-human-review/${TARGET.id}`,
      createdAt: '2026-06-09T08:00:00.000Z',
      lastActivity: '2026-06-09T08:05:00.000Z',
      sessionName: null,
      useOwnSession: null,
      lastUsage: null,
      execution: null,
      commit: null,
      commits: [],
      codeActivityDetected: false,
      summaryState: null,
      taskType: 'bug',
      tags: [],
      references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    },
    promptMarkdown: 'Fixture prompt.',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: '## Status\n\nWaiting for review.',
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

async function installRoutes(page: Page, planPresent: boolean): Promise<void> {
  const esc = encodeURIComponent(TARGET.id);
  const output = JSON.stringify(buildOutputBuffer());
  const plan = JSON.stringify(buildPlan(planPresent));
  // The feature under test uses SignalR as its primary plan refresh path. A
  // route-mocked browser has no hub server, so exercise the documented polling
  // fallback without letting proxy negotiation surface an unrelated dialog.
  await page.route('**/hubs/v1/studio/**', route => route.abort('connectionrefused'));
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/v1/studio/auth/status', route =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }));
  await page.route('**/api/projects/*/workbenches**', route =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ projectName: 'fixture', items: [] }),
    }));
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        backlog: [],
        preparation: [],
        orchestratorPrep: [],
        ready: [],
        progress: [],
        failedPickup: [],
        codeNotComplete: [],
        autoReview: [],
        humanReview: [detail().info],
        review: [],
        completed: [],
        archive: [],
      }),
    }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: 'fixture', path: TARGET.watchPath, rootPath: TARGET.watchPath }]),
    }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ snapshots: [], ttlSeconds: 600 }),
    }));
  await page.route('**/api/v1/studio/runner/status**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ projects: { fixture: { projectName: 'fixture', mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }),
    }));
  await page.route(`**/api/tasks/${esc}/output**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: output }));
  await page.route(`**/api/tasks/${esc}/plan**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: plan }));
  await page.route(`**/api/tasks/${esc}/pipeline**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ pipeline: { pre: [], core: [], post: [], allSteps: [] }, execution: null, executions: [], config: {}, cost: null }),
    }));
  await page.route(`**/api/tasks/${esc}/plan**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: plan }));
  await page.route(`**/api/tasks/${esc}/runs**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }));
  await page.route(`**/api/tasks/${esc}/session-events**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(`**/api/tasks/${esc}/claude-session**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(`**/api/v1/projects/*/tasks/${esc}?**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail()) }));
}

async function setFlag(page: Page, on: boolean): Promise<void> {
  // `nextGenChat` is default-ON now: a missing key reads as opt-in, so the
  // off-state must be written explicitly as '0' (mirrors writeExplicit in
  // FeatureFlagsService) rather than removing the key.
  await page.addInitScript((enable) => {
    localStorage.setItem('atp.flag.nextGenChat', enable ? '1' : '0');
  }, on);
}

async function openActivity(page: Page, job: { id: string; watchPath: string }): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);
  const activityTab = page.getByTestId('inspector-tab-activity');
  await expect(activityTab).toBeVisible({ timeout: 20_000 });
  await activityTab.click();
}

test.describe('Activity tab compact view switcher', () => {
  test('plan present: checklist and agent events share one readable stream', async ({ page }) => {
    await setFlag(page, false);
    await installRoutes(page, true);
    await openActivity(page, TARGET);

    await expect(page.getByTestId('activity-view-tab-plan')).toHaveCount(0);
    await expect(page.getByTestId('activity-view-tab-cli')).toHaveCount(0);
    // Trace is no longer a primary tab.
    await expect(page.getByTestId('activity-view-tab-trace')).toHaveCount(0);
    // The old Conversation test id was retired in favour of the user-facing CLI label.
    await expect(page.getByTestId('activity-view-tab-conversation')).toHaveCount(0);
    const activityPanel = page.getByTestId('activity-panel');
    await expect(activityPanel.getByTestId('plan-strip')).toBeVisible({ timeout: 15_000 });
    await expect(activityPanel.getByTestId('plan-item').first()).toBeVisible();
    await expect(activityPanel.getByTestId('activity-log-body')).toBeVisible();
    await expect(activityPanel.getByTestId('plan-strip')).toHaveCount(1);

    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate((value) => {
        document.documentElement.dataset['studioTheme'] = value;
        localStorage.setItem('atp.studio.theme', value);
      }, theme);
      await activityPanel.screenshot({
        path: path.join(SHOTS_DIR, `activity-todo-list-${theme}--mocked.png`),
      });
    }

    if (!await page.locator('app-orchestrator-side-sheet.is-open').count()) {
      await page.getByTestId('orch-side-sheet-toggle').click();
    }
    const sheet = page.getByTestId('orch-side-sheet');
    await expect(sheet).toBeVisible();
    await expect(page.getByTestId('orch-task-progress')).toBeVisible();
    await expect(page.getByTestId('orch-task-progress')).toContainText('Agent progress');
    await expect(page.getByTestId('orch-task-progress')).toContainText('1/3 done');
    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate((value) => {
        document.documentElement.dataset['studioTheme'] = value;
        localStorage.setItem('atp.studio.theme', value);
      }, theme);
      await sheet.screenshot({
        path: path.join(SHOTS_DIR, `orchestrator-task-progress-${theme}--mocked.png`),
      });
    }
  });

  test('Trace owns raw output and Agent events restores the living checklist', async ({ page }) => {
    await setFlag(page, false);
    await installRoutes(page, true);
    await openActivity(page, TARGET);

    const activityPanel = page.getByTestId('activity-panel');
    await expect(activityPanel.getByTestId('plan-strip')).toBeVisible({ timeout: 15_000 });

    // Switch to Trace from the overflow menu: the raw activity-log body shows, the plan hides.
    await page.getByTestId('activity-toolbar-menu').click();
    const traceItem = page.getByTestId('activity-toolbar-menu-item-trace');
    await expect(traceItem).toBeVisible();
    await expect(page.getByTestId('activity-toolbar-menu-item-debug')).toContainText('Debug');
    await expect(page.getByTestId('activity-toolbar-menu-item-conversation')).toContainText('Agent events');
    await expect(page.getByTestId('activity-toolbar-menu-item-copy')).toContainText('Copy');
    await page.screenshot({ path: path.join(SHOTS_DIR, 'toolbar-menu-open--mocked.png'), fullPage: false });
    await traceItem.click();
    await expect(page.getByTestId('activity-log-body')).toBeVisible();
    await expect(page.getByTestId('activity-log-trace')).toBeVisible();
    await expect(activityPanel.getByTestId('plan-strip')).toHaveCount(0);
    await expect(page.getByTestId('activity-view-tab-cli')).toHaveCount(0);

    await page.screenshot({ path: path.join(SHOTS_DIR, 'trace-after-switch--mocked.png'), fullPage: false });

    await page.getByTestId('activity-toolbar-menu').click();
    await page.getByTestId('activity-toolbar-menu-item-conversation').click();
    await expect(activityPanel.getByTestId('plan-strip')).toBeVisible();
    await expect(page.getByTestId('activity-log-conversation')).toBeVisible();
  });

  test('no plan (flag off): event body renders without an empty single-tab toggle', async ({ page }) => {
    await setFlag(page, false);
    await installRoutes(page, false);
    await openActivity(page, TARGET);

    const activityPanel = page.getByTestId('activity-panel');
    await expect(page.getByTestId('activity-view-tab-cli')).toHaveCount(0);
    await expect(page.getByTestId('activity-log-body')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('activity-log-conversation')).toBeVisible();
    await expect(page.getByTestId('activity-view-tab-plan')).toHaveCount(0);
    await expect(page.getByTestId('activity-view-tab-trace')).toHaveCount(0);
    await expect(activityPanel.getByTestId('plan-strip')).toHaveCount(0);

    await page.screenshot({ path: path.join(SHOTS_DIR, 'no-plan-events--mocked.png'), fullPage: false });
  });

  test('Agent events live in the context menu and render in both themes', async ({ page }) => {
    await setFlag(page, true);
    await installRoutes(page, false);
    await openActivity(page, TARGET);

    await expect(page.getByTestId('activity-view-tab-cli')).toHaveCount(0);
    await expect(page.getByTestId('activity-view-tab-trace')).toHaveCount(0);
    await page.getByTestId('activity-toolbar-menu').click();
    const eventsItem = page.getByTestId('activity-toolbar-menu-item-conversation');
    await expect(eventsItem).toBeVisible();
    await expect(eventsItem).toContainText('Agent events');
    await eventsItem.click();
    await expect(page.getByTestId('activity-panel').getByTestId('conversation-view'))
      .toBeVisible({ timeout: 15_000 });

    for (const theme of ['dark', 'light'] as const) {
      await page.evaluate((value) => {
        document.documentElement.dataset['studioTheme'] = value;
        localStorage.setItem('atp.studio.theme', value);
      }, theme);
      await page.screenshot({ path: path.join(SHOTS_DIR, `agent-events-${theme}--mocked.png`), fullPage: false });
    }
  });
});
