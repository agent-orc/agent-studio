import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * AGT-3032: the left sidebar "Chats" panel is the one place that lists
 * orchestrator chat sessions. Fully mocked; no backend required.
 */
test.use({ serviceWorkers: 'block', viewport: { width: 1440, height: 900 } });

const PROJECT = 'Agent Studio';
const DOCS = 'Documentation Platform';
const FRESH = 'Fresh Project';
const RESULTS = resolve(process.env.JOB_RESULTS_DIR ?? '../results/AGT-3032');
mkdirSync(RESULTS, { recursive: true });

const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

interface MockSession {
  contextKey: string;
  kind: 'project' | 'task' | 'workbench';
  projectId: string;
  taskKey: string | null;
  updatedAt: string;
  lastUsedAt: string;
  model: string | null;
  cumulativeInputTokens: number;
  cumulativeOutputTokens: number;
  cumulativeCacheReadTokens: number;
  cumulativeCacheCreationTokens: number;
  runtimeStatus: 'idle' | 'active' | 'queued' | 'parked';
  queuePosition: number;
  turnCount: number;
  calls: number;
  summary: string;
  hiddenAt: string | null;
  archivedAt: string | null;
}

function mockSession(overrides: Partial<MockSession> & Pick<MockSession, 'contextKey'>): MockSession {
  const kind = overrides.contextKey.startsWith('task:') ? 'task'
    : overrides.contextKey.startsWith('workbench:') ? 'workbench' : 'project';
  const updatedAt = overrides.updatedAt ?? minutesAgo(30);
  return {
    kind,
    projectId: PROJECT,
    taskKey: null,
    updatedAt,
    lastUsedAt: updatedAt,
    model: 'gpt-5.5',
    cumulativeInputTokens: 12_000,
    cumulativeOutputTokens: 2_400,
    cumulativeCacheReadTokens: 0,
    cumulativeCacheCreationTokens: 0,
    runtimeStatus: 'idle',
    queuePosition: 0,
    turnCount: 4,
    calls: 4,
    summary: '',
    hiddenAt: null,
    archivedAt: null,
    ...overrides,
  };
}

function seedSessions(): MockSession[] {
  return [
    mockSession({
      contextKey: `project:${PROJECT}`,
      summary: 'Plan the release notes for the sidebar chats',
      runtimeStatus: 'active',
      updatedAt: minutesAgo(1),
      cumulativeInputTokens: 5_400_000,
      cumulativeOutputTokens: 200_000,
    }),
    mockSession({
      contextKey: `task:${PROJECT}/AGT-3031`,
      taskKey: 'AGT-3031',
      summary: 'Why does the runner skip this card?',
      runtimeStatus: 'queued',
      queuePosition: 2,
      updatedAt: minutesAgo(2),
      model: 'claude-opus-5-5',
    }),
    mockSession({
      contextKey: `task:${PROJECT}/AGT-3032`,
      taskKey: 'AGT-3032',
      summary: 'Move chat sessions into the left sidebar',
      updatedAt: minutesAgo(3),
      model: 'claude-opus-5-5',
      cumulativeInputTokens: 56_000,
    }),
    mockSession({
      contextKey: `workbench:${PROJECT}/AGT-W43`,
      summary: 'Compare the two Dossier options for archive',
      updatedAt: minutesAgo(4 * 24 * 60),
    }),
    mockSession({
      contextKey: `project:${DOCS}`,
      projectId: DOCS,
      summary: 'Review operator documentation navigation',
      updatedAt: minutesAgo(3 * 60),
      model: null,
      cumulativeInputTokens: 0,
      cumulativeOutputTokens: 0,
    }),
    mockSession({
      contextKey: `task:${PROJECT}/AGT-2401`,
      taskKey: 'AGT-2401',
      summary: 'Old task chat whose card is archived',
      updatedAt: minutesAgo(20 * 24 * 60),
      hiddenAt: minutesAgo(10 * 24 * 60),
    }),
    // Seeded on project onboarding, never used: must not be listed.
    mockSession({
      contextKey: `project:${FRESH}`,
      projectId: FRESH,
      summary: `Project chat for ${FRESH}`,
      turnCount: 0,
      calls: 0,
      model: null,
      cumulativeInputTokens: 0,
      cumulativeOutputTokens: 0,
    }),
  ];
}

async function stubWorkspace(page: Page): Promise<{ sessions: MockSession[]; archiveCalls: unknown[] }> {
  const state = { sessions: seedSessions(), archiveCalls: [] as unknown[] };
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    // The usage cockpit is not under test; an unavailable source keeps its chips quiet.
    if (path === '/api/usage/cockpit') {
      await route.fulfill({ status: 503, contentType: 'application/json', body: '{}' });
      return;
    }
    let body: unknown = {};
    if (/\/api\/(?:tags|v1\/workspaces|clients|v1\/projects)\/?$/.test(path)
      || path.startsWith('/api/bus/')
      || path === '/api/v1/management/links'
      || path === '/api/v1/management/remote-hosts') body = [];
    if (path === '/api/v1/studio/runner/status') body = { projects: {} };
    if (path === '/api/cli/quota') body = { snapshots: [] };
    if (path === '/api/tasks/archive') body = { items: [], total: 0, offset: 0, limit: 50 };
    if (path === '/api/cli/codex/models') {
      body = { source: 'test', models: [{ id: 'gpt-5.5', label: 'GPT-5.5', multiplier: null, vendor: 'openai', isDefault: true }] };
    }
    if (path === '/api/cli/claude/models') {
      body = { source: 'test', models: [{ id: 'claude-opus-5-5', label: 'Opus 5.5', multiplier: null, vendor: 'anthropic', isDefault: true }] };
    }
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.route(/\/api\/v1\/studio\/auth\/status$/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true }),
  }));
  await page.route(/\/api\/watch-paths$/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([PROJECT, DOCS, FRESH].map(name => ({
      name, path: `/workspace/${name}`, rootPath: `/workspace/${name}`, repositoryPath: '',
    }))),
  }));
  await page.route(/\/api\/v1\/studio\/board(?:\?.*)?$/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
      failedPickup: [], autoReview: [], humanReview: [], review: [], completed: [], archive: [],
    }),
  }));
  await page.route(/\/api\/tasks(?:\?.*)?$/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: '[]',
  }));
  await page.route(/\/api\/v1\/studio\/orchestrator\/sessions(?:\?.*)?$/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ sessions: state.sessions }),
  }));
  await page.route(/\/api\/v1\/studio\/orchestrator\/sessions\/archive$/, async route => {
    const request = route.request();
    const body = request.postDataJSON() as { contextKey: string; archived: boolean };
    state.archiveCalls.push({ ...body, clientId: request.headers()['x-client-id'] ?? null });
    const session = state.sessions.find(item => item.contextKey === body.contextKey);
    if (!session) {
      await route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
      return;
    }
    session.archivedAt = body.archived ? new Date().toISOString() : null;
    if (!body.archived) session.hiddenAt = null;
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(session) });
  });
  await page.route(/\/api\/runner\/[^/]+(?:\/[^/]+)?\/orchestrator-chat$/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      project: PROJECT,
      executionContext: null,
      turns: [
        { id: 'turn-1', ts: minutesAgo(3), role: 'user', text: 'Move chat sessions into the left sidebar' },
        { id: 'turn-2', ts: minutesAgo(3), role: 'orchestrator', text: 'One panel, three groups.', model: 'claude-opus-5-5' },
      ],
    }),
  }));
  await page.route('**/hubs/**', route => route.abort());
  return state;
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
  const panel = page.getByTestId('chat-sessions-panel');
  if (!(await panel.isVisible())) await page.getByTestId('studio-ab-chats').click();
  await expect(panel).toBeVisible();
}

test('redirects the retired /chat-history route to the sidebar Chats panel', async ({ page }) => {
  await stubWorkspace(page);
  await page.goto('/#/chat-history');
  await dismissErrorDialogs(page);

  await expect(page.getByTestId('chat-sessions-panel')).toBeVisible();
  await expect(page.getByTestId('studio-ab-chats')).toHaveClass(/studio-ab__btn--active/);
  await expect(page).not.toHaveURL(/chat-history/);
  await expect(page.getByTestId('orchestrator-chat-history')).toHaveCount(0);
});

test('lists only used chats, groups live sessions, and opens a row in the side sheet', async ({ page }) => {
  await stubWorkspace(page);
  await openChatsPanel(page);
  const panel = page.getByTestId('chat-sessions-panel');

  // Empty chats never appear: the onboarding seed for a fresh project has no turns.
  await expect(panel).not.toContainText(`Project chat for ${FRESH}`);
  await expect(page.getByTestId(`chat-session-row-project:${FRESH}`)).toHaveCount(0);

  const active = page.getByTestId('chat-sessions-active');
  await expect(page.getByTestId('chat-sessions-active-head')).toContainText('2');
  await expect(active.getByTestId(`chat-session-row-project:${PROJECT}`)).toContainText('running');
  await expect(active.getByTestId(`chat-session-row-task:${PROJECT}/AGT-3031`)).toContainText('queued 2');
  await expect(active.getByTestId(`chat-session-archive-project:${PROJECT}`)).toHaveCount(0);

  const recent = page.getByTestId('chat-sessions-recent');
  const keys = await recent.locator('[data-context-key]').evaluateAll(rows =>
    rows.map(row => row.getAttribute('data-context-key')));
  expect(keys).toEqual([`task:${PROJECT}/AGT-3032`, `project:${DOCS}`, `workbench:${PROJECT}/AGT-W43`]);

  const row = recent.getByTestId(`chat-session-row-task:${PROJECT}/AGT-3032`);
  await expect(row.getByTestId('chat-session-title')).toHaveText('Move chat sessions into the left sidebar');
  await expect(row.getByTestId('chat-session-time')).toHaveText('3m');
  await expect(row.getByTestId('chat-session-scope')).toHaveText('AGT-3032');
  await expect(row.getByTestId('chat-session-model')).toHaveText('Opus 5.5');
  await expect(row.getByTestId('chat-session-tokens')).toHaveText('58k tok');

  await expect(page.getByTestId('chat-sessions-archived-toggle')).toHaveAttribute('aria-expanded', 'false');
  await expect(page.getByTestId('chat-sessions-archived')).toHaveCount(0);

  await row.getByTestId('chat-session-open').click();
  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
  await expect(page.getByTestId('orchestrator-conversation')).toContainText('One panel, three groups.');
  await expect(row.getByTestId('chat-session-open')).toHaveAttribute('aria-current', 'true');
  // The side sheet keeps its context actions but no longer lists sessions.
  await page.getByTestId('orch-context-badge').click();
  await expect(page.getByTestId('orch-context-menu')).toBeVisible();
  await expect(page.getByTestId('orch-side-sheet-pin')).toBeVisible();
  await expect(page.getByTestId('orch-context-header')).toHaveAttribute('data-context-key', `task:${PROJECT}/AGT-3032`);
});

test('archives and restores a chat through the Task Server mutation', async ({ page }) => {
  const state = await stubWorkspace(page);
  await openChatsPanel(page);
  const key = `project:${DOCS}`;

  const row = page.getByTestId(`chat-session-row-${key}`);
  await row.hover();
  await row.getByTestId(`chat-session-archive-${key}`).click();
  await expect(page.getByTestId('chat-sessions-recent').getByTestId(`chat-session-row-${key}`)).toHaveCount(0);
  await expect(page.getByTestId('chat-sessions-archived-toggle')).toContainText('2');
  expect(state.archiveCalls).toEqual([
    expect.objectContaining({ contextKey: key, archived: true }),
  ]);
  expect((state.archiveCalls[0] as { clientId: string | null }).clientId).toBeTruthy();

  // A second load (another browser, or a reload) reads the persisted state.
  await page.reload();
  await dismissErrorDialogs(page);
  await page.getByTestId('chat-sessions-archived-toggle').click();
  const archived = page.getByTestId('chat-sessions-archived');
  await expect(archived.getByTestId(`chat-session-row-${key}`)).toBeVisible();

  await archived.getByTestId(`chat-session-row-${key}`).hover();
  await archived.getByTestId(`chat-session-unarchive-${key}`).click();
  await expect(page.getByTestId('chat-sessions-recent').getByTestId(`chat-session-row-${key}`)).toBeVisible();
  expect(state.archiveCalls.at(-1)).toEqual(expect.objectContaining({ contextKey: key, archived: false }));
});

for (const theme of ['light', 'dark'] as const) {
  test(`renders active, recent, and archived rows in the ${theme} theme`, async ({ page }, testInfo) => {
    await page.addInitScript(selectedTheme => {
      localStorage.setItem('atp.studio.theme', selectedTheme);
      localStorage.setItem('atp.studio.sidebarWidth', '320');
      // The Documentation chat changed since it was last read: one unread dot.
      localStorage.setItem('atp.chatSwitcher.seen.v1', JSON.stringify({
        'project:Documentation Platform': '2000-01-01T00:00:00Z',
      }));
    }, theme);
    await stubWorkspace(page);
    await openChatsPanel(page);
    await page.getByTestId('chat-sessions-archived-toggle').click();
    await expect(page.getByTestId('chat-sessions-archived')).toBeVisible();
    await page.getByTestId(`chat-session-row-task:${PROJECT}/AGT-3032`).getByTestId('chat-session-open').click();
    await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
    await expect(page.getByTestId(`chat-session-row-project:${DOCS}`).getByTestId('chat-session-unread')).toBeVisible();
    await expect(page.getByTestId('chat-session-unread')).toHaveCount(1);
    await page.getByTestId(`chat-session-row-workbench:${PROJECT}/AGT-W43`).hover();

    const panelShot = join(RESULTS, `chat-sessions-panel-${theme}--mocked.png`);
    await page.getByTestId('studio-sidebar').screenshot({ path: panelShot });
    await testInfo.attach(`Chats panel ${theme}`, { path: panelShot, contentType: 'image/png' });

    const fullShot = join(RESULTS, `chat-sessions-studio-${theme}--mocked.png`);
    await page.screenshot({ path: fullShot });
    await testInfo.attach(`Studio with Chats panel ${theme}`, { path: fullShot, contentType: 'image/png' });
  });
}
