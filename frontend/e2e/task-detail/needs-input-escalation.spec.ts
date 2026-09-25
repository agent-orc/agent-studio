import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { contrastRatio } from '../helpers/contrast';
import { dismissDevErrorDialog, sampleColours, setTheme } from '../helpers/theme';

const PROJECT = 'NeedsInput fixture';
const WATCH_PATH = '/fixtures/needs-input';
const JOB_ID = 'AGT-2736';
const RESULTS = process.env.JOB_RESULTS_DIR ?? 'test-results';
const QUESTION = `Which deployment strategy should I implement?

- Option A: managed connector. Recommended for simpler operations.
- Option B: direct LAN deployment. Requires customer network access.

Reply with A or B to continue.`;

/**
 * The dev-only error dialog re-raises itself under `ng serve` and can repaint
 * over a panel between a dismissal and a capture. Hiding it for the duration of
 * the screenshot is deterministic where dismissing is not; it never appears in
 * the stable/prod build this evidence stands in for.
 */
const HIDE_DEV_DIALOG = 'app-error-dialog { display: none !important; }';

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**:5039/update/status', route => json(route, { isRunning: false, behindBy: 0 }));
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-09-11T14:44:00.000Z', ttlSeconds: 600, snapshots: [],
  }));
  await page.route('**/api/cli/usage**', route => json(route, { sessions: [] }));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: false, user: null,
  }));
  await page.route('**/api/tasks/grouped**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    codeNotComplete: [], review: [], autoReview: [], humanReview: [], escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/clients', route => json(route, [
    { id: 'local-default', displayName: 'Local', kind: 'agent-instance' },
  ]));
  await page.route(/\/api\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route(new RegExp(`/api/tasks/${JOB_ID}(\\?|$)`), route => json(route, {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      key: JOB_ID,
      title: 'Choose connector or LAN deployment',
      state: '5e-escalated',
      order: 1,
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5.6-codex',
      modelExplicit: true,
      thinkingLevel: 'xhigh',
      thinkingLevelExplicit: true,
      createdAt: '2026-09-11T14:00:00.000Z',
      lastActivity: '2026-09-11T14:44:00.000Z',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/${JOB_ID}`,
      ownerClientId: 'local-default',
      commits: [],
      orchestratorVerdict: 'escalate',
      needsInput: {
        message: QUESTION,
        firstLine: 'Which deployment strategy should I implement?',
        runAttemptId: 'run_4e53d6d6',
        salvageBranch: 'runner/agent-runner-01/AGT-2736',
        artifactPath: 'results/needs-input.md',
      },
      parkedBlocker: {
        blockerType: 'agent-needs-input',
        conditionKind: 'manual',
        conditionDescription:
          'Only a person can clear this park; no automatic precondition is recorded.',
        lane: '5e-escalated',
        parkedAt: '2026-09-11T14:44:00.000Z',
        parkedForSeconds: 3 * 24 * 60 * 60,
        reason:
          '[agent-needs-input] The remote agent requires operator input: choose-connector-vs-lan-deployment-strategy',
        recallStatus: 'blocked',
        lastEvaluatedAt: '2026-09-14T11:50:00.000Z',
        detail: 'Only a person can clear this park.',
        evaluationAgeSeconds: 600,
        evaluationStale: false,
        requiresDecisionCard: true,
        needsInputFile: 'results/needs-input.md',
        decision: {
          questionId: 'choose-connector-vs-lan-deployment-strategy',
          question: 'Which deployment strategy should I implement?',
          options: [
            { id: 'a', label: 'Managed connector.', consequences: 'Recommended for simpler operations.', recommended: true },
            { id: 'b', label: 'Direct LAN deployment.', consequences: 'Requires customer network access.', recommended: false },
          ],
          documents: ['docs/operations/setup/docker-compose-connector-gap.md'],
          decisionCardKey: null,
        },
      },
    },
    promptMarkdown: 'Choose a deployment design.',
    statusMarkdown: '',
    log: [],
    promptHistory: [{
      index: 1,
      fileName: 'prompt-1.md',
      markdown: 'Compare the connector and LAN options, then park for the operator decision.',
      writtenAt: '2026-09-11T14:40:00.000Z',
    }],
    reviewEvidence: [],
  }));
  await page.route(new RegExp(`/api/tasks/${JOB_ID}/runs(\\?|$)`), route => json(route, {
    runCount: 2,
    firstStartedAt: '2026-09-11T14:01:00.000Z',
    lastActivityAt: '2026-09-11T14:44:00.000Z',
    hasActiveRun: false,
    runs: [
      {
        index: 1, intent: 'start', startedAt: '2026-09-11T14:01:00.000Z', endedAt: '2026-09-11T14:30:00.000Z',
        status: 'completed', cli: 'codex', model: 'gpt-5.6-codex', thinkingLevel: 'xhigh', exitCode: 0,
        durationSeconds: 1740, inputSessionId: null, capturedSessionId: 'session-1', resumed: false,
        reason: null, userFollowup: null, lineStart: 1, lineEnd: 2, headShaBefore: null, headShaAfter: null, contextRef: null,
      },
      {
        index: 2, intent: 'continue', startedAt: '2026-09-11T14:40:00.000Z', endedAt: '2026-09-11T14:44:00.000Z',
        status: 'completed', cli: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high', exitCode: 0,
        durationSeconds: 240, inputSessionId: 'session-1', capturedSessionId: 'session-1', resumed: true,
        reason: null, userFollowup: null, lineStart: 3, lineEnd: 284, headShaBefore: null, headShaAfter: null, contextRef: null,
      },
    ],
    promptEntries: [{
      index: 2, runIndex: 2, intent: 'continue', at: '2026-09-11T14:40:00.000Z', label: 'Prompt #2',
      fileName: 'prompt-1.md', promptTokenSource: 'prompt-history',
      promptPreview: 'Compare the connector and LAN options, then park for the operator decision.',
      promptTokenEstimate: 14, contextTokenEstimate: 200, contextRef: null, contextSnapshot: null,
    }],
  }));
  const transcript = [
    { timestamp: '2026-09-11T14:01:00.000Z', stream: 'system', text: '[taskboard] Started codex CLI' },
    { timestamp: '2026-09-11T14:30:00.000Z', stream: 'system', text: '[taskboard] codex CLI exited' },
    ...Array.from({ length: 278 }, (_, index) => ({
      timestamp: `2026-09-11T14:4${index % 4}:00.000Z`,
      stream: 'stdout',
      text: index % 7 === 0 ? `* Read deployment-option-${index}.md` : `Compared deployment constraint ${index + 1}.`,
    })),
    { timestamp: '2026-09-11T14:43:58.000Z', stream: 'stdout', text: 'I need the operator to choose between the connector and LAN deployment.' },
    { timestamp: '2026-09-11T14:44:00.000Z', stream: 'system', text: '[taskboard] codex CLI exited' },
  ];
  await page.route(new RegExp(`/api/tasks/${JOB_ID}/output(\\?|$)`), route => json(route, transcript));
}

test('NeedsInput escalation shows the full question, options, and answer field', async ({ page }, testInfo) => {
  await installRoutes(page);
  await page.goto(`/?job=${JOB_ID}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await page.addStyleTag({ content: HIDE_DEV_DIALOG });
  await dismissDevErrorDialog(page);

  const question = page.getByTestId('needs-input-question');
  await expect(question).toBeVisible();
  await expect(page.getByTestId('needs-input-message')).toContainText('Option A: managed connector');
  await expect(page.getByTestId('needs-input-message')).toContainText('Option B: direct LAN deployment');
  await expect(question).toContainText('runner/agent-runner-01/AGT-2736');

  mkdirSync(RESULTS, { recursive: true });
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    const path = join(RESULTS, `needs-input-escalation-${theme}--mocked.png`);
    await question.screenshot({ path, style: HIDE_DEV_DIALOG });
    await testInfo.attach(`NeedsInput escalation ${theme}`, { path, contentType: 'image/png' });
  }

  await page.getByTestId('needs-input-answer').pressSequentially(
    'Choose A. Keep a LAN fallback for isolated networks.',
  );
  await expect(page.getByTestId('needs-input-answer')).toHaveValue(
    'Choose A. Keep a LAN fallback for isolated networks.',
  );
});

/**
 * AGT-2816 acceptance. AGT-2736 opened after this lands shows, without opening a
 * single file: parked since 2026-09-11, an operator decision, the question, the
 * options, and the link to the gap document - and the park renders above the
 * derived escalation headline, because the park is the authoritative statement.
 */
test('a parked card says why it is parked, above the derived escalation headline', async ({ page }, testInfo) => {
  await page.addInitScript(([key, taskId]) => {
    localStorage.setItem(key, JSON.stringify({ [taskId]: true }));
  }, ['taskboard.parkedSession.expanded.v1', JOB_ID]);
  await installRoutes(page);
  await page.goto(`/?job=${JOB_ID}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await page.addStyleTag({ content: HIDE_DEV_DIALOG });
  await dismissDevErrorDialog(page);

  const park = page.getByTestId('parked-blocker');
  await expect(park).toBeVisible();
  await expect(park).toHaveAttribute('data-decision', 'true');
  await expect(page.getByTestId('parked-blocker-type')).toHaveText('Agent needs input');
  await expect(page.getByTestId('parked-blocker-since')).toContainText('3 days');
  await expect(page.getByTestId('parked-blocker-since')).toContainText('2026-09-11');
  await expect(page.getByTestId('parked-blocker-question'))
    .toHaveText('Which deployment strategy should I implement?');
  await expect(page.getByTestId('parked-blocker-options').locator('li')).toHaveCount(2);
  await expect(page.getByTestId('parked-blocker-document'))
    .toHaveText('docs/operations/setup/docker-compose-connector-gap.md');
  await expect(page.getByTestId('parked-blocker-condition')).toContainText('Only a person can clear');

  // Never "nothing open" while the park stands.
  const openItems = page.getByTestId('parked-blocker-open-items').locator('li');
  await expect(openItems.first()).toBeVisible();
  for (const item of await openItems.allTextContents()) {
    expect(item.toLowerCase()).not.toMatch(/\bnone\b/);
  }

  // The park outranks the derived escalation headline, so it renders above it.
  const parkTop = (await park.boundingBox())!.y;
  const escalationTop = (await page.getByTestId('escalation-summary').boundingBox())!.y;
  expect(parkTop).toBeLessThan(escalationTop);

  await expect(page.getByTestId('parked-session-context')).toBeVisible();
  await expect(page.getByTestId('parked-session-model')).toContainText('gpt-6-sol');
  await expect(page.getByTestId('parked-session-model')).toContainText('Pinned model');
  await expect(page.getByTestId('parked-session-prompt')).toContainText('Compare the connector and LAN options');
  await expect(page.getByTestId('parked-session-transcript')).toBeVisible();
  await expect(page.getByTestId('parked-session-transcript')).toContainText('I need the operator to choose');
  await expect(page.getByTestId('parked-session-transcript').getByTestId('convo-tools-pill').first()).toBeVisible();
  await expect(page.getByTestId('parked-session-show-earlier')).toBeVisible();
  await expect(page.getByTestId('parked-session-full-log')).toHaveAttribute('href', /cli-output\.log/);
  await page.setViewportSize({ width: 1280, height: 1400 });

  mkdirSync(RESULTS, { recursive: true });
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    for (const selector of [
      '[data-testid="parked-blocker-type"]',
      '[data-testid="parked-blocker-since"]',
      '[data-testid="parked-blocker-question"]',
      '[data-testid="parked-blocker-fact-label"]',
      '[data-testid="parked-blocker-condition"]',
    ]) {
      const { color, bg } = await sampleColours(page, selector);
      expect(contrastRatio(color, bg), `${theme} ${selector}: ${color} on ${bg}`)
        .toBeGreaterThanOrEqual(4.5);
    }
    const path = join(RESULTS, `parked-session-expanded-${theme}--mocked.png`);
    await park.screenshot({ path, style: HIDE_DEV_DIALOG });
    await testInfo.attach(`Parked session expanded ${theme}`, { path, contentType: 'image/png' });
  }
});
