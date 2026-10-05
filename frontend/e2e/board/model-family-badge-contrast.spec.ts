import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { contrastRatio } from '../helpers/contrast';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

const PROJECT = 'Model family contrast';
const WATCH_PATH = 'C:/fixtures/model-family-contrast';
const PRIMARY_ID = 'AGT-9251';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? resolve(process.env.JOB_RESULTS_DIR)
  : resolve(__dirname, '../../../results');

const LIGHT_SURFACE = 'rgb(250, 248, 244)';
const LIGHT_TOKENS = {
  claude: { token: '--model-family-claude', hex: '#c2410c', rgb: 'rgb(194, 65, 12)' },
  codex: { token: '--model-family-codex', hex: '#0369a1', rgb: 'rgb(3, 105, 161)' },
  gemini: { token: '--model-family-gemini', hex: '#6d28d9', rgb: 'rgb(109, 40, 217)' },
  openai: { token: '--model-family-openai', hex: '#0f766e', rgb: 'rgb(15, 118, 110)' },
} as const;

const DARK_LIBRARY_DEFAULTS = {
  claude: 'rgb(217, 119, 87)',
  codex: 'rgb(56, 189, 248)',
  gemini: 'rgb(167, 139, 250)',
  openai: 'rgb(78, 201, 176)',
} as const;

function task(
  id: string,
  title: string,
  model: string,
  cliType: 'claude' | 'codex' | 'gemini',
  thinkingLevel: string,
) {
  return {
    id,
    key: id,
    displayKey: id,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state: '2-ready',
    order: Number(id.slice(-1)),
    agent: cliType,
    cliType,
    model,
    thinkingLevel,
    createdAt: '2026-08-09T08:00:00.000Z',
    lastActivity: '2026-08-09T08:05:00.000Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/${id}`,
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
  };
}

const tasks = [
  task(PRIMARY_ID, 'SOL at xhigh on a board card', 'gpt-5.6-sol', 'codex', 'xhigh'),
  task('AGT-9252', 'Claude family badge', 'claude-opus-4-8', 'claude', 'high'),
  task('AGT-9253', 'Codex family badge', 'gpt-5-codex', 'codex', 'medium'),
  task('AGT-9254', 'Gemini family badge', 'gemini-2.5-pro', 'gemini', 'low'),
];

const runnerEvents = [
  runnerStart('runner-claude', 1, '2026-08-09T08:00:01.000Z', 'claude', 'claude-opus-4-8', 'high'),
  runnerStart('runner-codex', 2, '2026-08-09T08:00:02.000Z', 'codex', 'gpt-5-codex', 'medium'),
  runnerStart('runner-gemini', 3, '2026-08-09T08:00:03.000Z', 'gemini', 'gemini-2.5-pro', 'low'),
  runnerStart('runner-openai', 4, '2026-08-09T08:00:04.000Z', 'codex', 'gpt-5', 'xhigh'),
];

const activityOutput = runnerEvents.flatMap((event, index) => [{
  timestamp: event.timestamp,
  stream: 'system',
  text: `[taskboard] Started ${event.cli} CLI (PID ${100 + index}), model=${event.model}, thinkingLevel=${event.thinkingLevel}`,
}, {
  timestamp: event.timestamp.replace('.000Z', '.500Z'),
  stream: 'stdout',
  text: `Conversation evidence for the ${event.cli} model family.`,
}]);

function runnerStart(
  id: string,
  runIndex: number,
  timestamp: string,
  cli: string,
  model: string,
  thinkingLevel: string,
) {
  return {
    id,
    kind: 'session.started',
    timestamp,
    sessionId: `session-${id}`,
    runIndex,
    cli,
    model,
    thinkingLevel,
  };
}

function detail() {
  return {
    info: {
      ...tasks[0],
      summaryState: null,
      taskType: 'feature',
      codeActivityDetected: true,
      tokenSummary: null,
    },
    promptMarkdown: '# Model family contrast fixture',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: '## Status\n\nReady for visual review.',
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

function timeline() {
  return {
    runCount: runnerEvents.length,
    firstStartedAt: runnerEvents[0].timestamp,
    lastActivityAt: runnerEvents.at(-1)?.timestamp,
    hasActiveRun: false,
    runs: runnerEvents.map((event, index) => ({
      index: index + 1,
      intent: index === 0 ? 'start' : 'continue',
      startedAt: event.timestamp,
      endedAt: event.timestamp,
      status: 'completed',
      cli: event.cli,
      model: event.model,
      thinkingLevel: event.thinkingLevel,
      exitCode: 0,
      durationSeconds: 1,
      inputSessionId: null,
      capturedSessionId: event.sessionId,
      resumed: index > 0,
      reason: null,
      userFollowup: null,
      lineStart: index * 2 + 1,
      lineEnd: index * 2 + 2,
      headShaBefore: null,
      headShaAfter: null,
      contextRef: null,
    })),
    promptEntries: [],
    runnerEvents: [],
  };
}

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: tasks, progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
    escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [{
    name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH,
  }]));
  await page.route('**/api/clients/local-default/defaults**', route => json(route, {
    cliType: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'xhigh',
  }));
  await page.route('**/api/clients**', route => json(route, [{
    id: 'local-default', displayName: 'Local', kind: 'agent-instance',
    defaultCliType: 'codex', defaultModel: 'gpt-5.6-sol', defaultThinkingLevel: 'xhigh',
  }]));
  await page.route('**/api/v1/studio/runner/status**', route => json(route, { projects: {} }));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-08-09T08:00:00.000Z', ttlSeconds: 600, snapshots: [],
  }));
  await page.route('**/api/cli/usage**', route => json(route, {
    at: '2026-08-09T08:00:00.000Z', sessions: [],
  }));

  const encodedId = encodeURIComponent(PRIMARY_ID);
  await page.route(`**/api/tasks/${encodedId}/output?**`, route => json(route, activityOutput));
  await page.route(`**/api/tasks/${encodedId}/runs?**`, route => json(route, timeline()));
  await page.route(`**/api/tasks/${encodedId}/pipeline?**`, route => json(route, null));
  await page.route(`**/api/tasks/${encodedId}/session-events?**`, route => json(route, { events: [], sessionChain: [] }));
  await page.route(`**/api/tasks/${encodedId}/claude-session?**`, route => json(route, null));
  await page.route(`**/api/v1/projects/*/tasks/${encodedId}?**`, route => json(route, detail()));
}

async function expectThemeTokens(page: Page, theme: Theme): Promise<void> {
  const values = await page.locator('html').evaluate((root, tokens) => {
    const style = getComputedStyle(root);
    return Object.fromEntries(tokens.map(token => [token, style.getPropertyValue(token).trim()]));
  }, Object.values(LIGHT_TOKENS).map(value => value.token));

  for (const { token, hex, rgb } of Object.values(LIGHT_TOKENS)) {
    if (theme === 'light') {
      expect(values[token], `${token} must resolve to Candidate A in light theme`).toBe(hex);
      expect(
        contrastRatio(rgb, LIGHT_SURFACE),
        `${token} contrast against ${LIGHT_SURFACE}`,
      ).toBeGreaterThanOrEqual(4.5);
    } else {
      expect(values[token], `${token} must not override CAC's dark default`).toBe('');
    }
  }
}

async function expectConversationColours(page: Page, theme: Theme): Promise<void> {
  const indicators = page
    .getByTestId('conversation-message-model')
    .getByTestId('model-level-indicator');
  await expect(indicators).toHaveCount(4);
  const colours = await indicators.evaluateAll(nodes => Object.fromEntries(nodes.map(node => [
    node.getAttribute('data-family'),
    getComputedStyle(node).color,
  ])));
  const expected = theme === 'light'
    ? Object.fromEntries(Object.entries(LIGHT_TOKENS).map(([family, value]) => [family, value.rgb]))
    : DARK_LIBRARY_DEFAULTS;
  expect(colours).toEqual(expected);
}

test('Candidate A keeps model-family badges AA-safe across Studio surfaces and preserves dark defaults', async ({ page }) => {
  test.setTimeout(120_000);
  mkdirSync(RESULTS_DIR, { recursive: true });
  await page.setViewportSize({ width: 1500, height: 980 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.nextGenChat', '1');
    localStorage.setItem('atp.studio.theme', 'light');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
    localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: true, git: false }));
  });
  await installRoutes(page);

  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  const boardIndicators = page.getByTestId('task-card-effective-model');
  await expect(boardIndicators).toHaveCount(4);
  await expect(boardIndicators.first()).toHaveAttribute('data-model-code', 'SOL');
  await expect(boardIndicators.first()).toContainText('xh');

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expectThemeTokens(page, theme);
    await dismissDevErrorDialog(page);
    await page.getByTestId('lane-2-ready').screenshot({
      path: join(RESULTS_DIR, `model-family-board--${theme}--mocked.png`),
    });
  }

  // The current Studio shell moves task actions into its slim tab bar and
  // intentionally hides the projected detail-header component. Exercise the
  // supported legacy-chrome flag here so the actual Task Detail header badge
  // remains part of the visual evidence requested for this host surface.
  await page.evaluate(() => localStorage.setItem('atp.flag.vsCodeLayout', '0'));
  await page.goto(`/?job=${encodeURIComponent(PRIMARY_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  const detailModel = page.getByTestId('detail-model-chip');
  await expect(detailModel).toBeVisible();
  await expect(detailModel).toHaveAttribute('data-model-code', 'SOL');
  await expect(detailModel).toContainText('xh');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    await page.screenshot({
      path: join(RESULTS_DIR, `model-family-detail-header--${theme}--mocked.png`),
      fullPage: false,
    });
  }

  await page.getByTestId('inspector-tab-activity').click();
  const activity = page.getByTestId('activity-panel');
  await expect(activity.getByTestId('conversation-view')).toBeVisible();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expectThemeTokens(page, theme);
    await expectConversationColours(page, theme);
    await dismissDevErrorDialog(page);
    await activity.screenshot({
      path: join(RESULTS_DIR, `model-family-conversation--${theme}--mocked.png`),
    });
  }
});
