import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

test.use({ serviceWorkers: 'block', viewport: { width: 1440, height: 1000 } });

const PROJECT = 'Agent Studio';
const TASK_CONTEXT_KEY = `task:${PROJECT}/AGT-2577`;
const ARCHIVED_CONTEXT_KEY = `task:${PROJECT}/AGT-2401`;
const RESULTS = resolve(process.env.JOB_RESULTS_DIR ?? '../results/AGT-2577');

mkdirSync(RESULTS, { recursive: true });

const sessions = [
  {
    contextKey: `project:${PROJECT}`,
    kind: 'project',
    projectId: PROJECT,
    taskKey: null,
    createdAt: '2026-08-01T08:00:00Z',
    updatedAt: '2026-08-10T11:41:00Z',
    lastUsedAt: '2026-08-10T11:41:00Z',
    model: 'codex',
    calls: 8,
    cumulativeInputTokens: 8400,
    cumulativeOutputTokens: 1900,
    cumulativeCacheReadTokens: 4100,
    cumulativeCacheCreationTokens: 0,
    runtimeStatus: 'active',
    queuePosition: 0,
    summary: 'Coordinate the central Orchestrator context rollout',
    hiddenAt: null,
  },
  {
    contextKey: 'project:Documentation Platform',
    kind: 'project',
    projectId: 'Documentation Platform',
    taskKey: null,
    createdAt: '2026-07-22T07:30:00Z',
    updatedAt: '2026-08-09T16:12:00Z',
    lastUsedAt: '2026-08-09T16:12:00Z',
    model: 'codex',
    calls: 3,
    cumulativeInputTokens: 2700,
    cumulativeOutputTokens: 740,
    cumulativeCacheReadTokens: 900,
    cumulativeCacheCreationTokens: 0,
    runtimeStatus: 'parked',
    queuePosition: 0,
    summary: 'Review operator documentation and navigation',
    hiddenAt: null,
  },
  {
    contextKey: TASK_CONTEXT_KEY,
    kind: 'task',
    projectId: PROJECT,
    taskKey: 'AGT-2577',
    createdAt: '2026-08-10T08:05:00Z',
    updatedAt: '2026-08-10T11:48:00Z',
    lastUsedAt: '2026-08-10T11:48:00Z',
    model: 'codex',
    calls: 5,
    cumulativeInputTokens: 6100,
    cumulativeOutputTokens: 1420,
    cumulativeCacheReadTokens: 2800,
    cumulativeCacheCreationTokens: 0,
    runtimeStatus: 'active',
    queuePosition: 0,
    summary: 'Deliver central Chat History with live context summaries',
    hiddenAt: null,
  },
  {
    contextKey: `task:${PROJECT}/AGT-2506`,
    kind: 'task',
    projectId: PROJECT,
    taskKey: 'AGT-2506',
    createdAt: '2026-08-08T10:00:00Z',
    updatedAt: '2026-08-09T12:25:00Z',
    lastUsedAt: '2026-08-09T12:25:00Z',
    model: 'codex',
    calls: 2,
    cumulativeInputTokens: 1200,
    cumulativeOutputTokens: 360,
    cumulativeCacheReadTokens: 440,
    cumulativeCacheCreationTokens: 0,
    runtimeStatus: 'parked',
    queuePosition: 0,
    summary: 'Validate SignalR delivery on the existing TaskHub',
    hiddenAt: null,
  },
];

async function stubWorkspace(page: Page): Promise<void> {
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    let body: unknown = {};
    if (/\/api\/(?:tags|workspaces|clients|projects)\/?$/.test(path)
      || path.startsWith('/api/bus/')
      || path === '/api/v1/management/links'
      || path === '/api/v1/management/remote-hosts') body = [];
    if (path === '/api/runner/status') body = { projects: {} };
    if (path === '/api/cli/quota') body = { snapshots: [] };
    if (path === '/api/tasks/archive') body = { items: [], total: 0, offset: 0, limit: 50 };
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });

  await page.route(/\/api\/auth\/status$/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true }),
  }));
  await page.route(/\/api\/watch-paths$/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([
      { name: PROJECT, path: '/workspace/agent-studio', rootPath: '/workspace/agent-studio', repositoryPath: '' },
      { name: 'Documentation Platform', path: '/workspace/docs', rootPath: '/workspace/docs', repositoryPath: '' },
    ]),
  }));
  await page.route(/\/api\/tasks\/grouped(?:\?.*)?$/, route => route.fulfill({
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
  await page.route(/\/api\/orchestrator\/sessions(?:\?.*)?$/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ sessions }),
  }));
  await page.route(/\/api\/orchestrator\/context\/task:Agent(?:%20| )Studio\/AGT-2577(?:\/refresh)?$/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      contextKey: TASK_CONTEXT_KEY,
      capturedAt: '2026-08-10T11:48:00Z',
      digest: 'task: AGT-2577 | context store: Task Server | health: ok',
      sources: [],
    }),
  }));
  await page.route(/\/api\/runner\/task:Agent(?:%20| )Studio\/AGT-2577\/orchestrator-chat$/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      project: PROJECT,
      contextKey: TASK_CONTEXT_KEY,
      executionContext: null,
      turns: [{
        id: 'turn-1',
        ts: '2026-08-10T11:48:00Z',
        role: 'user',
        text: 'Deliver central Chat History with live context summaries',
        model: null,
      }, {
        id: 'turn-2',
        ts: '2026-08-10T11:48:10Z',
        role: 'orchestrator',
        text: 'The task chat has usage metadata.',
        model: 'gpt-6-astra',
        metadata: {
          model: 'gpt-6-astra', effort: 'medium', providerThreadId: 'thread-1',
          host: 'runner-01', queuedAt: '2026-08-10T11:48:00Z',
          startedAt: '2026-08-10T11:48:02Z', finishedAt: '2026-08-10T11:48:10Z',
          inputTokens: 80, cachedInputTokens: 20, outputTokens: 10,
          reasoningTokens: 2, cost: 0.001, currency: 'USD',
          priceCatalogueVersion: 'TokenEconomy/0.3.5',
        },
      }],
    }),
  }));
  await page.route('**/hubs/**', route => route.abort());
}

async function dismissErrorDialogs(page: Page): Promise<void> {
  for (let attempt = 0; attempt < 5; attempt++) {
    if (await page.getByTestId('error-dialog-overlay').count() === 0) return;
    await page.keyboard.press('Escape');
    await page.waitForTimeout(100);
  }
}

for (const theme of ['light', 'dark'] as const) {
  test(`lists current Task Server contexts and opens a chat in ${theme} theme`, async ({ page }, testInfo) => {
    await stubWorkspace(page);
    await page.addInitScript(selectedTheme => {
      localStorage.setItem('atp.studio.theme', selectedTheme);
    }, theme);

    await page.goto('/#/chat-history');
    await dismissErrorDialogs(page);

    const history = page.getByTestId('orchestrator-chat-history');
    await expect(history).toBeVisible();
    await expect(page).toHaveURL(/#\/chat-history$/);
    await expect(page.getByTestId('studio-ab-chat-history')).toHaveClass(/studio-ab__btn--active/);
    await expect(page.getByTestId('chat-history-counts')).toContainText('2 project contexts');
    await expect(page.getByTestId('chat-history-counts')).toContainText('2 task contexts');
    await expect(history).toContainText('Deliver central Chat History with live context summaries');
    await expect(history).not.toContainText(ARCHIVED_CONTEXT_KEY);

    const screenshotPath = join(RESULTS, `orchestrator-chat-history-${theme}--mocked.png`);
    await history.screenshot({ path: screenshotPath });
    await testInfo.attach(`Chat History ${theme}`, { path: screenshotPath, contentType: 'image/png' });

    await page.locator(`[data-testid="chat-history-row"][data-context-key="${TASK_CONTEXT_KEY}"]`).click();
    await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
    await expect(page.getByTestId('orchestrator-conversation'))
      .toContainText('Deliver central Chat History with live context summaries');
    await expect(page.getByTestId('chat-session-usage')).toContainText('1 turn · 110 tokens · $0.0010');
    await expect(page.getByTestId('chat-session-usage'))
      .toContainText('gpt-6-astra · 10s total (2s waiting) · runner-01 · session thread-1');
    await page.getByTestId('chat-metadata-toggle').click();
    await expect(page.getByTestId('chat-session-usage')).not.toContainText('110 tokens');
    await page.getByTestId('chat-metadata-toggle').click();
    const metadataScreenshot = join(RESULTS, `task-chat-metadata-${theme}--mocked.png`);
    await page.getByTestId('orch-side-sheet').screenshot({ path: metadataScreenshot });
    await page.getByTestId('orch-context-badge').click();
    await expect(page.getByTestId('orch-context-header')).toHaveAttribute('data-context-key', TASK_CONTEXT_KEY);
  });
}
