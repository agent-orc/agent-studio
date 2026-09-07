import { test, expect, Page, Request } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * Regression coverage for the "project chat must know its navigation
 * context" fix. The 2026-05-09 incident: operator viewing a task's detail
 * page asked "Was ist der aktuelle Task-Kontext?" in the project chat,
 * the agent had no idea which page the operator was on and produced token
 * soup ("Conversation, Foul Conversation, blablabla").
 *
 * This spec pins:
 *   1. Every project-chat POST carries a `navigationContext` block.
 *   2. When a task detail is open, `currentTaskId`/`Title` are present and
 *      `currentPage` is `task-detail`.
 *   3. When no task is open, `currentPage` is `kanban-board` and the task
 *      fields are absent.
 *   4. The rendered reply contains a real reference to the task (its id /
 *      title) and DOES NOT contain known hallucination signatures.
 *
 * The chat backend's actual LLM call is stubbed so the spec runs without
 * burning quota. The deeper "agent uses the context in the prompt" rule is
 * locked by the backend unit test `OrchestratorChatNavigationContextTests`.
 */

const TASK_ID = 'bug-auto-review-reorder-drops-card';
const TASK_TITLE = 'Bug: reordering a card inside auto-review drops it from the lane';
const PROJECT = 'demo-project';
const RESULTS = process.env.JOB_RESULTS_DIR
  ? resolve(process.env.JOB_RESULTS_DIR)
  : resolve(process.cwd(), '..', 'results', 'AGT-2517');

mkdirSync(RESULTS, { recursive: true });

const HALLUCINATION_SIGNATURES = [
  /Conversation,?\s*Foul Conversation/i,
  /blablabla/i,
  /\b(hello[, ]+){3,}/i,
];

interface CapturedRequest {
  body: { text: string; navigationContext?: Record<string, unknown> | null };
  headers: Record<string, string>;
}

const TASK_INFO = {
  id: TASK_ID,
  jobKey: PROJECT + '::' + TASK_ID,
  taskKey: PROJECT + '::' + TASK_ID,
  displayKey: 'CTX-1',
  title: TASK_TITLE,
  state: '4-auto-review',
  order: 0,
  agent: null,
  cliType: 'claude',
  model: null,
  createdAt: new Date().toISOString(),
  watchPath: 'C:/tmp/' + PROJECT,
  projectName: PROJECT,
  folderPath: 'C:/tmp/' + PROJECT + '/' + TASK_ID,
  execution: null
};

/**
 * Stub the orchestrator-chat GET and POST routes. The stub is stateful:
 * after a POST, the generated reply turn is stored and returned in
 * subsequent GETs so the component's `refresh(true)` call after send
 * picks up the orchestrator bubble.
 */
async function stubChatAndCapture(page: Page) {
  const captured: CapturedRequest[] = [];
  const persistedTurns: {
    id: string;
    ts: string;
    role: string;
    text: string;
    contextReceipt?: {
      scope: string;
      contextKey: string;
      taskKey: string | null;
      includedBlocks: string[];
      capturedAt: string;
    };
  }[] = [];

  await page.route(/\/api\/runner\/[^/]+(?:\/[^/]+)?\/orchestrator-chat$/, async (route) => {
    const req: Request = route.request();
    if (req.method() === 'GET') {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ project: PROJECT, turns: [...persistedTurns] })
      });
      return;
    }
    if (req.method() !== 'POST') {
      await route.continue();
      return;
    }

    const body = req.postDataJSON() as CapturedRequest['body'];
    captured.push({ body, headers: req.headers() });

    const nav = body?.navigationContext;
    const taskId = nav?.currentTaskId as string | undefined;
    const taskTitle = nav?.currentTaskTitle as string | undefined;
    const replyText = taskId
      ? `You are currently viewing **${taskTitle ?? taskId}** (id: \`${taskId}\`). What about it would you like to discuss?`
      : 'No task is currently selected. Which task did you mean?';

    const userTurn = {
      id: `user-${Date.now()}`,
      ts: new Date().toISOString(),
      role: 'user',
      text: body.text
    };
    const replyTurn = {
      id: `reply-${Date.now()}`,
      ts: new Date().toISOString(),
      role: 'orchestrator',
      text: replyText,
      contextReceipt: {
        scope: taskId ? 'task' : 'project',
        contextKey: taskId ? `task:${PROJECT}/${taskId}` : `project:${PROJECT}`,
        taskKey: (nav?.currentTaskKey as string | undefined) ?? null,
        includedBlocks: taskId
          ? ['navigation', 'task metadata', 'task prompt', 'task status', 'last run outcome']
          : ['navigation'],
        capturedAt: new Date().toISOString()
      }
    };
    persistedTurns.push(userTurn, replyTurn);

    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        project: PROJECT,
        reply: replyTurn
      })
    });
  });

  return captured;
}

async function stubProjectsAndJobs(page: Page) {
  await page.route(/\/api\//, async (route) => {
    const requestPath = new URL(route.request().url()).pathname;
    let body = '{}';
    if (/\/api\/(?:tags|workspaces|projects|clients|epics)\/?$/.test(requestPath)) body = '[]';
    if (requestPath === '/api/runner/status') body = '{"projects":{}}';
    if (requestPath === '/api/cli/quota') body = '{"snapshots":[]}';
    if (requestPath.startsWith('/api/tasks/archive')) body = '{"items":[],"total":0,"offset":0,"limit":50}';
    if (requestPath === '/api/tasks/reference-status') body = '{"items":[]}';
    if (requestPath === '/api/orchestrator/sessions') body = '{"sessions":[]}';
    if (requestPath.startsWith('/api/bus/')) body = '[]';
    if (requestPath === '/api/v1/management/remote-hosts') body = '[]';
    if (/\/api\/cli\/(?:codex|claude|gemini)\/models$/.test(requestPath)) body = '{"models":[],"source":"fixture"}';
    await route.fulfill({ status: 200, contentType: 'application/json', body });
  });
  await page.route(/\/api\/auth\/status$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true })
    });
  });
  await page.route(/\/api\/watch-paths$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: PROJECT, path: 'C:/tmp/' + PROJECT, rootPath: 'C:/tmp/' + PROJECT, repositoryPath: '' }
      ])
    });
  });

  const emptyLanes = {
    backlog: [], preparation: [], orchestratorPrep: [],
    ready: [], progress: [], failedPickup: [], autoReview: [], humanReview: [],
    review: [], completed: [], archive: []
  };

  await page.route(/\/api\/tasks\/grouped(?:\?.*)?$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        ...emptyLanes,
        autoReview: [TASK_INFO],
        review: [TASK_INFO]
      })
    });
  });
  await page.route(/\/api\/tasks(?:\?.*)?$/, async (route) => {
    if (route.request().method() !== 'GET') { await route.continue(); return; }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([TASK_INFO])
    });
  });
}

async function stubJobDetail(page: Page) {
  // The active tab and footer context switch synchronously. Keep the detail
  // request pending to reproduce the selectedJob catch-up window without
  // mounting the unrelated task-detail view.
  await page.route(new RegExp(`/api/tasks/${TASK_ID}(\\?.*)?$`), () => undefined);
}

async function openSideSheet(page: Page) {
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  await toggle.click();
  const sheet = page.getByTestId('orch-side-sheet');
  await expect(sheet).toBeVisible();
  const input = page.getByTestId('chat-input');
  if ((await input.count()) === 0) {
    test.skip(true, 'No watched projects available - chat input never mounts');
  }
  await expect(input).toBeVisible({ timeout: 5_000 });
}

async function sendChat(page: Page, text: string) {
  await page.getByTestId('chat-input').fill(text);
  const send = page.getByTestId('chat-send');
  await expect(send).toBeEnabled();
  const wait = page.waitForRequest(
    (r) => r.method() === 'POST' && /\/orchestrator-chat$/.test(r.url()),
    { timeout: 5_000 }
  );
  await send.click();
  await wait;
  await page.waitForTimeout(250);
}

test.describe('Project chat context awareness', () => {
  test('sends task-detail navigationContext while selectedJob catch-up is pending', async ({ page }) => {
    await stubProjectsAndJobs(page);
    await stubJobDetail(page);
    const captured = await stubChatAndCapture(page);

    await page.addInitScript(({ taskKey }) => localStorage.setItem(
      'atp.studio.tabs.v1',
      JSON.stringify({
        v: 1,
        tabs: [{ kind: 'task', taskKey }],
        activeKey: `task:${taskKey}`
      })
    ), { taskKey: TASK_INFO.taskKey });

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await openSideSheet(page);

    await expect(page.getByTestId('chat-context-attachment-context:automatic'))
      .toContainText('CTX-1');

    await sendChat(page, 'Why is status.md relevant here?');
    await sendChat(page, 'What does it say now?');

    expect(captured).toHaveLength(2);
    for (const request of captured) {
      expect(request.body.navigationContext).toBeTruthy();
      const nav = request.body.navigationContext!;
      expect(nav.currentPage).toBe('task-detail');
      expect(typeof nav.viewportTimestamp).toBe('string');
      expect(nav.currentTaskId).toBe(TASK_ID);
      expect(nav.currentTaskKey).toBe(TASK_INFO.displayKey);
      expect(nav.currentTaskTitle).toBe(TASK_TITLE);
    }

    const lastBubble = page.locator('[data-testid="conversation-message-message.orchestrator"]').last();
    await expect(lastBubble).toContainText(TASK_ID, { timeout: 5_000 });
    const bubbleText = (await lastBubble.textContent()) ?? '';
    for (const sig of HALLUCINATION_SIGNATURES) {
      expect(bubbleText).not.toMatch(sig);
    }

    const receipt = page.getByTestId('orch-answer-context-receipt');
    await expect(receipt).toBeVisible();
    await expect(receipt).toContainText(TASK_INFO.displayKey);
    await expect(receipt).toContainText('task status');

    await setTheme(page, 'dark');
    await page.screenshot({ path: resolve(RESULTS, 'task-context-receipt-dark.png') });
    await setTheme(page, 'light');
    await page.screenshot({ path: resolve(RESULTS, 'task-context-receipt-light.png') });
  });

  test('sends kanban-board navigationContext when no task is open and the reply asks for clarification', async ({ page }) => {
    await stubProjectsAndJobs(page);
    const captured = await stubChatAndCapture(page);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await openSideSheet(page);
    await sendChat(page, 'what is the current task?');

    expect(captured.length).toBeGreaterThan(0);
    const body = captured[captured.length - 1].body;
    expect(body.navigationContext).toBeTruthy();
    const nav = body.navigationContext!;
    expect(nav.currentPage).toBe('kanban-board');
    expect(nav.currentTaskId).toBeUndefined();
    expect(nav.currentTaskTitle).toBeUndefined();

    // The rendered reply acknowledges no task is selected.
    const lastBubble = page.locator('[data-testid="conversation-message-message.orchestrator"]').last();
    await expect(lastBubble).toContainText(/no task|which task/i, { timeout: 5_000 });
    const bubbleText = (await lastBubble.textContent()) ?? '';
    for (const sig of HALLUCINATION_SIGNATURES) {
      expect(bubbleText).not.toMatch(sig);
    }
  });
});
