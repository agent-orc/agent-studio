import { expect, test, type Page, type Request, type Route, type TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const TASK_ID = 'task-orchestrator-chat-fixture';
const TASK_KEY = 'AGT-2577';
const PROJECT = 'Agent Studio';
const WATCH_PATH = '/tmp/task-orchestrator-chat';
const CONTEXT_KEY = `task:${PROJECT}/${TASK_KEY}`;

interface ChatTurn {
  id: string;
  ts: string;
  role: 'user' | 'orchestrator';
  text: string;
  contextReceipt?: {
    scope: 'task';
    contextKey: string;
    taskKey: string;
    includedBlocks: string[];
    capturedAt: string;
  };
}

const TASK_INFO = {
  id: TASK_ID,
  taskKey: `${WATCH_PATH}::${TASK_ID}`,
  key: TASK_KEY,
  displayKey: TASK_KEY,
  title: 'Keep task conversations in the Orchestrator side sheet',
  state: '2-ready',
  order: 1,
  agent: 'codex',
  cliType: 'codex',
  model: null,
  createdAt: '2026-08-10T12:00:00Z',
  lastActivity: '2026-08-10T12:00:00Z',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/2-ready/${TASK_ID}`,
  sessionName: null,
  useOwnSession: null,
  lastUsage: null,
  execution: null,
  commit: null,
  commits: [],
  ownerClientId: 'local-default',
};

const TASK_DETAIL = {
  info: TASK_INFO,
  promptMarkdown: '# Orchestrator task context fixture\n\nKeep the Activity transcript unchanged.',
  statusMarkdown: null,
  log: [],
  promptHistory: [],
  titleHistory: [],
  contextUsage: null,
  reviewEvidence: [],
  summaryState: null,
};

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [TASK_INFO],
  progress: [], failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
  humanReview: [], escalated: [], completed: [], archive: [],
};

const evidenceDir = path.join(
  process.env.JOB_RESULTS_DIR?.trim() || path.resolve('test-results'),
  'task-orchestrator-chat',
);

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installRoutes(
  page: Page,
  chatPosts: { url: string; body: Record<string, unknown> }[],
): Promise<void> {
  const turns: ChatTurn[] = [];
  await page.route('**/api/**', async route => {
    const request = route.request();
    const pathname = decodeURIComponent(new URL(request.url()).pathname);

    if (/\/api\/runner\/task:[^/]+\/[^/]+\/orchestrator-chat$/.test(pathname)) {
      if (request.method() === 'GET') return json(route, { project: PROJECT, turns });

      const body = request.postDataJSON() as Record<string, unknown>;
      chatPosts.push({ url: request.url(), body });
      const now = new Date().toISOString();
      const userTurn: ChatTurn = {
        id: `user-${Date.now()}`,
        ts: now,
        role: 'user',
        text: String(body['text']),
      };
      const reply: ChatTurn = {
        id: `reply-${Date.now()}`,
        ts: now,
        role: 'orchestrator',
        text: `This answer is scoped to ${TASK_KEY}. The task agent remains unchanged.`,
        contextReceipt: {
          scope: 'task',
          contextKey: CONTEXT_KEY,
          taskKey: TASK_KEY,
          includedBlocks: ['navigation', 'task metadata', 'task prompt', 'task status'],
          capturedAt: now,
        },
      };
      turns.push(userTurn, reply);
      return json(route, { project: PROJECT, reply });
    }
    if (pathname === '/api/auth/status') {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (pathname === '/api/watch-paths') {
      return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    }
    if (pathname === '/api/tasks/grouped') return json(route, EMPTY_GROUPED);
    if (pathname === '/api/tasks/archive') return json(route, { items: [], total: 0 });
    if (pathname === '/api/tasks/reference-status') return json(route, { items: [] });
    if (pathname === `/api/tasks/${TASK_ID}`) return json(route, TASK_DETAIL);
    if (/\/api\/tasks\/[^/]+\/pipeline$/.test(pathname)) {
      return json(route, {
        pipeline: {
          id: 'fixture', displayName: 'Fixture', version: 1,
          pre: [], core: [], post: [], allSteps: [],
        },
        execution: null,
        cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
        config: {},
      });
    }
    if (/\/api\/tasks\/[^/]+\/plan$/.test(pathname)) {
      return json(route, {
        hasPlan: false, source: null, snapshotCount: 0, activeItemId: null,
        softEstimateMedian: null, items: [], unassignedSubActions: [],
      });
    }
    if (/\/api\/tasks\/[^/]+\/agent-work-summary$/.test(pathname)) {
      return json(route, { calls: 0, toolCalls: 0, toolCounts: [], recovered: false });
    }
    if (/\/api\/tasks\/[^/]+\/runs$/.test(pathname)) return json(route, { runs: [] });
    if (/\/api\/tasks\/[^/]+\/session-events$/.test(pathname)) {
      return json(route, { events: [], sessionChain: [] });
    }
    if (pathname === '/api/runner/status') return json(route, { projects: {} });
    if (pathname === '/api/runner/global') return json(route, { mode: 'paused', activeProjects: [] });
    if (pathname === '/api/crash-recovery/pending') return json(route, { pending: [] });
    if (pathname === '/api/cli/quota') return json(route, { snapshots: [], ttlSeconds: 600 });
    if (pathname === '/api/environment') return json(route, { isDev: false, devTools: {} });
    if (pathname === '/api/orchestrator/sessions') return json(route, { sessions: [] });
    if (pathname === `/api/orchestrator/context/${CONTEXT_KEY}`) {
      return json(route, {
        contextKey: CONTEXT_KEY,
        capturedAt: '2026-08-11T10:00:00Z',
        digest: `${TASK_KEY} task context`,
        sources: [],
      });
    }
    if (/\/api\/projects\/[^/]+\/workbenches$/.test(pathname)) {
      return json(route, { items: [] });
    }
    if (/\/api\/cli\/[^/]+\/models$/.test(pathname)) {
      return json(route, { models: [], source: 'task-context-sidesheet-e2e' });
    }
    return json(route, []);
  });
}

async function captureWorkspace(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  mkdirSync(evidenceDir, { recursive: true });
  const screenshot = await page.screenshot({
    path: path.join(evidenceDir, `${name}--mocked.png`),
    fullPage: false,
  });
  await testInfo.attach(`${name}--mocked.png`, {
    body: screenshot,
    contentType: 'image/png',
  });
}

test('Task detail uses the Orchestrator side sheet for task context without a Chat tab', async (
  { page },
  testInfo,
) => {
  const chatPosts: { url: string; body: Record<string, unknown> }[] = [];
  const taskAgentMutations: string[] = [];
  await installRoutes(page, chatPosts);
  page.on('request', (request: Request) => {
    if (request.method() === 'GET') return;
    const pathname = new URL(request.url()).pathname;
    if (/\/(?:start|stop|continue)$/.test(pathname)) taskAgentMutations.push(pathname);
  });

  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto(`/?job=${TASK_ID}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

  const activityTab = page.getByTestId('inspector-tab-activity');
  await expect(activityTab).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('inspector-tab-chat')).toHaveCount(0);
  await expect(page.getByTestId('inspector-tab-task')).toBeVisible();
  await expect(page.getByTestId('inspector-tab-protocol')).toBeVisible();
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('activity-chat-compose')).toBeVisible();

  const transcriptRequest = page.waitForRequest(request =>
    request.method() === 'GET'
    && /\/api\/runner\/task:[^/]+\/[^/]+\/orchestrator-chat$/.test(
      decodeURIComponent(new URL(request.url()).pathname),
    ),
  );
  await page.getByTestId('orch-side-sheet-toggle').click();
  const openedRequest = await transcriptRequest;
  expect(decodeURIComponent(openedRequest.url())).toContain(CONTEXT_KEY);

  const sideSheet = page.getByTestId('orch-side-sheet');
  await expect(sideSheet).toBeVisible();
  await expect(sideSheet.getByTestId('orch-panel-context-type')).toHaveText('Task');
  await expect(sideSheet.getByTestId('orch-panel-context-name')).toContainText(TASK_KEY);
  await expect(sideSheet.getByTestId('orch-task-context-note')).toContainText(
    `Questions automatically refer to ${TASK_KEY}.`,
  );
  await expect(sideSheet.getByTestId('orch-task-context-note')).toContainText(
    'Answers do not start, pause, or continue the task agent.',
  );
  await expect(sideSheet.getByTestId('chat-context-attachment-automatic-context'))
    .toContainText(`${TASK_KEY} · ~1.6k`);
  await expect(sideSheet.getByTestId('chat-toolbar')).toHaveCount(0);
  await expect(sideSheet.getByTestId('chat-attach')).toHaveCount(0);
  await expect(page.getByTestId('activity-chat-compose')).toBeVisible();

  await sideSheet.getByTestId('chat-input').fill('What is the current verification status?');
  await sideSheet.getByTestId('chat-send').click();
  await expect.poll(() => chatPosts.length).toBe(1);
  await expect(sideSheet).toContainText(`This answer is scoped to ${TASK_KEY}.`);

  const requestBody = chatPosts[0].body;
  expect(requestBody['navigationContext']).toMatchObject({
    currentPage: 'task-detail',
    currentTaskId: TASK_ID,
    currentTaskKey: TASK_KEY,
    observedSurface: 'Agent Studio Orchestrator chat',
  });
  expect(requestBody['contextEnvelope']).toMatchObject({
    scope: { kind: 'task', contextKey: CONTEXT_KEY, projectId: PROJECT, taskKey: TASK_KEY },
    activeSurface: { kind: 'task', taskKey: TASK_KEY },
  });
  expect(taskAgentMutations).toEqual([]);
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);

  await setTheme(page, 'light');
  await captureWorkspace(page, testInfo, 'task-context-sidesheet-light');
  await setTheme(page, 'dark');
  await captureWorkspace(page, testInfo, 'task-context-sidesheet-dark');
});
