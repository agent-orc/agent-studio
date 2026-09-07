import { expect, test, type Page, type Route } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'Thinking level indicator';
const WATCH_PATH = 'C:/fixtures/thinking-level-indicator';

function task(id: string, title: string, model: string, cliType: 'claude' | 'codex' | 'gemini', configured: string, effective?: string) {
  return {
    id, key: id, displayKey: id, taskKey: `${WATCH_PATH}::${id}`, title, state: '2-ready', order: Number(id.split('-').at(-1)) || 1,
    agent: cliType, cliType, createdAt: '2026-07-11T00:00:00Z', watchPath: WATCH_PATH,
    projectName: PROJECT, folderPath: `${WATCH_PATH}/${id}`, lastActivity: '2026-07-11T00:01:00Z',
    sessionName: null, model, thinkingLevel: configured, useOwnSession: null,
    modelExplicit: true,
    lastUsage: null, commit: null, ownerClientId: 'local-default', tags: [],
    execution: {
      jobId: id, taskKey: `${WATCH_PATH}::${id}`, processId: 7, startedAt: '2026-07-11T00:00:30Z',
      status: 'completed', exitCode: 0, durationSeconds: 30, model,
      thinkingLevel: effective ?? configured, runOutcome: 'success',
    },
  };
}

const modelFixtures = [
  ['gpt-5.6-sol', 'codex', 'Sol family'],
  ['gpt-5.6-ter', 'codex', 'Ter family'],
  ['claude-opus-4-8', 'claude', 'Opus family'],
  ['claude-sonnet-4-6', 'claude', 'Sonnet family'],
  ['claude-haiku-4-5', 'claude', 'Haiku family'],
  ['gemini-2.5-pro', 'gemini', 'Gemini family'],
] as const;
const levels = ['low', 'medium', 'high', 'xhigh'] as const;
const tasks = levels.flatMap((level, levelIndex) => modelFixtures.map(([model, cli, label], modelIndex) =>
  task(`AGT-${9001 + levelIndex * modelFixtures.length + modelIndex}`, `${label} at ${level}`, model, cli, level, levelIndex === 3 ? 'medium' : level),
));
const grouped = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: tasks, progress: [], failedPickup: [],
  codeNotComplete: [], review: [], autoReview: [], humanReview: [], escalated: [], completed: [], archive: [],
};

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  let migrationAvailable = true;
  await page.route('**/api/**', route => {
    const url = route.request().url();
    if (url.includes('/api/model-migrations/apply')) {
      migrationAvailable = false;
      return json(route, {});
    }
    if (url.includes('/api/model-migrations')) return json(route, {
      catalogVersion: '2026-09-06', catalogSource: 'Token Economy', autoApplySafe: true,
      proposals: migrationAvailable ? [{
        scope: 'task', projectName: PROJECT, taskId: 'AGT-9004',
        fromModel: 'claude-sonnet-4-6', toModel: 'claude-sonnet-5',
        rule: 'same-family-current', catalogVersion: '2026-09-06', explicit: true,
        costClassFrom: 'standard', costClassTo: 'standard',
        reasoningLadderFrom: ['low', 'high'], reasoningLadderTo: ['low', 'high'],
      }] : [],
    });
    if (url.includes('/api/auth/status')) return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0, offset: 0, limit: 50 });
    if (url.includes('/api/tasks/grouped')) return json(route, grouped);
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, tasks);
    const detailMatch = /\/api\/(?:tasks|jobs)\/([^/?]+)(?:\?|$)/.exec(url);
    if (detailMatch) {
      const key = decodeURIComponent(detailMatch[1]);
      const info = tasks.find(item => item.id === key || item.taskKey === key);
      return json(route, info ? {
        info, promptMarkdown: '# Model-level indicator fixture', promptHistory: [], titleHistory: [],
        statusMarkdown: null, contextUsage: null, log: [], summaryState: null, reviewEvidence: [],
      } : null);
    }
    if (url.includes('/api/watch-paths')) return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    if (url.includes('/api/clients/local-default/defaults')) return json(route, {
      cliType: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'high',
    });
    if (url.includes('/api/clients')) return json(route, [{ id: 'local-default', displayName: 'Local', kind: 'agent-instance', defaultCliType: 'codex', defaultModel: 'gpt-5.6-sol', defaultThinkingLevel: 'high' }]);
    if (url.includes('/api/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/cli/quota')) return json(route, { at: '2026-07-11T00:00:00Z', ttlSeconds: 600, snapshots: [] });
    if (url.includes('/api/cli/usage')) return json(route, { at: '2026-07-11T00:00:00Z', sessions: [] });
    return json(route, []);
  });
}

test('keeps 24 mixed-model cards scannable and exposes full execution context', async ({ page }) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 1600, height: 1600 });
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await installRoutes(page);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded', timeout: 60_000 });

  const indicators = page.getByTestId('task-card-effective-model');
  const thinkingLevels = page.getByTestId('task-card-thinking-level');
  await expect(indicators).toHaveCount(24);
  await expect(thinkingLevels).toHaveCount(24);
  expect(new Set(await indicators.evaluateAll(nodes => nodes.map(node => node.getAttribute('data-model-family')))))
    .toEqual(new Set(['sol', 'ter', 'opus', 'sonnet', 'haiku', 'gemini']));
  await expect(indicators.nth(0)).toHaveAttribute('data-model-code', 'SOL');
  await expect(indicators.nth(1)).toHaveAttribute('data-model-code', 'TER');
  await expect(indicators.nth(2)).toHaveAttribute('data-model-code', 'OP4.8');
  await expect(thinkingLevels.nth(6)).toHaveText('m');
  await expect(thinkingLevels.nth(12)).toHaveText('h');
  await expect(thinkingLevels.nth(18)).toHaveAttribute('data-thinking-level-override', 'true');
  const migrationCard = page.locator('[data-testid="task-card"]', { hasText: 'Sonnet family at low' });
  const migration = migrationCard.getByTestId('model-migration-task');
  await expect(migration).toContainText('Update available: claude-sonnet-4-6 to claude-sonnet-5');
  await expect(migration.getByTestId('model-migration-diff')).toContainText('Cost standard → standard');

  const heights = await indicators.evaluateAll(nodes => nodes.map(node => node.getBoundingClientRect().height));
  expect(Math.max(...heights)).toBeLessThanOrEqual(22);

  await dismissDevErrorDialog(page);
  // The ng-serve-only NG0919 dialog can be re-raised by mocked polling after
  // Escape closes it. Suppress only that dev artifact so the real pointer
  // interaction and tooltip path remain under test.
  await page.addStyleTag({ content: 'app-error-dialog, app-offline-banner { display: none !important; }' });
  await indicators.nth(0).hover();
  const tooltip = page.getByTestId('cac-tooltip');
  await expect(tooltip).toContainText('Model: gpt-5.6-sol');
  await expect(tooltip).toContainText('Thinking level: low');
  await expect(tooltip).toContainText('CLI: Codex');
  const resultsDir = process.env.JOB_RESULTS_DIR?.trim()
    ? path.resolve(process.env.JOB_RESULTS_DIR)
    : path.resolve(__dirname, '../../../results');
  fs.mkdirSync(resultsDir, { recursive: true });

  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    const familyColors = await indicators.evaluateAll(nodes => nodes.slice(0, 6).map(node => getComputedStyle(node).color));
    expect(new Set(familyColors).size).toBe(6);

    await dismissDevErrorDialog(page);
    await expect(page.getByTestId('lane-2-ready')).toBeVisible();
    await page.mouse.move(1200, 80);
    await expect(tooltip).toBeHidden();
    await page.screenshot({
      path: path.join(resultsDir, `model-level-board-mixed--${theme}.png`),
      fullPage: true,
    });
  }

  const applyRequest = page.waitForRequest(request =>
    request.url().includes('/api/model-migrations/apply') && request.method() === 'POST');
  await migration.getByTestId('model-migration-apply').click();
  const applied = await applyRequest;
  expect(applied.postDataJSON()).toEqual({
    scope: 'task', projectName: PROJECT, taskId: 'AGT-9004',
    expectedFromModel: 'claude-sonnet-4-6', catalogVersion: '2026-09-06',
  });
  await expect(migrationCard.getByTestId('model-migration-task')).toHaveCount(0);
});
