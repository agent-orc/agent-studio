import { test, expect, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

test.use({ serviceWorkers: 'block' });

/**
 * Regression coverage for the orchestrator "where am I right now" header
 * (`<app-orchestrator-context-header>`) wired into the side sheet.
 *
 * The chat + task endpoints are stubbed so the spec runs without a live
 * backend or CLI quota. What we lock:
 *   1. On board scope the header shows the active project chip and the
 *      "Board" scope chip (no task, no lane).
 *   2. When a CLI run is live in the active project, the header surfaces
 *      the live-run pill with the short model name and a ticking duration
 *      even without opening the task detail (board-scope run resolution
 *      via `App.orchSideSheetActiveRun`).
 *
 * The task-scope rendering (task key + title + lane pill) and the elapsed
 * formatter are covered exhaustively by the component unit spec; this E2E
 * proves the real-app wiring and produces the review screenshot.
 */

const PROJECT = 'project-neuen';
const RUNNING_TASK_ID = 'run-task-1';
const RUNNING_TASK_TITLE = 'Wire up the orchestrator header';
const LONG_CONTEXT_PROJECT = 'Agent Orchestrator Website';
const LONG_CONTEXT_TITLE = 'Family navigation consistency - decision';
const LONG_CONTEXT_WORKBENCH = 'family-navigation-consistency';
const RESULTS = process.env.JOB_RESULTS_DIR
  ? resolve(process.env.JOB_RESULTS_DIR)
  : resolve(process.cwd(), '..', 'results', 'AGT-2269');

mkdirSync(RESULTS, { recursive: true });

async function seedActiveTab(
  page: Page,
  tab: Record<string, unknown>,
  activeKey: string,
  theme: 'light' | 'dark',
): Promise<void> {
  await page.addInitScript(({ tab, activeKey, theme }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1, tabs: [tab], activeKey }));
    localStorage.setItem('atp.studio.theme', theme);
  }, { tab, activeKey, theme });
}

async function fulfillKnownGet(route: Route, body: unknown, unexpectedRequests: string[]) {
  const request = route.request();
  if (request.method() !== 'GET') {
    unexpectedRequests.push(`${request.method()} ${new URL(request.url()).pathname}`);
    await route.fulfill({
      status: 405,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'Unexpected method in mocked regression' }),
    });
    return;
  }
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

function runningTask() {
  return {
    id: RUNNING_TASK_ID,
    taskKey: PROJECT + '::' + RUNNING_TASK_ID,
    displayKey: 'AGT-1916',
    title: RUNNING_TASK_TITLE,
    state: '3-progress',
    order: 0,
    agent: 'claude',
    cliType: 'claude',
    model: 'claude-opus-4-8',
    createdAt: new Date().toISOString(),
    watchPath: 'C:/tmp/' + PROJECT,
    projectName: PROJECT,
    folderPath: 'C:/tmp/' + PROJECT + '/' + RUNNING_TASK_ID,
    lastActivity: new Date().toISOString(),
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    commit: null,
    execution: {
      jobId: RUNNING_TASK_ID,
      taskKey: PROJECT + '::' + RUNNING_TASK_ID,
      processId: 1234,
      // Two minutes ago -> duration label bucket "2m".
      startedAt: new Date(Date.now() - 120_000).toISOString(),
      status: 'running',
      exitCode: null,
      durationSeconds: null,
      model: 'claude-opus-4-8',
      thinkingLevel: null,
      runOutcome: null,
    },
  };
}

/**
 * The board bootstrap reads several endpoints beyond the four this spec cares
 * about. Stub each known object or list with its valid empty shape so Angular
 * can boot without a backend. The recorded fallback makes future dependencies
 * fail an assertion instead of silently broadening the mock surface.
 */
async function stubBoardBootstrap(page: Page): Promise<string[]> {
  const unexpectedRequests: string[] = [];

  // Keep this fallback first so the shape-correct routes below take precedence.
  // Recording every fallback hit prevents the hermetic boot from masking new
  // application dependencies when the board bootstrap changes.
  await page.route('**/api/**', async (route) => {
    const request = route.request();
    unexpectedRequests.push(`${request.method()} ${new URL(request.url()).pathname}`);
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });

  await page.route('**/api/auth/status', async (route) => {
    await fulfillKnownGet(
      route,
      { profile: 'local', bootstrapRequired: false, authenticated: false },
      unexpectedRequests,
    );
  });

  const emptyArrayEndpoints = /\/api\/(?:cli\/(?:claude|codex|gemini)\/models|clients\/?|crash-recovery\/pending|git\/summary|tags|workspaces)(?:\?.*)?$/;
  await page.route(emptyArrayEndpoints, async (route) => {
    await fulfillKnownGet(route, [], unexpectedRequests);
  });
  await page.route(/\/api\/(?:environment|clients\/[^/]+\/defaults|projects\/settings)(?:\?.*)?$/, async (route) => {
    await fulfillKnownGet(route, {}, unexpectedRequests);
  });
  await page.route(/\/api\/orchestrator\/sessions(?:\?.*)?$/, async (route) => {
    await fulfillKnownGet(route, { sessions: [] }, unexpectedRequests);
  });
  await page.route(/\/api\/runner\/status(?:\?.*)?$/, async (route) => {
    await fulfillKnownGet(route, { projects: {} }, unexpectedRequests);
  });
  await page.route(/\/api\/runner\/orchestrator-feed$/, async (route) => {
    await fulfillKnownGet(route, { entries: [], generatedAtUtc: '2026-08-10T08:00:00Z' }, unexpectedRequests);
  });
  // Board bootstrap dependencies added after this stub set was written
  // (AGT-2711 remote runner link health, auto-review queue).
  await page.route(/\/api\/runner\/auto-review-queue(?:\?.*)?$/, async (route) => {
    await fulfillKnownGet(route, { items: [], generatedAtUtc: '2026-08-10T08:00:00Z' }, unexpectedRequests);
  });
  await page.route(/\/api\/v1\/management\/remote-hosts\/link-health(?:\?.*)?$/, async (route) => {
    await fulfillKnownGet(route, { hosts: [], generatedAtUtc: '2026-08-10T08:00:00Z' }, unexpectedRequests);
  });
  await page.route(/\/api\/runner\/queue-starvation$/, async (route) => {
    await fulfillKnownGet(route, {
      active: false, waitingTaskCount: 0, availableSlots: 0, thresholdMinutes: 30,
      observedAt: '2026-08-10T08:00:00Z', oldestEnteredLaneAt: null, items: [],
    }, unexpectedRequests);
  });
  await page.route(/\/api\/pipeline\/accepted-integration-alert$/, async (route) => {
    await fulfillKnownGet(route, {
      active: false, stalledTaskCount: 0, thresholdMinutes: 30,
      observedAt: '2026-08-10T08:00:00Z', oldestAcceptedAt: null, items: [],
    }, unexpectedRequests);
  });
  await page.route(/\/api\/auto-review\/status$/, async (route) => {
    await fulfillKnownGet(route, {
      lastTickAt: null, accept: 0, reissue: 0, escalate: 0, aspectsRun: 0, pending: 0,
      currentJob: null, currentProject: null, activeJobs: [],
    }, unexpectedRequests);
  });
  await page.route(/\/api\/v1\/management\/remote-hosts$/, async (route) => {
    await fulfillKnownGet(route, [], unexpectedRequests);
  });
  await page.route(/\/api\/projects$/, async (route) => {
    await fulfillKnownGet(route, [], unexpectedRequests);
  });
  await page.route(/\/api\/cli\/quota(?:\?.*)?$/, async (route) => {
    await fulfillKnownGet(
      route,
      { at: '2026-01-01T00:00:00Z', snapshots: [], ttlSeconds: 600 },
      unexpectedRequests,
    );
  });
  await page.route(/\/api\/tasks\/archive(?:\?.*)?$/, async (route) => {
    await fulfillKnownGet(route, { items: [], total: 0, offset: 0, limit: 50 }, unexpectedRequests);
  });
  await page.route(/\/api\/bus\/[^/]+\/messages(?:\?.*)?$/, async (route) => {
    await fulfillKnownGet(route, [], unexpectedRequests);
  });

  // The live hub is outside this mocked regression's scope. Aborting the hub
  // is the established hermetic-suite behavior and avoids retrying a fake 404.
  await page.route('**/hubs/**', async (route) => route.abort());
  return unexpectedRequests;
}

async function stubWorkspace(
  page: Page,
  opts: {
    withRunningTask: boolean;
    executionContext?: Record<string, unknown>;
    project?: string;
  },
): Promise<string[]> {
  const unexpectedRequests = await stubBoardBootstrap(page);
  const project = opts.project ?? PROJECT;

  await page.route(
    new RegExp(`/api/projects/${encodeURIComponent(project)}/workbenches(?:\\?.*)?$`),
    async (route) => {
      await fulfillKnownGet(route, {
        projectName: project, includesHistory: false, count: 0, items: [],
      }, unexpectedRequests);
    },
  );

  await page.route(/\/api\/watch-paths$/, async (route) => {
    await fulfillKnownGet(
      route,
      [
        { name: project, path: 'C:/tmp/' + project, rootPath: 'C:/tmp/' + project, repositoryPath: '' },
      ],
      unexpectedRequests,
    );
  });

  await page.route(new RegExp(`/api/orchestrator/context/project:${encodeURIComponent(project)}$`), async (route) => {
    await fulfillKnownGet(
      route,
      {
        contextKey: `project:${project}`,
        capturedAt: '2026-07-11T10:00:00Z',
        digest: 'lanes: ready=0 | runs: active=0 | health: ok',
        sources: [
          { name: 'lanes', status: 'empty', capturedAt: '2026-07-11T10:00:00Z', detail: null },
          { name: 'health', status: 'ok', capturedAt: '2026-07-11T10:00:00Z', detail: null },
        ],
      },
      unexpectedRequests,
    );
  });

  const flatTasks = opts.withRunningTask ? [runningTask()] : [];
  await page.route(/\/api\/tasks(?:\?.*)?$/, async (route) => {
    await fulfillKnownGet(route, flatTasks, unexpectedRequests);
  });

  await page.route(/\/api\/tasks\/grouped(?:\?.*)?$/, async (route) => {
    const empty = {
      backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
      failedPickup: [], autoReview: [], humanReview: [], review: [], completed: [], archive: [],
    };
    await fulfillKnownGet(
      route,
      opts.withRunningTask ? { ...empty, progress: [runningTask()] } : empty,
      unexpectedRequests,
    );
  });

  await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, async (route) => {
    const projectMatch = /\/api\/runner\/([^/]+)\/orchestrator-chat/.exec(route.request().url());
    const project = projectMatch ? decodeURIComponent(projectMatch[1]) : '';
    await fulfillKnownGet(
      route,
      { project, turns: [], executionContext: opts.executionContext ?? null },
      unexpectedRequests,
    );
  });

  if (opts.withRunningTask) {
    // The active task tab and composer context resolve from the canonical tab
    // plus the already-loaded task list. Keep the heavy task-detail request
    // pending so unrelated detail-pane subresources cannot open an error
    // dialog over this focused composer regression.
    await page.route(
      new RegExp(`/api/tasks/${RUNNING_TASK_ID}(?:\\?.*)?$`),
      async () => new Promise<void>(() => undefined),
    );
    await page.route(new RegExp(`/api/orchestrator/context/task:${PROJECT}/AGT-1916$`), async (route) => {
      await fulfillKnownGet(route, {
        contextKey: `task:${PROJECT}/AGT-1916`,
        capturedAt: '2026-07-11T10:00:00Z',
        digest: 'task: AGT-1916 | health: ok',
        sources: [],
      }, unexpectedRequests);
    });
  }
  return unexpectedRequests;
}

async function stubLongWorkbench(page: Page): Promise<string[]> {
  const unexpectedRequests = await stubWorkspace(page, {
    withRunningTask: false,
    project: LONG_CONTEXT_PROJECT,
  });
  const encodedProject = encodeURIComponent(LONG_CONTEXT_PROJECT);

  await page.route(/\/api\/workbenches(?:\?.*)?$/, async (route) => {
    await fulfillKnownGet(route, {
      projectName: LONG_CONTEXT_PROJECT,
      count: 1,
      currentCount: 1,
      historyCount: 0,
      items: [],
    }, unexpectedRequests);
  });
  await page.route(
    new RegExp(`/api/orchestrator/context/project:${encodedProject}$`),
    async (route) => {
      await fulfillKnownGet(route, {
        contextKey: `project:${LONG_CONTEXT_PROJECT}`,
        capturedAt: '2026-08-10T08:00:00Z',
        digest: 'workbench: decision-pending | health: ok',
        sources: [],
      }, unexpectedRequests);
    },
  );
  await page.route(/\/api\/cli\/codex\/models(?:\?.*)?$/, async (route) => {
    await fulfillKnownGet(route, {
      models: [{
        id: 'gpt-5.6-sol',
        label: 'GPT-5.6 Sol',
        isDefault: true,
        available: true,
        thinkingLevels: ['high'],
        defaultThinkingLevel: 'high',
      }],
      source: 'long-context-regression',
    }, unexpectedRequests);
  });
  await page.route(
    new RegExp(`/api/projects/${encodedProject}/workbenches(?:\\?.*)?$`),
    async (route) => {
      await fulfillKnownGet(route, {
        projectName: LONG_CONTEXT_PROJECT,
        includesHistory: false,
        count: 1,
        items: [{
          id: LONG_CONTEXT_WORKBENCH,
          key: 'AOW-W1',
          title: LONG_CONTEXT_TITLE,
          summary: 'Long composer context regression fixture.',
          status: 'decision-pending',
          phase: 'decision',
          updatedAtUtc: '2026-08-10T08:00:00Z',
          entryPath: `docs/workbenches/${LONG_CONTEXT_WORKBENCH}/index.html`,
          valid: true,
          error: null,
          sourceTaskKeys: [],
          relatedTaskKeys: [],
        }],
      }, unexpectedRequests);
    },
  );
  await page.route(
    new RegExp(`/api/projects/${encodedProject}/workbenches/${LONG_CONTEXT_WORKBENCH}$`),
    async (route) => {
      await fulfillKnownGet(route, {
        workbench: {
          id: LONG_CONTEXT_WORKBENCH,
          key: 'AOW-W1',
          title: LONG_CONTEXT_TITLE,
          summary: 'Long composer context regression fixture.',
          status: 'decision-pending',
          phase: 'decision',
          updatedAtUtc: '2026-08-10T08:00:00Z',
          entryPath: `docs/workbenches/${LONG_CONTEXT_WORKBENCH}/index.html`,
          valid: true,
          error: null,
          sourceTaskKeys: [],
          relatedTaskKeys: [],
        },
        html: '<h1>Family navigation consistency</h1>',
        branch: 'develop',
        revision: '0123456789abcdef0123456789abcdef01234567',
        workingTreeModified: false,
        fingerprint: null,
      }, unexpectedRequests);
    },
  );
  await page.route(
    new RegExp(`/api/projects/${encodedProject}/workbenches/AOW-W1/references$`),
    async (route) => {
      await fulfillKnownGet(route, {
        projectName: LONG_CONTEXT_PROJECT,
        workbenchKey: 'AOW-W1',
        workbenchId: LONG_CONTEXT_WORKBENCH,
        legacyTaskKeys: [],
        items: [],
      }, unexpectedRequests);
    },
  );
  await page.route(/\/api\/tasks\/reference-status$/, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  return unexpectedRequests;
}

async function openSideSheet(page: Page, openContextMenu = true) {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  await showSideSheet(page, openContextMenu);
}

async function showSideSheet(page: Page, openContextMenu = true) {
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  await toggle.click();
  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
  if (openContextMenu) {
    await page.getByTestId('orch-context-badge').click();
    await expect(page.getByTestId('orch-context-menu')).toBeVisible();
  }
}

test.describe('Orchestrator context header · where am I', () => {
  test('shows the exact remote host checkout, branch, and HEAD reported by the runner', async ({ page }) => {
    await seedActiveTab(page, { kind: 'board', projectName: PROJECT }, `board:${PROJECT}`, 'dark');
    const head = '0123456789abcdef0123456789abcdef01234567';
    const repoPath = '/srv/agent-runner/work/PROJ-002/project-chat';
    const unexpectedRequests = await stubWorkspace(page, {
      withRunningTask: false,
      executionContext: {
        executionKind: 'remote',
        hostName: 'agent-runner-01',
        repoPath,
        branch: 'develop',
        headSha: head,
        state: 'ready',
        capturedAt: '2026-07-23T15:00:00Z',
      },
    });
    await openSideSheet(page, false);

    const execution = page.getByTestId('orch-execution-context');
    await expect(execution).toBeVisible();
    await expect(page.getByTestId('orch-execution-host')).toHaveText('agent-runner-01');
    await expect(page.getByTestId('orch-execution-repo')).toHaveText(repoPath);
    await expect(page.getByTestId('orch-execution-revision')).toHaveText(
      `· develop@${head.slice(0, 8)}`,
    );
    await expect(execution).toHaveAttribute('title', new RegExp(
      `Execution: agent-runner-01[\\s\\S]*Repository: ${repoPath}[\\s\\S]*Branch: develop[\\s\\S]*HEAD: ${head}`,
    ));

    await page.screenshot({
      path: resolve(RESULTS, 'orchestrator-chat-remote-execution-context--mocked.png'),
      fullPage: false,
    });
    expect(unexpectedRequests).toEqual([]);
  });

  test('labels a project without an execution runner as local', async ({ page }) => {
    await seedActiveTab(page, { kind: 'board', projectName: PROJECT }, `board:${PROJECT}`, 'light');
    const unexpectedRequests = await stubWorkspace(page, {
      withRunningTask: false,
      executionContext: {
        executionKind: 'local',
        hostName: 'local',
        repoPath: '/workspace/agent-studio',
        branch: 'develop',
        headSha: 'fedcba9876543210fedcba9876543210fedcba98',
        state: 'ready',
        capturedAt: '2026-07-23T15:00:00Z',
      },
    });
    await openSideSheet(page, false);

    await expect(page.getByTestId('orch-execution-host')).toHaveText('Local');
    await expect(page.getByTestId('orch-execution-repo')).toHaveText('/workspace/agent-studio');
    await expect(page.getByTestId('orch-execution-revision')).toHaveText('· develop@fedcba98');
    expect(unexpectedRequests).toEqual([]);
  });

  test('board scope shows the project chip and the Board scope chip', async ({ page }) => {
    const unexpectedRequests = await stubWorkspace(page, { withRunningTask: false });
    await openSideSheet(page);

    const header = page.getByTestId('orch-context-header');
    await expect(header).toBeVisible();
    await expect(header).toHaveAttribute('data-scope', 'board');
    await expect(page.getByTestId('orch-context-project')).toContainText(PROJECT);
    await expect(page.getByTestId('orch-context-board')).toHaveText('Board');
    // Nothing running -> no live-run pill.
    await expect(page.getByTestId('orch-context-run')).toHaveCount(0);
    expect(unexpectedRequests).toEqual([]);
  });

  test('surfaces the live run (model + duration) when a run is active in the project', async ({ page }) => {
    const unexpectedRequests = await stubWorkspace(page, { withRunningTask: true });
    await openSideSheet(page);

    const header = page.getByTestId('orch-context-header');
    await expect(header).toBeVisible();
    await expect(page.getByTestId('orch-context-project')).toContainText(PROJECT);

    const run = page.getByTestId('orch-context-run');
    await expect(run).toBeVisible();
    await expect(page.getByTestId('orch-context-run-model')).toHaveText('opus 4.8');
    await expect(page.getByTestId('orch-context-run-duration')).toBeVisible();

    await page.screenshot({
      path: 'screenshots/orchestrator-context-header/live-run--mocked.png',
      fullPage: false,
    });
    expect(unexpectedRequests).toEqual([]);
  });

  test('standard footer receives Board context and keeps canonical keyboard order in light theme', async ({ page }) => {
    await seedActiveTab(page, { kind: 'board', projectName: PROJECT }, `board:${PROJECT}`, 'light');
    const unexpectedRequests = await stubWorkspace(page, { withRunningTask: false });
    await openSideSheet(page, false);

    const sheet = page.getByTestId('orch-side-sheet');
    await expect(page.getByTestId('chat-composer-foot')).toHaveCount(1);
    // Three rows at most: chips, textarea, footer. No toolbar row, no
    // breadcrumb, no attach affordance.
    await expect(page.getByTestId('chat-toolbar')).toHaveCount(0);
    await expect(page.getByTestId('chat-composer-context')).toHaveCount(0);
    await expect(page.getByTestId('chat-attach')).toHaveCount(0);
    await expect(page.getByTestId('chat-context-attachment-context:automatic'))
      .toContainText('Board');
    await expect(page.getByText('Make a task from your message', { exact: true })).toHaveCount(0);
    await expect(page.getByText('Make a task from this reply', { exact: true })).toHaveCount(0);

    const input = page.getByTestId('chat-input');
    await input.fill('Keyboard order draft');
    await input.focus();
    await page.keyboard.press('Shift+Tab');
    await expect(page.getByTestId('chat-context-attachment-add')).toBeFocused();
    await input.focus();
    await page.keyboard.press('Tab');
    await expect(page.getByTestId('cac-model-selector-trigger')).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(page.getByTestId('chat-send')).toBeFocused();

    await sheet.screenshot({ path: resolve(RESULTS, 'orchestrator-board-context-light.png') });
    expect(unexpectedRequests).toEqual([]);
  });

  test('standard footer receives Task context at mobile width in dark theme', async ({ page }) => {
    const task = runningTask();
    await page.setViewportSize({ width: 390, height: 844 });
    await seedActiveTab(
      page,
      { kind: 'task', taskKey: task.taskKey },
      `task:${task.taskKey}`,
      'dark',
    );
    await stubWorkspace(page, { withRunningTask: true });
    await openSideSheet(page, false);

    const sheet = page.getByTestId('orch-side-sheet');
    // The automatic context chip is the composer's location indicator now.
    await expect(page.getByTestId('chat-context-attachment-context:automatic'))
      .toContainText('AGT-1916');
    await expect(page.getByTestId('chat-composer-context')).toHaveCount(0);
    const box = await sheet.boundingBox();
    expect(box?.width ?? 999).toBeLessThanOrEqual(390);
    await sheet.screenshot({ path: resolve(RESULTS, 'orchestrator-task-context-dark-mobile.png') });
  });

  for (const variant of [
    { name: 'narrow light', width: 390, height: 844, theme: 'light' as const },
    { name: 'narrow dark', width: 390, height: 844, theme: 'dark' as const },
  ]) {
    test(`keeps the Dossier context chip row compact in ${variant.name}`, async ({ page }) => {
      await page.setViewportSize({ width: 1280, height: variant.height });
      await seedActiveTab(page, {
        kind: 'board',
        projectName: LONG_CONTEXT_PROJECT,
      }, `board:${LONG_CONTEXT_PROJECT}`, variant.theme);
      const unexpectedRequests = await stubLongWorkbench(page);
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      const workbenchSection = page.getByTestId(
        `studio-explorer-project-workbenches-${LONG_CONTEXT_PROJECT}`,
      );
      const workbench = page.getByTestId(
        `studio-explorer-workbench-${LONG_CONTEXT_PROJECT}-${LONG_CONTEXT_WORKBENCH}`,
      );
      if (await page.locator('app-orchestrator-side-sheet.is-open').count()) {
        await page.getByTestId('orch-side-sheet-toggle').click();
      }
      if (!await workbench.isVisible()) await workbenchSection.click();
      await expect(workbench).toBeVisible();
      await workbench.click();
      await page.setViewportSize({ width: variant.width, height: variant.height });
      if (!await page.locator('app-orchestrator-side-sheet.is-open').count()) {
        await showSideSheet(page, false);
      }

      // The chip row belongs to the library composer since 0.4.1; the host
      // only feeds it. The stable Dossier key plus its estimate must stay on
      // one line, with the long title behind the chip tooltip.
      const chipRow = page.getByTestId('chat-context-attachments');
      const chip = page.getByTestId('chat-context-attachment-context:automatic');
      await expect(chipRow).toBeVisible();
      await expect(chip).toContainText('AOW-W1');
      await expect(chip).toContainText('~1.6k');
      await expect(chip).not.toContainText(LONG_CONTEXT_TITLE);

      const layout = await chipRow.evaluate((element) => {
        const rowItems = [
          element.querySelector<HTMLElement>('[data-testid="chat-context-attachment-context:automatic"]')!,
          element.querySelector<HTMLElement>('[data-testid="chat-context-attachment-add"]')!,
        ];
        // The library owns the chip's inner label and gives it no testid, so
        // this one structural lookup reads its truncation style directly.
        const label = rowItems[0].querySelector<HTMLElement>('span')!;
        const centres = rowItems.map(item => {
          const box = item.getBoundingClientRect();
          return box.top + box.height / 2;
        });
        return {
          fits: element.scrollWidth <= element.clientWidth + 1,
          // Chip and add button share one visual row. The two controls differ
          // by a pixel of border/line-box, so compare centres with a tolerance
          // instead of requiring an identical rounded value.
          sameRow: Math.max(...centres) - Math.min(...centres) <= 2,
          chipWhiteSpace: getComputedStyle(label).whiteSpace,
          labelOverflow: getComputedStyle(label).textOverflow,
        };
      });
      expect(layout).toEqual({
        fits: true,
        sameRow: true,
        chipWhiteSpace: 'nowrap',
        labelOverflow: 'ellipsis',
      });

      await page.mouse.move(1, 1);
      await page.getByTestId('orch-side-sheet').screenshot({
        path: resolve(
          RESULTS,
          `orchestrator-context-chip--after--${variant.name.replace(' ', '-')}--mocked.png`,
        ),
      });

      await chip.hover();
      const tooltip = page.locator('.cac-tooltip');
      await expect(tooltip).toContainText(LONG_CONTEXT_TITLE);
      await expect(tooltip).toContainText('Current tab · Dossier');
      await expect(tooltip).toContainText('~1.6k');
      await page.getByTestId('orch-side-sheet').screenshot({
        path: resolve(
          RESULTS,
          `orchestrator-context-chip-meta-tooltip--after--${variant.name.replace(' ', '-')}--mocked.png`,
        ),
      });

      expect(unexpectedRequests).toEqual([]);
    });
  }
  for (const variant of [
    { name: 'wide-light', width: 1440, panelWidth: 640, theme: 'light' as const },
    { name: 'wide-dark', width: 1440, panelWidth: 640, theme: 'dark' as const },
    { name: 'narrow-light', width: 900, panelWidth: 360, theme: 'light' as const },
    { name: 'narrow-dark', width: 900, panelWidth: 360, theme: 'dark' as const },
  ]) {
    test(`AGT-2613 keeps the panel frame and header collision-free in ${variant.name}`, async ({
      page,
    }) => {
      await page.setViewportSize({ width: variant.width, height: 900 });
      await seedActiveTab(
        page,
        { kind: 'board', projectName: LONG_CONTEXT_PROJECT },
        `board:${LONG_CONTEXT_PROJECT}`,
        variant.theme,
      );
      await page.addInitScript((width) => {
        localStorage.setItem('atp.studio.orchestratorWidth', String(width));
      }, variant.panelWidth);
      const unexpectedRequests = await stubWorkspace(page, {
        withRunningTask: false,
        project: LONG_CONTEXT_PROJECT,
      });
      const turns = Array.from({ length: 60 }, (_, index) => ({
        id: `agt-2613-turn-${index}`,
        ts: new Date(Date.UTC(2026, 7, 11, 9, index)).toISOString(),
        role: index % 2 === 0 ? 'user' : 'orchestrator',
        text: `Panel layering regression message ${index}. This line remains readable behind transient controls.`,
      }));
      await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            project: LONG_CONTEXT_PROJECT,
            turns,
            executionContext: {
              executionKind: 'remote',
              hostName: 'agent-runner-01',
              repoPath: '/srv/agent-runner/work/PROJ-002/agent-studio',
              branch: 'develop',
              headSha: '0123456789abcdef0123456789abcdef01234567',
              state: 'ready',
              capturedAt: '2026-08-11T09:00:00Z',
            },
          }),
        }),
      );
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      await expect(page.getByTestId('lane-2-ready')).toBeVisible();
      await showSideSheet(page, false);

      const sheet = page.getByTestId('orch-side-sheet');
      const sheetHost = page.locator('app-orchestrator-side-sheet');
      const ensureSideSheetOpen = async () => {
        const width = (await sheetHost.boundingBox())?.width ?? 0;
        if (width < variant.panelWidth - 2) {
          await page.getByTestId('orch-side-sheet-toggle').click();
        }
        await expect
          .poll(async () => (await sheetHost.boundingBox())?.width ?? 0)
          .toBeGreaterThanOrEqual(variant.panelWidth - 1);
        await expect(sheet).toBeVisible();
      };
      const phase = process.env.AGT2613_PHASE ?? 'after';
      if (phase === 'before') {
        await ensureSideSheetOpen();
        await page.getByTestId('orchestrator-conversation').hover();
        await page.mouse.wheel(0, -600);
        await expect(page.getByTestId('conversation-jump-latest')).toBeVisible();
        await ensureSideSheetOpen();
        await page.screenshot({
          path: resolve(RESULTS, `${phase}-${variant.name}.png`),
          fullPage: false,
        });
        expect(unexpectedRequests).toEqual([]);
        return;
      }

      await ensureSideSheetOpen();
      const header = page.getByTestId('orch-panel-header');
      const contextName = page.getByTestId('orch-panel-context-name');
      await expect(header).toBeVisible();
      await expect(page.getByTestId('orch-panel-context-type')).toHaveText('Project');
      await expect(contextName).toHaveText(LONG_CONTEXT_PROJECT);
      await expect(page.getByTestId('orch-execution-host')).toHaveText('agent-runner-01');
      await expect(page.getByTestId('orch-execution-revision')).toHaveText('· develop@01234567');
      await expect(page.getByTestId('orch-execution-context')).toHaveAttribute(
        'title',
        /HEAD: 0123456789abcdef0123456789abcdef01234567/,
      );
      await expect(page.getByTestId('orch-execution-ref')).toHaveCSS('text-overflow', 'ellipsis');

      const scroller = page.getByTestId('conversation-view');
      await expect(
        scroller.getByText('Panel layering regression message 59.', { exact: false }),
      ).toBeAttached();
      await scroller.evaluate((element) => {
        element.scrollTop = Math.max(0, element.scrollHeight - element.clientHeight - 120);
        element.dispatchEvent(new Event('scroll'));
      });
      const jump = page.getByTestId('orchestrator-jump-latest');
      await expect(jump).toBeVisible();
      await ensureSideSheetOpen();

      const geometry = await page.evaluate(() => {
        const sheet = document.querySelector<HTMLElement>('[data-testid="orch-side-sheet"]')!;
        const editor = document.querySelector<HTMLElement>('app-studio-shell .studio-editor')!;
        const sharedHeader = sheet.querySelector<HTMLElement>('.sidesheet__header')!;
        const panelHeader = sheet.querySelector<HTMLElement>('[data-testid="orch-panel-header"]')!;
        const identity = sheet.querySelector<HTMLElement>('[data-testid="orch-panel-context-identity"]')!;
        const chats = sheet.querySelector<HTMLElement>('[data-testid="orch-context-badge"]')!;
        const close = sheet.querySelector<HTMLElement>('[data-testid="sidesheet-close"]')!;
        const jump = sheet.querySelector<HTMLElement>('[data-testid="orchestrator-jump-latest"]')!;
        const composer = sheet.querySelector<HTMLElement>('.sheet__composer')!;
        const scroller = sheet.querySelector<HTMLElement>('[data-testid="conversation-view"]')!;
        const rect = (element: HTMLElement) => {
          const box = element.getBoundingClientRect();
          return {
            left: box.left,
            right: box.right,
            top: box.top,
            bottom: box.bottom,
            width: box.width,
            height: box.height,
          };
        };
        const scrollerBox = rect(scroller);
        const jumpBox = rect(jump);
        const visibleRows = Array.from(
          sheet.querySelectorAll<HTMLElement>('[data-testid^="conversation-message-message."]'),
        )
          .map((row) => {
            const box = rect(row);
            return {
              ...box,
              top: Math.max(box.top, scrollerBox.top),
              bottom: Math.min(box.bottom, scrollerBox.bottom),
            };
          })
          .filter(
            (box) =>
              box.bottom > box.top &&
              box.bottom > jumpBox.top - 80 &&
              box.top < jumpBox.bottom + 80,
          );
        const sheetStyle = getComputedStyle(sheet);
        return {
          sheet: rect(sheet),
          editor: rect(editor),
          header: rect(sharedHeader),
          identity: rect(identity),
          chats: rect(chats),
          close: rect(close),
          jump: rect(jump),
          composer: rect(composer),
          nearbyRows: visibleRows,
          borderLeftWidth: sheetStyle.borderLeftWidth,
          panelBackground: sheetStyle.backgroundColor,
          editorBackground: getComputedStyle(editor).backgroundColor,
          panelFits: panelHeader.scrollWidth <= panelHeader.clientWidth + 1,
        };
      });
      expect(geometry.borderLeftWidth).toBe('1px');
      expect(Math.round(geometry.sheet.width)).toBe(variant.panelWidth);
      expect(geometry.panelBackground).not.toBe(geometry.editorBackground);
      expect(geometry.editor.right).toBeLessThanOrEqual(geometry.sheet.left + 1);
      expect(geometry.header.height).toBe(36);
      expect(geometry.panelFits).toBe(true);
      expect(geometry.identity.right).toBeLessThanOrEqual(geometry.chats.left + 1);
      expect(geometry.chats.height).toBe(28);
      expect(geometry.close.height).toBe(28);
      expect(geometry.jump.bottom).toBeLessThanOrEqual(geometry.composer.top);
      expect(
        geometry.nearbyRows.every(
          (row) => row.bottom <= geometry.jump.top || row.top >= geometry.jump.bottom,
        ),
      ).toBe(true);
      await expect(contextName).toHaveCSS('text-overflow', 'ellipsis');
      await expect(contextName).toHaveAttribute('title', LONG_CONTEXT_PROJECT);

      await ensureSideSheetOpen();
      await page.screenshot({
        path: resolve(RESULTS, `${phase}-${variant.name}.png`),
        fullPage: false,
      });
      expect(unexpectedRequests).toEqual([]);
    });
  }
});
