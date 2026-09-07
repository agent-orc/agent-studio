import { test, expect, type Page, type TestInfo } from '@playwright/test';
import * as path from 'path';
import { mkdirSync, writeFileSync } from 'fs';

/**
 * Escalation summary panel — collapsible + compact (AGT-2060, round 2 on AGT-2019).
 *
 * Round 1 (AGT-2019) shipped a prominent escalation card. Operator feedback:
 * it is a "RIESIGE Karte" that must be collapsible so the rest of the detail
 * view stays reachable. This round makes the panel:
 *   1. COLLAPSIBLE — the whole header row is the toggle; state remembered per task.
 *   2. DEFAULT by lane — open on the acute `5e-escalated` lane, closed elsewhere
 *      (an escalate verdict parked in `5-human-review` is historical context).
 *   3. COMPACT — the header carries a one-line essence (reason category · review
 *      grade · merge status); the gate checklist + detail grid live in the
 *      height-capped, internally-scrolling body shown only when expanded.
 *   4. NO left accent line (operator hard-rule).
 *
 * Fully mocked so it is deterministic and needs no live backend.
 */

const PROJECT = 'fixture-escalation';
const WATCH_PATH = 'C:/fixtures/escalation';
const JOB_ID = 'AGT-1994-fixture';

const SHOTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.join(process.env.JOB_RESULTS_DIR, '.')
  : path.resolve(__dirname, '../../test-results/escalation-summary');

/** Persist a shot both as a report attachment and as a durable file under results/. */
async function saveShot(testInfo: TestInfo, name: string, body: Buffer): Promise<void> {
  await testInfo.attach(name, { body, contentType: 'image/png' });
  try {
    mkdirSync(SHOTS_DIR, { recursive: true });
  } catch {
    /* best-effort: the attachment above is the fallback */
  }
}

function buildInfo(state: string, emptyContext = false) {
  return {
    id: JOB_ID,
    taskKey: `${WATCH_PATH}::${JOB_ID}`,
    title: 'Result-Templates Teil 2: JSON-Meta-Haelfte',
    state,
    orchestratorVerdict: 'escalate',
    agent: 'claude',
    cliType: 'claude',
    model: 'claude-opus-4-8',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${JOB_ID}`,
    execution: null,
    kind: 'task',
    epicId: null,
    commit: null,
    commits: emptyContext ? [] : [
      { sha: 'b2ed3f47', shortSha: 'b2ed3f47', message: 'first slice', filesChanged: 3, files: ['a.ts', 'b.ts', 'c.ts'], at: '2026-07-09T18:00:00Z' },
      { sha: '1a526e97', shortSha: '1a526e97', message: 'wire it', filesChanged: 2, files: ['c.ts', 'd.ts'], at: '2026-07-09T19:00:00Z' },
    ],
    mergeSignal: emptyContext ? null : {
      branch: 'task/AGT-1994',
      inIntegration: true,
      inRelease: true,
      integrationBranch: 'main',
      releaseBranch: 'main',
      integrationSha: 'b2ed3f4',
      releaseSha: '1a526e9',
    },
    // AGT-2717: the escalation banner reads the canonical review projection
    // instead of counting `code-review/list` entries client-side; keep this
    // fixture's rounds/outcome in sync with CODE_REVIEW_LIST above.
    reviewProjection: emptyContext ? null : {
      attempts: [
        {
          plane: 'local', attemptId: 'code-review-grade-2026-07-09T19-22-02Z', receivedAt: '2026-07-09T19:22:02Z',
          outcome: 'pass', grade: 'B', buildTestsResult: 'not-proven', buildTestsReason: null, aspects: [],
          subjectSha: null, reportRef: 'code-review-grade-2026-07-09T19-22-02Z.md',
        },
        {
          plane: 'local', attemptId: 'code-review-grade-2026-07-09T18-22-02Z', receivedAt: '2026-07-09T18:22:02Z',
          outcome: 'concerns', grade: 'C', buildTestsResult: 'not-proven', buildTestsReason: null, aspects: [],
          subjectSha: null, reportRef: 'code-review-grade-2026-07-09T18-22-02Z.md',
        },
        {
          plane: 'local', attemptId: 'code-review-grade-2026-07-09T17-22-02Z', receivedAt: '2026-07-09T17:22:02Z',
          outcome: 'concerns', grade: 'D', buildTestsResult: 'not-proven', buildTestsReason: null, aspects: [],
          subjectSha: null, reportRef: 'code-review-grade-2026-07-09T17-22-02Z.md',
        },
      ],
      rounds: 3,
      latestPlane: 'local',
      latestOutcome: 'pass',
      latestReceivedAt: '2026-07-09T19:22:02Z',
      blockingAspects: [],
      delivery: { status: 'integrated', reason: null },
      decisionRequired: { required: true, source: 'escalation-event', reason: null },
    },
    tags: [],
    ownerClientId: 'local-default',
    lastUsage: null,
  };
}

function buildDetail(state: string, emptyContext = false) {
  return {
    info: buildInfo(state, emptyContext),
    promptMarkdown: '# Task',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: '# Status\nResult: escalated\n\n- test bullet one\n- test bullet two\n- test bullet three',
    statusGeneration: null,
    contextUsage: null,
    log: [],
    summaryState: { status: 'ready', startedAt: null, finishedAt: null, errorMessage: null, bytesWritten: 10 },
    reviewEvidence: [],
  };
}

const LONG_COUNCIL_CONTEXT = Array.from(
  { length: 18 },
  (_, index) => `### Council finding ${index + 1}\nThe review reaction keeps the complete finding context for round three, including evidence, affected files, and the required focused verification.`,
).join('\n\n');

const FOLLOW_UP = [
  '# Orchestrator follow-up',
  '',
  'STEER THE DIFF, DO NOT RESTART: close out only the open items.',
  '',
  '- [ ] Frontend build/unit/Playwright verification skipped (worktree limitation).',
  '- [ ] Live Haiku probe not run (dev backend offline).',
  '- [ ] Structured JSON aspect artefacts left for follow-up.',
  '- [ ] Preserve the complete council reaction and its focused evidence.',
  '',
  LONG_COUNCIL_CONTEXT,
].join('\n');

const CODE_REVIEW_LIST = {
  entries: [
    {
      fileName: 'code-review-grade-2026-07-09T19-22-02Z.md',
      verdict: 'pass',
      grade: 'B',
      summary: 'High-quality, wired, well-tested first slice that defers the JSON half and two metrics.',
      model: 'claude-opus-4-8',
      cliType: 'claude',
      runAt: '2026-07-09T19:22:02Z',
      councilReaction: {
        createdAt: '2026-07-09T19:22:03Z',
        reviewFileName: 'code-review-grade-2026-07-09T19-22-02Z.md',
        grade: 'B',
        disposition: 'Escalate',
        summary: `Escalate 4 open review findings; loop budget exhausted.\n\n${LONG_COUNCIL_CONTEXT}`,
        assessments: [
          { finding: 'Preserve the complete council response.', action: 'Escalate', reason: 'Review-loop budget exhausted.' },
          { finding: 'Render every grade document.', action: 'Escalate', reason: 'Review-loop budget exhausted.' },
          { finding: 'Keep artifact history reachable.', action: 'Escalate', reason: 'Review-loop budget exhausted.' },
          { finding: 'Verify the three-round card.', action: 'Escalate', reason: 'Review-loop budget exhausted.' },
        ],
        startsNewRound: false,
        targetJobId: null,
        targetRunAttempt: null,
      },
    },
    {
      fileName: 'code-review-grade-2026-07-09T18-22-02Z.md',
      verdict: 'concerns',
      grade: 'C',
      summary: 'Round two found two remaining gaps.',
      model: 'claude-opus-4-8',
      cliType: 'claude',
      runAt: '2026-07-09T18:22:02Z',
      councilReaction: {
        createdAt: '2026-07-09T18:22:03Z',
        reviewFileName: 'code-review-grade-2026-07-09T18-22-02Z.md',
        grade: 'C',
        disposition: 'Reissue',
        summary: 'Fix two review findings in the next round.',
        assessments: [
          { finding: 'Show all findings.', action: 'FixNextRound', reason: 'Concrete review deficiency.' },
          { finding: 'Add browser evidence.', action: 'FixNextRound', reason: 'Concrete review deficiency.' },
        ],
        startsNewRound: true,
        targetJobId: JOB_ID,
        targetRunAttempt: 3,
      },
    },
    {
      fileName: 'code-review-grade-2026-07-09T17-22-02Z.md',
      verdict: 'concerns',
      grade: 'D',
      summary: 'Round one found an incomplete operator handoff.',
      model: 'claude-opus-4-8',
      cliType: 'claude',
      runAt: '2026-07-09T17:22:02Z',
      councilReaction: {
        createdAt: '2026-07-09T17:22:03Z',
        reviewFileName: 'code-review-grade-2026-07-09T17-22-02Z.md',
        grade: 'D',
        disposition: 'Reissue',
        summary: 'Fix one review finding in the next round.',
        assessments: [
          { finding: 'Replace raw Markdown in the banner.', action: 'FixNextRound', reason: 'Concrete review deficiency.' },
        ],
        startsNewRound: true,
        targetJobId: JOB_ID,
        targetRunAttempt: 2,
      },
    },
  ],
};

const GRADE_DOCUMENTS = new Map(CODE_REVIEW_LIST.entries.map((entry, index) => [
  entry.fileName,
  [
    '---',
    `grade: ${entry.grade}`,
    `summary: ${entry.summary}`,
    '---',
    '',
    `# Grade document body round ${3 - index}`,
    '',
    index === 0 ? LONG_COUNCIL_CONTEXT : entry.summary,
    '',
    'Complete review evidence remains readable to the final sentence.',
  ].join('\n'),
]));

const TIMELINE = [
  {
    ts: '2026-07-09T18:20:00Z',
    kind: 'quality_loop_reopened',
    actor: 'quality-loop',
    summary: 'Reopened after build/test gate.',
    details: { cause: 'build/test gate failed', reason: 'npm test exit 1' },
  },
  {
    ts: '2026-07-09T19:10:00Z',
    kind: 'quality_loop_reopened',
    actor: 'quality-loop',
    summary: 'Reopened after verification findings.',
    details: { cause: 'verification gate', reason: 'bundle budget and apply_patch stderr' },
  },
  {
    ts: '2026-07-09T19:45:00Z',
    kind: 'orchestrator_escalated',
    actor: 'orchestrator',
    summary: 'escalated',
    details: {
      reason: `Completion gate found unfinished work in the previous run.\n\n${LONG_COUNCIL_CONTEXT}`,
      cause: 'completion-gate',
      attempt: '3',
      maxAttempts: '3',
    },
  },
];

async function installRoutes(page: Page, state: string, emptyContext = false): Promise<void> {
  const info = buildInfo(state, emptyContext);
  const detail = buildDetail(state, emptyContext);
  const grouped = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
    humanReview: state === '5-human-review' ? [info] : [],
    escalated: state === '5e-escalated' ? [info] : [],
    completed: [], archive: [],
  };

  // Catch-all first (lowest priority); specific routes registered later win.
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));

  // Shell boot dependencies.
  await page.route('**/api/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }));
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
  await page.route(/\/api\/runner\/status(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-07-09T20:00:00Z', snapshots: [] }) }));
  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) }));

  // Detail (broad) — must be registered before the narrower sub-routes below.
  await page.route(new RegExp(`/api/tasks/${JOB_ID}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }));

  // The Overview tab polls `/api/tasks/{id}/pipeline`; answer the real shape
  // (`null` = no pipeline yet) so no shell-error overlay floats over the panel.
  await page.route(/\/api\/tasks\/[^/]+\/pipeline(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));

  // Narrow sub-routes win over the detail route (registered later).
  await page.route('**/code-review/list**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(emptyContext ? { entries: [] } : CODE_REVIEW_LIST),
    }));
  await page.route('**/files/orchestrator-follow-up.md**', (route) =>
    route.fulfill(emptyContext
      ? { status: 404, contentType: 'text/plain', body: '' }
      : { status: 200, contentType: 'text/plain', body: FOLLOW_UP }));
  await page.route(/\/files\/code-review-grade-[^/?]+\.md(\?|$)/, (route) => {
    const fileName = decodeURIComponent(new URL(route.request().url()).pathname.split('/').at(-1) ?? '');
    return route.fulfill({
      status: 200,
      contentType: 'text/plain',
      body: GRADE_DOCUMENTS.get(fileName) ?? '# Missing grade fixture',
    });
  });
  await page.route('**/timeline**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(TIMELINE) }));
}

/**
 * Dismiss any transient "Unexpected application error" overlay before capturing
 * (an un-mocked shell poll can raise one and it would bleed into the shot).
 */
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

async function openDetail(page: Page, state: string, emptyContext = false): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 960 });
  await installRoutes(page, state, emptyContext);
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await expect(page.getByTestId('escalation-summary')).toBeVisible({ timeout: 20_000 });
}

type ThemeShots = Record<'dark' | 'light', Buffer>;

async function shootBothThemes(page: Page, testInfo: TestInfo, baseName: string): Promise<ThemeShots> {
  const panel = page.getByTestId('escalation-summary');
  const out: Partial<ThemeShots> = {};
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await page.waitForTimeout(200);
    await dismissAppErrorDialog(page);
    await panel.scrollIntoViewIfNeeded();
    const shot = await panel.screenshot();
    const name = `${baseName}-${theme}--mocked.png`;
    await saveShot(testInfo, name, shot);
    writeFileSync(path.join(SHOTS_DIR, name), shot);
    out[theme] = shot;
  }
  return out as ThemeShots;
}

test.describe('Escalation summary panel — collapsible + compact', () => {
  test.beforeEach(() => test.setTimeout(90_000));

  test('keeps the MKT-20 three-round essence bounded and every artifact readable', async ({ page }, testInfo) => {
    await openDetail(page, '5e-escalated');
    await setTheme(page, 'dark');
    await dismissAppErrorDialog(page);

    const panel = page.getByTestId('escalation-summary');
    const essence = page.getByTestId('escalation-essence');
    await expect(essence).toContainText(
      '3 review rounds (local) · latest 09.07. 19:22 pass · build and tests not proven · in develop',
    );
    await expect(essence).not.toContainText('Council finding 1');
    await expect(essence).not.toContainText('###');

    await expect(page.locator('[data-testid="escalation-gate-items"] li')).toHaveCount(4);
    await expect(page.getByTestId('escalation-gate-source')).toContainText('Council reaction');

    const council = page.getByTestId('escalation-council-follow-up');
    await council.locator('summary').first().click();
    const councilContent = page.getByTestId('escalation-council-follow-up-content');
    await expect(councilContent).toContainText('Council finding 18');
    await expect(councilContent.getByText('orchestrator-follow-up.md')).toBeVisible();
    await expect(page.getByTestId('escalation-follow-up-document')).toContainText('Council finding 18');
    const councilScroll = await councilContent.evaluate((element) => ({
      clientHeight: element.clientHeight,
      scrollHeight: element.scrollHeight,
      overflowY: getComputedStyle(element).overflowY,
    }));
    expect(councilScroll.overflowY).toBe('auto');
    expect(councilScroll.scrollHeight).toBeGreaterThan(councilScroll.clientHeight);
    await council.locator('summary').first().click();

    const documents = page.getByTestId('escalation-grade-documents');
    await documents.locator('summary').first().click();
    const roundThree = page.getByTestId('escalation-grade-round-3');
    await roundThree.locator('summary').click();
    await expect(roundThree).toContainText('Grade document body round 3');
    await expect(roundThree).toContainText('Complete review evidence remains readable to the final sentence.');
    await expect(roundThree.getByTestId('file-source-history-toggle')).toBeVisible();
    const gradeScroll = await page.getByTestId('escalation-grade-body-3').evaluate((element) => ({
      clientHeight: element.clientHeight,
      scrollHeight: element.scrollHeight,
      overflowY: getComputedStyle(element).overflowY,
    }));
    expect(gradeScroll.overflowY).toBe('auto');
    expect(gradeScroll.scrollHeight).toBeGreaterThan(gradeScroll.clientHeight);
    await documents.locator('summary').first().click();
    await council.locator('summary').first().click();

    await dismissAppErrorDialog(page);
    await shootBothThemes(page, testInfo, 'escalation-mkt20-after');

    const panelHeight = await panel.evaluate((element) => element.getBoundingClientRect().height);
    expect(panelHeight).toBeLessThan(900);
  });

  test('collapses by default off the acute lane, expands on click, carries a one-line essence', async ({ page }, testInfo) => {
    // Fixture is the AGT-1994 shape: escalate verdict parked in 5-human-review.
    await openDetail(page, '5-human-review');

    const panel = page.getByTestId('escalation-summary');
    const toggle = page.getByTestId('escalation-toggle');

    // 1. Default CLOSED off the acute lane; body hidden, header still present.
    await expect(page.getByTestId('escalation-body')).toHaveCount(0);
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');

    // 2. The header carries one bounded line from structured fields.
    await expect(page.getByTestId('escalation-essence')).toContainText(
      '3 review rounds (local) · latest 09.07. 19:22 pass · build and tests not proven · in develop',
    );
    await expect(page.getByTestId('escalation-essence')).not.toContainText('Council finding 1');
    const mergeSegments = page.getByTestId('escalation-merge-segment');
    await expect(mergeSegments).toHaveCount(1);
    await expect(mergeSegments).toHaveText('main ✓ merged');
    await expect(mergeSegments).toHaveAttribute('data-state', 'merged');
    // Recommendation stays on the header.
    await expect(page.getByTestId('escalation-recommendation')).toHaveText('Needs decision');
    await expect(panel.getByTestId('escalation-action-reissue-escalated')).toHaveCount(0);

    // 3. The section is fully borderless. Separation comes from its quiet wash.
    const borders = await panel.evaluate((el) => {
      const s = getComputedStyle(el);
      return {
        left: s.borderLeftWidth,
        right: s.borderRightWidth,
        top: s.borderTopWidth,
        bottom: s.borderBottomWidth,
      };
    });
    expect(borders.left).toBe('0px');
    expect(borders.right).toBe('0px');
    expect(borders.top).toBe('1px');
    expect(borders.bottom).toBe(borders.top);

    const geometry = await panel.evaluate((el) => {
      const panelRect = el.getBoundingClientRect();
      const workspaceRect = el.closest('.workspace__main--studio')?.getBoundingClientRect();
      return workspaceRect ? {
        leftDelta: Math.abs(panelRect.left - workspaceRect.left),
        rightDelta: Math.abs(panelRect.right - workspaceRect.right),
      } : null;
    });
    expect(geometry).not.toBeNull();
    expect(geometry!.leftDelta).toBeLessThanOrEqual(1);
    expect(geometry!.rightDelta).toBeLessThanOrEqual(1);

    await dismissAppErrorDialog(page);
    await shootBothThemes(page, testInfo, 'agt-2519-branch-chip-wide');
    await page.setViewportSize({ width: 720, height: 960 });
    await shootBothThemes(page, testInfo, 'agt-2519-branch-chip-narrow');
    await page.setViewportSize({ width: 1440, height: 960 });
    const afterShots = await shootBothThemes(page, testInfo, 'escalation-collapsed');

    // 4. Clicking the header row expands the panel and reveals the details.
    await setTheme(page, 'dark');
    await toggle.click();
    await expect(page.getByTestId('escalation-body')).toBeVisible();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');

    // Typed council findings and all grade artifacts are available as details.
    await expect(page.locator('[data-testid="escalation-gate-items"] li')).toHaveCount(4);
    await expect(page.getByTestId('escalation-gate-source')).toContainText('Council reaction');
    await expect(page.getByTestId('escalation-gate-count')).toContainText('4 open');
    await expect(page.getByTestId('escalation-grade-documents')).toContainText('Grade documents');
    // Delivery context: deduped file count across both commits.
    await expect(page.getByTestId('escalation-delivery-counts')).toContainText('2 commits');
    await expect(page.getByTestId('escalation-delivery-counts')).toContainText('4 files');
    // Raw timeline prose stays out of both the header and the expanded summary.
    await expect(page.getByTestId('escalation-reason')).toHaveCount(0);
    const reissues = page.getByTestId('escalation-reissues');
    await expect(reissues).toBeVisible();
    await reissues.locator('summary').click();
    await expect(page.getByTestId('escalation-reissue-1')).toContainText('npm test exit 1');
    await expect(page.getByTestId('escalation-reissue-2')).toContainText('bundle budget and apply_patch stderr');
    // The escalation section no longer contributes a second merge signal.
    await expect(page.getByTestId('escalation-essence-merge')).toHaveCount(1);

    await dismissAppErrorDialog(page);
    await shootBothThemes(page, testInfo, 'escalation-expanded');

    // 5. Reconstruct the round-1 look (always-open, unbounded, left severity
    //    stripe, full title) by mutating the real panel in place, and shoot it
    //    as the "before". Same component styles, so it is a faithful mock-up of
    //    what the operator saw after AGT-2019 — not a live capture.
    await mutateToRound1(page);
    const beforeShots = await shootBothThemes(page, testInfo, 'escalation-before-round1');

    // 6. Stitch a before/after composite on an isolated page (no app chrome to
    //    bleed through): round-1 giant beside the round-2 compact collapsed head.
    await captureComposite(page, testInfo, beforeShots, afterShots);
  });

  test('defaults open on the acute 5e-escalated lane', async ({ page }, testInfo) => {
    await openDetail(page, '5e-escalated');
    // Acute lane: the operator is here to act, so the panel opens by default.
    await expect(page.getByTestId('escalation-body')).toBeVisible();
    await expect(page.getByTestId('escalation-toggle')).toHaveAttribute('aria-expanded', 'true');
    const panel = page.getByTestId('escalation-summary');
    await expect(panel.getByTestId('escalation-action-reissue-escalated')).toHaveText('Continue (reissue)');
    await expect(panel.getByTestId('escalation-action-accept-escalated')).toHaveText('Accept as-is');
    await expect(panel.getByTestId('escalation-action-discard-escalated')).toHaveText('Abort');
    const recommendation = panel.getByTestId('escalation-recommendation');
    await expect(recommendation).toHaveText('Needs decision');
    await expect(recommendation).toHaveAttribute('data-label-kind', 'status');

    const decisionContract = await panel.evaluate((element) => {
      const read = (testId: string) => {
        const node = element.querySelector<HTMLElement>(`[data-testid="${testId}"]`)!;
        const style = getComputedStyle(node);
        return {
          tag: node.tagName,
          height: node.getBoundingClientRect().height,
          radius: style.borderRadius,
          fontSize: style.fontSize,
          fontWeight: style.fontWeight,
          background: style.backgroundColor,
          borderTop: style.borderTopWidth,
          transform: style.textTransform,
        };
      };
      return {
        label: read('escalation-recommendation'),
        primary: read('escalation-action-reissue-escalated'),
        secondary: read('escalation-action-accept-escalated'),
        danger: read('escalation-action-discard-escalated'),
      };
    });
    expect(decisionContract.label).toMatchObject({
      tag: 'SPAN',
      borderTop: '0px',
      transform: 'none',
    });
    expect(decisionContract.label.background).toBe('rgba(0, 0, 0, 0)');
    expect(decisionContract.primary.height).toBe(decisionContract.secondary.height);
    expect(decisionContract.primary.height).toBe(decisionContract.danger.height);
    expect(decisionContract.primary.radius).toBe(decisionContract.secondary.radius);
    expect(decisionContract.primary.radius).toBe(decisionContract.danger.radius);
    expect(decisionContract.primary.fontSize).toBe(decisionContract.secondary.fontSize);
    expect(decisionContract.primary.fontWeight).toBe(decisionContract.secondary.fontWeight);
    await dismissAppErrorDialog(page);
    await shootBothThemes(page, testInfo, 'escalation-5e-open');
  });

  test('collapses three empty context columns into one compact row', async ({ page }, testInfo) => {
    await openDetail(page, '5e-escalated', true);
    const panel = page.getByTestId('escalation-summary');

    await expect(page.getByTestId('escalation-essence')).toContainText(
      '0 review rounds · Reissue budget exhausted',
    );
    await expect(page.getByTestId('escalation-context-empty')).toHaveText(
      'No structured findings, review artifacts, or delivery context were recorded.',
    );
    await expect(page.getByTestId('escalation-gate-items')).toHaveCount(0);
    await expect(page.getByTestId('escalation-grade-documents')).toHaveCount(0);
    await expect(page.getByTestId('escalation-delivery')).toHaveCount(0);
    await expect(panel.getByTestId('escalation-action-reissue-escalated')).toBeEnabled();

    const height = await panel.evaluate((element) => element.getBoundingClientRect().height);
    expect(height).toBeLessThan(300);
    await dismissAppErrorDialog(page);
    await shootBothThemes(page, testInfo, 'escalation-empty-context');
  });
});

/**
 * Mutate the real (expanded) panel in place back to the round-1 presentation:
 * always-open, unbounded height, the left severity stripe the operator hard-rule
 * now bans, and the full title with no compact essence line. Reuses the live
 * component styles, so the element shot is a faithful mock-up of the AGT-2019
 * look for the before/after comparison.
 */
async function mutateToRound1(page: Page): Promise<void> {
  await page.evaluate(() => {
    const panel = document.querySelector('[data-testid="escalation-summary"]') as HTMLElement | null;
    if (!panel) return;
    panel.style.borderLeft = '3px solid var(--severity-high)';
    panel.querySelector('.escalation__chevron')?.remove();
    panel.querySelector('.escalation__essence')?.remove();
    const title = panel.querySelector('.escalation__title');
    if (title) title.textContent = 'Escalation — operator decision needed';
    const body = panel.querySelector('[data-testid="escalation-body"]') as HTMLElement | null;
    if (body) body.style.maxHeight = 'none';
  });
}

/**
 * Stitch a before/after composite on an isolated blank page (no live app chrome
 * to bleed into the shot): the round-1 giant beside the round-2 compact collapsed
 * header. The two source shots are already rendered per theme, so we only lay the
 * PNGs out side by side on a theme-matched backdrop.
 */
async function captureComposite(
  page: Page,
  testInfo: TestInfo,
  before: ThemeShots,
  after: ThemeShots,
): Promise<void> {
  const backdrop = { dark: '#1e1e2e', light: '#eff1f5' } as const;
  const caption = { dark: '#a6adc8', light: '#5c5f77' } as const;
  for (const theme of ['dark', 'light'] as const) {
    const b64 = (buf: Buffer) => buf.toString('base64');
    await page.setContent(
      `<!doctype html><html><body style="margin:0;background:${backdrop[theme]};padding:24px;`
      + `display:flex;gap:24px;align-items:flex-start;font-family:system-ui,sans-serif">`
      + `<figure style="margin:0;display:flex;flex-direction:column;gap:8px">`
      + `<figcaption style="font:600 12px/1.4 system-ui;letter-spacing:.04em;text-transform:uppercase;color:${caption[theme]}">Before · round 1 (AGT-2019)</figcaption>`
      + `<img alt="before" style="display:block;box-shadow:0 0 0 1px rgba(128,128,128,.25)" src="data:image/png;base64,${b64(before[theme])}"></figure>`
      + `<figure style="margin:0;display:flex;flex-direction:column;gap:8px">`
      + `<figcaption style="font:600 12px/1.4 system-ui;letter-spacing:.04em;text-transform:uppercase;color:${caption[theme]}">After · round 2 (AGT-2060)</figcaption>`
      + `<img alt="after" style="display:block;box-shadow:0 0 0 1px rgba(128,128,128,.25)" src="data:image/png;base64,${b64(after[theme])}"></figure>`
      + `</body></html>`,
    );
    await page.waitForTimeout(100);
    const shot = await page.screenshot({ fullPage: true });
    const name = `before-after-${theme}--composite-mocked.png`;
    await saveShot(testInfo, name, shot);
    writeFileSync(path.join(SHOTS_DIR, name), shot);
  }
}
