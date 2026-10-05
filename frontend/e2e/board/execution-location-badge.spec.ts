import { expect, test, type Locator, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const PROJECT = 'Execution ownership';
const WATCH_PATH = '/fixtures/execution-ownership';
const RESULTS = process.env.JOB_RESULTS_DIR
  ? join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : join(process.cwd(), '..', 'results');

function location(
  state: string,
  runnerId: string | null,
  kind: 'local' | 'remote' | 'none',
  historical = false,
  lastRejection?: {
    code: string;
    runnerId: string;
    runnerName: string;
    reason: string;
    rejectedAtUtc: string;
  },
) {
  return {
    state, executionKind: kind, runnerId, clientId: runnerId,
    hostDisplayName: kind === 'remote' ? runnerId : 'Local workstation',
    configuredRunnerId: kind === 'local' ? 'agent-runner-01' : 'agent-runner-01',
    startedAt: '2026-07-12T06:00:00Z', lastHeartbeat: '2026-07-12T06:02:00Z',
    lastActivityAt: '2026-07-12T06:02:03Z', processId: kind === 'local' ? 321 : 654,
    sessionId: `session-${runnerId ?? 'recovery'}`, branch: `task/${runnerId ?? 'recovering'}`,
    worktreePath: `${WATCH_PATH}/${runnerId ?? 'recovering'}`,
    connectionState: state === 'remote-disconnected' ? 'disconnected' : 'connected',
    leaseState: kind === 'remote' ? 'active' : 'local-process',
    trustReason: kind === 'remote' ? 'The task server holds the fenced run lease.' : 'The local CLI registry reports a live process.',
    historical, lastRejection,
  };
}

function task(
  id: string,
  title: string,
  executionLocation: ReturnType<typeof location>,
  state = '3-progress',
) {
  return {
    id, taskKey: `${WATCH_PATH}::${id}`, key: id.toUpperCase(), title, state, order: 1,
    agent: 'codex', cliType: 'codex', createdAt: '2026-07-12T06:00:00Z', watchPath: WATCH_PATH,
    projectName: PROJECT, folderPath: `${WATCH_PATH}/${id}`, lastActivity: '2026-07-12T06:02:03Z',
    sessionName: `session-${id}`, model: 'gpt-5.6-codex', useOwnSession: null, lastUsage: null,
    commit: null, ownerClientId: 'local-default', tags: [], executionLocation,
  };
}

const initialTasks = [
  task('local-one', 'Local execution', location('local-running', 'stable@local', 'local')),
  task('remote-one', 'Remote execution', location('remote-running', 'agent-runner-01', 'remote')),
  task('remote-two', 'Second remote slot', location('remote-running', 'agent-runner-02', 'remote')),
];

const gateBlockedReadyTask = task(
  'remote-rejected',
  'Remote dispatch is visibly blocked',
  location('awaiting-remote', 'agent-runner-01', 'remote', false, {
    code: 'build-profile-gate',
    runnerId: 'agent-runner-01',
    runnerName: 'agent-runner-01',
    reason: 'build profile revalidation pending; grace runs exhausted',
    rejectedAtUtc: '2026-08-08T07:30:00Z',
  }),
  '2-ready',
);

const stalledAcceptedTask = {
  ...task(
    'accepted-stalled',
    'Accepted delivery is still missing',
    location('none', null, 'none'),
    '5-human-review',
  ),
  key: 'AGT-2531',
  tags: ['integrationpending'],
};

const stalledAcceptedTasks = Array.from({ length: 14 }, (_, index) => ({
  ...stalledAcceptedTask,
  id: `accepted-stalled-${index + 1}`,
  taskKey: `${WATCH_PATH}::accepted-stalled-${index + 1}`,
  key: `AGT-${2601 + index}`,
  title: `Accepted delivery ${index + 1} is still missing`,
  folderPath: `${WATCH_PATH}/accepted-stalled-${index + 1}`,
}));

const ordinaryReviewTask = {
  ...stalledAcceptedTask,
  id: 'ordinary-review',
  taskKey: `${WATCH_PATH}::ordinary-review`,
  key: 'AGT-2700',
  title: 'Ordinary review is not stalled',
  folderPath: `${WATCH_PATH}/ordinary-review`,
  tags: [],
};

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function expectFlatFullBleedNoticeBar(banner: Locator): Promise<void> {
  const geometry = await banner.evaluate(element => {
    const workspaceBanner = element.closest('app-workspace-banner');
    const icon = element.querySelector('[data-testid="notification-icon"]');
    if (!workspaceBanner || !icon) throw new Error('Notice-bar shell or status point is missing.');

    const rect = element.getBoundingClientRect();
    const workspaceRect = workspaceBanner.getBoundingClientRect();
    const iconRect = icon.getBoundingClientRect();
    const style = getComputedStyle(element);
    return {
      left: rect.left,
      right: rect.right,
      workspaceLeft: workspaceRect.left,
      workspaceRight: workspaceRect.right,
      borderRadius: style.borderRadius,
      marginBlockStart: style.marginBlockStart,
      marginBlockEnd: style.marginBlockEnd,
      marginInlineStart: style.marginInlineStart,
      marginInlineEnd: style.marginInlineEnd,
      boxShadow: style.boxShadow,
      paddingBlockStart: Number.parseFloat(style.paddingBlockStart),
      paddingBlockEnd: Number.parseFloat(style.paddingBlockEnd),
      iconWidth: iconRect.width,
      iconHeight: iconRect.height,
    };
  });

  expect(geometry.left).toBeCloseTo(geometry.workspaceLeft, 0);
  expect(geometry.right).toBeCloseTo(geometry.workspaceRight, 0);
  expect(geometry.borderRadius).toBe('0px');
  expect(geometry.marginBlockStart).toBe('0px');
  expect(geometry.marginBlockEnd).toBe('0px');
  expect(geometry.marginInlineStart).toBe('0px');
  expect(geometry.marginInlineEnd).toBe('0px');
  expect(geometry.boxShadow).toBe('none');
  expect(geometry.paddingBlockStart).toBeLessThanOrEqual(8);
  expect(geometry.paddingBlockEnd).toBeLessThanOrEqual(8);
  expect(geometry.iconWidth).toBeCloseTo(9, 3);
  expect(geometry.iconHeight).toBeCloseTo(9, 3);
}

async function expectNoticeCopySharesWideLine(page: Page, banner: Locator): Promise<void> {
  await page.setViewportSize({ width: 1920, height: 800 });
  const headline = await banner.getByTestId('notice-bar-headline').boundingBox();
  const detail = await banner.getByTestId('notice-bar-detail').boundingBox();
  expect(headline).not.toBeNull();
  expect(detail).not.toBeNull();
  expect(detail!.y).toBeCloseTo(headline!.y, 0);
  await page.setViewportSize({ width: 720, height: 800 });
}

async function installRoutes(
  page: Page,
  currentTasks: () => typeof initialTasks,
  queueSnapshot?: Record<string, unknown>,
): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const taskDetail = new URL(url).pathname.match(/^\/api\/v1\/projects\/[^/]+\/tasks\/([^/]+)$/);
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0 });
    if (url.includes('/api/v1/studio/auth/status')) return json(route, {
      profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
    });
    if (route.request().method() === 'GET' && taskDetail && !['archive', 'grouped'].includes(taskDetail[1])) {
      const info = currentTasks().find(item => item.id === decodeURIComponent(taskDetail[1]));
      return json(route, {
        info, promptMarkdown: null, promptHistory: [], titleHistory: [], statusMarkdown: null,
        contextUsage: null, log: [], summaryState: null, reviewEvidence: [],
      });
    }
    if (url.includes('/api/v1/studio/board')) return json(route, {
      backlog: [], preparation: [], orchestratorPrep: [],
      ready: currentTasks().filter(item => item.state === '2-ready'),
      progress: currentTasks().filter(item => item.state === '3-progress'),
      failedPickup: [],
      codeNotComplete: [], review: [], autoReview: [],
      humanReview: currentTasks().filter(item => item.state === '5-human-review'),
      escalated: [], completed: currentTasks().filter(item => item.state === '6-completed'), archive: [],
    });
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, currentTasks());
    if (url.includes('/api/watch-paths')) return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    if (url.includes('/api/clients')) return json(route, [{ id: 'local-default', displayName: 'Local', kind: 'agent-instance' }]);
    if (url.includes('/api/v1/studio/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/runner/queue-starvation')) {
      if (queueSnapshot) return json(route, queueSnapshot);
      const waiting = currentTasks().filter(item => item.state === '2-ready');
      return json(route, {
        active: waiting.length > 0,
        waitingTaskCount: waiting.length,
        availableSlots: waiting.length > 0 ? 8 : 0,
        thresholdMinutes: 30,
        observedAt: '2026-08-08T13:00:00Z',
        oldestEnteredLaneAt: waiting.length > 0 ? '2026-08-08T07:30:00Z' : null,
        items: waiting.map(item => ({
          taskId: item.id,
          taskKey: item.taskKey,
          projectName: item.projectName,
          title: item.title,
          enteredLaneAt: '2026-08-08T07:30:00Z',
          waitingMinutes: 330,
          lastRejection: item.executionLocation.lastRejection,
          blockReasonCode: item.executionLocation.lastRejection?.code === 'build-profile-gate'
            ? 'build-profile-gate'
            : null,
          blockReason: item.executionLocation.lastRejection?.code === 'build-profile-gate'
            ? item.executionLocation.lastRejection.reason
            : null,
        })),
      });
    }
    if (url.includes('/api/pipeline/accepted-integration-alert')) {
      const stalled = currentTasks().filter(item => item.tags.includes('integrationpending'));
      return json(route, {
        active: stalled.length > 0,
        stalledTaskCount: stalled.length,
        thresholdMinutes: 30,
        observedAt: '2026-08-09T13:00:00Z',
        oldestAcceptedAt: stalled.length > 0 ? '2026-08-09T12:00:00Z' : null,
        items: stalled.map(item => ({
          taskId: item.id,
          taskKey: item.key,
          projectName: item.projectName,
          title: item.title,
          acceptedAt: '2026-08-09T12:00:00Z',
          integrationStatus: 'no-branch',
          lastOutcome: 'NoTaskBranch',
          detail: 'No delivery branch exists.',
        })),
      });
    }
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/cli/')) return json(route, { snapshots: [], sessions: [] });
    return json(route, []);
  });
}

test('shows each concurrent task owner and limits warnings to the stale remote run', async ({ page }) => {
  mkdirSync(RESULTS, { recursive: true });
  await page.clock.install();
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  let liveTasks = initialTasks;
  await installRoutes(page, () => liveTasks);
  await page.goto('/?includeFixtures=true');
  // The broad mocked API returns empty values for shell features outside this
  // spec. Suppress the dev-only global diagnostic overlay so persisted visual
  // evidence shows the execution cards under test, not unrelated fixture noise.
  await page.addStyleTag({ content: '.dialog__overlay { display: none !important; }' });

  const badges = page.getByTestId('execution-location-badge');
  await expect(badges).toHaveCount(3);
  await expect(badges.nth(0)).toContainText('Local');
  await expect(badges.nth(1)).toContainText('Host · agent-runner-01');
  await expect(badges.nth(2)).toContainText('Host · agent-runner-02');
  await expect(page.locator('[data-execution-state="remote-running"]')).toHaveCount(2);
  await expect(page.locator('[data-execution-state="remote-disconnected"]')).toHaveCount(0);

  liveTasks = initialTasks.map(item => item.id === 'remote-one'
    ? { ...item, executionLocation: location('remote-disconnected', 'agent-runner-01', 'remote') }
    : item);
  await page.clock.fastForward(30_100);
  await expect(page.locator('[data-execution-state="remote-disconnected"]')).toHaveCount(1);
  await expect(page.locator('[data-execution-state="remote-running"]')).toHaveCount(1);
  await expect(page.locator('[data-execution-state="local-running"]')).toHaveCount(1);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.clock.runFor(100);
    await dismissDevErrorDialog(page);
    await page.evaluate(() => {
      document.querySelector('vite-error-overlay')?.remove();
      document.querySelector('ng-error-overlay')?.remove();
    });
    await expect(page.locator('[data-testid="error-dialog-overlay"]:visible')).toHaveCount(0);
    await expect(page.getByTestId('lane-3-progress')).toBeVisible();
    await page.screenshot({ path: join(RESULTS, `execution-location--${theme}.png`), fullPage: true });
  }

  liveTasks = initialTasks.map(item => item.id === 'remote-one'
    ? { ...item, executionLocation: location('remote-running', 'agent-runner-01', 'remote') }
    : item);
  await page.clock.fastForward(30_100);
  await expect(page.locator('[data-execution-state="remote-disconnected"]')).toHaveCount(0);
  await expect(page.locator('[data-execution-state="remote-running"]')).toHaveCount(2);
});

test('shows a durable build-profile gate refusal on the card and the starvation banner', async ({ page }) => {
  mkdirSync(RESULTS, { recursive: true });
  await page.setViewportSize({ width: 720, height: 800 });
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await installRoutes(page, () => [gateBlockedReadyTask]);
  await page.goto('/?includeFixtures=true');
  await page.addStyleTag({ content: '.dialog__overlay { display: none !important; }' });

  const card = page.getByTestId('task-card').filter({ hasText: gateBlockedReadyTask.title });
  const rejection = card.getByTestId('remote-dispatch-rejection');
  await expect(rejection).toContainText('Runner agent-runner-01 rejected:');
  await expect(rejection).toHaveAttribute('data-rejection-code', 'build-profile-gate');
  await expect(rejection).toContainText('build profile revalidation pending; grace runs exhausted');

  const banner = page.getByTestId('remote-queue-starvation-banner');
  await expect(banner).toContainText('1 ready card not claimable: build profile not validated.');
  await expect(banner).toContainText('8 Runner slots are available.');
  await expectFlatFullBleedNoticeBar(banner);
  await expectNoticeCopySharesWideLine(page, banner);

  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    await expect(rejection).toBeVisible();
    await page.screenshot({
      path: join(RESULTS, `notice-bar-after-starvation-narrow-${theme}--mocked.png`),
      fullPage: false,
    });
  }
});

test('distinguishes a Claude provider limit from stalled pickup and promises automatic resume', async ({ page }) => {
  mkdirSync(RESULTS, { recursive: true });
  await page.setViewportSize({ width: 960, height: 700 });
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await installRoutes(page, () => [], {
    active: true,
    signal: 'limited',
    waitingTaskCount: 1,
    availableSlots: 8,
    thresholdMinutes: 30,
    claimProgressStalled: false,
    lastSuccessfulClaimAt: '2026-08-23T20:00:00Z',
    hasRejections: false,
    buildProfileGateBlockedTaskCount: 0,
    oldestEnteredLaneAt: null,
    observedAt: '2026-08-23T22:00:00Z',
    items: [],
    providerLimits: [{
      cliType: 'claude',
      observedAt: '2026-08-23T22:00:00Z',
      limitedUntil: '2026-08-24T00:20:00Z',
      reason: 'claude: limited until 2026-08-24T00:20:00Z',
      resetTimeReported: true,
    }],
    pickupPauses: [],
  });
  await page.goto('/?includeFixtures=true');
  await page.addStyleTag({ content: '.dialog__overlay { display: none !important; }' });

  const banner = page.getByTestId('remote-queue-starvation-banner');
  await expect(banner.getByTestId('notice-bar-headline')).toContainText('Claude claims limited until');
  await expect(banner.getByTestId('notice-bar-detail')).toContainText('resume automatically after a fresh quota probe');
  await expect(banner.getByTestId('notice-bar-detail')).toContainText('Codex and other CLI claims remain eligible');
  await expect(banner).not.toContainText('waiting despite free Runner capacity');
  await expectFlatFullBleedNoticeBar(banner);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    await page.screenshot({
      path: join(RESULTS, `provider-limit-banner--${theme}--mocked.png`),
      fullPage: false,
    });
  }
});

test('shows accepted deliveries that remain unintegrated beyond the threshold', async ({ page }) => {
  mkdirSync(RESULTS, { recursive: true });
  await page.setViewportSize({ width: 720, height: 800 });
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await installRoutes(page, () => [...stalledAcceptedTasks, ordinaryReviewTask]);
  await page.goto('/?includeFixtures=true');
  await page.addStyleTag({ content: '.dialog__overlay { display: none !important; }' });

  const banner = page.getByTestId('accepted-integration-alert-banner');
  await expect(banner).toContainText('14 accepted tasks have not reached successful integration for over 30 minutes.');
  await page.screenshot({
    path: join(RESULTS, 'accepted-integration-alert-after--mocked.png'),
    fullPage: false,
  });
  await expect(banner).toContainText('AGT-2601');
  await expect(banner).toContainText('AGT-2610');
  await expect(banner).not.toContainText('AGT-2611');
  await expect(banner.getByTestId('accepted-integration-full-list')).toHaveText('and 4 more');
  await expectFlatFullBleedNoticeBar(banner);
  await expectNoticeCopySharesWideLine(page, banner);

  await banner.getByTestId('accepted-integration-full-list').click();
  await expect(page).toHaveURL(/\/board/);
  await expect(page.getByTestId('task-card')).toHaveCount(14);
  await expect(page.getByTestId('task-card').filter({ hasText: ordinaryReviewTask.title })).toHaveCount(0);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    await expect(banner).toBeVisible();
    await page.screenshot({
      path: join(RESULTS, `notice-bar-after-integration-narrow-${theme}--mocked.png`),
      fullPage: false,
    });
  }
});
