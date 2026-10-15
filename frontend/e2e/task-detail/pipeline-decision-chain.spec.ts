/**
 * AGT-3015: the decisions between pipeline steps, and what deciding cost.
 *
 * The dev-backend fixture runs this checkout's backend against an isolated
 * task repository. The spec writes one finished card into it, with a
 * pipeline-execution.json shaped exactly as PipelineExecutionLog.RecordStep
 * writes it after this card (model, modelSource, tokens, costBasis, runs,
 * earlierRuns). The real backend then computes the card rollup, the transition
 * data and the project ledger; the browser proves the Overview decision chain
 * and the project usage view render them in both themes.
 *
 * Enum values are numeric on disk: StepKind Core=1, Aspect=2, Orchestrator=3;
 * PipelineStepStatus Passed=2, Skipped=4.
 */
import { expect, test } from '../fixtures/dev-backend';
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import * as path from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const resultsDir = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'decision-chain')
  : path.resolve('test-results', 'decision-chain');

const TASK_ID = 'AGT-3998';
const CORE = 1;
const ASPECT = 2;
const ORCHESTRATOR = 3;
const PASSED = 2;
const SKIPPED = 4;

// The isolated backend may need a cold compile on a busy review host before
// the browser part of this full-stack evidence test can begin.
test.setTimeout(720_000);

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function at(base: number, minutes: number): string {
  return new Date(base + minutes * 60_000).toISOString();
}

function row(base: number, partial: Record<string, unknown> & { start: number; end: number }) {
  const { start, end, ...rest } = partial;
  return {
    attempt: 1,
    status: PASSED,
    startedAt: at(base, start),
    completedAt: at(base, end),
    durationMs: Math.round((end - start) * 60_000),
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    runs: 1,
    ...rest,
  };
}

function writeCard(projectPath: string): void {
  const base = Date.now() - 2 * 60 * 60_000;
  const tasksRoot = path.join(projectPath, 'tasks');
  const folder = readdirSync(tasksRoot, { withFileTypes: true })
    .filter(bucket => bucket.isDirectory())
    .flatMap(bucket => readdirSync(path.join(tasksRoot, bucket.name), { withFileTypes: true })
      .filter(entry => entry.isDirectory())
      .map(entry => path.join(tasksRoot, bucket.name, entry.name)))
    .find(candidate => {
      const taskFile = path.join(candidate, 'task.json');
      if (!existsSync(taskFile)) return false;
      const task = JSON.parse(readFileSync(taskFile, 'utf8')) as { id?: string };
      return task.id === TASK_ID;
    });
  if (!folder) throw new Error(`Created task ${TASK_ID} has no folder`);
  writeFileSync(path.join(folder, 'code-review-grade-2026-10-04.md'), '# Quality grade B\n', 'utf8');
  const haiku = 'claude-haiku-4-5';
  writeFileSync(path.join(folder, 'pipeline-execution.json'), JSON.stringify({
    pipelineId: 'standard-task-pipeline',
    pipelineVersion: 1,
    jobId: TASK_ID,
    project: 'agent-studio-worktree',
    startedAt: at(base, 0),
    completedAt: at(base, 50),
    attempt: 1,
    previousAttempts: [],
    steps: [
      row(base, {
        stepId: 'core-agent-run', kind: CORE, start: 0, end: 20,
        model: 'claude-opus-4-8', thinkingLevel: 'high', modelSource: 'task',
        inputTokens: 400_000, outputTokens: 20_000,
        costBasis: 'model', modelPriced: true, estimatedCostUsd: 2.5,
      }),
      row(base, {
        stepId: 'post-orchestrator-review', kind: ORCHESTRATOR, start: 20.1, end: 20.1,
        verdict: 'pass', costBasis: 'deterministic', estimatedCostUsd: 0,
      }),
      row(base, {
        stepId: 'aspect-code-quality', kind: ASPECT, start: 20.2, end: 21,
        model: haiku, modelSource: 'runtime', inputTokens: 120_000, outputTokens: 4_000,
        verdict: 'pass', costBasis: 'model', modelPriced: true, estimatedCostUsd: 0.14,
      }),
      row(base, {
        stepId: 'post-code-review-grade', kind: ORCHESTRATOR, start: 21.5, end: 22.2,
        model: haiku, thinkingLevel: 'medium', modelSource: 'catalogue',
        inputTokens: 80_000, outputTokens: 3_000, verdict: 'B',
        evidenceRef: 'code-review-grade-2026-10-04.md',
        costBasis: 'model', modelPriced: true, estimatedCostUsd: 0.095,
      }),
      row(base, {
        stepId: 'post-orchestrator-decision', kind: ORCHESTRATOR, start: 45, end: 45.05,
        model: haiku, modelSource: 'config', inputTokens: 32_000, outputTokens: 1_000,
        verdict: 'accept', costBasis: 'model', modelPriced: true, estimatedCostUsd: 0.037,
        runs: 2,
        earlierRuns: [{
          status: PASSED, startedAt: at(base, 22.5), completedAt: at(base, 22.55), durationMs: 3000,
          model: haiku, modelSource: 'config', inputTokens: 30_000, outputTokens: 1_000,
          cacheReadTokens: 0, cacheCreationTokens: 0, costBasis: 'model', modelPriced: true,
          estimatedCostUsd: 0.035, verdict: 'reissue', reason: 'Code quality raised one concern.',
        }],
      }),
      row(base, {
        stepId: 'post-task-spawner', kind: ORCHESTRATOR, status: SKIPPED, start: 45.5, end: 46,
        model: 'unpriced-test-model', modelSource: 'runtime', inputTokens: 20_000, outputTokens: 2_000,
        verdict: 'not-relevant', costBasis: 'model', modelPriced: false,
      }),
    ],
  }), 'utf8');
}

test('decision chain and cost of deciding render on the card and in project usage', async ({ page, devBackend }) => {
  const pathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  expect(pathsResponse.ok).toBe(true);
  const project = (await pathsResponse.json() as Array<{ name: string; path: string }>)[0];
  expect(project).toBeTruthy();
  const create = await fetch(`${devBackend.baseUrl}/api/tasks`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'X-Client-Id': 'local-default' },
    body: JSON.stringify({
      id: TASK_ID,
      title: 'Measure the cost of deciding',
      watchPath: project.path,
      targetState: '5e-escalated',
      taskType: 'chore',
      mode: 'coding',
      cliType: 'codex',
      model: 'gpt-5.6-luna',
      thinkingLevel: 'medium',
      promptMarkdown: '# Measure the cost of deciding',
    }),
  });
  const createBody = await create.text();
  expect(create.ok, `Task create returned ${create.status}: ${createBody}`).toBe(true);
  expect((JSON.parse(createBody) as { id: string }).id).toBe(TASK_ID);
  writeCard(project.path);

  // The backend computes the card rollup over every attempt.
  const pipelineUrl = `${devBackend.baseUrl}/api/tasks/${TASK_ID}/pipeline?watchPath=${encodeURIComponent(project.path)}`;
  // The task index publishes filesystem changes asynchronously. Wait for the
  // fixture card to enter that index before asking for its pipeline.
  await expect.poll(async () => (await fetch(pipelineUrl)).status, { timeout: 30_000 }).toBe(200);
  const pipelineResponse = await fetch(pipelineUrl);
  expect(pipelineResponse.ok).toBe(true);
  const pipeline = await pipelineResponse.json() as {
    decisionCost: { deciding: { runs: number; unpricedTokens: number; pricedCostUsd: number }; agentRuns: { runs: number } };
  };
  expect(pipeline.decisionCost.deciding.runs).toBe(5);
  expect(pipeline.decisionCost.deciding.unpricedTokens).toBe(22_000);
  expect(pipeline.decisionCost.agentRuns.runs).toBe(1);

  mkdirSync(resultsDir, { recursive: true });
  await page.setViewportSize({ width: 1600, height: 1100 });
  const receiptId = 'odr_ui_1';
  const receiptRef = `/api/orchestration/decision-receipts/${receiptId}`;
  let operatorIntent: Record<string, unknown> | null = null;
  let stopSettled = false;
  let laterDecision = false;
  await page.route('**/api/orchestration/decision-receipts**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith('/operator-intent')) {
      if (route.request().method() === 'POST') {
        operatorIntent = { ...route.request().postDataJSON() as Record<string, unknown>,
          actor: 'Human Operator', outcome: 'recorded, not applied:stop-requested' };
        await route.fulfill({ json: operatorIntent });
      } else if (operatorIntent) await route.fulfill({ json: { ...operatorIntent,
        outcome: stopSettled ? 'applied:stopped-and-held' : operatorIntent['outcome'] } });
      else await route.fulfill({ status: 404, body: '' });
      return;
    }
    const body = url.pathname.endsWith('/spend')
      ? { scope: url.searchParams.has('taskId') ? 'card' : 'project',
          occurrences: laterDecision ? 2 : 1, inputTokens: laterDecision ? 140 : 120,
          outputTokens: laterDecision ? 40 : 30, pricedUsd: laterDecision ? 0.0143 : 0.0123,
          unknownPriceCalls: 1 }
      : url.pathname.endsWith(`/${receiptId}`)
        ? { reasoning: 'The review remains blocked by one open finding.',
            responseContent: '{"chosenActions":[{"action":"ask","target":"operator"}]}',
            grounds: { facts: [{ id: 'finding', name: 'Open finding', value: '1',
              source: { kind: 'report', ref: 'review-report.md' } }] } }
        : [...(laterDecision ? [{ receiptId: 'odr_ui_2', occurrenceId: 'orch_ui_2', taskId: TASK_ID,
          projectId: project.name, profile: 'OperatorResponse', chosenAction: 'adapt(cut-smaller)',
          callStatus: 'completed', model: 'gpt-6-sol', thinkingLevel: 'high', totalTokens: 30,
          inputTokens: 20, outputTokens: 10, estimatedUsd: 0.002,
          unknownPriceCalls: 0, priceStatus: 'priced', evidenceRef: '/api/orchestration/decision-receipts/odr_ui_2',
          endedAt: new Date(Date.now() + 60_000).toISOString(), actor: 'human-operator' }] : []),
        { receiptId, occurrenceId: 'orch_ui_1', taskId: TASK_ID, projectId: project.name,
          profile: 'CodingCompletion', chosenAction: 'ask(operator)', callStatus: 'completed',
          model: 'gpt-6-sol', thinkingLevel: 'high', totalTokens: 150,
          inputTokens: 120, outputTokens: 30, estimatedUsd: 0.0123,
          unknownPriceCalls: 1, priceStatus: 'partial', evidenceRef: receiptRef,
          endedAt: new Date().toISOString(), actor: 'decision-step' }];
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.goto(`/?job=${TASK_ID}&watchPath=${encodeURIComponent(project.path)}`,
    { waitUntil: 'domcontentloaded', timeout: 90_000 });
  const recovery = page.getByTestId('crash-recovery-prompt-overlay');
  if (await recovery.isVisible().catch(() => false)) await page.getByTestId('crash-recovery-dismiss-all').click();
  const overviewTab = page.getByTestId('prompt-tab-overview');
  await expect(overviewTab).toBeVisible({ timeout: 30_000 });
  await overviewTab.click();

  const chain = page.getByTestId('overview-decision-chain');
  await expect(chain).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId('escalation-summary')).toHaveCount(1);
  await expect(page.getByTestId('escalation-title')).toHaveText('Escalation');
  await expect(page.getByTestId('escalation-essence')).not.toContainText('one open finding');
  const receipt = chain.getByTestId('decision-receipt-occurrence');
  await expect(receipt).toHaveCount(1);
  await expect(receipt.getByTestId('decision-receipt-cost')).toHaveText('$0.0123 priced + 1 unknown price');
  await expect(chain.getByTestId('decision-receipt-rollups')).toContainText('120 in / 30 out');
  await receipt.getByTestId('decision-receipt-evidence').click();
  await expect(chain.getByTestId('decision-receipt-detail')).toContainText('Open finding');
  await expect(chain.getByTestId('decision-receipt-detail')).toContainText('chosenActions');
  const ask = chain.getByTestId('decision-operator-ask');
  await expect(ask).toContainText('Human Operator');
  await ask.getByRole('textbox', { name: 'Reason' }).fill('Continue only with a bounded limit');
  await ask.getByRole('button', { name: 'Stop', exact: true }).click();
  await expect(ask.getByTestId('decision-operator-recorded')).toContainText('stop');
  await expect(ask.getByTestId('decision-operator-recorded')).toContainText('recorded, not applied');
  stopSettled = true;
  await expect(ask.getByTestId('decision-operator-recorded')).toContainText('run stopped and card held',
    { timeout: 20_000 });
  await expect(page.getByTestId('escalation-title')).toHaveText('Escalation');
  const transitions = chain.getByTestId('overview-decision-transition');
  await expect(transitions).toHaveCount(5);
  await expect(transitions.nth(0)).toHaveAttribute('data-decided-by', 'rule');
  await expect(transitions.nth(0)).toContainText('Agent execution');
  await expect(transitions.nth(0).getByTestId('overview-decision-cost-cell')).toHaveText('$0 · rule');
  const decisions = chain.locator('[data-testid="overview-decision-transition"][data-step-id="post-orchestrator-decision"]');
  await expect(decisions).toHaveCount(2);
  await expect(decisions.nth(0).getByTestId('overview-decision-verdict')).toHaveText('reissue');
  await expect(decisions.nth(1).getByTestId('overview-decision-verdict')).toHaveText('accept');
  await expect(decisions.nth(1)).toContainText('#2/2');
  await expect(decisions.nth(1).getByTestId('overview-decision-model')).toContainText('claude-haiku-4-5');
  const spawner = chain.locator('[data-testid="overview-decision-transition"][data-step-id="post-task-spawner"]');
  await expect(spawner.getByTestId('overview-decision-cost-cell')).toHaveText('no price data');
  await expect(spawner.getByTestId('overview-decision-cost-cell')).toHaveAttribute('data-cost-tone', 'unpriced');
  await expect(chain.locator('[data-step-id="post-code-review-grade"]').getByTestId('overview-decision-evidence')).toBeVisible();
  await expect(chain.getByTestId('overview-decision-cost-deciding')).toHaveText('$0.17+');
  await expect(chain.getByTestId('overview-decision-cost-agent')).toHaveText('$2.50');
  await expect(chain.getByTestId('overview-decision-cost-unpriced')).toHaveText('22,000 tokens on 1 run without a price');
  const chainBounds = await chain.boundingBox();
  const costBounds = await decisions.nth(1).getByTestId('overview-decision-cost-cell').boundingBox();
  const evidenceBounds = await chain.locator('[data-step-id="post-code-review-grade"]')
    .getByTestId('overview-decision-evidence').boundingBox();
  expect(chainBounds && costBounds && evidenceBounds).toBeTruthy();
  expect(costBounds!.x + costBounds!.width).toBeLessThanOrEqual(chainBounds!.x + chainBounds!.width + 1);
  expect(evidenceBounds!.x + evidenceBounds!.width).toBeLessThanOrEqual(chainBounds!.x + chainBounds!.width + 1);

  await dismissDevErrorDialog(page);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.getByTestId('escalation-summary').screenshot({ path: path.join(resultsDir, `escalated-blocker-${theme}--mocked.png`) });
    await chain.scrollIntoViewIfNeeded();
    await page.getByTestId('overview-pipeline').screenshot({ path: path.join(resultsDir, `overview-decision-chain-${theme}--mocked.png`) });
  }

  // The open card receives a new receipt on the next pipeline poll.
  laterDecision = true;
  await expect(chain.getByTestId('decision-receipt-occurrence')).toHaveCount(2, { timeout: 20_000 });
  await expect(chain.getByTestId('decision-receipt-occurrence').last()).toContainText('Human Operator');

  // Project usage: the orchestrator steps are in the ledger under their own kind.
  await page.goto(`/#/projects/${slugFor(project.name)}/token-usage`,
    { waitUntil: 'domcontentloaded', timeout: 90_000 });
  await expect(page.getByTestId('project-token-usage-panel')).toBeVisible({ timeout: 30_000 });
  const leaveRecoveryUncommitted = page.getByRole('button', { name: 'Leave all uncommitted' });
  if (await leaveRecoveryUncommitted.isVisible().catch(() => false)) await leaveRecoveryUncommitted.click();
  await expect(page.getByTestId('pipeline-cost-legend-orchestrator')).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId('pipeline-cost-runs-orchestrator')).toHaveText('5 runs');
  await expect(page.getByTestId('pipeline-cost-deciding-amount')).toHaveText('$0.17+');
  await expect(page.getByTestId('pipeline-cost-deciding-unpriced')).toContainText('22,000 tokens');
  const decisionRows = page.getByTestId('pipeline-cost-decision-step');
  await expect(decisionRows).toHaveCount(4);
  await expect(page.locator('[data-testid="pipeline-cost-decision-step"][data-step-id="post-orchestrator-decision"]'))
    .toContainText('claude-haiku-4-5');
  await expect(page.locator('[data-testid="pipeline-cost-decision-step"][data-step-id="post-task-spawner"]'))
    .toContainText('unpriced-test-model');

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.getByTestId('token-usage-pipeline-cost').screenshot({ path: path.join(resultsDir, `project-cost-of-deciding-${theme}--mocked.png`) });
  }
});
