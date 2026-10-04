import { test, expect, type Page, type TestInfo } from '@playwright/test';
import * as path from 'path';
import { mkdirSync, writeFileSync } from 'fs';

/**
 * DtC step 6 — GaveUpToHuman escalation reason, prominent + visually distinct
 * (task `dtc-t6-ui---cooldownretry-banner--gaveuptohuman-grund-sichtbar`).
 *
 * A `5e-escalated` card the orchestrator/infra could NOT conclude (infra crash,
 * quarantine, cli-launch-failed, quota, …) used to render exactly like a logical
 * NeedsReview escalation a human judges on its merits — hiding WHY the human is
 * here. This surface makes the give-up terminal read apart at a glance:
 *   - the header title says "Orchestrator gave up" (not the neutral "Escalation"),
 *   - the panel wears a distinct amber "system fault" wash (⚙, not the red ⚠), and
 *   - a prominent give-up banner names the escalation category + the honest reason.
 *
 * Source is the escalation category + reason the runtime already writes into the
 * card's `status.md` stub (`- Category:` / `- Reason:`) — no new side-channel.
 *
 * Fully mocked via route interception, so it runs against any served frontend
 * without a live backend. The give-up fixture mirrors a real quarantine stub.
 * Both jobs' routes are registered once so a single page can hop between them.
 */

const PROJECT = 'fixture-gaveup';
const WATCH_PATH = 'C:/fixtures/gaveup-repo';

const SHOTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? process.env.JOB_RESULTS_DIR
  : path.resolve(__dirname, '../../test-results/escalation-gave-up');

async function saveShot(testInfo: TestInfo, name: string, body: Buffer): Promise<void> {
  await testInfo.attach(name, { body, contentType: 'image/png' });
  try {
    mkdirSync(SHOTS_DIR, { recursive: true });
    writeFileSync(path.join(SHOTS_DIR, name), body);
  } catch {
    /* best-effort: the attachment above is the fallback */
  }
}

type Kind = 'gave-up' | 'needs-review';
const KINDS: Kind[] = ['gave-up', 'needs-review'];

const JOB_ID: Record<Kind, string> = {
  'gave-up': 'GAVEUP-fixture',
  'needs-review': 'NEEDSREVIEW-fixture',
};

const TITLE: Record<Kind, string> = {
  'gave-up': 'DtC T6 — orchestrator gave up (quarantined)',
  'needs-review': 'Result-Templates Teil 2 — logical escalation',
};

/**
 * The give-up card carries the runtime's `BuildStatusStub` output: a `- Category:`
 * / `- Reason:` pair (here a real quarantine stub). The needs-review card carries
 * a normal agent-written status with NO `Category:` line — which is itself the
 * signal that it is a logical escalation, not a system give-up.
 */
const STATUS_MD: Record<Kind, string> = {
  'gave-up': [
    '# Status',
    '',
    '- Result: Escalated to human decision (quarantined)',
    '',
    'This card was routed to 5e-escalated by the orchestrator runtime without an automated quality review, so there is no agent-written summary.',
    '',
    '- Category: quarantined',
    '- Reason: quarantined after 3 consecutive failed runs without progress (last issue: cli-launch-failed). Re-issuing would loop; the task is parked for human review.',
  ].join('\n'),
  'needs-review': [
    '# Status',
    '',
    '- Result: NeedsReview',
    '',
    'The agent finished the slice but flagged an open design question for a human to decide before it can be accepted.',
  ].join('\n'),
};

const TIMELINE: Record<Kind, unknown[]> = {
  'gave-up': [
    {
      ts: '2026-07-11T00:34:00Z',
      kind: 'orchestrator_escalated',
      actor: 'orchestrator',
      summary: 'quarantined',
      details: {
        reason: 'quarantined after 3 consecutive failed runs without progress (last issue: cli-launch-failed).',
        cause: 'quarantined',
        attempt: '3',
        maxAttempts: '3',
      },
    },
  ],
  'needs-review': [
    {
      ts: '2026-07-09T19:45:00Z',
      kind: 'orchestrator_escalated',
      actor: 'orchestrator',
      summary: 'escalated',
      details: {
        reason: 'Completion gate found an unresolved design question in the previous run.',
        cause: 'completion-gate',
        attempt: '2',
        maxAttempts: '3',
      },
    },
  ],
};

function buildInfo(kind: Kind) {
  return {
    id: JOB_ID[kind],
    taskKey: `${WATCH_PATH}::${JOB_ID[kind]}`,
    key: JOB_ID[kind],
    title: TITLE[kind],
    state: '5e-escalated',
    orchestratorVerdict: 'escalate',
    agent: 'claude',
    cliType: 'claude',
    model: 'claude-opus-4-8',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/5e-escalated/${JOB_ID[kind]}`,
    execution: null,
    kind: 'task',
    epicId: null,
    commit: null,
    commits: [
      { sha: 'b2ed3f47', shortSha: 'b2ed3f47', message: 'first slice', filesChanged: 3, files: ['a.ts', 'b.ts', 'c.ts'], at: '2026-07-09T18:00:00Z' },
      { sha: '1a526e97', shortSha: '1a526e97', message: 'wire it', filesChanged: 2, files: ['c.ts', 'd.ts'], at: '2026-07-09T19:00:00Z' },
    ],
    mergeSignal: {
      branch: `task/${JOB_ID[kind]}`,
      inIntegration: true,
      inRelease: false,
      integrationBranch: 'develop',
      releaseBranch: 'main',
      integrationSha: 'b2ed3f4',
      releaseSha: null,
    },
    tags: [],
    ownerClientId: 'local-default',
    lastUsage: null,
  };
}

function buildDetail(kind: Kind) {
  return {
    info: buildInfo(kind),
    promptMarkdown: '# Task',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: STATUS_MD[kind],
    statusGeneration: null,
    contextUsage: null,
    log: [],
    summaryState: { status: 'ready', startedAt: null, finishedAt: null, errorMessage: null, bytesWritten: 10 },
    reviewEvidence: [],
  };
}

const CODE_REVIEW_LIST = {
  entries: [
    {
      fileName: 'code-review-grade-2026-07-09T19-22-02Z.md',
      verdict: 'pass',
      grade: 'B',
      summary: 'High-quality, wired, well-tested first slice.',
      model: 'claude-opus-4-8',
      cliType: 'claude',
      runAt: '2026-07-09T19:22:02Z',
    },
  ],
};

function kindFromUrl(url: string): Kind {
  return url.includes(JOB_ID['gave-up']) ? 'gave-up' : 'needs-review';
}

/** Register routes for BOTH jobs once, so one page can hop between them. */
async function installRoutes(page: Page): Promise<void> {
  // Reset tabs to a clean board on every load so a deep-link opens exactly one
  // fresh task tab (no stale tab restored from a prior navigation).
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });

  const grouped = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
    humanReview: [], escalated: [buildInfo('gave-up'), buildInfo('needs-review')],
    completed: [], archive: [],
  };

  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-07-11T00:00:00Z', snapshots: [] }) }));
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) }));

  // Per-job detail (broad) — matches either fixture id; must precede the narrower
  // sub-routes below in priority (registered later wins in Playwright).
  await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/(GAVEUP|NEEDSREVIEW)-fixture(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(buildDetail(kindFromUrl(route.request().url()))) }));
  await page.route(/\/api\/tasks\/[^/]+\/pipeline(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));

  await page.route('**/code-review/list**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(CODE_REVIEW_LIST) }));
  await page.route('**/files/orchestrator-follow-up.md**', (route) =>
    route.fulfill({ status: 404, contentType: 'text/plain', body: 'not found' }));
  await page.route('**/timeline**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(TIMELINE[kindFromUrl(route.request().url())]) }));
}

async function dismissAppErrorDialog(page: Page): Promise<void> {
  const dialog = page.getByTestId('error-dialog');
  for (let i = 0; i < 3 && (await dialog.isVisible().catch(() => false)); i++) {
    await page.keyboard.press('Escape');
    await dialog.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => undefined);
  }
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

async function openDetail(page: Page, kind: Kind): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(`/?job=${encodeURIComponent(JOB_ID[kind])}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await expect(page.getByTestId('escalation-summary')).toBeVisible({ timeout: 20_000 });
  // The acute 5e-escalated lane opens by default; ensure the body is expanded so
  // the give-up banner (body-only) is captured even if a stored state collapsed it.
  const toggle = page.getByTestId('escalation-toggle');
  if ((await toggle.getAttribute('aria-expanded')) === 'false') {
    await toggle.click();
  }
  await expect(page.getByTestId('escalation-body')).toBeVisible();
}

async function shootPanel(page: Page, testInfo: TestInfo, name: string): Promise<Buffer> {
  const panel = page.getByTestId('escalation-summary');
  await dismissAppErrorDialog(page);
  await panel.scrollIntoViewIfNeeded();
  await page.waitForTimeout(150);
  const shot = await panel.screenshot();
  await saveShot(testInfo, name, shot);
  return shot;
}

async function captureComposite(
  page: Page,
  testInfo: TestInfo,
  theme: 'dark' | 'light',
  before: Buffer,
  after: Buffer,
): Promise<void> {
  const backdrop = { dark: '#1e1e2e', light: '#eff1f5' } as const;
  const caption = { dark: '#a6adc8', light: '#5c5f77' } as const;
  const b64 = (buf: Buffer) => buf.toString('base64');
  await page.setContent(
    `<!doctype html><html><body style="margin:0">`
    + `<div id="cmp" style="background:${backdrop[theme]};padding:24px;`
    + `display:inline-flex;gap:24px;align-items:flex-start;font-family:system-ui,sans-serif">`
    + `<figure style="margin:0;display:flex;flex-direction:column;gap:8px">`
    + `<figcaption style="font:600 12px/1.4 system-ui;letter-spacing:.04em;text-transform:uppercase;color:${caption[theme]}">Before · logical NeedsReview escalation</figcaption>`
    + `<img alt="before" style="display:block;box-shadow:0 0 0 1px rgba(128,128,128,.25)" src="data:image/png;base64,${b64(before)}"></figure>`
    + `<figure style="margin:0;display:flex;flex-direction:column;gap:8px">`
    + `<figcaption style="font:600 12px/1.4 system-ui;letter-spacing:.04em;text-transform:uppercase;color:${caption[theme]}">After · GaveUpToHuman (DtC step 6)</figcaption>`
    + `<img alt="after" style="display:block;box-shadow:0 0 0 1px rgba(128,128,128,.25)" src="data:image/png;base64,${b64(after)}"></figure>`
    + `</div></body></html>`,
  );
  await page.waitForTimeout(100);
  const shot = await page.locator('#cmp').screenshot();
  await saveShot(testInfo, `escalation-gave-up-vs-needs-review-${theme}--composite-mocked.png`, shot);
}

test.describe('DtC step 6 — GaveUpToHuman escalation reason', () => {
  test.beforeEach(() => test.setTimeout(120_000));

  test('a give-up escalation names its category + reason and reads distinct from a logical NeedsReview', async ({ page }, testInfo) => {
    await installRoutes(page);

    // 1. GaveUpToHuman card: distinct title, category chip, honest reason.
    await openDetail(page, 'gave-up');
    await expect(page.getByTestId('escalation-title')).toHaveText('Orchestrator gave up');
    await expect(page.getByTestId('escalation-gave-up')).toBeVisible();
    await expect(page.getByTestId('escalation-gave-up-category')).toHaveText('Quarantined');
    await expect(page.getByTestId('escalation-gave-up-reason')).toContainText('3 consecutive failed runs');
    await expect(page.getByTestId('escalation-summary')).toHaveClass(/escalation--gave-up/);
    await expect(page.getByTestId('escalation-summary')).toHaveAttribute('data-escalation-kind', 'gave-up');

    const after: Record<'dark' | 'light', Buffer> = {} as never;
    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await page.waitForTimeout(200);
      after[theme] = await shootPanel(page, testInfo, `escalation-gave-up-${theme}--mocked.png`);
    }

    // 2. Contrast: a logical NeedsReview escalation keeps the neutral presentation.
    await openDetail(page, 'needs-review');
    await expect(page.getByTestId('escalation-title')).toHaveText('Escalation');
    await expect(page.getByTestId('escalation-summary')).not.toHaveClass(/escalation--gave-up/);
    await expect(page.getByTestId('escalation-gave-up')).toHaveCount(0);

    const before: Record<'dark' | 'light', Buffer> = {} as never;
    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await page.waitForTimeout(200);
      before[theme] = await shootPanel(page, testInfo, `escalation-needs-review-${theme}--mocked.png`);
    }

    // 3. Before/after composite: neutral NeedsReview beside the distinct give-up.
    for (const theme of ['dark', 'light'] as const) {
      await captureComposite(page, testInfo, theme, before[theme], after[theme]);
    }
  });
});
