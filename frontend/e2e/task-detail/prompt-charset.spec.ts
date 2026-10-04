import { test, expect, type Locator, type Page, type TestInfo } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

const JOB_ID = 'charset-utf8-fixture';
const WATCH_PATH = 'C:/fixtures/charset-project';
const SAMPLE_TEXT = 'Lücken / gehört / für / „Anführung"';
const NOTE_FILE = 'REVIEW_NOTE.md';

const PROMPT_MARKDOWN = [
  '# UTF-8 prompt probe',
  '',
  SAMPLE_TEXT,
  '',
  'Regression guard for the task prompt viewer and markdown file renderer.',
].join('\n');

const NOTE_MARKDOWN = [
  '# UTF-8 side file',
  '',
  SAMPLE_TEXT,
  '',
  'This file is served as raw UTF-8 bytes without a charset header in the route fixture.',
].join('\n');

const MOJIBAKE_NEEDLES = ['LÃ', 'gehÃ', 'fÃ', 'Ã¢â'];

function taskInfo() {
  const now = new Date().toISOString();
  return {
    id: JOB_ID,
    jobKey: `${WATCH_PATH}::${JOB_ID}`,
    taskKey: `${WATCH_PATH}::${JOB_ID}`,
    title: 'UTF-8 charset fixture',
    state: '2-ready',
    order: 1,
    agent: 'claude',
    cliType: 'claude',
    model: null,
    createdAt: now,
    watchPath: WATCH_PATH,
    projectName: 'charset-project',
    folderPath: `${WATCH_PATH}/2-ready/${JOB_ID}`,
    lastActivity: now,
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    codeActivityDetected: false,
    promptHistory: [],
    ownerClientId: 'local-default',
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
  };
}

function grouped() {
  return {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [taskInfo()],
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    autoReview: [],
    humanReview: [],
    escalated: [],
    review: [],
    completed: [],
    archive: [],
  };
}

function detail() {
  return {
    info: taskInfo(),
    promptMarkdown: PROMPT_MARKDOWN,
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: null,
    statusGeneration: null,
    contextUsage: null,
    log: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null, bytesWritten: null },
    reviewEvidence: [],
  };
}

async function installRoutes(page: Page): Promise<void> {
  const json = (body: unknown) => async (route: import('@playwright/test').Route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json; charset=utf-8',
      body: JSON.stringify(body),
    });
  };

  await page.route('**/api/**', json([]));

  await page.route(/\/api\/(?:jobs|tasks)(\?.*)?$/, json([taskInfo()]));
  await page.route('**/api/v1/studio/board**', json(grouped()));
  await page.route('**/api/watch-paths**', json([
    { name: 'charset-project', path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, json({ projects: {} }));
  await page.route('**/api/environment**', json({
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/dev-tools/flags**', json({
    updateStableEnabled: false,
    deleteE2EJobsEnabled: false,
  }));
  await page.route('**/api/clients**', json([]));
  await page.route('**/api/cli/usage**', json({ entries: [] }));
  await page.route('**/api/cli/quota**', json({ ttlMs: 600_000, snapshots: [] }));
  await page.route('**/api/git/summary**', json([]));
  await page.route(/\/api\/git\/hygiene(\?|$)/, json({ isRepo: false, error: null }));

  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${JOB_ID}(\\?.*)?$`), json(detail()));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${JOB_ID}/artifacts(\\?.*)?$`), json({
    jobId: JOB_ID,
    files: [
      {
        name: 'prompt.md',
        sizeBytes: Buffer.byteLength(PROMPT_MARKDOWN, 'utf8'),
        mtime: new Date().toISOString(),
        kind: 'prompt',
        aspectName: null,
        generation: null,
      },
      {
        name: NOTE_FILE,
        sizeBytes: Buffer.byteLength(NOTE_MARKDOWN, 'utf8'),
        mtime: new Date().toISOString(),
        kind: 'note',
        aspectName: null,
        generation: null,
      },
    ],
  }));

  await page.route(new RegExp(`/api/(?:jobs|tasks)/${JOB_ID}/files/${NOTE_FILE}(\\?.*)?$`), async (route) => {
    await route.fulfill({
      status: 200,
      headers: { 'content-type': 'text/plain' },
      body: Buffer.from(NOTE_MARKDOWN, 'utf8'),
    });
  });

  await page.route(new RegExp(`/api/(?:jobs|tasks)/${JOB_ID}/output(\\?.*)?$`), json([]));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${JOB_ID}/runs(\\?.*)?$`), json({
    runs: [],
    runCount: 0,
    hasActiveRun: false,
  }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${JOB_ID}/screenshots(\\?.*)?$`), json({
    jobId: JOB_ID,
    screenshots: [],
  }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${JOB_ID}/session-events(\\?.*)?$`), json({
    events: [],
    sessionChain: [],
    currentSessionId: null,
  }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${JOB_ID}/claude/session-info(\\?.*)?$`), json({
    sessionInfo: null,
    rateLimit: null,
  }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${JOB_ID}/git/status(\\?.*)?$`), json({
    isRepo: false,
    branch: null,
    filesChanged: 0,
    totalAdded: 0,
    totalRemoved: 0,
    files: [],
    error: null,
  }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${JOB_ID}/agent-work-summary(\\?.*)?$`), json({
    calls: 0,
    recovered: false,
    toolCalls: 0,
    toolCounts: [],
    startedAt: null,
    lastTouchAt: null,
    currentSessionId: null,
  }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${JOB_ID}/plan(\\?.*)?$`), json({
    hasPlan: false,
    source: null,
    snapshotCount: 0,
    activeItemId: null,
    softEstimateMedian: null,
    items: [],
    unassignedSubActions: [],
  }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${JOB_ID}/timeline(\\?.*)?$`), json([]));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${JOB_ID}/pipeline(\\?.*)?$`), json({
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null,
    cost: {
      steps: [],
      totalInputTokens: 0,
      totalOutputTokens: 0,
      totalCacheReadTokens: 0,
      totalCacheCreationTokens: 0,
      totalTokens: 0,
      totalInputCostUsd: 0,
      totalOutputCostUsd: 0,
      totalCacheReadCostUsd: 0,
      totalCacheCreationCostUsd: 0,
      totalCostUsd: 0,
      anyModelUnknown: false,
    },
    config: {},
  }));
}

async function captureEvidence(
  page: Page,
  testInfo: TestInfo,
  fileName: string,
  locator: Locator = page.locator('body'),
): Promise<void> {
  const buf = await locator.screenshot();
  await testInfo.attach(fileName, { body: buf, contentType: 'image/png' });

  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (!resultsDir) return;
  const dir = join(resultsDir, 'charset');
  await mkdir(dir, { recursive: true });
  await writeFile(join(dir, fileName), buf);
}

async function writeEvidenceNote(fileName: string, body: string): Promise<void> {
  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (!resultsDir) return;
  const dir = join(resultsDir, 'charset');
  await mkdir(dir, { recursive: true });
  await writeFile(join(dir, fileName), body, 'utf8');
}

async function assertNoMojibake(locator: Locator): Promise<void> {
  await expect(locator).toContainText(SAMPLE_TEXT);
  for (const needle of MOJIBAKE_NEEDLES) {
    await expect(locator).not.toContainText(needle);
  }
}

async function dismissAnyErrorDialog(page: Page): Promise<void> {
  const close = page.locator('.error-dialog__close').first();
  for (let i = 0; i < 3; i++) {
    if (!(await close.isVisible().catch(() => false))) return;
    await close.click({ timeout: 1_000 }).catch(() => {});
    await page.waitForTimeout(100);
  }
}

test.describe('Task prompt charset rendering', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false }));
      } catch {
        // Private mode.
      }
    });
    await installRoutes(page);
  });

  test('renders German UTF-8 text in the prompt viewer and Files tab without mojibake', async ({ page }, testInfo) => {
    await page.setContent(`
      <!doctype html>
      <html>
        <head><meta charset="utf-8"><title>charset before reproduction</title></head>
        <body style="font: 16px system-ui; padding: 24px;">
          <h1>Before reproduction</h1>
          <p>UTF-8 bytes decoded as Latin-1/cp1252 produce: LÃ¼cken / gehÃ¶rt / fÃ¼r / Ã¢â‚¬Å¾AnfÃ¼hrung"</p>
          <p>Expected UTF-8 text: ${SAMPLE_TEXT}</p>
        </body>
      </html>
    `);
    await captureEvidence(page, testInfo, 'charset-before-mojibake-reproduction.png');

    await page.setViewportSize({ width: 1440, height: 950 });
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('detail-panes')).toBeVisible({ timeout: 15_000 });
    await dismissAnyErrorDialog(page);

    const promptTrigger = page.getByTestId('overview-prompt-trigger');
    await expect(promptTrigger).toBeVisible();
    await promptTrigger.click();
    const promptModal = page.getByTestId('overview-prompt-popover');
    const promptModalBody = page.getByTestId('overview-prompt-popover-body');
    await assertNoMojibake(promptModalBody);
    await captureEvidence(page, testInfo, 'charset-after-overview-prompt.png', promptModal);
    await page.getByTestId('overview-prompt-popover-close').click();

    await page.getByTestId('prompt-tab-description').click();
    const promptCard = page.getByTestId('file-card-prompt.md');
    await expect(promptCard).toBeVisible();
    await assertNoMojibake(promptCard);

    const noteCard = page.getByTestId(`file-card-${NOTE_FILE}`);
    await expect(noteCard).toBeVisible();
    await assertNoMojibake(noteCard);
    await noteCard.getByText(NOTE_FILE).click();
    await expect(noteCard).toHaveAttribute('class', /file-card--expanded/);
    await assertNoMojibake(noteCard);
    await captureEvidence(page, testInfo, 'charset-after-files-tab.png', page.getByTestId('files-pane'));

    await writeEvidenceNote(
      'charset-e2e-summary.md',
      [
        '# Charset e2e evidence',
        '',
        `Fixture text: ${SAMPLE_TEXT}`,
        '',
        'Verified surfaces:',
        '- Overview task prompt modal renders the promptMarkdown payload without mojibake.',
        '- Files tab prompt card renders the same prompt without mojibake.',
        '- Files tab note card renders raw UTF-8 bytes from /files/REVIEW_NOTE.md without a charset header.',
        '',
        'Artifacts:',
        '- charset-before-mojibake-reproduction.png',
        '- charset-after-overview-prompt.png',
        '- charset-after-files-tab.png',
      ].join('\n'),
    );
  });
});
