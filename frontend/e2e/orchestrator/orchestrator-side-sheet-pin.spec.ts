import { test, expect, Page } from '@playwright/test';
import * as path from 'node:path';

/**
 * MC-2 (Concept §4): the orchestrator side sheet follows the operator's
 * navigation and can be pinned to freeze the current context.
 *
 * The chat + task endpoints are stubbed so the spec runs without a live
 * backend or CLI quota. What we lock here (real-app wiring; the exhaustive
 * task-vs-board derivation and freeze/resume is in the component unit spec):
 *   1. On the board the header derives a `project:<name>` context key and the
 *      toolbar shows an unpinned "Pin" button.
 *   2. Clicking Pin flips the header into the pinned state (`data-pinned`,
 *      the "Pinned" chip) and disables the project picker.
 *   3. Clicking again unpins and re-enables the picker.
 */

const PROJECT = 'project-neuen';

interface OrchestratorRequestTrace {
  requests: string[];
}

async function stubWorkspace(page: Page) {
  const trace: OrchestratorRequestTrace = { requests: [] };
  // Hermetic catch-all so the spec runs without a live backend (its stated
  // design). Registered first, so the specific stubs below take precedence
  // (Playwright matches routes in reverse registration order). Without this,
  // the board's background polls (cli models/quota, runner status, environment,
  // projects/settings, crash-recovery, archive, …) hit the down backend, return
  // status 0, and pop the shared "Backend not reachable" error dialog whose
  // overlay intercepts every click. Most endpoints tolerate an empty object;
  // the ones below whose consumers read a nested array (`.length`/`Object.keys`)
  // or that are list-shaped need a minimally-valid empty body so nothing throws.
  const emptyBodyFor = (path: string): string => {
    if (path === '/api/v1/management/remote-hosts' || path.startsWith('/api/bus/')) return '[]';
    if (/\/api\/(tags|v1\/workspaces|clients|v1\/projects)\/?$/.test(path)) return '[]';
    if (/\/api\/v1\/studio\/runner\/status$/.test(path)) return '{"projects":{}}';
    if (/\/api\/cli\/quota$/.test(path)) return '{"snapshots":[]}';
    if (/\/api\/tasks\/archive/.test(path)) return '{"items":[],"total":0,"offset":0,"limit":0}';
    return '{}';
  };
  await page.route(/\/api\//, async (route) => {
    const path = new URL(route.request().url()).pathname;
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: emptyBodyFor(path),
    });
  });

  await page.route(/\/api\/v1\/studio\/auth\/status$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      }),
    });
  });

  await page.route(/\/api\/watch-paths$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: PROJECT, path: 'C:/tmp/' + PROJECT, rootPath: 'C:/tmp/' + PROJECT, repositoryPath: '' },
      ]),
    });
  });

  await page.route(/\/api\/tasks(?:\?.*)?$/, async (route) => {
    if (route.request().method() !== 'GET') { await route.continue(); return; }
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });

  await page.route(/\/api\/v1\/studio\/board(?:\?.*)?$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], autoReview: [], humanReview: [], review: [], completed: [], archive: [],
      }),
    });
  });

  await page.route(/\/api\/v1\/studio\/orchestrator\/sessions$/, async (route) => {
    trace.requests.push(`${route.request().method()} /api/v1/studio/orchestrator/sessions`);
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ sessions: [
        {
          contextKey: `project:${PROJECT}`, kind: 'project', projectId: PROJECT, taskKey: null,
          updatedAt: '2026-07-11T10:00:00Z', model: 'codex', cumulativeInputTokens: 800,
          cumulativeOutputTokens: 200, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
          runtimeStatus: 'active', queuePosition: 0, summary: 'Review the project rollout',
        },
        {
          contextKey: `task:${PROJECT}/AGT-1933`, kind: 'task', projectId: PROJECT, taskKey: 'AGT-1933',
          updatedAt: '2026-07-11T10:01:00Z', model: 'codex', cumulativeInputTokens: 12_000,
          cumulativeOutputTokens: 3_000, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
          runtimeStatus: 'parked', queuePosition: 0, summary: 'Verify the task context lifecycle',
        },
      ] }),
    });
  });

  await page.route(new RegExp(`/api/v1/studio/orchestrator/context/project:${PROJECT}(?:/refresh)?$`), async (route) => {
    trace.requests.push(`${route.request().method()} ${new URL(route.request().url()).pathname}`);
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        contextKey: `project:${PROJECT}`,
        capturedAt: new Date().toISOString(),
        digest: 'lanes: ready=0 | runs: active=0 | health: ok',
        sources: [
          { name: 'lanes', status: 'empty', capturedAt: new Date().toISOString(), detail: null },
          { name: 'health', status: 'ok', capturedAt: new Date().toISOString(), detail: null },
        ],
      }),
    });
  });

  await page.route(new RegExp(`/api/v1/studio/orchestrator/context/task:${PROJECT}/AGT-1933(?:/refresh)?$`), async (route) => {
    trace.requests.push(`${route.request().method()} ${new URL(route.request().url()).pathname}`);
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        contextKey: `task:${PROJECT}/AGT-1933`,
        capturedAt: new Date().toISOString(),
        digest: 'task focus: AGT-1933',
        sources: [],
      }),
    });
  });

  await page.route(new RegExp(`/api/runner/task:${PROJECT}/AGT-1933/orchestrator-chat$`), async (route) => {
    trace.requests.push(`${route.request().method()} ${new URL(route.request().url()).pathname}`);
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ contextKey: `task:${PROJECT}/AGT-1933`, project: PROJECT, turns: [] }),
    });
  });

  await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, async (route) => {
    trace.requests.push(`${route.request().method()} ${new URL(route.request().url()).pathname}`);
    const projectMatch = /\/api\/runner\/([^/]+)\/orchestrator-chat/.exec(route.request().url());
    const project = projectMatch ? decodeURIComponent(projectMatch[1]) : '';
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project, turns: [] }),
    });
  });

  return trace;
}

/**
 * The dev backend is offline in this run, so background polls for endpoints
 * we do not stub fail and pop the shared error dialog. Its overlay would
 * intercept our clicks; dismiss any that are open before proceeding.
 */
async function dismissErrorDialogs(page: Page) {
  for (let i = 0; i < 5; i++) {
    const overlay = page.getByTestId('error-dialog-overlay');
    if ((await overlay.count()) === 0) return;
    await page.keyboard.press('Escape');
    await page.waitForTimeout(150);
  }
}

async function openSideSheet(page: Page) {
  await page.goto('/');
  if (process.env.PW_BASE_URL) await expect(page).toHaveURL(new RegExp(process.env.PW_BASE_URL.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
  await page.waitForLoadState('domcontentloaded');
  await dismissErrorDialogs(page);
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  await toggle.click();
  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
  await dismissErrorDialogs(page);
}

test.describe('Orchestrator side sheet · navigation context + pin', () => {
  test('a session selection resets to the navigation key when the project is reselected', async ({ page }) => {
    await stubWorkspace(page);
    await openSideSheet(page);

    await page.getByTestId('orch-context-badge').click();
    const taskRow = page.getByTestId(`chat-switcher-row-task:${PROJECT}/AGT-1933`);
    await taskRow.getByRole('button').first().click();

    await page.getByTestId('orch-context-badge').click();
    await expect(page.getByTestId('orch-context-header'))
      .toHaveAttribute('data-context-key', `task:${PROJECT}/AGT-1933`);

    const picker = page.getByTestId('orch-side-sheet-project-combo');
    await picker.click();
    await page.getByTestId(`orch-side-sheet-project-combo-option-${PROJECT}`).click();

    await expect(page.getByTestId('orch-context-header'))
      .toHaveAttribute('data-context-key', `project:${PROJECT}`);
  });

  test('board scope derives a project context key and pin toggles the frozen state', async ({ page }) => {
    const requestTrace = await stubWorkspace(page);
    await openSideSheet(page);

    const badge = page.getByTestId('orch-context-badge');
    await expect(badge).toBeVisible();
    await expect(badge).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByTestId('orch-context-count')).toHaveText('3');
    await expect(page.getByTestId('orch-context-menu')).toHaveCount(0);
    await expect(page.getByTestId('orch-side-sheet-pin')).toHaveCount(0);
    await expect(page.getByTestId('orch-side-sheet-settings')).toHaveCount(0);
    await expect(page.getByTestId('orch-side-sheet-refresh')).toHaveCount(0);

    const resultsDir = process.env.JOB_RESULTS_DIR;
    if (resultsDir) {
      await page.screenshot({ path: path.join(resultsDir, 'orchestrator-context-collapsed--mocked.png'), fullPage: false });
    }

    await badge.click();
    await expect(badge).toHaveAttribute('aria-expanded', 'true');
    const contextMenu = page.getByTestId('orch-context-menu');
    await expect(contextMenu).toBeVisible();
    await expect(page.getByTestId('chat-context-groups')).toBeVisible();

    const sheetBox = await page.getByTestId('orch-side-sheet').boundingBox();
    const menuBox = await contextMenu.boundingBox();
    expect(sheetBox).not.toBeNull();
    expect(menuBox).not.toBeNull();
    expect(Math.abs(menuBox!.x - sheetBox!.x)).toBeLessThanOrEqual(1);
    expect(Math.abs(menuBox!.width - sheetBox!.width)).toBeLessThanOrEqual(2);

    const header = page.getByTestId('orch-context-header');
    await expect(header).toBeVisible();
    await expect(header).toHaveAttribute('data-scope', 'board');
    await expect(header).toHaveAttribute('data-context-key', `project:${PROJECT}`);
    await expect(page.getByTestId('orch-context-scope')).toHaveText('Project context');
    await expect(page.getByTestId('orch-context-freshness')).toContainText('Context captured');
    await expect(page.getByTestId(`chat-switcher-row-project:${PROJECT}`)).toContainText('running');
    await expect(page.getByTestId(`chat-switcher-row-project:${PROJECT}`)).toContainText('Review the project rollout');
    await expect(page.getByTestId(`chat-switcher-row-task:${PROJECT}/AGT-1933`)).toContainText('parked');
    await expect(page.getByTestId(`chat-switcher-row-task:${PROJECT}/AGT-1933`)).toContainText('15k');
    await expect(page.getByTestId(`chat-switcher-row-task:${PROJECT}/AGT-1933`)).toContainText('Verify the task context lifecycle');

    if (resultsDir) {
      await page.screenshot({ path: path.join(resultsDir, 'orchestrator-context-expanded--mocked.png'), fullPage: false });
    }

    const refreshTraceStart = requestTrace.requests.length;
    const forcedRefresh = page.waitForResponse((response) =>
      response.request().method() === 'POST'
      && new URL(response.url()).pathname === `/api/v1/studio/orchestrator/context/project:${PROJECT}/refresh`);
    const chatRefresh = page.waitForResponse((response) =>
      response.request().method() === 'GET'
      && new URL(response.url()).pathname === `/api/runner/project:${PROJECT}/orchestrator-chat`);
    const sessionsRefresh = page.waitForResponse((response) =>
      response.request().method() === 'GET'
      && new URL(response.url()).pathname === '/api/v1/studio/orchestrator/sessions');
    await page.getByTestId('orch-side-sheet-refresh').click();
    await Promise.all([forcedRefresh, chatRefresh, sessionsRefresh]);
    await expect(page.getByTestId('orch-context-freshness')).toContainText('Context captured');
    expect(requestTrace.requests.slice(refreshTraceStart)).toEqual([
      `POST /api/v1/studio/orchestrator/context/project:${PROJECT}/refresh`,
      `GET /api/runner/project:${PROJECT}/orchestrator-chat`,
      'GET /api/v1/studio/orchestrator/sessions',
    ]);

    const pinBtn = page.getByTestId('orch-side-sheet-pin');
    await expect(pinBtn).toContainText('Pin context');
    await expect(pinBtn).toHaveAttribute('aria-pressed', 'false');
    await expect(page.getByTestId('orch-side-sheet-project-combo')).toBeEnabled();

    await page.screenshot({
      path: 'screenshots/orchestrator-side-sheet-pin/following--mocked.png',
      fullPage: false,
    });

    // Pin: header enters the pinned state, the picker locks.
    await pinBtn.click();
    await expect(header).toHaveAttribute('data-pinned', 'true');
    await expect(page.getByTestId('orch-context-pin')).toContainText('Pinned');
    await expect(pinBtn).toHaveAttribute('aria-pressed', 'true');
    await expect(pinBtn).toContainText('Follow navigation');
    await expect(page.getByTestId('orch-side-sheet-project-combo')).toBeDisabled();

    await page.screenshot({
      path: 'screenshots/orchestrator-side-sheet-pin/pinned--mocked.png',
      fullPage: false,
    });

    // Unpin: back to following navigation.
    await pinBtn.click();
    await expect(header).not.toHaveAttribute('data-pinned', 'true');
    await expect(page.getByTestId('orch-context-pin')).toHaveCount(0);
    await expect(page.getByTestId('orch-side-sheet-project-combo')).toBeEnabled();
  });
});
