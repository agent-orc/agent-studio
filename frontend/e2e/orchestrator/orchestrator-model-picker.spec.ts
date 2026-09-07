import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'model-picker-project';
const TASK_KEY = 'AGT-2163';
const MODELS = [
  { id: 'gpt-5.4', label: 'GPT-5.4', available: true,
    thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'high' },
  { id: 'gpt-5.6-sol', label: 'GPT-5.6 Sol', isDefault: true, available: true,
    thinkingLevels: ['minimal', 'low', 'medium', 'high', 'xhigh', 'ultra'], defaultThinkingLevel: 'ultra' },
  { id: 'gpt-5.5', label: 'GPT-5.5', available: true, deprecated: true,
    availabilityNote: 'Superseded by GPT-5.6.',
    thinkingLevels: ['minimal', 'low', 'medium', 'high', 'xhigh'], defaultThinkingLevel: 'xhigh' },
  { id: 'gpt-5.6-pro', label: 'GPT-5.6 Pro', available: true,
    thinkingLevels: ['low', 'medium', 'high', 'xhigh'], defaultThinkingLevel: 'xhigh' },
  { id: 'gpt-5.4-mini', label: 'GPT-5.4 Mini', available: true,
    thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'high' },
  { id: 'gpt-5.3-codex-spark', label: 'GPT-5.3 Codex Spark', available: true,
    thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'high' },
];

const CLAUDE_MODELS = [
  { id: 'claude-opus-4-8', label: 'Claude Opus 4.8', available: true,
    thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max'], defaultThinkingLevel: 'high' },
  { id: 'claude-opus-5', label: 'Claude Opus 5', available: true,
    thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max'], defaultThinkingLevel: 'high' },
  { id: 'claude-fable-5-1', label: 'Claude Fable 5.1', isDefault: true, available: true,
    thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max'], defaultThinkingLevel: 'high' },
  { id: 'claude-sonnet-5', label: 'Claude Sonnet 5', available: true,
    thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max'], defaultThinkingLevel: 'high' },
  { id: 'claude-haiku-4-5', label: 'Claude Haiku 4.5', available: true,
    thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'high' },
  { id: 'claude-sonnet-4-6', label: 'Claude Sonnet 4.6', available: false,
    availabilityNote: 'Known in registry but not reported by the installed Claude CLI.',
    thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'high' },
];

async function stubWorkspace(
  page: Page,
  sent: Record<string, unknown>[] = [],
  claudeModels: readonly Record<string, unknown>[] = [],
) {
  await page.route(/\/api\//, route => {
    const requestPath = new URL(route.request().url()).pathname;
    let body = '{}';
    if (requestPath === '/api/auth/status') {
      body = JSON.stringify({
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      });
    }
    if (/\/api\/(?:tags|workspaces|clients|git\/summary|crash-recovery\/pending)\/?$/.test(requestPath)) body = '[]';
    if (requestPath.startsWith('/api/bus/')) body = '[]';
    if (requestPath === '/api/v1/management/remote-hosts') body = '[]';
    if (requestPath === '/api/runner/status') body = '{"projects":{}}';
    if (requestPath === '/api/cli/quota') body = '{"snapshots":[]}';
    if (requestPath === '/api/epics') body = '[]';
    if (requestPath.startsWith('/api/tasks/archive')) body = '{"items":[],"total":0,"offset":0,"limit":50}';
    if (requestPath.endsWith('/visual-evidence')) body = JSON.stringify({
      project: PROJECT, capturedAt: '2026-07-12T10:00:00Z', unseenCount: 0, items: [],
    });
    if (requestPath.endsWith('/wiki/pulse')) body = JSON.stringify({
      projectName: PROJECT, baseDir: '/tmp/wiki', exists: true, generatedAtUtc: '2026-07-12T10:00:00Z',
      feed: { available: true, reason: null, items: [] },
      inbox: { available: true, reason: null, count: 0, items: [] },
      drift: { available: true, reason: null, overallGrade: 'Fresh', areas: [],
        counts: { fresh: 0, aging: 0, stale: 0, graded: 0 } },
      critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
    });
    return route.fulfill({ status: 200, contentType: 'application/json', body });
  });
  await page.route(/\/api\/cli\/codex\/models(?:\?.*)?$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ models: MODELS, source: 'live-codex-fixture' }),
  }));
  await page.route(/\/api\/cli\/claude\/models(?:\?.*)?$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ models: claudeModels, source: 'claude-picker-fixture' }),
  }));
  await page.route(/\/api\/cli\/gemini\/models(?:\?.*)?$/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ models: [], source: 'fixture' }),
  }));
  await page.route(/\/api\/(?:tags|workspaces|clients)\/?$/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: '[]',
  }));
  await page.route(/\/api\/workspaces\/?$/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify([{ id: 'workspace-1',
      displayName: 'Picker workspace', sortOrder: 0, isDefault: true, projects: [{ id: PROJECT,
        displayName: PROJECT, shortCode: 'MP', workspaceId: 'workspace-1', storageLocation: `/tmp/${PROJECT}`,
        archived: false, urls: [{ id: 'preview', label: 'Preview', url: 'https://example.test' }] }] }]),
  }));
  await page.route(/\/api\/watch-paths$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify([{ name: PROJECT, path: `/tmp/${PROJECT}`, rootPath: `/tmp/${PROJECT}`, repositoryPath: '' }]),
  }));
  await page.route(/\/api\/tasks(?:\?.*)?$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify([{ id: 'task-1', taskKey: `${PROJECT}::task-1`, displayKey: TASK_KEY,
      title: 'Picker persistence task', state: '2-ready', projectName: PROJECT, watchPath: `/tmp/${PROJECT}` }]),
  }));
  // The active tab and composer context switch synchronously. Keep the detail
  // request pending so this footer-focused spec does not mount the unrelated,
  // very large task-detail chunk in the dev server.
  await page.route(/\/api\/tasks\/task-1(?:\?.*)?$/, () => undefined);
  await page.route(/\/api\/tasks\/grouped(?:\?.*)?$/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
      failedPickup: [], autoReview: [], humanReview: [], review: [], completed: [], archive: [] }),
  }));
  await page.route(/\/api\/orchestrator\/sessions$/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ sessions: [
      { contextKey: `project:${PROJECT}`, kind: 'project', projectId: PROJECT, taskKey: null,
        updatedAt: '2026-07-12T10:00:00Z', model: null, cumulativeInputTokens: 0,
        cumulativeOutputTokens: 0, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
        runtimeStatus: 'idle', queuePosition: 0 },
      { contextKey: `task:${PROJECT}/${TASK_KEY}`, kind: 'task', projectId: PROJECT, taskKey: TASK_KEY,
        updatedAt: '2026-07-12T10:01:00Z', model: null, cumulativeInputTokens: 0,
        cumulativeOutputTokens: 0, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
        runtimeStatus: 'idle', queuePosition: 0 },
    ] }),
  }));
  await page.route(/\/api\/runner\/[^/]+(?:\/[^/]+)?\/orchestrator-chat$/, async route => {
    if (route.request().method() === 'POST') {
      sent.push({ ...route.request().postDataJSON(), requestUrl: route.request().url() });
    }
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ project: PROJECT, turns: [] }) });
  });
}

async function choose(page: Page, model: string, reasoning: string) {
  const trigger = page.getByTestId('cac-model-selector-trigger');
  await trigger.click();
  await page.getByTestId(`cac-model-selector-picker-model-${model}`).click();
  await trigger.click();
  await expect(page.getByTestId('cac-model-selector-picker-thinking-pills')).toBeVisible();
  await page.getByTestId(`cac-model-selector-picker-thinking-${reasoning}`).click();
  await expect(trigger).toContainText(reasoning);
}

test('full live GPT picker persists across Board and Task contexts', async ({ page }, testInfo) => {
  await stubWorkspace(page);
  await page.addInitScript(({ project }) => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1,
    tabs: [
      { kind: 'board', projectName: project },
      { kind: 'task', taskKey: `${project}::task-1` },
      { kind: 'hub', projectName: project },
      { kind: 'url-preview', projectName: project, urlId: 'preview' },
    ],
    activeKey: `board:${project}`,
  })), { project: PROJECT });
  await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  // This focused spec mocks REST but deliberately does not start SignalR.
  // Keep the resulting connectivity chrome visible in screenshots without
  // letting it intercept unrelated tab and context-picker interactions.
  await page.addStyleTag({
    content: [
      '[data-testid="offline-banner"],',
      'app-notification-stack,',
      'app-notification-stack * { pointer-events: none !important; }',
    ].join('\n'),
  });
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  await page.getByTestId('orch-side-sheet-toggle').click();
  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
  await expect(page.getByTestId('chat-composer-context-surface')).toHaveText('Board');
  await expect(page.getByTestId('chat-toolbar-routing')).toHaveText('GPT-only · Inherited Codex default');
  await expect(page.getByTestId('orch-side-sheet-draft-actions')).toHaveCount(0);
  await expect(page.getByTestId('orch-side-sheet-make-task')).toHaveCount(0);
  await expect(page.getByTestId('orch-side-sheet-make-task-from-yours')).toHaveCount(0);

  const input = page.getByTestId('chat-input');
  await input.fill('/bug preserved picker draft');
  await page.getByTestId('chat-attach').click();
  await page.locator('input[type="file"]').setInputFiles({
    name: 'picker-proof.png', mimeType: 'image/png', buffer: Buffer.from(
      'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
      'base64'),
  });
  await expect(page.getByTestId('chat-drafts')).toContainText('picker-proof');

  await page.getByTestId('cac-model-selector-trigger').click();
  await expect(page.getByTestId('cac-model-selector-picker-cli-claude')).toBeDisabled();
  await expect(page.getByTestId('cac-model-selector-picker-cli-claude'))
    .toContainText('Unavailable in this GPT-only chat');
  await expect(page.getByTestId('cac-model-selector-picker-cli-codex')).toBeEnabled();
  await expect(page.getByTestId('cac-model-selector-picker-cli-gemini')).toBeDisabled();
  await expect(page.getByTestId('cac-model-selector-picker-cli-gemini'))
    .toContainText('Unavailable in this GPT-only chat');
  for (const model of MODELS) {
    await expect(page.getByTestId(`cac-model-selector-picker-model-${model.id}`)).toHaveCount(1);
  }
  const modelList = page.getByTestId('cac-model-selector-picker-model-pills');
  await expect.poll(async () => modelList.evaluate(element => ({
    clientHeight: element.clientHeight,
    scrollHeight: element.scrollHeight,
    overflowY: getComputedStyle(element).overflowY,
    maskImage: getComputedStyle(element).maskImage,
  }))).toMatchObject({
    overflowY: 'auto',
  });
  const modelListLayout = await modelList.evaluate(element => ({
    clientHeight: element.clientHeight,
    scrollHeight: element.scrollHeight,
    maskImage: getComputedStyle(element).maskImage,
  }));
  expect(modelListLayout.scrollHeight).toBeGreaterThan(modelListLayout.clientHeight);
  expect(modelListLayout.maskImage).toContain('linear-gradient');
  await page.getByTestId('cac-model-selector-picker-cancel').click();
  await expect(input).toHaveValue('/bug preserved picker draft');
  await expect(page.getByTestId('chat-drafts')).toContainText('picker-proof');
  await expect(page.getByTestId('cac-model-selector-trigger')).toBeFocused();
  await input.fill('');
  await page.getByTestId('chat-drafts').getByRole('button').click();

  await choose(page, 'gpt-5.6-sol', 'xhigh');
  await expect(page.getByTestId('chat-toolbar-routing')).toHaveText('GPT-only · Operator choice');
  await page.getByTestId(`studio-tab-hub:${PROJECT}`).click();
  await expect(page.getByTestId('chat-composer-context-surface')).toHaveText('Deck');
  await expect(page.getByTestId('cac-model-selector-trigger')).toContainText('gpt-5.6-sol');
  await page.getByTestId(`studio-tab-url-preview:${PROJECT}:preview`).click();
  await expect(page.getByTestId('chat-composer-context-surface')).toHaveText('URL preview');
  await expect(page.getByTestId('chat-composer-context-detail')).toHaveText('preview');
  await expect(page.getByTestId('cac-model-selector-trigger')).toContainText('gpt-5.6-sol');
  await page.getByTestId(`studio-tab-task:${PROJECT}::task-1`).click();
  await expect(page.getByTestId('chat-composer-context-surface')).toHaveText('Task');
  await expect(page.getByTestId('chat-composer-context-detail'))
    .toHaveText(`${PROJECT}::task-1`);
  await expect(page.getByTestId('cac-model-selector-trigger'))
    .toHaveAttribute('aria-label', /gpt-5\.6-sol.*xhigh/);
  await page.getByTestId('orch-context-badge').click();
  await page.getByTestId(`chat-switcher-row-task:${PROJECT}/${TASK_KEY}`)
    .getByRole('button').first().click();

  await choose(page, 'gpt-5.4-mini', 'low');
  await choose(page, 'gpt-5.3-codex-spark', 'high');
  await expect(page.getByTestId('cac-model-selector-trigger'))
    .toHaveAttribute('aria-label', /gpt-5\.3-codex-spark.*high/);

  await choose(page, 'gpt-5.6-sol', 'ultra');
  const results = process.env.JOB_RESULTS_DIR ?? testInfo.outputPath('evidence');
  mkdirSync(results, { recursive: true });
  await page.setViewportSize({ width: 760, height: 900 });
  await page.getByTestId('cac-model-selector-trigger').click();
  const picker = page.getByTestId('cac-model-selector-picker');
  await expect(picker).toBeVisible();
  const levelPositions = await page.getByTestId('cac-model-selector-picker-thinking-pills')
    .getByRole('radio')
    .evaluateAll(elements => elements.map(element => {
      const rect = element.getBoundingClientRect();
      return { x: Math.round(rect.x), y: Math.round(rect.y), width: Math.round(rect.width) };
    }));
  expect(new Set(levelPositions.map(position => position.x)).size).toBe(3);
  expect(new Set(levelPositions.map(position => position.y)).size).toBe(2);
  expect(new Set(levelPositions.map(position => position.width)).size).toBe(1);
  await setTheme(page, 'light');
  await picker.screenshot({ path: path.join(results, 'orchestrator-model-picker-after-light.png') });
  await page.screenshot({ path: path.join(results, 'orchestrator-model-picker-light-compact.png') });
  await picker.screenshot({ path: path.join(results, 'orchestrator-model-picker-light-popover.png') });
  await setTheme(page, 'dark');
  await picker.screenshot({ path: path.join(results, 'orchestrator-model-picker-after-dark.png') });
  await page.screenshot({ path: path.join(results, 'orchestrator-model-picker-dark-compact.png') });
  await picker.screenshot({ path: path.join(results, 'orchestrator-model-picker-dark-popover.png') });

  // Persist a deterministic rendering of the reported pre-fix state alongside
  // the live after-state: Codex was the only visible CLI, the model list ended
  // at a hard 220px clip, and level pills wrapped by intrinsic width.
  await page.addStyleTag({
    content: `
      [data-testid="cac-model-selector-picker-cli-claude"],
      [data-testid="cac-model-selector-picker-cli-gemini"] {
        display: none !important;
      }
      [data-testid="cac-model-selector-picker"] {
        width: 300px !important;
        min-width: 300px !important;
      }
      [data-testid="cac-model-selector-picker-cli-pills"],
      [data-testid="cac-model-selector-picker-thinking-pills"] {
        display: flex !important;
      }
      [data-testid="cac-model-selector-picker-model-pills"] {
        max-height: 220px !important;
        padding-block-end: 0 !important;
        mask-image: none !important;
      }
    `,
  });
  await setTheme(page, 'light');
  await picker.screenshot({ path: path.join(results, 'orchestrator-model-picker-before-light.png') });
  await setTheme(page, 'dark');
  await picker.screenshot({ path: path.join(results, 'orchestrator-model-picker-before-dark.png') });
});

test('Studio Board picker leads with the latest generation and keeps older models selectable', async ({ page }, testInfo) => {
  const sent: Record<string, unknown>[] = [];
  await stubWorkspace(page, sent);
  await page.addInitScript(({ project }) => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1,
    tabs: [{ kind: 'board', projectName: project }],
    activeKey: `board:${project}`,
  })), { project: PROJECT });
  await page.goto('/');
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);

  await page.getByTestId('studio-board-add-task').click();
  await page.getByTestId('create-agent').click();
  await page.getByTestId('create-agent-picker-cli-codex').click();
  const createModelRows = page.getByTestId('create-agent-picker-model-pills').getByRole('radio');
  await expect(createModelRows).toHaveCount(MODELS.length + 1);
  expect(await createModelRows.evaluateAll(rows => rows.map(row => row.getAttribute('data-testid')))).toEqual([
    'create-agent-picker-model-default',
    'create-agent-picker-model-gpt-5.6-sol',
    'create-agent-picker-model-gpt-5.6-pro',
    'create-agent-picker-model-gpt-5.5',
    'create-agent-picker-model-gpt-5.4',
    'create-agent-picker-model-gpt-5.4-mini',
    'create-agent-picker-model-gpt-5.3-codex-spark',
  ]);
  await expect(page.getByTestId('create-agent-picker-older-heading')).toContainText('Older models');
  const currentModel = page.getByTestId('create-agent-picker-model-gpt-5.6-sol');
  const deprecatedModel = page.getByTestId('create-agent-picker-model-gpt-5.5');
  await expect(currentModel).not.toHaveAttribute('data-generation', 'older');
  await expect(deprecatedModel).toHaveAttribute('data-generation', 'older');
  await expect(deprecatedModel).toHaveAttribute('data-deprecated', 'true');
  await expect(deprecatedModel).toContainText('Superseded by GPT-5.6.');
  await expect(deprecatedModel).toBeEnabled();
  expect(Number(await deprecatedModel.evaluate(element => getComputedStyle(element).opacity))).toBeLessThan(1);

  const results = process.env.JOB_RESULTS_DIR ?? testInfo.outputPath('evidence');
  mkdirSync(results, { recursive: true });
  await page.setViewportSize({ width: 760, height: 900 });
  await setTheme(page, 'light');
  await page.screenshot({ path: path.join(results, 'orchestrator-model-picker-light-compact.png') });
  await setTheme(page, 'dark');
  await page.screenshot({ path: path.join(results, 'orchestrator-model-picker-dark-compact.png') });

  await deprecatedModel.click();
  await page.getByTestId('create-agent-picker-done').click();
  await expect(page.getByTestId('create-agent')).toContainText('gpt-5.5');
});

test('Claude picker keeps the 5 family above 4.x and shows unavailable registry models disabled', async ({ page }, testInfo) => {
  await stubWorkspace(page, [], CLAUDE_MODELS);
  await page.addInitScript(({ project }) => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1,
    tabs: [{ kind: 'board', projectName: project }],
    activeKey: `board:${project}`,
  })), { project: PROJECT });
  await page.goto('/');
  await page.addStyleTag({
    content: 'app-error-dialog, app-offline-banner, [data-testid="error-dialog-overlay"] { display: none !important; }',
  });

  await page.getByTestId('studio-board-add-task').click();
  await page.getByTestId('create-agent').click();
  await page.getByTestId('create-agent-picker-cli-claude').click();

  const rows = page.getByTestId('create-agent-picker-model-pills').getByRole('radio');
  expect(await rows.evaluateAll(elements => elements.map(element => element.getAttribute('data-testid')))).toEqual([
    'create-agent-picker-model-default',
    'create-agent-picker-model-claude-fable-5-1',
    'create-agent-picker-model-claude-opus-5',
    'create-agent-picker-model-claude-sonnet-5',
    'create-agent-picker-model-claude-opus-4-8',
    'create-agent-picker-model-claude-haiku-4-5',
    'create-agent-picker-model-claude-sonnet-4-6',
  ]);
  await expect(page.getByTestId('create-agent-picker-older-heading')).toContainText('Older models');
  const opusFour = page.getByTestId('create-agent-picker-model-claude-opus-4-8');
  const haikuFour = page.getByTestId('create-agent-picker-model-claude-haiku-4-5');
  await expect(opusFour).toBeEnabled();
  await expect(opusFour).not.toHaveAttribute('data-deprecated');
  await expect(haikuFour).toBeEnabled();
  await expect(haikuFour).not.toHaveAttribute('data-deprecated');
  const unavailable = page.getByTestId('create-agent-picker-model-claude-sonnet-4-6');
  await expect(unavailable).toBeDisabled();
  await expect(unavailable).toHaveAttribute('aria-disabled', 'true');
  await expect(unavailable).toContainText('Known in registry but not reported by the installed Claude CLI.');
  await expect(unavailable).toHaveAttribute('data-generation', 'older');

  const results = process.env.JOB_RESULTS_DIR ?? testInfo.outputPath('evidence');
  mkdirSync(results, { recursive: true });
  const picker = page.getByTestId('create-agent-picker');
  await setTheme(page, 'light');
  await picker.screenshot({ path: path.join(results, 'claude-model-picker-unavailable-light--mocked.png') });
  await setTheme(page, 'dark');
  await picker.screenshot({ path: path.join(results, 'claude-model-picker-unavailable-dark--mocked.png') });
});
