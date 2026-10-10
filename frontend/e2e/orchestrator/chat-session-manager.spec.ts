import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * AGT-3032: the left-sidebar Chats panel is the one place that lists chat
 * sessions. Fully mocked: every Studio API call is stubbed, so the spec runs
 * against a bare `ng serve` without a backend.
 */
test.use({ serviceWorkers: 'block', viewport: { width: 1440, height: 1000 } });

const PROJECT = 'Agent Studio';
const ACTIVE_TASK = `task:${PROJECT}/AGT-2577`;
const RESULTS = resolve(process.env.JOB_RESULTS_DIR ?? '../results/AGT-3032');
mkdirSync(RESULTS, { recursive: true });

type Session = Record<string, unknown> & { contextKey: string };

const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

function session(contextKey: string, overrides: Record<string, unknown>): Session {
  const [kind, rest] = contextKey.split(':');
  const [projectId, key] = rest.split('/');
  return {
    contextKey,
    kind,
    projectId,
    taskKey: kind === 'task' ? key : null,
    workbenchKey: kind === 'workbench' ? key : null,
    createdAt: minutesAgo(60 * 24 * 30),
    updatedAt: minutesAgo(10),
    lastUsedAt: minutesAgo(10),
    model: 'gpt-5.4',
    calls: 4,
    turnCount: 4,
    cumulativeInputTokens: 8_400,
    cumulativeOutputTokens: 1_900,
    cumulativeCacheReadTokens: 4_100,
    cumulativeCacheCreationTokens: 0,
    runtimeStatus: 'idle',
    queuePosition: 0,
    hiddenAt: null,
    archivedAt: null,
    summary: null,
    ...overrides,
  };
}

function seedSessions(): Session[] {
  return [
    session(`project:${PROJECT}`, {
      runtimeStatus: 'active', updatedAt: minutesAgo(1), lastUsedAt: minutesAgo(1),
      summary: 'Coordinate the central Orchestrator context rollout',
    }),
    session(ACTIVE_TASK, {
      runtimeStatus: 'queued', queuePosition: 2, updatedAt: minutesAgo(3), lastUsedAt: minutesAgo(3),
      summary: 'Deliver the chat session manager in the left sidebar',
    }),
    session('project:Documentation Platform', {
      model: 'claude-opus-5-5', updatedAt: minutesAgo(42), lastUsedAt: minutesAgo(42),
      cumulativeInputTokens: 5_200_000, cumulativeOutputTokens: 300_000, cumulativeCacheReadTokens: 100_000,
      summary: 'Review operator documentation and navigation',
    }),
    session(`task:${PROJECT}/AGT-2506`, {
      updatedAt: minutesAgo(60 * 5), lastUsedAt: minutesAgo(60 * 5),
      summary: 'Validate SignalR delivery on the existing TaskHub',
    }),
    session(`workbench:${PROJECT}/AGT-W12`, {
      updatedAt: minutesAgo(60 * 24 * 4), lastUsedAt: minutesAgo(60 * 24 * 4),
      summary: 'Decide the archive retention for chat sessions',
    }),
    // Seeded on project onboarding, never used: must not be listed.
    session('project:Fresh Project', {
      calls: 0, turnCount: 0, model: null, cumulativeInputTokens: 0, cumulativeOutputTokens: 0,
      cumulativeCacheReadTokens: 0, summary: 'Project chat for Fresh Project',
    }),
    session(`task:${PROJECT}/AGT-2401`, {
      hiddenAt: minutesAgo(60 * 24 * 9), updatedAt: minutesAgo(60 * 24 * 12), lastUsedAt: minutesAgo(60 * 24 * 12),
      summary: 'Retire the legacy side-sheet chat list',
    }),
    session('project:Legacy Ops', {
      archivedAt: minutesAgo(60 * 24 * 2), updatedAt: minutesAgo(60 * 24 * 20), lastUsedAt: minutesAgo(60 * 24 * 20),
      summary: 'Old deployment questions',
    }),
  ];
}

interface Workspace {
  sessions: Session[];
  archiveRequests: { contextKey: string; archived: boolean }[];
}

async function stubWorkspace(page: Page, includeLegacyTurn = false): Promise<Workspace> {
  const workspace: Workspace = { sessions: seedSessions(), archiveRequests: [] };
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    let body: unknown = {};
    if (/\/api\/(?:tags|v1\/workspaces|clients|v1\/projects)\/?$/.test(path)
      || path.startsWith('/api/bus/')
      || path === '/api/v1/management/links'
      || path === '/api/v1/management/remote-hosts') body = [];
    if (path === '/api/v1/studio/runner/status') body = { projects: {} };
    if (path === '/api/cli/quota') body = { snapshots: [] };
    if (path === '/api/tasks/archive') body = { items: [], total: 0, offset: 0, limit: 50 };
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.route(/\/api\/v1\/studio\/auth\/status$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true }),
  }));
  await page.route(/\/api\/watch-paths$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify([PROJECT, 'Documentation Platform', 'Fresh Project', 'Legacy Ops'].map(name => ({
      name, path: `/workspace/${name}`, rootPath: `/workspace/${name}`, repositoryPath: '',
    }))),
  }));
  await page.route(/\/api\/v1\/studio\/board(?:\?.*)?$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({
      backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
      failedPickup: [], autoReview: [], humanReview: [], review: [], completed: [], archive: [],
    }),
  }));
  await page.route(/\/api\/tasks(?:\?.*)?$/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: '[]',
  }));
  await page.route(/\/api\/v1\/studio\/orchestrator\/sessions(?:\?.*)?$/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ sessions: workspace.sessions }),
  }));
  await page.route(/\/api\/v1\/studio\/orchestrator\/sessions\/.+\/archive$/, async route => {
    const raw = new URL(route.request().url()).pathname
      .replace('/api/v1/studio/orchestrator/sessions/', '')
      .replace(/\/archive$/, '');
    const contextKey = decodeURIComponent(raw);
    const { archived } = route.request().postDataJSON() as { archived: boolean };
    workspace.archiveRequests.push({ contextKey, archived });
    workspace.sessions = workspace.sessions.map(item => item.contextKey === contextKey
      ? { ...item, archivedAt: archived ? new Date().toISOString() : null }
      : item);
    await route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify(workspace.sessions.find(item => item.contextKey === contextKey)),
    });
  });
  await page.route(/\/api\/v1\/studio\/orchestrator\/context\/.+$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({
      contextKey: ACTIVE_TASK, capturedAt: minutesAgo(3),
      digest: 'task: AGT-2577 | context store: Task Server | health: ok', sources: [],
    }),
  }));
  await page.route(/\/api\/runner\/task:Agent(?:%20| )Studio\/AGT-2577\/orchestrator-chat$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({
      project: PROJECT,
      contextKey: ACTIVE_TASK,
      executionContext: null,
      turns: [{
        id: 'turn-1', ts: minutesAgo(4), role: 'user',
        text: 'Deliver the chat session manager in the left sidebar', model: null,
      }, {
        id: 'turn-2', ts: minutesAgo(3), role: 'orchestrator',
        text: 'The task chat has usage metadata.', model: 'gpt-6-astra',
        metadata: {
          model: 'gpt-6-astra', effort: 'medium', providerThreadId: 'thread-1',
          host: 'runner-01', queuedAt: '2026-08-10T11:48:00Z',
          startedAt: '2026-08-10T11:48:02Z', finishedAt: '2026-08-10T11:48:10Z',
          inputTokens: 80, cachedInputTokens: 20, outputTokens: 10,
          reasoningTokens: 2, cost: 0.001, currency: 'USD',
          priceCatalogueVersion: 'TokenEconomy/0.3.5',
        },
      }, ...(includeLegacyTurn ? [{
        id: 'turn-3', ts: minutesAgo(2), role: 'orchestrator', text: 'This reply predates usage capture.',
      }] : [])],
    }),
  }));
  await page.route('**/hubs/**', route => route.abort());
  return workspace;
}

async function dismissErrorDialogs(page: Page): Promise<void> {
  for (let attempt = 0; attempt < 5; attempt++) {
    if (await page.getByTestId('error-dialog-overlay').count() === 0) return;
    await page.keyboard.press('Escape');
    await page.waitForTimeout(100);
  }
}

async function openChatsPanel(page: Page): Promise<void> {
  await page.goto('/#/board');
  await dismissErrorDialogs(page);
  await page.getByTestId('studio-ab-chats').click();
  await expect(page.getByTestId('chat-session-manager')).toBeVisible();
}

const row = (page: Page, contextKey: string) => page.getByTestId(`chat-row-${contextKey}`);

test('lists every used session once, grouped by Active, Recent and Archived', async ({ page }) => {
  await stubWorkspace(page);
  await openChatsPanel(page);

  const active = page.getByTestId('chat-group-active');
  await expect(active.getByTestId('chat-group-active-head')).toContainText('2');
  await expect(row(page, `project:${PROJECT}`).getByTestId('chat-row-state')).toHaveText('running');
  await expect(row(page, ACTIVE_TASK).getByTestId('chat-row-state')).toHaveText('queued 2');
  await expect(active.locator('[data-testid^="chat-row-live-dot"]')).toHaveCount(2);

  const recent = page.getByTestId('chat-group-recent');
  await expect(recent.locator('li')).toHaveCount(3);
  const docs = row(page, 'project:Documentation Platform');
  await expect(docs.getByTestId('chat-row-title')).toHaveText('Review operator documentation and navigation');
  await expect(docs.getByTestId('chat-row-time')).toHaveText('42m');
  await expect(docs.getByTestId('chat-row-scope')).toHaveText('Documentation Platform');
  await expect(docs.getByTestId('chat-row-model')).toHaveText('Opus 5.5');
  await expect(docs.getByTestId('chat-row-tokens')).toHaveText('5.6m tok');

  // A freshly onboarded project has a seeded, never-used context: no row.
  await expect(row(page, 'project:Fresh Project')).toHaveCount(0);
  await expect(page.getByTestId('chat-session-manager')).not.toContainText('Project chat for Fresh Project');

  // Archived holds the operator mark and the task-archive hide, collapsed.
  await expect(page.getByTestId('chat-group-archived-toggle')).toContainText('2');
  await expect(row(page, 'project:Legacy Ops')).toHaveCount(0);
  await page.getByTestId('chat-group-archived-toggle').click();
  await expect(row(page, 'project:Legacy Ops')).toBeVisible();
  await expect(row(page, `task:${PROJECT}/AGT-2401`)).toBeVisible();

  // The side sheet no longer carries its own list.
  await page.getByTestId('studio-ab-chats').click();
  await page.getByTestId('studio-ab-chats').click();
  await row(page, ACTIVE_TASK).getByTestId(`chat-row-open-${ACTIVE_TASK}`).click();
  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
  await expect(page.getByTestId('orch-side-sheet').getByTestId('chat-context-list')).toHaveCount(0);
  await expect(page.getByTestId('orch-context-count')).toHaveCount(0);
});

test('opens a session in the side sheet, highlights it, and keeps the context actions reachable', async ({ page }) => {
  await stubWorkspace(page);
  await openChatsPanel(page);

  await page.getByTestId(`chat-row-open-${ACTIVE_TASK}`).click();

  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
  await expect(page.getByTestId('orchestrator-conversation'))
    .toContainText('Deliver the chat session manager in the left sidebar');
  await expect(row(page, ACTIVE_TASK)).toHaveClass(/chat-row--open/);
  await expect(page.getByTestId(`chat-row-open-${ACTIVE_TASK}`)).toHaveAttribute('aria-current', 'true');
  await expect(page.getByTestId('chat-session-usage')).toContainText('1 turn · 110 tokens · $0.0010');
  await expect(page.getByTestId('chat-session-usage'))
    .toContainText('gpt-6-astra · 10s total (2s waiting) · runner-01 · session thread-1');

  await page.getByTestId('orch-context-badge').click();
  const actions = page.getByTestId('orch-context-menu');
  await expect(actions).toBeVisible();
  for (const id of ['orch-side-sheet-pin', 'orch-side-sheet-settings', 'orch-side-sheet-refresh', 'orch-context-send-toggle']) {
    await expect(actions.getByTestId(id)).toBeVisible();
  }
  await expect(page.getByTestId('orch-context-header')).toHaveAttribute('data-context-key', ACTIVE_TASK);
  // The conversation stays rendered under the popover.
  await expect(page.getByTestId('orchestrator-conversation')).toBeAttached();

  await page.getByTestId('orch-open-chats-panel').click();
  await expect(page.getByTestId('chat-session-manager')).toBeVisible();
});

test('shows incomplete session totals when a task chat includes a legacy reply', async ({ page }) => {
  await stubWorkspace(page, true);
  await openChatsPanel(page);
  await page.getByTestId(`chat-row-open-${ACTIVE_TASK}`).click();

  await expect(page.getByTestId('chat-session-usage'))
    .toContainText('2 turns · tokens incomplete · cost incomplete');
  await expect(page.getByTestId('chat-session-usage'))
    .toContainText('model incomplete · duration incomplete');
  await expect(page.getByTestId('chat-session-usage')).not.toContainText('session thread-1');
});

test('archives and unarchives a session through the Task Server route', async ({ page }) => {
  const workspace = await stubWorkspace(page);
  await openChatsPanel(page);

  // Running or queued sessions offer no archive.
  await row(page, `project:${PROJECT}`).hover();
  await expect(page.getByTestId(`chat-row-archive-project:${PROJECT}`)).toHaveCount(0);

  const docs = row(page, 'project:Documentation Platform');
  await docs.hover();
  await page.getByTestId('chat-row-archive-project:Documentation Platform').click();
  await expect(page.getByTestId('chat-group-recent').getByTestId('chat-row-project:Documentation Platform')).toHaveCount(0);
  await expect(page.getByTestId('chat-group-archived-toggle')).toContainText('3');
  expect(workspace.archiveRequests).toEqual([{ contextKey: 'project:Documentation Platform', archived: true }]);

  await page.getByTestId('chat-group-archived-toggle').click();
  await row(page, 'project:Documentation Platform').hover();
  await page.getByTestId('chat-row-unarchive-project:Documentation Platform').click();
  await expect(page.getByTestId('chat-group-recent').getByTestId('chat-row-project:Documentation Platform')).toBeVisible();
  expect(workspace.archiveRequests.at(-1)).toEqual({ contextKey: 'project:Documentation Platform', archived: false });
});

test('moves a session out of Active when it goes idle, without a reload', async ({ page }) => {
  const workspace = await stubWorkspace(page);
  await openChatsPanel(page);
  await expect(page.getByTestId('chat-group-active').getByTestId(`chat-row-project:${PROJECT}`)).toBeVisible();

  workspace.sessions = workspace.sessions.map(item => item.contextKey === `project:${PROJECT}`
    ? { ...item, runtimeStatus: 'idle' }
    : item);

  await expect(page.getByTestId('chat-group-recent').getByTestId(`chat-row-project:${PROJECT}`))
    .toBeVisible({ timeout: 8_000 });
  await expect(page.getByTestId('chat-group-active').getByTestId(`chat-row-project:${PROJECT}`)).toHaveCount(0);
});

test('redirects the retired Chat History route to the open Chats panel', async ({ page }) => {
  await stubWorkspace(page);
  await page.goto('/#/chat-history');
  await dismissErrorDialogs(page);

  await expect(page.getByTestId('chat-session-manager')).toBeVisible();
  await expect(page.getByTestId('studio-ab-chats')).toHaveClass(/studio-ab__btn--active/);
  await expect(page).not.toHaveURL(/chat-history/);
  await expect(page.getByTestId('orchestrator-chat-history')).toHaveCount(0);
});

test('starts a new chat in a chosen project scope', async ({ page }) => {
  await stubWorkspace(page);
  await openChatsPanel(page);

  await page.getByTestId('chat-new').click();
  await expect(page.getByTestId('chat-new-menu-panel')).toBeVisible();
  await page.getByTestId('chat-new-menu-item-project:Documentation Platform').click();

  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
  await expect(page.getByTestId('orch-panel-context-name')).toHaveText('Documentation Platform');
});

for (const theme of ['light', 'dark'] as const) {
  test(`captures the Chats panel with active, recent and archived rows in ${theme} theme`, async ({ page }, testInfo) => {
    await stubWorkspace(page);
    await page.addInitScript(selectedTheme => {
      localStorage.setItem('atp.studio.theme', selectedTheme);
      localStorage.setItem('atp.chatSwitcher.seen.v1', JSON.stringify({
        'project:Documentation Platform': new Date().toISOString(),
        'project:Legacy Ops': new Date().toISOString(),
      }));
    }, theme);
    await openChatsPanel(page);
    await page.getByTestId(`chat-row-open-${ACTIVE_TASK}`).click();
    await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
    await page.getByTestId('chat-group-archived-toggle').click();
    await expect(row(page, 'project:Legacy Ops')).toBeVisible();
    await row(page, 'project:Documentation Platform').hover();

    const panelShot = join(RESULTS, `chat-session-manager-panel-${theme}.png`);
    await page.getByTestId('studio-sidebar').screenshot({ path: panelShot });
    await testInfo.attach(`Chats panel ${theme}`, { path: panelShot, contentType: 'image/png' });
    const pageShot = join(RESULTS, `chat-session-manager-workspace-${theme}.png`);
    await page.screenshot({ path: pageShot });
    await testInfo.attach(`Chats panel with side sheet ${theme}`, { path: pageShot, contentType: 'image/png' });
  });
}
