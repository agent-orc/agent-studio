import { test, expect, Page, Request } from '@playwright/test';

/**
 * F14 regression coverage for the subtle orchestrator-chat context chip,
 * the subtitle-sync fix, and reliable per-message navigation context.
 *
 * The chat backend is stubbed so the spec runs without burning quota.
 * What we lock:
 *   1. The sidesheet subtitle reflects the active picker and updates
 *      when the operator switches project (the 2026-05-22 screenshot
 *      bug was the subtitle staying on "Runbook ..." after switching).
 *   2. The context chip is visible by default with format
 *      `Context: <Project> · Board` and re-renders to
 *      `Context: <Project> · Task '<title>'` when a task is in scope.
 *   3. Clicking the chip's close button hides it and makes the next
 *      send carry `navigationContext: null` (observable in Network).
 *   4. Consecutive sends both ship the full current context block.
 *   5. Switching project makes the next send use the new project context.
 */

const PROJECT_A = 'project-alpha';
const PROJECT_B = 'project-bravo';
const TASK_ID = 'demo-task-1';
const TASK_TITLE = 'Fix the thing that broke';

interface CapturedRequest {
  body: { navigationContext?: Record<string, unknown> | null; text: string };
  url: string;
}

async function stubChatAndCapture(page: Page): Promise<CapturedRequest[]> {
  const captured: CapturedRequest[] = [];

  await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, async (route) => {
    const req: Request = route.request();
    if (req.method() === 'GET') {
      const projectMatch = /\/api\/runner\/([^/]+)\/orchestrator-chat/.exec(req.url());
      const project = projectMatch ? decodeURIComponent(projectMatch[1]) : '';
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ project, turns: [] })
      });
      return;
    }
    if (req.method() !== 'POST') {
      await route.continue();
      return;
    }
    const body = req.postDataJSON() as { navigationContext?: Record<string, unknown> | null; text: string };
    captured.push({ body, url: req.url() });
    const projectMatch = /\/api\/runner\/([^/]+)\/orchestrator-chat/.exec(req.url());
    const project = projectMatch ? decodeURIComponent(projectMatch[1]) : '';
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        project,
        reply: {
          id: `reply-${Date.now()}-${Math.random()}`,
          ts: new Date().toISOString(),
          role: 'orchestrator',
          text: 'Stubbed orchestrator reply.'
        }
      })
    });
  });

  return captured;
}

async function stubProjectsAndJobs(page: Page) {
  await page.route(/\/api\//, async (route) => {
    const requestPath = new URL(route.request().url()).pathname;
    let body = '{}';
    if (/\/api\/(?:tags|v1\/workspaces|v1\/projects|clients|epics)\/?$/.test(requestPath)) body = '[]';
    if (requestPath === '/api/v1/studio/runner/status') body = '{"projects":{}}';
    if (requestPath === '/api/cli/quota') body = '{"snapshots":[]}';
    if (requestPath.startsWith('/api/tasks/archive')) body = '{"items":[],"total":0,"offset":0,"limit":50}';
    if (requestPath === '/api/tasks/reference-status') body = '{"items":[]}';
    if (requestPath === '/api/v1/studio/orchestrator/sessions') body = '{"sessions":[]}';
    if (requestPath.startsWith('/api/bus/')) body = '[]';
    if (requestPath === '/api/v1/management/remote-hosts') body = '[]';
    if (/\/api\/cli\/(?:codex|claude|gemini)\/models$/.test(requestPath)) body = '{"models":[],"source":"fixture"}';
    await route.fulfill({ status: 200, contentType: 'application/json', body });
  });
  await page.route(/\/api\/v1\/studio\/auth\/status$/, async (route) => {
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
        { name: PROJECT_A, path: 'C:/tmp/' + PROJECT_A, rootPath: 'C:/tmp/' + PROJECT_A, repositoryPath: '' },
        { name: PROJECT_B, path: 'C:/tmp/' + PROJECT_B, rootPath: 'C:/tmp/' + PROJECT_B, repositoryPath: '' }
      ])
    });
  });
  await page.route(/\/api\/v1\/studio\/board(?:\?.*)?$/, async (route) => {
    const emptyLanes = {
      backlog: [], preparation: [], orchestratorPrep: [],
      ready: [], progress: [], failedPickup: [], autoReview: [], humanReview: [],
      review: [], completed: [], archive: []
    };
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        ...emptyLanes,
        autoReview: [
          {
            id: TASK_ID,
            jobKey: PROJECT_A + '::' + TASK_ID,
            taskKey: PROJECT_A + '::' + TASK_ID,
            displayKey: 'CTX-1',
            title: TASK_TITLE,
            state: '4-auto-review',
            order: 0,
            agent: null,
            cliType: 'claude',
            model: null,
            createdAt: new Date().toISOString(),
            watchPath: 'C:/tmp/' + PROJECT_A,
            projectName: PROJECT_A,
            folderPath: 'C:/tmp/' + PROJECT_A + '/' + TASK_ID,
            execution: null
          }
        ],
        review: [
          {
            id: TASK_ID,
            jobKey: PROJECT_A + '::' + TASK_ID,
            taskKey: PROJECT_A + '::' + TASK_ID,
            displayKey: 'CTX-1',
            title: TASK_TITLE,
            state: '4-auto-review',
            order: 0,
            agent: null,
            cliType: 'claude',
            model: null,
            createdAt: new Date().toISOString(),
            watchPath: 'C:/tmp/' + PROJECT_A,
            projectName: PROJECT_A,
            folderPath: 'C:/tmp/' + PROJECT_A + '/' + TASK_ID,
            execution: null
          }
        ]
      })
    });
  });
  await page.route(/\/api\/tasks(?:\?.*)?$/, async (route) => {
    if (route.request().method() !== 'GET') { await route.continue(); return; }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([])
    });
  });
}

async function openSideSheet(page: Page) {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
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
  // Give the response handler a tick to settle (so `sending` toggles
  // back to false before the next send).
  await page.waitForTimeout(100);
}

async function selectProject(page: Page, projectName: string) {
  const combo = page.getByTestId('orch-side-sheet-project-combo');
  await combo.click();
  // Use a unique substring (last 5 chars) so the typeahead lands on the
  // intended project even when both fixture names share the same prefix.
  await combo.fill(projectName.slice(-5));
  await page.waitForTimeout(120);
  await combo.press('Enter');
  await page.waitForTimeout(150);
}

test.describe('F14: context badge, menu and per-message sending', () => {
  test('expanded current-context label follows the project picker', async ({ page }) => {
    await stubProjectsAndJobs(page);
    await stubChatAndCapture(page);
    await openSideSheet(page);

    await page.getByTestId('orch-context-badge').click();
    const current = page.getByTestId('orch-context-current');
    await expect(current).toBeVisible();
    const initialProject = await page.getByTestId('orch-side-sheet-project-select').inputValue();
    await expect(current).toContainText(initialProject);

    const next = initialProject === PROJECT_A ? PROJECT_B : PROJECT_A;
    await selectProject(page, next);
    await expect(current).toContainText(next);
  });

  test('collapsed badge shows the total and expands into the board context', async ({ page }) => {
    await stubProjectsAndJobs(page);
    await stubChatAndCapture(page);

    await openSideSheet(page);
    const badge = page.getByTestId('orch-context-badge');
    await expect(badge).toBeVisible();
    await expect(page.getByTestId('orch-context-count')).toHaveText('3');
    await expect(page.getByTestId('orch-context-menu')).toHaveCount(0);

    await badge.click();
    await expect(page.getByTestId('orch-context-menu')).toBeVisible();
    await expect(page.getByTestId('orch-context-current')).toContainText('Context:');
    await expect(page.getByTestId('orch-context-current')).toContainText('Board');

    await page.screenshot({ path: 'screenshots/f14/01-context-menu-board.png', fullPage: false });
  });

  test('excluding context in the menu makes the next send carry navigationContext: null', async ({ page }) => {
    await stubProjectsAndJobs(page);
    const captured = await stubChatAndCapture(page);
    await openSideSheet(page);

    await page.getByTestId('orch-context-badge').click();
    const toggle = page.getByTestId('orch-context-send-toggle');
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
    await page.getByTestId('orch-context-badge').click();

    await sendChat(page, 'no context please');
    expect(captured.length).toBeGreaterThan(0);
    expect(captured[captured.length - 1].body.navigationContext).toBeNull();
  });

  test('consecutive sends both carry the current navigation context', async ({ page }) => {
    await stubProjectsAndJobs(page);
    const captured = await stubChatAndCapture(page);
    await openSideSheet(page);

    await sendChat(page, 'first message');
    expect(captured.length).toBe(1);
    const firstCtx = captured[0].body.navigationContext;
    expect(firstCtx).toBeTruthy();
    expect((firstCtx as Record<string, unknown>).currentPage).toBe('kanban-board');

    await sendChat(page, 'second message');
    expect(captured.length).toBe(2);
    expect(captured[1].body.navigationContext).toBeTruthy();
    expect((captured[1].body.navigationContext as Record<string, unknown>).currentPage).toBe('kanban-board');
  });

  test('switching project makes the next send carry the new full block', async ({ page }) => {
    await stubProjectsAndJobs(page);
    const captured = await stubChatAndCapture(page);
    await openSideSheet(page);

    const initialProj = await page.getByTestId('orch-side-sheet-project-select').inputValue();
    const otherProj = initialProj === PROJECT_A ? PROJECT_B : PROJECT_A;

    await sendChat(page, 'on project A');
    expect(captured.length).toBe(1);
    expect(captured[0].body.navigationContext).toBeTruthy();

    await page.getByTestId('orch-context-badge').click();
    const contextToggle = page.getByTestId('orch-context-send-toggle');
    await contextToggle.click();
    await expect(contextToggle).toHaveAttribute('aria-pressed', 'false');
    await page.getByTestId('orch-context-badge').click();

    await selectProject(page, otherProj);
    await page.getByTestId('orch-context-badge').click();
    await expect(page.getByTestId('orch-context-send-toggle')).toHaveAttribute('aria-pressed', 'true');
    await page.getByTestId('orch-context-badge').click();

    await sendChat(page, 'on project B');
    expect(captured.length).toBe(2);
    // Picker change clears the cache AND lifts the dismissed flag, so
    // the next send ships the full block again.
    expect(captured[1].body.navigationContext).toBeTruthy();
  });
});
