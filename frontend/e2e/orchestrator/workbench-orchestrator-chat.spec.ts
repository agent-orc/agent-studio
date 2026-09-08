import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * AGT-2725: opening a Dossier owns its own orchestrator session, scoped to
 * `workbench:<PROJ>/<DOSSIER-KEY>`, separate from the project chat. This
 * mirrors `task-detail/task-orchestrator-chat.spec.ts`'s task-scope proof for
 * the new Dossier scope: empty transcript on first visit, a mandatory
 * automatic context chip, a turn that survives reload, and isolation from
 * the project's own thread.
 */
const PROJECT = 'Dossier Chat Project';
const PROJECT_SLUG = 'dossier-chat-project';
const WATCH_PATH = '/tmp/dossier-orchestrator-chat';
const WORKBENCH_ID = 'runner-link';
const WORKBENCH_KEY = 'AGT-W43';
const WORKBENCH_TITLE = 'Runner Link Health';
const WORKBENCH_CONTEXT_KEY = `workbench:${PROJECT}/${WORKBENCH_KEY}`;
const PROJECT_CONTEXT_KEY = `project:${PROJECT}`;

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [],
  progress: [], failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
  humanReview: [], escalated: [], completed: [], archive: [],
};

const WORKBENCH_LIST_ITEM = {
  id: WORKBENCH_ID,
  key: WORKBENCH_KEY,
  title: WORKBENCH_TITLE,
  summary: 'Dossier chat isolation proof.',
  status: 'decided',
  phase: 'testing',
  updatedAtUtc: '2026-09-06T10:00:00Z',
  entryPath: `docs/workbenches/${WORKBENCH_ID}/index.html`,
  valid: true,
  error: null,
  sourceTaskKeys: ['AGT-2725'],
  relatedTaskKeys: [],
};

interface ChatTurn {
  id: string;
  ts: string;
  role: 'user' | 'orchestrator';
  text: string;
}

const evidenceDir = path.join(
  process.env.JOB_RESULTS_DIR?.trim() || path.resolve('test-results'),
  'workbench-orchestrator-chat',
);

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

/**
 * Two independent in-memory transcripts, one per context key, so a Dossier
 * turn and a project turn can never appear in each other's history - the
 * same closure-holds-state idiom `task-orchestrator-chat.spec.ts` uses,
 * mutated on POST and read back on GET even after `page.reload()` because
 * the route handlers stay registered.
 */
async function installRoutes(
  page: Page,
  workbenchChatPosts: { url: string; body: Record<string, unknown> }[],
): Promise<void> {
  const workbenchTurns: ChatTurn[] = [];
  const projectTurns: ChatTurn[] = [];

  await page.route('**/api/**', async route => {
    const request = route.request();
    const pathname = decodeURIComponent(new URL(request.url()).pathname);

    if (/\/api\/runner\/workbench:[^/]+\/[^/]+\/orchestrator-chat$/.test(pathname)) {
      if (request.method() === 'GET') return json(route, { project: PROJECT, turns: workbenchTurns });
      const body = request.postDataJSON() as Record<string, unknown>;
      workbenchChatPosts.push({ url: request.url(), body });
      const now = new Date().toISOString();
      const userTurn: ChatTurn = { id: `wb-user-${Date.now()}`, ts: now, role: 'user', text: String(body['text']) };
      const reply: ChatTurn = {
        id: `wb-reply-${Date.now()}`, ts: now, role: 'orchestrator',
        text: `This answer is scoped to Dossier ${WORKBENCH_KEY}.`,
      };
      workbenchTurns.push(userTurn, reply);
      return json(route, { project: PROJECT, reply });
    }
    if (/\/api\/runner\/project:[^/]+\/orchestrator-chat$/.test(pathname)
      || pathname === `/api/runner/${encodeURIComponent(PROJECT)}/orchestrator-chat`) {
      if (request.method() === 'GET') return json(route, { project: PROJECT, turns: projectTurns });
      const body = request.postDataJSON() as Record<string, unknown>;
      const now = new Date().toISOString();
      projectTurns.push(
        { id: `proj-user-${Date.now()}`, ts: now, role: 'user', text: String(body['text']) },
        { id: `proj-reply-${Date.now()}`, ts: now, role: 'orchestrator', text: 'Project-scoped answer.' },
      );
      return json(route, { project: PROJECT, reply: projectTurns.at(-1) });
    }
    if (/\/api\/orchestrator\/context\/workbench:/.test(pathname)) {
      return json(route, {
        contextKey: WORKBENCH_CONTEXT_KEY,
        capturedAt: '2026-09-06T10:00:00Z',
        digest: `Dossier ${WORKBENCH_KEY} descriptor`,
        sources: [{ name: 'dossier', status: 'ok', capturedAt: '2026-09-06T10:00:00Z', detail: 'dossier metadata' }],
      });
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
    if (pathname === '/api/epics') return json(route, []);
    if (pathname === '/api/epics/completed/count') return json(route, { count: 0 });
    if (/\/api\/projects\/[^/]+\/workbenches$/.test(pathname)) {
      return json(route, {
        projectName: PROJECT, includesHistory: false, count: 1, items: [WORKBENCH_LIST_ITEM],
      });
    }
    if (new RegExp(`/api/projects/[^/]+/workbenches/${WORKBENCH_ID}$`).test(pathname)) {
      return json(route, {
        workbench: WORKBENCH_LIST_ITEM,
        html: `<h1>${WORKBENCH_TITLE}</h1><p>Dossier chat isolation proof.</p>`,
        branch: 'main',
        revision: '1234567890abcdef',
        workingTreeModified: false,
        fingerprint: 'a'.repeat(64),
      });
    }
    if (pathname === '/api/runner/status') return json(route, { projects: {} });
    if (pathname === '/api/runner/global') return json(route, { mode: 'paused', activeProjects: [] });
    if (pathname === '/api/crash-recovery/pending') return json(route, { pending: [] });
    if (pathname === '/api/cli/quota') return json(route, { snapshots: [], ttlSeconds: 600 });
    if (pathname === '/api/environment') return json(route, { isDev: false, devTools: {} });
    if (pathname === '/api/orchestrator/sessions') return json(route, { sessions: [] });
    if (/\/api\/cli\/[^/]+\/models$/.test(pathname)) {
      return json(route, { models: [], source: 'workbench-orchestrator-chat-e2e' });
    }
    return json(route, []);
  });
}

async function captureWorkspace(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  mkdirSync(evidenceDir, { recursive: true });
  const screenshot = await page.screenshot({ path: path.join(evidenceDir, `${name}--mocked.png`), fullPage: false });
  await testInfo.attach(`${name}--mocked.png`, { body: screenshot, contentType: 'image/png' });
}

test('Opening a Dossier starts an empty, isolated orchestrator session that survives reload', async (
  { page },
  testInfo,
) => {
  const workbenchChatPosts: { url: string; body: Record<string, unknown> }[] = [];
  await installRoutes(page, workbenchChatPosts);

  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto(`/#/projects/${PROJECT_SLUG}/workbenches/${WORKBENCH_ID}`, { waitUntil: 'commit' });
  await expect(page.getByTestId('workbench-viewer')).toContainText(WORKBENCH_TITLE);

  const transcriptRequest = page.waitForRequest(request =>
    request.method() === 'GET'
    && /\/api\/runner\/workbench:[^/]+\/[^/]+\/orchestrator-chat$/.test(
      decodeURIComponent(new URL(request.url()).pathname),
    ));
  await page.getByTestId('orch-side-sheet-toggle').click();
  const openedRequest = await transcriptRequest;
  expect(decodeURIComponent(openedRequest.url())).toContain(WORKBENCH_CONTEXT_KEY);

  const sideSheet = page.getByTestId('orch-side-sheet');
  await expect(sideSheet).toBeVisible();
  // Empty transcript on first visit (Concept item 3).
  await expect(sideSheet.getByTestId('chat-input')).toBeVisible();
  await expect(sideSheet.locator('[data-testid^="conversation-message-"]')).toHaveCount(0);

  // The Dossier is the automatic, mandatory context chip (item 5).
  await expect(sideSheet.getByTestId('orch-panel-context-type')).toHaveText('Dossier');
  await expect(sideSheet.getByTestId('orch-panel-context-name')).toContainText(WORKBENCH_KEY);
  await expect(sideSheet.getByTestId('orch-workbench-context-note')).toContainText(
    `Questions automatically refer to Dossier ${WORKBENCH_KEY}.`,
  );
  const automaticChip = sideSheet.getByTestId('chat-context-attachment-context:automatic');
  await expect(automaticChip).toBeVisible();
  await expect(automaticChip).toContainText(WORKBENCH_KEY);
  // The chip library gives every chip the same remove button; the host
  // answers a remove click on the automatic chip with a no-op for Dossier
  // scope (same rule as task scope), so it never disappears.
  await sideSheet.getByTestId('chat-context-attachment-remove-context:automatic').click();
  await expect(automaticChip).toBeVisible();

  // The manual send-time exclusion toggle (context menu) is disabled too -
  // a Dossier turn cannot drop its scope the way a project turn can.
  await sideSheet.getByTestId('orch-context-badge').click();
  await expect(sideSheet.getByTestId('orch-context-send-toggle')).toBeDisabled();
  await expect(sideSheet.getByTestId('orch-context-send-toggle')).toHaveText('Dossier context always included');
  await sideSheet.getByTestId('orch-context-badge').click();

  await setTheme(page, 'light');
  await captureWorkspace(page, testInfo, 'workbench-chat-empty-light');

  await sideSheet.getByTestId('chat-input').fill('What does this Dossier decide?');
  await sideSheet.getByTestId('chat-send').click();
  await expect.poll(() => workbenchChatPosts.length).toBe(1);
  await expect(sideSheet).toContainText(`This answer is scoped to Dossier ${WORKBENCH_KEY}.`);

  const requestBody = workbenchChatPosts[0].body;
  expect(requestBody['contextEnvelope']).toMatchObject({
    scope: { kind: 'workbench', contextKey: WORKBENCH_CONTEXT_KEY, projectId: PROJECT, workbenchKey: WORKBENCH_KEY },
  });
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);

  // Reload: the turn is still there, and the project chat never received it.
  await page.reload({ waitUntil: 'commit' });
  await expect(page.getByTestId('workbench-viewer')).toContainText(WORKBENCH_TITLE);
  await page.getByTestId('orch-side-sheet-toggle').click();
  await expect(page.getByTestId('orch-side-sheet')).toContainText('What does this Dossier decide?');
  await expect(page.getByTestId('orch-side-sheet')).toContainText(`This answer is scoped to Dossier ${WORKBENCH_KEY}.`);

  // `page.request` bypasses page.route mocks (separate context) and would hit
  // the real dev backend for a project fixture that doesn't exist there, so
  // fetch from inside the page instead - the same mocked routes the app uses.
  const projectChatBody = await page.evaluate(
    async (path: string) => (await fetch(path)).json(),
    `/api/runner/${encodeURIComponent(PROJECT_CONTEXT_KEY)}/orchestrator-chat`,
  ) as { turns: unknown[] };
  expect(projectChatBody.turns).toEqual([]);

  await setTheme(page, 'dark');
  // Let the panel's resize/layout transition settle before the evidence shot.
  await page.waitForTimeout(300);
  await captureWorkspace(page, testInfo, 'workbench-chat-turn-dark');
});
