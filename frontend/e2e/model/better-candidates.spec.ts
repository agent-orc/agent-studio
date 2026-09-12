import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const PROJECT = 'Agent Studio';
const WATCH_PATH = '/tmp/agent-studio';
const TASK_ID = 'candidate-task';
const TASK_REFERENCE = 'AGT-2770';
const RESULTS = resolve(process.env.JOB_RESULTS_DIR ?? 'test-results/better-candidates');

const CANDIDATE_NOTE = {
  currentModel: 'gpt-5.6-sol',
  currentThinkingLevel: 'max',
  capabilityClass: 'CodingAgent',
  evidenceSnapshot: '1:deepswe-v1.1:1:2026-09-02:2',
  evaluatedAtUtc: '2026-09-13T08:00:00Z',
  matrixUrl: 'https://agent-orchestrator.dev/token-economy/model-benchmarks/',
  candidates: [{
    model: 'gpt-6-astra',
    thinkingLevel: null,
    benchmarkType: 'deepswe-v1.1',
    benchmarkName: 'DeepSWE v1.1',
    scoreDelta: 1.1,
    costDeltaUsd: -4.96,
    evidenceAgeDays: 10,
    evidenceStale: false,
  }],
};

const TASK = {
  id: TASK_ID,
  key: TASK_REFERENCE,
  displayKey: TASK_REFERENCE,
  taskKey: `${WATCH_PATH}::${TASK_ID}`,
  title: 'Show benchmark-backed model candidates',
  state: '2-ready',
  kind: 'task',
  mode: 'coding',
  order: 1,
  agent: 'codex',
  createdAt: '2026-09-13T07:00:00Z',
  lastActivity: '2026-09-13T08:00:00Z',
  sessionName: null,
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/2-ready/${TASK_ID}`,
  cliType: 'codex',
  model: 'gpt-5.6-sol',
  thinkingLevel: 'max',
  betterCandidates: CANDIDATE_NOTE,
  references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [], workbenches: [] },
};

const GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [TASK], progress: [],
  failedPickup: [], codeNotComplete: [], autoReview: [], review: [], humanReview: [],
  escalated: [], completed: [], archive: [],
};

async function stubApis(page: Page): Promise<void> {
  await page.route('**/api/**', async (route: Route) => {
    const url = new URL(route.request().url());
    const json = (body: unknown) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });

    if (url.pathname === '/api/auth/status') {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.pathname === '/api/workspaces') return json([{
      id: 'WS-1', displayName: 'Default', sortOrder: 0, isDefault: true,
      color: null, createdAt: '2026-09-13T07:00:00Z',
      projects: [{
        sourceType: 'local-folder', id: 'PROJ-002', displayName: PROJECT,
        shortCode: 'AGT', workspaceId: 'WS-1', color: null, cliDefault: 'codex', modelDefault: null,
        storageLocation: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH,
        repositoryUrl: null, sortOrder: 0, archived: false, urls: [],
        createdAt: '2026-09-13T07:00:00Z',
      }],
    }]);
    if (url.pathname === '/api/watch-paths') {
      return json([{
        id: 'PROJ-002', name: PROJECT, shortCode: 'AGT', path: WATCH_PATH,
        rootPath: WATCH_PATH, repositoryPath: WATCH_PATH,
      }]);
    }
    if (url.pathname === '/api/tasks/grouped') return json(GROUPED);
    if (url.pathname === '/api/tasks') return json([TASK]);
    if (url.pathname === `/api/tasks/${TASK_REFERENCE}` || url.pathname === `/api/tasks/${TASK_ID}`) {
      return json({
        info: TASK,
        promptMarkdown: '# Better candidates\n\nKeep the selected route unchanged.',
        promptHistory: [], titleHistory: [], statusMarkdown: null, contextUsage: null,
        log: [], summaryState: null, reviewEvidence: [],
      });
    }
    if (url.pathname === '/api/cli/codex/models') return json({
      source: 'fixture',
      models: [
        { id: 'gpt-5.6-sol', label: 'GPT-5.6 Sol', available: true,
          thinkingLevels: ['medium', 'high', 'max'], defaultThinkingLevel: 'medium' },
        { id: 'gpt-6-astra', label: 'GPT-6 Astra', available: true,
          thinkingLevels: ['medium', 'high'], defaultThinkingLevel: 'high' },
      ],
    });
    if (url.pathname === '/api/cli/quota') return json({ snapshots: [], ttlSeconds: 600 });
    if (url.pathname === '/api/runner/status') return json({ projects: {} });
    if (url.pathname === '/api/runner/queue-starvation') return json({ active: false, items: [] });
    if (url.pathname === '/api/v1/management/remote-hosts') return json([]);
    if (url.pathname === '/api/v1/management/links') return json([]);
    if (url.pathname.includes('/auto-review-queue')) return json({
      queueDepth: 0, activeJobs: 0, isStagnant: false, stagnantSince: null,
      stagnantThresholdMinutes: 20, drainRatePerMinute: 0, medianReviewDurationMs: 0,
      throughputWindowMinutes: 60, observedAt: '2026-09-13T08:00:00Z',
    });
    if (url.pathname.startsWith('/api/workspace/tokens/timeline')) return json({
      windowStart: '2026-09-12T08:00:00Z', windowEnd: '2026-09-13T08:00:00Z',
      windowHours: 24, bucketMinutes: 60, bucketCount: 24, cells: [], projects: [],
      betterCandidateUsage: [], fetchedAt: '2026-09-13T08:00:00Z', disclaimer: '',
    });
    if (url.pathname.startsWith('/api/workspace/tokens/expensive-jobs')) return json({ jobs: [] });
    if (url.pathname.startsWith('/api/runner/token-summary-aggregate')) return json({
      projects: 0, orchestratorEntries: 0, orchestratorLlmCalls: 0,
      totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0,
      totalCacheCreationTokens: 0, estimatedApiCostUsd: 0, allModelsPriced: true,
      byModel: [], byProject: [], fetchedAt: '2026-09-13T08:00:00Z', disclaimer: '',
    });
    if (url.pathname === '/api/tasks/archive') return json({ items: [], total: 0 });
    if (url.pathname === '/api/epics') return json([]);
    if (url.pathname.startsWith('/api/bus/')) return json([]);
    if (url.pathname === '/api/tags' || url.pathname === '/api/clients'
        || url.pathname === '/api/projects') return json([]);
    return json([]);
  });
}

test('shows the same informational candidate on the Ready card and in Execution Hosts', async ({ page }) => {
  mkdirSync(RESULTS, { recursive: true });
  await stubApis(page);
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/', { waitUntil: 'domcontentloaded' });

  const card = page.locator('[data-testid="task-card"], [data-testid="job-card"]')
    .filter({ hasText: TASK.title });
  await expect(card).toBeVisible({ timeout: 15_000 });
  await expect(card.getByTestId('better-candidate-gpt-6-astra')).toContainText('deepswe-v1.1');
  await expect(card.getByTestId('better-candidate-gpt-6-astra')).toContainText('$Δ-4.96');
  await card.screenshot({ path: join(RESULTS, 'better-candidate-ready-card--mocked.png') });

  await page.goto('/#/workspace/settings/remote-hosts', { waitUntil: 'domcontentloaded' });
  const hosts = page.getByTestId('execution-host-candidates');
  await expect(hosts).toBeVisible({ timeout: 15_000 });
  await expect(hosts).toContainText(TASK_REFERENCE);
  await expect(hosts).toContainText('gpt-6-astra');
  await hosts.screenshot({ path: join(RESULTS, 'better-candidate-execution-hosts--mocked.png') });
});
