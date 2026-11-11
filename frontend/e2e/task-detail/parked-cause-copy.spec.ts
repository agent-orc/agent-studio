import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3586 (walkthrough AGT-3561, finding F11). DEMO-6 was parked by the
 * acceptance audit, three months after its last run. The detail view said
 * "The parking run stated no question", labelled the park "Operator decision",
 * joined the old run end with today's reason ("Latest attempt ended ...:
 * missing-delivery: ... rerun the gate"), and repeated that reason in the park
 * panel and the escalation header, although no button reruns a gate.
 *
 * The copy now follows the recorded cause: the audit is named, the reason is
 * one sentence shown once, the old run is not presented as the cause, and the
 * next step names buttons the Escalated lane offers.
 */
const PROJECT = 'Demo App';
const WATCH_PATH = '/fixtures/demo-app';
const JOB_ID = 'DEMO-6';
const RESULTS = process.env.JOB_RESULTS_DIR ?? 'test-results';
const NOW = '2026-11-09T08:24:00.000Z';
const PARKED_AT = '2026-11-09T08:23:00.000Z';
const HIDE_DEV_DIALOG = 'app-error-dialog { display: none !important; }';

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  await page.clock.setFixedTime(new Date(NOW));
  await page.route('**:5039/update/status', route => json(route, { isRunning: false, behindBy: 0 }));
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/cli/quota**', route => json(route, { at: NOW, ttlSeconds: 600, snapshots: [] }));
  await page.route('**/api/cli/usage**', route => json(route, { sessions: [] }));
  await page.route('**/api/usage/cockpit**', route => json(route, { workspaceId: null, clis: [], cost: null }));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: false, user: null,
  }));
  await page.route('**/api/v1/studio/board**', route => json(route, {
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
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${JOB_ID}(\\?|$)`), route => json(route, {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      key: JOB_ID,
      title: 'Correct calendar timezone handling',
      state: '5e-escalated',
      order: 1,
      agent: 'codex',
      cliType: 'codex',
      createdAt: '2026-08-09T11:00:00.000Z',
      lastActivity: PARKED_AT,
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/${JOB_ID}`,
      ownerClientId: 'local-default',
      commits: [],
      orchestratorVerdict: 'escalate',
      parkedBlocker: {
        blockerType: 'operator-decision',
        conditionKind: 'manual',
        conditionDescription: 'Only a person can clear this park; no automatic precondition is recorded.',
        lane: '5e-escalated',
        parkedAt: PARKED_AT,
        // The reason the acceptance audit wrote before AGT-3586.
        reason: 'missing-delivery: Recover the delivery, resolve the integration failure and rerun the gate.',
        cause: 'acceptance-integration-failed',
        causeDetail: 'missing-delivery',
        recallStatus: 'blocked',
        lastEvaluatedAt: null,
        detail: 'No recall sweep has evaluated this blocker yet.',
        evaluationAgeSeconds: null,
        evaluationStale: true,
        requiresDecisionCard: true,
        needsInputFile: null,
        decision: null,
      },
    },
    promptMarkdown: 'Fix the calendar timezone handling.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    reviewEvidence: [],
  }));
  await page.route(/\/api\/tasks\/[^/?]+\/pipeline(?:\?.*)?$/, route => json(route, {
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null, cost: null, config: {},
  }));
  // The run three months before the park; it did not cause it.
  await page.route(/\/api\/tasks\/[^/?]+\/runs(?:\?.*)?$/, route => json(route, {
    runCount: 1, firstStartedAt: '2026-08-09T12:00:00.000Z', lastActivityAt: '2026-08-09T12:20:00.000Z',
    hasActiveRun: false,
    runs: [{
      index: 1, intent: 'start', startedAt: '2026-08-09T12:00:00.000Z', endedAt: '2026-08-09T12:20:00.000Z',
      status: 'completed', cli: 'codex', model: 'gpt-5.6-codex', thinkingLevel: 'high', exitCode: 0,
      durationSeconds: 1200, inputSessionId: null, capturedSessionId: 'session-1', resumed: false,
      reason: null, userFollowup: null, lineStart: null, lineEnd: null, headShaBefore: null, headShaAfter: null,
      contextRef: null,
    }],
    promptEntries: [], refinements: [], runnerEvents: [], reviewAttemptEpoch: 0, reviewAttemptCycles: [],
  }));
  await page.route(/\/api\/tasks\/[^/?]+\/session-events(?:\?.*)?$/, route => json(route, {
    events: [], sessionChain: [], currentSessionId: null,
  }));
  // The catch-all `[]` would hand the panel `[].entries`, a function.
  await page.route(/\/api\/tasks\/[^/?]+\/code-review\/list(?:\?.*)?$/, route => json(route, { entries: [] }));
  await page.route(/\/api\/tasks\/[^/?]+\/plan(?:\?.*)?$/, route => json(route, {
    hasPlan: false, source: null, snapshotCount: 0, activeItemId: null,
    softEstimateMedian: null, items: [], unassignedSubActions: [],
  }));
  await page.route(new RegExp(`/api/tasks/${JOB_ID}/timeline(\\?|$)`), route => json(route, [
    { ts: '2026-08-09T12:00:00.000Z', kind: 'agent_run_started', actor: 'agent', summary: 'Run started' },
    { ts: '2026-08-09T12:20:00.000Z', kind: 'agent_run_finished', actor: 'agent', summary: 'Run finished' },
    {
      ts: PARKED_AT, kind: 'lane_changed', actor: 'system', summary: '6-completed → 5e-escalated',
      details: {
        from: '6-completed', to: '5e-escalated',
        cause: 'acceptance-integration-failed', causeDetail: 'missing-delivery',
      },
    },
  ]));
}

test('an acceptance-audit park names the audit, states its reason once, and suggests an offered button', async ({ page }, testInfo) => {
  await installRoutes(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`/?job=${JOB_ID}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await page.addStyleTag({ content: HIDE_DEV_DIALOG });
  await dismissDevErrorDialog(page);

  const park = page.getByTestId('parked-blocker');
  await expect(park).toBeVisible();
  await expect(page.getByTestId('parked-blocker-type')).toHaveText('Acceptance audit');
  await expect(page.getByTestId('parked-blocker-statement')).toHaveText(
    'The acceptance audit parked this card. No delivery branch was found for this card, so its work cannot be integrated.',
  );
  await expect(page.getByTestId('parked-blocker-question-missing')).toHaveCount(0);
  await expect(page.getByTestId('parked-blocker-reason')).toHaveCount(0);
  await expect(page.getByTestId('parked-blocker-evaluation')).toHaveText('No sweep has evaluated this blocker yet.');

  // The suggestion names buttons the Escalated header really offers.
  const nextStep = page.getByTestId('parked-blocker-next-step');
  await expect(nextStep).toHaveText(
    'Use Continue (reissue) to build and deliver the work again, or Abort if the work is no longer needed.',
  );
  await expect(page.getByTestId('escalation-action-reissue-escalated')).toHaveText('Continue (reissue)');
  await expect(page.getByTestId('escalation-action-discard-escalated')).toHaveText('Abort');

  // The three-month-old run did not cause this park; the header says nothing about it.
  const essence = page.getByTestId('escalation-essence');
  await expect(essence).toBeVisible();
  await expect(essence).not.toContainText('Latest attempt');

  // One statement of the reason across both panels; no slug, no unoffered step.
  const panels = `${await park.textContent()} ${await page.getByTestId('escalation-summary').textContent()}`;
  expect(panels.split('No delivery branch was found')).toHaveLength(2);
  expect(panels).not.toContain('missing-delivery');
  expect(panels).not.toContain('rerun the gate');
  expect(panels).not.toContain('parking run');

  mkdirSync(RESULTS, { recursive: true });
  const escalation = page.getByTestId('escalation-summary');
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    for (const [name, locator] of [['park', park], ['escalation', escalation]] as const) {
      const path = join(RESULTS, `parked-cause-acceptance-audit-${name}-${theme}--mocked.png`);
      await locator.screenshot({ path, style: HIDE_DEV_DIALOG });
      await testInfo.attach(`Acceptance-audit ${name} ${theme}`, { path, contentType: 'image/png' });
    }
  }
});

test('Auto Review watchdog park links its receipt and retries the same delivery', async ({ page }) => {
  await installRoutes(page);
  let retryCalls = 0;
  let undoCalls = 0;
  await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/DEMO-6(\?|$)/, route => json(route, {
    info: {
      id: JOB_ID, taskKey: JOB_ID, key: JOB_ID, title: 'Review the delivered fix',
      state: '5e-escalated', order: 1, agent: 'codex', cliType: 'codex',
      createdAt: '2026-11-09T01:00:00Z', lastActivity: PARKED_AT,
      watchPath: WATCH_PATH, projectName: PROJECT, folderPath: `${WATCH_PATH}/${JOB_ID}`,
      commits: [], orchestratorVerdict: 'escalate',
      parkedBlocker: {
        blockerType: 'environmental', conditionKind: 'review-retry-available',
        conditionDescription: 'A completed immutable delivery is still current and can receive a fresh review attempt.',
        lane: '5e-escalated', parkedAt: PARKED_AT,
        reason: '[environmental] Auto Review had no active owner for 4 minutes. Last run completed at 2026-11-09 01:23 UTC. Last review ended as ExecutorRestarted at 2026-11-09 01:27 UTC. Infrastructure retry budget is exhausted.',
        cause: 'escalated', causeDetail: 'auto-review-stranded', recallStatus: 'recallable',
        lastEvaluatedAt: NOW, detail: 'The completed delivery is available for Retry review.',
        evaluationAgeSeconds: 0, evaluationStale: false, requiresDecisionCard: false,
        needsInputFile: null, decision: null,
      },
    },
    promptMarkdown: 'Review the delivered fix.', statusMarkdown: '', log: [],
    promptHistory: [], reviewEvidence: [],
  }));
  await page.route(/\/api\/tasks\/DEMO-6\/timeline(\?|$)/, route => json(route, [
    { ts: PARKED_AT, kind: 'lane_changed', actor: 'system', summary: 'Auto Review parked',
      details: { from: '4-auto-review', to: '5e-escalated', cause: 'escalated', causeDetail: 'auto-review-stranded' } },
  ]));
  await page.route(/\/api\/watcher\/interventions\?/, route => json(route, {
    count: 1, interventions: [{
      kind: 'auto-review-stranded', host: 'task-server', project: PROJECT,
      decidedAtUtc: PARKED_AT, affectedCards: [JOB_ID], headline: 'Auto Review parked',
      receipt: { id: 'watchdog-1', subject: `auto-review:${JOB_ID}`, action: 'Escalate',
        autonomy: 'A1', status: 'escalated', observed: 'No Auto Review owner.',
        done: 'Parked the card.', why: 'No running review owned the card.',
        before: '4-auto-review', after: '5e-escalated', undo: 'Undo restores Auto Review.',
        chain: [], undoable: true },
    }],
  }));
  await page.route(/\/api\/watcher\/interventions\/watchdog-1\/undo$/, route => {
    undoCalls++;
    return json(route, { receipt: { id: 'watchdog-1', undoable: false, status: 'undone' } });
  });
  await page.route(/\/api\/tasks\/DEMO-6\/review\/retry/, route => {
    retryCalls++;
    return json(route, { status: 'review-started' });
  });

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`/?job=${JOB_ID}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await page.addStyleTag({ content: HIDE_DEV_DIALOG });
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('parked-blocker-type')).toHaveText('Environmental · Auto Review watchdog');
  await expect(page.getByTestId('parked-blocker-statement')).toContainText('Parked by the Auto Review watchdog.');
  await expect(page.getByTestId('parked-blocker-statement')).toContainText('ExecutorRestarted');
  await expect(page.getByTestId('parked-blocker-condition')).toContainText('fresh review attempt');
  await expect(page.getByTestId('parked-watchdog-receipt-link')).toHaveAttribute('href', '#watchdog-receipt-watchdog-1');
  await expect(page.getByTestId('escalation-action-retry-review')).toBeVisible();
  await expect(page.getByTestId('escalation-essence')).not.toContainText('Latest attempt ended');
  await expect(page.getByTestId('escalation-essence')).not.toContainText('Human decision required');
  await expect(page.getByTestId('escalation-recommendation')).toContainText('Review retry available');
  await expect(page.getByTestId('decision-surface')).toHaveCount(0);

  mkdirSync(RESULTS, { recursive: true });
  for (const [theme, width] of [['light', 1280], ['dark', 1280], ['light', 390]] as const) {
    await page.setViewportSize({ width, height: 900 });
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    await page.waitForTimeout(500);
    await page.screenshot({ path: join(RESULTS, `auto-review-watchdog-${theme}-${width}--mocked.png`), fullPage: true });
  }
  await page.getByTestId('escalation-action-retry-review').click();
  await expect(page.getByTestId('review-retry-message')).toContainText('fresh review attempt');
  expect(retryCalls).toBe(1);
  await page.getByTestId('parked-watchdog-undo').click();
  await expect(page.getByTestId('parked-watchdog-undo')).toHaveCount(0);
  expect(undoCalls).toBe(1);
});
