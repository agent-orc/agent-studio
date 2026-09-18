import { test, expect, type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { dismissDevErrorDialog } from '../helpers/theme';

const JOB_ID = 'core-token-usage-fixture';
const WATCH_PATH = 'C:/fixtures/agent-taskboard';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

function json(body: unknown) {
  return {
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  };
}

function jobDetail() {
  return {
    info: {
      id: JOB_ID,
      jobKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'CORE CLI-footer token usage fixture',
      state: '4-auto-review',
      agent: 'codex',
      cliType: 'codex',
      model: 'claude-opus-4-8',
      thinkingLevel: null,
      watchPath: WATCH_PATH,
      projectName: 'agent-taskboard',
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/4-auto-review/${JOB_ID}`,
      sessionName: 'fixture-session',
      tokenSummary: null,
      lastUsage: {
        at: '2026-06-06T20:00:00Z',
        tokens: '19.7M',
        changes: null,
        requests: '8',
      },
      execution: null,
      order: 1,
      commit: null,
      commits: [],
      codeActivityDetected: false,
      useOwnSession: null,
      kind: 'task',
      mode: 'coding',
      allowWebAccess: false,
      summaryState: null,
      outcomeIssue: null,
      orchestratorVerdict: null,
      ownerClientId: 'local-default',
    },
    promptMarkdown: '# Token usage fixture',
    statusMarkdown: '## Done\n\nFixture status.',
    contextUsage: null,
    log: [],
    promptHistory: [],
    titleHistory: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

function pipeline(runCount = 4) {
  const coreStep = {
    id: 'core-agent-run',
    displayName: 'Agent execution',
    kind: 'core',
    runMode: 'sequential',
    dependsOn: [],
    idempotent: false,
    stub: false,
  };
  const startedAt = '2026-06-06T20:00:00Z';
  const completedAt = '2026-06-06T20:02:05Z';
  const currentModel = {
    model: 'claude-opus-4-8',
    modelKnown: true,
    thinkingLevel: 'high',
    steps: 1,
    inputTokens: 2500,
    outputTokens: 195600,
    cacheReadTokens: 18500000,
    cacheCreationTokens: 1000000,
    totalTokens: 19698100,
    costUsd: 20.4025,
  };
  const runs = Array.from({ length: runCount }, (_, index) => {
    const current = index === runCount - 1;
    const scale = current ? 1 : (index + 1) / 20;
    return {
      attempt: current ? 8 : index + 1,
      current,
      startedAt: `2026-06-06T${String(17 + index).padStart(2, '0')}:00:00Z`,
      completedAt: current ? completedAt : `2026-06-06T${String(17 + index).padStart(2, '0')}:05:00Z`,
      models: [{
        ...currentModel,
        inputTokens: Math.round(currentModel.inputTokens * scale),
        outputTokens: Math.round(currentModel.outputTokens * scale),
        cacheReadTokens: Math.round(currentModel.cacheReadTokens * scale),
        cacheCreationTokens: Math.round(currentModel.cacheCreationTokens * scale),
        totalTokens: Math.round(currentModel.totalTokens * scale),
        costUsd: currentModel.costUsd * scale,
      }],
      totalTokens: Math.round(currentModel.totalTokens * scale),
      totalCostUsd: currentModel.costUsd * scale,
      anyModelUnknown: false,
      tokenUsageAvailable: true,
    };
  });
  const allTokens = runs.reduce((sum, run) => sum + run.totalTokens, 0);
  const allCost = runs.reduce((sum, run) => sum + run.totalCostUsd, 0);
  return {
    pipeline: {
      id: 'standard-task-pipeline',
      displayName: 'Standard',
      version: 1,
      pre: [],
      core: [coreStep],
      post: [],
      allSteps: [coreStep],
    },
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: 'agent-taskboard',
      startedAt,
      completedAt,
      attempt: 8,
      previousAttempts: [],
      steps: [{
        stepId: 'core-agent-run',
        kind: 'core',
        model: 'claude-opus-4-8',
        status: 'passed',
        startedAt,
        completedAt,
        durationMs: 125000,
        inputTokens: 2500,
        outputTokens: 195600,
        cacheReadTokens: 18500000,
        cacheCreationTokens: 1000000,
        tokenUsageSource: 'AGENT (CLI FOOTER) / reported',
        reason: null,
        verdict: null,
        verdictSummary: null,
      }],
    },
    cost: {
      steps: [{
        stepId: 'core-agent-run',
        kind: 'core',
        model: 'claude-opus-4-8',
        tokenUsageSource: 'AGENT (CLI FOOTER) / reported',
        modelKnown: true,
        inputTokens: 2500,
        outputTokens: 195600,
        cacheReadTokens: 18500000,
        cacheCreationTokens: 1000000,
        totalTokens: 19698100,
        inputCostUsd: 0.0125,
        outputCostUsd: 4.89,
        cacheReadCostUsd: 9.25,
        cacheCreationCostUsd: 6.25,
        costUsd: 20.4025,
      }],
      totalInputTokens: 2500,
      totalOutputTokens: 195600,
      totalCacheReadTokens: 18500000,
      totalCacheCreationTokens: 1000000,
      totalTokens: 19698100,
      totalInputCostUsd: 0.0125,
      totalOutputCostUsd: 4.89,
      totalCacheReadCostUsd: 9.25,
      totalCacheCreationCostUsd: 6.25,
      totalCostUsd: 20.4025,
      anyModelUnknown: false,
    },
    tokensByModel: {
      runs,
      totalByModel: [{
        ...currentModel,
        steps: runCount,
        totalTokens: allTokens,
        inputTokens: runs.reduce((sum, run) => sum + run.models[0].inputTokens, 0),
        outputTokens: runs.reduce((sum, run) => sum + run.models[0].outputTokens, 0),
        cacheReadTokens: runs.reduce((sum, run) => sum + run.models[0].cacheReadTokens, 0),
        cacheCreationTokens: runs.reduce((sum, run) => sum + run.models[0].cacheCreationTokens, 0),
        costUsd: allCost,
      }],
      totalTokens: allTokens,
      totalCostUsd: allCost,
      anyModelUnknown: false,
    },
    config: {},
  };
}

function runTimeline() {
  return {
    runCount: 8,
    firstStartedAt: '2026-06-06T19:00:00Z',
    lastActivityAt: '2026-06-06T20:02:05Z',
    hasActiveRun: false,
    runs: Array.from({ length: 8 }, (_, i) => ({
      index: i + 1,
      intent: i === 0 ? 'start' : 'continue',
      startedAt: `2026-06-06T19:${String(i).padStart(2, '0')}:00Z`,
      endedAt: `2026-06-06T19:${String(i).padStart(2, '0')}:30Z`,
      status: 'completed',
      cli: 'codex',
      exitCode: 0,
      durationSeconds: 30,
      inputSessionId: null,
      capturedSessionId: `session-${i + 1}`,
      resumed: i > 0,
      reason: null,
      userFollowup: null,
      lineStart: null,
      lineEnd: null,
      headShaBefore: null,
      headShaAfter: null,
      contextRef: null,
    })),
  };
}

async function installFixtureRoutes(page: Page, tokenRunCount = 4) {
  await page.route('**/api/**', route => route.fulfill(json([])));
  await page.route('**/api/auth/status', route => route.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/tasks/grouped**', route => route.fulfill(json({
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    autoReview: [jobDetail().info],
    humanReview: [],
    completed: [],
    archive: [],
  })));
  await page.route('**/api/watch-paths**', route => route.fulfill(json([
    { name: 'agent-taskboard', path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ])));
  await page.route('**/api/runner/status**', route => route.fulfill(json({ projects: {} })));
  await page.route('**/api/environment**', route => route.fulfill(json({
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  })));
  await page.route('**/api/clients', route => route.fulfill(json([])));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ items: [] })));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({
    snapshots: [],
    ttlSeconds: 600,
  })));

  const id = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route(new RegExp(`/api/tasks/${id}/pipeline(\\?|$)`), route => route.fulfill(json(pipeline(tokenRunCount))));
  await page.route(new RegExp(`/api/tasks/${id}/runs(\\?|$)`), route => route.fulfill(json(runTimeline())));
  await page.route(new RegExp(`/api/tasks/${id}/output(\\?|$)`), route => route.fulfill(json([])));
  await page.route(new RegExp(`/api/tasks/${id}/session-events(\\?|$)`), route => route.fulfill(json({ events: [], sessionChain: [] })));
  await page.route(new RegExp(`/api/tasks/${id}/agent-work(\\?|$)`), route => route.fulfill(json(null)));
  await page.route(new RegExp(`/api/tasks/${id}/timeline(\\?|$)`), route => route.fulfill(json([])));
  await page.route(new RegExp(`/api/tasks/${id}/claude-session(\\?|$)`), route => route.fulfill(json(null)));
  await page.route(new RegExp(`/api/tasks/${id}/screenshots(\\?|$)`), route => route.fulfill(json([])));
  await page.route(new RegExp(`/api/tasks/${id}(\\?|$)`), route => route.fulfill(json(jobDetail())));
}

async function saveShot(page: Page, name: string) {
  const buf = await page.screenshot({ fullPage: false });
  if (!RESULTS_DIR) return;
  await mkdir(RESULTS_DIR, { recursive: true });
  await writeFile(join(RESULTS_DIR, name), buf);
}

async function paintLegacyFinding(page: Page, calls: number) {
  await page.evaluate((sessionCalls) => {
    const pipelineLabel = document.querySelector<HTMLElement>('[data-testid="overview-pipeline-total-label"]');
    if (pipelineLabel) {
      pipelineLabel.dataset['legacyOriginal'] = pipelineLabel.innerHTML;
      pipelineLabel.innerHTML = 'Task total <span>SUM</span>';
    }
    const taskLabel = document.querySelector<HTMLElement>('.ptu__sum-title');
    if (taskLabel) taskLabel.textContent = 'Tokens across all runs';
    else document.querySelector<HTMLElement>('[data-testid="pipeline-token-usage"]')?.insertAdjacentHTML(
      'afterbegin',
      '<div class="legacy-total" style="padding:6px 20px;font:12px var(--font-mono)">&gt; &nbsp; TOKENS ACROSS ALL RUNS</div>',
    );
    document.querySelectorAll<HTMLElement>('.studio-disclosure__marker').forEach((marker) => {
      marker.style.visibility = 'hidden';
      marker.insertAdjacentHTML('afterend', '<span class="legacy-marker" aria-hidden="true" style="padding-inline:8px">&gt;</span>');
    });
    const tokenUsage = document.querySelector<HTMLElement>('[data-testid="pipeline-token-usage"]');
    tokenUsage?.insertAdjacentHTML('afterend', `
      <section class="legacy-agent-work" style="width:100%;margin-top:8px;font:12px var(--font-mono);color:var(--studio-fg-muted)">
        <strong style="display:block;padding:4px 8px;background:var(--studio-bg-hover);color:var(--studio-fg-strong)">AGENT WORK</strong>
        <div style="display:grid;grid-template-columns:1fr auto auto auto auto;gap:12px;padding:6px 20px">
          <span>Calls &nbsp; ${sessionCalls} calls</span><span>2d ago</span><span>1d ago</span><span>-</span><span>-</span>
        </div>
      </section>`);
  }, calls);
}

async function restoreCurrentFinding(page: Page) {
  await page.evaluate(() => {
    const pipelineLabel = document.querySelector<HTMLElement>('[data-testid="overview-pipeline-total-label"]');
    if (pipelineLabel?.dataset['legacyOriginal']) pipelineLabel.innerHTML = pipelineLabel.dataset['legacyOriginal'];
    const taskLabel = document.querySelector<HTMLElement>('.ptu__sum-title');
    if (taskLabel) taskLabel.textContent = 'All runs · task total';
    document.querySelectorAll<HTMLElement>('.studio-disclosure__marker').forEach(marker => marker.style.visibility = '');
    document.querySelectorAll('.legacy-marker, .legacy-total, .legacy-agent-work').forEach(node => node.remove());
  });
}

test('task detail pipeline names run and task totals, aligns disclosures, and explains API pricing', async ({ page }) => {
  await page.addInitScript(() => {
    try {
      localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false }));
    } catch { /* ignore */ }
  });
  await installFixtureRoutes(page);

  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

  const pipelineBlock = page.getByTestId('overview-pipeline');
  await expect(pipelineBlock).toBeVisible({ timeout: 10000 });
  await dismissDevErrorDialog(page);
  await page.getByTestId('overview-pipeline-phase').click();
  await expect(page.getByTestId('overview-pipeline-step-name')).toContainText('Agent execution');
  await expect(page.getByTestId('overview-pipeline-agent-runs')).toContainText('8 runs');
  await expect(page.getByTestId('overview-pipeline-step-tokens')).toContainText('19.7M');
  await expect(page.getByTestId('overview-pipeline-step-cost')).toContainText('$20.40');
  await expect(page.getByTestId('overview-pipeline-total')).toContainText('This run · pipeline total');
  await expect(page.getByTestId('overview-pipeline-total')).toContainText('Run #8 incl. pre/post/review steps');
  await expect(page.getByTestId('overview-pipeline-total')).not.toContainText('SUM');
  await expect(page.getByTestId('overview-pipeline-total-tokens')).toContainText('19.7M');
  await expect(page.getByTestId('pipeline-token-usage-total')).toContainText('All runs · task total');

  await pipelineBlock.screenshot({ path: RESULTS_DIR ? join(RESULTS_DIR, 'pipeline-core-token-usage.png') : 'test-results/pipeline-core-token-usage.png' });

  await page.getByTestId('overview-pipeline-step-tokens').hover();
  const tooltip = page.getByTestId('cac-tooltip');
  await expect(tooltip).toContainText('Source: AGENT (CLI FOOTER) / reported');
  await expect(tooltip).toContainText('Input: 3k');
  await expect(tooltip).toContainText('Output: 196k');
  await expect(tooltip).toContainText('Cache read: 18.5M');
  await expect(tooltip).toContainText('Cache creation: 1.0M');
  await expect(tooltip).toContainText('Estimated cost: $20.40');
  await expect(tooltip).toContainText('historical list prices');
  await saveShot(page, 'pipeline-core-token-tooltip.png');
});

for (const runCase of [
  { name: 'multi-run', count: 4 },
  { name: 'single-run', count: 1 },
] as const) {
  for (const theme of ['light', 'dark'] as const) {
    for (const viewport of [
      { name: 'desktop', width: 1440, height: 900 },
      { name: '400px', width: 400, height: 900 },
    ] as const) {
      test(`${runCase.name} totals remain clear at ${viewport.name} in ${theme}`, async ({ page }) => {
        await page.setViewportSize({ width: viewport.width, height: viewport.height });
        await page.addInitScript(({ selectedTheme }) => {
          localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false }));
          localStorage.setItem('atp.studio.theme', selectedTheme);
        }, { selectedTheme: theme });
        await installFixtureRoutes(page, runCase.count);
        await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

        const overview = page.getByTestId('overview-tab');
        await expect(overview).toBeVisible({ timeout: 10_000 });
        await dismissDevErrorDialog(page);
        await paintLegacyFinding(page, runCase.count);
        await overview.screenshot({
          path: join(RESULTS_DIR || 'test-results', `overview-totals-before-${runCase.name}-${viewport.name}-${theme}--composite.png`),
        });

        await restoreCurrentFinding(page);
        if (runCase.count === 1) {
          await expect(page.getByTestId('overview-pipeline-total')).toContainText('All runs · task total');
          await expect(page.getByTestId('pipeline-token-usage-total')).toHaveCount(0);
        } else {
          await expect(page.getByTestId('overview-pipeline-total')).toContainText('This run · pipeline total');
          await expect(page.getByTestId('pipeline-token-usage-total')).toContainText('All runs · task total');
        }

        await overview.screenshot({
          path: join(RESULTS_DIR || 'test-results', `overview-totals-after-${runCase.name}-${viewport.name}-${theme}--mocked.png`),
        });
      });
    }
  }
}
