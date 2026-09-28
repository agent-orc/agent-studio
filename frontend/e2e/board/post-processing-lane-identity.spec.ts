import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { setTheme } from '../helpers/theme';

const PROJECT = 'post-processing-identity';
const WATCH_PATH = 'C:/fixtures/post-processing-identity';
const SHOTS = process.env.JOB_RESULTS_DIR
  ? `${process.env.JOB_RESULTS_DIR}/post-processing-lane`
  : 'screenshots/post-processing-lane';
const HEADER_CAPTURE_PHASE = process.env.LANE_HEADER_CAPTURE_PHASE ?? 'after';

interface JobInfoStub {
  id: string;
  taskKey: string;
  title: string;
  state: string;
  order: number;
  agent: string;
  createdAt: string;
  watchPath: string;
  projectName: string;
  folderPath: string;
  lastActivity: string;
  enteredLaneAt?: string | null;
  sessionName: null;
  model: string | null;
  cliType: string | null;
  useOwnSession: null;
  lastUsage: null;
  execution: null;
  commit: null;
  commits: unknown[];
  ownerClientId: string;
  tokenSummary: {
    calls: number;
    inputTokens: number;
    outputTokens: number;
    cacheReadTokens: number;
    cacheCreationTokens: number;
    totalTokens: number;
    lastModel: string | null;
    lastUpdate: string | null;
    entries: {
      ts: string;
      model: string | null;
      participantId?: string | null;
      inputTokens: number;
      outputTokens: number;
      cacheReadTokens: number;
      cacheCreationTokens: number;
    }[];
  } | null;
  phase: string | null;
  phaseEnteredAt?: string | null;
  postProcessingChecks?: { name: string; status: string; startedAt?: string | null }[];
  steerPendingSince?: string | null;
  tags: string[];
  taskType: string;
}

function jobInfo(over: Partial<JobInfoStub> = {}): JobInfoStub {
  const id = over.id ?? 'post-processing-codex-main';
  const state = over.state ?? '4-auto-review';
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title: over.title ?? 'Codex task with Claude post-processing',
    state,
    order: over.order ?? 1,
    agent: over.agent ?? 'codex',
    createdAt: '2026-06-09T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/tasks/000/${id}`,
    lastActivity: '2026-06-09T08:12:00Z',
    enteredLaneAt: over.enteredLaneAt ?? '2026-06-09T08:12:00Z',
    sessionName: null,
    model: over.model ?? 'GPT-5 Codex',
    cliType: over.cliType ?? 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    phase: over.phase ?? 'post-processing-running',
    phaseEnteredAt: over.phaseEnteredAt ?? null,
    steerPendingSince: over.steerPendingSince ?? null,
    tags: [],
    taskType: 'feature',
    tokenSummary: over.tokenSummary ?? {
      calls: 2,
      inputTokens: 72_000,
      outputTokens: 9_000,
      cacheReadTokens: 80_000,
      cacheCreationTokens: 4_000,
      totalTokens: 165_000,
      lastModel: 'Claude Haiku 4.5',
      lastUpdate: '2026-06-09T08:12:00Z',
      entries: [
        {
          ts: '2026-06-09T08:00:00Z',
          model: 'GPT-5 Codex',
          participantId: 'agent:codex',
          inputTokens: 52_000,
          outputTokens: 8_000,
          cacheReadTokens: 60_000,
          cacheCreationTokens: 3_000,
        },
        {
          ts: '2026-06-09T08:12:00Z',
          model: 'Claude Haiku 4.5',
          participantId: 'supporting-agent:post-processing',
          inputTokens: 20_000,
          outputTokens: 1_000,
          cacheReadTokens: 20_000,
          cacheCreationTokens: 1_000,
        },
      ],
    },
    ...over,
  };
}

function grouped(jobs: JobInfoStub[]) {
  const autoReview = jobs.filter((j) => j.state === '4-auto-review');
  return {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: jobs.filter((j) => j.state === '2-ready'),
    progress: jobs.filter((j) => j.state === '3-progress'),
    failedPickup: [],
    codeNotComplete: [],
    autoReview,
    review: autoReview,
    humanReview: jobs.filter((j) => j.state === '5-human-review'),
    escalated: [],
    completed: [],
    archive: [],
  };
}

interface AutoReviewStatusStub {
  lastTickAt: string;
  accept: number;
  reissue: number;
  escalate: number;
  aspectsRun: number;
  pending: number;
  currentJob: string | null;
  currentProject: string | null;
  activeJobs?: {
    project: string;
    jobId: string;
    step: string;
    startedAt: string;
  }[];
}

async function installRoutes(
  page: Page,
  jobs: JobInfoStub[],
  autoReviewStatus?: AutoReviewStatusStub,
): Promise<void> {
  const groupedBody = grouped(jobs);
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const p = url.pathname;
    if (p === '/api/v1/studio/auth/status') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
      });
    }
    if (p === '/api/v1/studio/board' || p === '/api/v1/studio/board') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(groupedBody) });
    }
    if (p === '/api/tasks/archive') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ items: [], total: 0, offset: 0, limit: 50 }),
      });
    }
    if (p === '/api/crash-recovery/pending') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ pending: [] }) });
    }
    if (p === '/api/tasks' || p === '/api/tasks/' || p === '/api/tasks' || p === '/api/tasks/') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(jobs) });
    }
    if (p === '/api/auto-review/status') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(autoReviewStatus ?? {
          lastTickAt: '2026-06-09T08:12:00Z',
          accept: 0,
          reissue: 0,
          escalate: 0,
          aspectsRun: 1,
          pending: 1,
          currentJob: 'post-processing-codex-main',
          currentProject: PROJECT,
          activeJobs: [{
            project: PROJECT,
            jobId: 'post-processing-codex-main',
            step: 'aspects',
            startedAt: '2026-06-09T08:12:00Z',
          }],
        }),
      });
    }
    if (p === '/api/watch-paths') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
      });
    }
    if (p === '/api/v1/workspaces') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{
          id: 'default',
          displayName: 'Workspaces',
          sortOrder: 0,
          isDefault: true,
          color: null,
          createdAt: '2026-06-09T08:00:00Z',
          projects: [{
            id: PROJECT,
            displayName: PROJECT,
            shortCode: 'PP',
            workspaceId: 'default',
            color: null,
            cliDefault: null,
            modelDefault: null,
            sortOrder: 0,
            storageLocation: WATCH_PATH,
            archived: false,
            createdAt: '2026-06-09T08:00:00Z',
          }],
        }]),
      });
    }
    if (p.startsWith('/api/clients')) {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([{
          id: 'local-default',
          displayName: 'Local Default',
          emoji: '🤖',
          colour: '#64748b',
          kind: 'agent-instance',
          registeredAt: '2026-01-01T00:00:00Z',
          lastSeenAt: null,
          tokenBudgetMonthly: null,
          notes: null,
          defaultCliType: null,
          defaultModel: null,
        }]),
      });
    }
    if (p === '/api/environment') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
      });
    }
    if (p === '/api/projects/settings') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({}) });
    }
    if (p === '/api/dev-tools/flags') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ updateStableEnabled: false, deleteE2EJobsEnabled: false }),
      });
    }
    if (p === '/api/orchestrator/global') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ session: null }) });
    }
    if (p === '/api/cli/quota') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ at: '2026-06-09T08:12:00Z', ttlSeconds: 600, snapshots: [] }),
      });
    }
    if (p === '/api/cli/usage') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ at: '2026-06-09T08:12:00Z', sections: [] }),
      });
    }
    if (p.startsWith('/api/runner') || p.startsWith('/api/v1/studio/runner')) {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }),
      });
    }
    if (p === '/api/tags' || p === '/api/git/summary' || p === '/api/git/projects') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: 'null' });
  });
}

async function seedBoardTab(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
}

async function dismissRuntimeErrorOverlay(page: Page): Promise<void> {
  const error = page.getByText('Unexpected application error', { exact: true });
  if (await error.isVisible()) {
    await page.keyboard.press('Escape');
    await expect(error).toBeHidden();
  }
}

test.describe('Post Processing lane identity', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('shows Codex as the coding agent and Claude as supporting post-processing evidence', async ({ page }) => {
    const job = jobInfo();
    await seedBoardTab(page);
    await installRoutes(page, [job]);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const lane = page.getByTestId('lane-4-auto-review');
    await expect(lane).toBeVisible({ timeout: 10_000 });
    await expect(lane.getByRole('heading', { name: 'Post Processing' })).toBeVisible();

    const card = page.locator('[data-testid="task-card"]', { hasText: job.title });
    await expect(card).toBeVisible({ timeout: 10_000 });
    await expect(card.getByTestId('task-card-state')).toHaveCount(0);
    await expect(card.getByTestId('task-card-phase')).toHaveCount(0);
    await expect(card.getByTestId('task-card-post-processing-activity')).toContainText('Aspects');
    await expect(card.getByTestId('task-card-effective-model')).toHaveAttribute('data-cli', 'codex');

    const bubble = card.getByTestId('task-card-token-bubble');
    await expect(bubble).toBeVisible();
    await bubble.focus();

    const popover = page.getByTestId('task-card-token-popover');
    await expect(popover).toBeVisible();
    await expect(popover).toContainText('GPT-5 Codex');
    await expect(popover).toContainText('Claude Haiku 4.5');

    await dismissRuntimeErrorOverlay(page);
    mkdirSync(SHOTS, { recursive: true });
    await page.screenshot({ path: `${SHOTS}/post-processing-codex-claude--mocked.png`, fullPage: false });
  });

  test('shows mixed activity and switches card state on the next status snapshot', async ({ page }) => {
    const now = new Date('2026-07-23T12:00:00Z');
    await page.clock.install({ time: now });
    const active = jobInfo({
      id: 'active-aspects',
      title: 'Active aspect review',
      order: 1,
      enteredLaneAt: '2026-07-23T11:45:00Z',
    });
    const waiting = jobInfo({
      id: 'waiting-review',
      title: 'Waiting review',
      order: 2,
      enteredLaneAt: '2026-07-23T09:50:00Z',
      phase: 'awaiting-review',
    });
    const gateQueued = jobInfo({
      id: 'gate-queued',
      title: 'Waiting for machine gate',
      order: 3,
      enteredLaneAt: '2026-07-23T11:30:00Z',
    });
    const status: AutoReviewStatusStub = {
      lastTickAt: now.toISOString(),
      accept: 0,
      reissue: 0,
      escalate: 0,
      aspectsRun: 0,
      pending: 3,
      currentJob: active.id,
      currentProject: PROJECT,
      activeJobs: [
        { project: PROJECT, jobId: active.id, step: 'aspects', startedAt: '2026-07-23T11:58:00Z' },
        { project: PROJECT, jobId: gateQueued.id, step: 'gate-queued', startedAt: '2026-07-23T11:52:00Z' },
      ],
    };
    await seedBoardTab(page);
    await installRoutes(page, [active, waiting, gateQueued], status);
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const lane = page.getByTestId('lane-4-auto-review');
    const activeCard = page.locator('[data-testid="task-card"]', { hasText: active.title });
    const waitingCard = page.locator('[data-testid="task-card"]', { hasText: waiting.title });
    const gateCard = page.locator('[data-testid="task-card"]', { hasText: gateQueued.title });

    await expect(activeCard.getByTestId('task-card-post-processing-activity')).toContainText('Aspects');
    await expect(waitingCard.getByTestId('task-card-post-processing-activity')).toContainText('waiting 2h 10m');
    await expect(gateCard.getByTestId('task-card-post-processing-activity')).toContainText('Gate queued 8m');
    const summary = lane.getByTestId('lane-post-processing-summary');
    await expect(summary).toContainText('1 active / 2 waiting');
    await expect(summary).toHaveAttribute('data-active-count', '1');
    await expect(summary).toHaveAttribute('data-waiting-count', '2');

    mkdirSync(SHOTS, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await lane.screenshot({
        path: `${SHOTS}/mixed-activity-${theme}--mocked.png`,
      });
    }

    status.currentJob = waiting.id;
    status.activeJobs = [
      { project: PROJECT, jobId: waiting.id, step: 'grade', startedAt: now.toISOString() },
      { project: PROJECT, jobId: gateQueued.id, step: 'gate-queued', startedAt: '2026-07-23T11:52:00Z' },
    ];
    await page.clock.fastForward(30_000);

    await expect(waitingCard.getByTestId('task-card-post-processing-activity')).toContainText('Grade');
    await expect(waitingCard.getByTestId('task-card-post-processing-activity'))
      .toHaveAttribute('data-activity-state', 'active');
    await expect(activeCard.getByTestId('task-card-post-processing-activity')).toContainText('waiting 15m');
    await expect(activeCard.getByTestId('task-card-post-processing-activity'))
      .toHaveAttribute('data-activity-state', 'waiting');
  });

  test('keeps the lane name primary and aligns secondary header facts on one line', async ({ page }) => {
    const waitingJobs = Array.from({ length: 10 }, (_, index) => jobInfo({
      id: `waiting-review-${index + 1}`,
      title: `Waiting review ${index + 1}`,
      order: index + 1,
      phase: 'awaiting-review',
    }));
    const status: AutoReviewStatusStub = {
      lastTickAt: '2026-08-11T12:00:00Z',
      accept: 0,
      reissue: 0,
      escalate: 0,
      aspectsRun: 0,
      pending: waitingJobs.length,
      currentJob: null,
      currentProject: null,
      activeJobs: [],
    };

    await page.setViewportSize({ width: 1024, height: 900 });
    await seedBoardTab(page);
    await installRoutes(page, waitingJobs, status);
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const lane = page.getByTestId('lane-4-auto-review');
    const title = lane.getByRole('heading', { name: 'Post Processing' });
    const header = title.locator('..');
    const count = lane.getByTestId('lane-count-4-auto-review');
    const summary = lane.getByTestId('lane-post-processing-summary');
    const infoButton = page.getByTestId('info-button-lane-4-auto-review');
    await expect(lane).toBeVisible();
    await expect(summary).toHaveAttribute('data-active-count', '0');
    await expect(summary).toHaveAttribute('data-waiting-count', '10');
    await dismissRuntimeErrorOverlay(page);

    mkdirSync(SHOTS, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await header.screenshot({
        path: `${SHOTS}/AGT-2644--lane-header-${HEADER_CAPTURE_PHASE}-${theme}--mocked.png`,
      });
    }

    if (HEADER_CAPTURE_PHASE === 'before') {
      await expect(summary).toContainText('0 active / 10 waiting');
      return;
    }

    await expect(summary.getByTestId('lane-post-processing-summary-full')).toBeHidden();
    await expect(summary.getByTestId('lane-post-processing-summary-compact')).toHaveText('0/10');
    await expect(infoButton).toBeHidden();

    const metrics = await Promise.all([
      lane.getByTestId('lane-header-avatar-4-auto-review'),
      title,
      count,
      summary,
      lane.getByTestId('lane-collapse-4-auto-review'),
    ].map(locator => locator.evaluate((element) => {
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      return {
        center: rect.top + rect.height / 2,
        fontFamily: style.fontFamily,
        fontSize: style.fontSize,
        width: rect.width,
      };
    })));

    expect(Math.max(...metrics.map(metric => metric.center)) - Math.min(...metrics.map(metric => metric.center)))
      .toBeLessThanOrEqual(1);
    expect(new Set(metrics.slice(1, 4).map(metric => metric.fontFamily)).size).toBe(1);
    expect(metrics[1].fontSize).toBe('13px');
    expect(metrics[2].fontSize).toBe('11px');
    expect(metrics[3].fontSize).toBe('11px');
    expect(metrics[1].width).toBeGreaterThanOrEqual(90);
    expect(await title.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);

    await summary.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText('0 active post-processing tasks, 10 waiting');

    await lane.evaluate(element => {
      element.style.flex = '0 0 400px';
      element.style.width = '400px';
    });
    await expect(summary.getByTestId('lane-post-processing-summary-full')).toBeVisible();
    await expect(summary.getByTestId('lane-post-processing-summary-compact')).toBeHidden();
    await expect(infoButton).toBeVisible();

    const wideCenters = await Promise.all([
      lane.getByTestId('lane-header-avatar-4-auto-review'),
      title,
      count,
      summary,
      infoButton,
      lane.getByTestId('lane-collapse-4-auto-review'),
    ].map(locator => locator.evaluate((element) => {
      const rect = element.getBoundingClientRect();
      return rect.top + rect.height / 2;
    })));
    expect(Math.max(...wideCenters) - Math.min(...wideCenters)).toBeLessThanOrEqual(1);
  });

  test('shows a timed loop-waiting phase without claiming a runner slot', async ({ page }) => {
    const job = jobInfo({
      id: 'loop-waiting-card',
      title: 'Waiting for orchestrator loop continuation',
      state: '3-progress',
      phase: 'loop-waiting',
      phaseEnteredAt: new Date(Date.now() - 42_000).toISOString(),
    });
    await seedBoardTab(page);
    await installRoutes(page, [job]);

    await page.goto('/');
    const card = page.locator('[data-testid="task-card"]', { hasText: job.title });
    await expect(card).toBeVisible({ timeout: 10_000 });
    await expect(card.getByTestId('task-card-phase'))
      .toContainText(/Waiting for loop continuation 0:4[2-9]/);

    await dismissRuntimeErrorOverlay(page);
    mkdirSync(SHOTS, { recursive: true });
    await page.screenshot({
      path: `${SHOTS}/loop-waiting-phase--mocked.png`,
      fullPage: false,
    });
  });

  test('shows how long a progress card has been waiting for a steer answer', async ({ page }) => {
    const fiveHourWait = 5 * 3_600_000 + 7 * 60_000 + 9_000;
    const waitStarted = new Date(Date.now() - fiveHourWait).toISOString();
    const job = jobInfo({
      id: 'steer-pending-card',
      title: 'Run waiting on an unanswered question',
      state: '3-progress',
      phase: 'steer-pending',
      steerPendingSince: waitStarted,
      tokenSummary: null,
    });
    await seedBoardTab(page);
    await installRoutes(page, [job]);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const lane = page.getByTestId('lane-3-progress');
    await expect(lane).toBeVisible({ timeout: 10_000 });
    const card = lane.locator('[data-testid="task-card"]', { hasText: job.title });
    await expect(card).toBeVisible({ timeout: 10_000 });

    const phase = card.getByTestId('task-card-phase');
    await expect(phase).toContainText(/Waiting for answer · 307:\d{2}/);

    mkdirSync(SHOTS, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(phase).toBeVisible();
      await page.screenshot({ path: `${SHOTS}/steer-pending-${theme}--mocked.png`, fullPage: false });
    }
  });
});
