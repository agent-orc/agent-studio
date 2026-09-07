import { test, expect } from '../fixtures/dev-backend';
import * as fs from 'fs';
import * as path from 'path';
import { setTheme } from '../helpers/theme';

/**
 * T4a evidence — the reworked project-level Pipeline page.
 *
 * Renders the real compiled <app-project-pipeline-panel> in a browser with a
 * deterministic mocked catalogue / overrides / cost so the screenshot shows
 * every control the page now owns: the pre / core / post grouping, per-step
 * activation + ordering, the per-step model picker, the prompt *binding*
 * reference to the Prompts registry (content lives there, never inline here)
 * plus the legacy inline-override escape hatch, the gate / run-condition
 * controls, and each step's 90-day token sum. Pure reads + route mocks, so it
 * is safe against the shared stack; the captured shots are labelled --mocked
 * because the panel data is mocked (the component + SCSS are the real build).
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'pipeline-page');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pipeline-page');
})();

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

const CATALOGUE = {
  pipelineId: 'default',
  steps: [
    { id: 'pre-context-scan', displayName: 'Pre: Context scan', kind: 'module', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'pre-context-scan', canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'core-run', displayName: 'Core: Agent run', kind: 'core', usesModel: false, usesPrompt: false, supportsMode: false, canDisable: false, defaultEnabled: true, supportsCondition: false },
    { id: 'aspect-requirement-fit', displayName: 'Aspect: Requirement fit', kind: 'aspect', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'aspect-requirement-fit', canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'aspect-code-quality', displayName: 'Aspect: Code quality', kind: 'aspect', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'aspect-code-quality', canDisable: true, defaultEnabled: true, supportsCondition: false,
      resolvedModel: 'claude-sonnet-4-6', modelSource: 'step', modelMigration: {
        from: 'claude-sonnet-4-6', to: 'claude-sonnet-5', family: 'claude-sonnet',
        rule: 'latestInFamily:claude-sonnet', catalogVersion: '2026-09-06',
        safeAuto: true, targetAvailable: true, safeAutoCandidate: true,
        costClassFrom: 'standard', costClassTo: 'standard',
        fromReasoningLevels: ['medium'], toReasoningLevels: ['medium', 'high'],
        ladderCompatible: true, note: 'Latest Sonnet generation with a compatible reasoning ladder.',
      } },
    { id: 'aspect-security', displayName: 'Aspect: Security', kind: 'aspect', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'aspect-security', canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'decision-gate', displayName: 'Decision: Lint gate', kind: 'tool', usesModel: false, usesPrompt: false, supportsMode: true, canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'post-abort-review', displayName: 'Post: Abort review', kind: 'orchestrator', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'post-abort-review', canDisable: true, defaultEnabled: true, supportsCondition: true },
    { id: 'post-lint-scss', displayName: 'Frontend stylelint', kind: 'tool', framework: 'angular', appliesTo: 'angular', applicable: true, effectiveExecution: { executionKind: 'shell', source: 'catalogue', commands: [{ workingSubdir: 'frontend', command: 'npx stylelint "src/**/*.scss"' }] }, usesModel: false, usesPrompt: false, supportsMode: true, canDisable: true, defaultEnabled: true, supportsCondition: true },
  ],
};

const SETTINGS_PROJECTION = {
  pipelineSteps: {
    'aspect-code-quality': { enabled: true, cliType: 'claude', model: 'claude-sonnet-4-6', thinkingLevel: null },
    // A legacy inline prompt override -> renders the "inline override" badge + Clear.
    'aspect-security': { enabled: true, prompt: 'Project-specific security checklist (legacy inline).' },
    // Opt-out step (default on) with a run condition set.
    'post-abort-review': { enabled: true, condition: { when: 'on-nonzero-exit' } },
  },
  pipelineStepOrder: [],
};

function fakeCost(project: string) {
  const day = '2026-08-09';
  const gap = { modelId: 'gpt-5.6-sol', reason: 'NoPriceForDate', affectedRuns: 2 };
  const k = (kind: string, tokens: number, cost: number, unknown = false) => ({
    kind, totalTokens: tokens, totalCostUsd: cost, anyModelUnknown: unknown,
    unpricedRuns: unknown ? 2 : 0, pricingGaps: unknown ? [gap] : [],
    cells: [{
      day, totalTokens: tokens, costUsd: cost,
      unpricedRuns: unknown ? 2 : 0, pricingGaps: unknown ? [gap] : [],
    }],
  });
  const kinds = [k('core', 480000, 1.44), k('aspect', 96000, 0.31), k('module', 21000, 0.011, true)];
  const s = (stepId: string, kind: string, tokens: number, cost: number, unknown = false) => ({
    stepId, kind, totalTokens: tokens, totalCostUsd: cost, anyModelUnknown: unknown,
    unpricedRuns: unknown ? 2 : 0, pricingGaps: unknown ? [gap] : [],
  });
  const steps = [
    s('core-run', 'core', 480000, 1.44),
    s('aspect-code-quality', 'aspect', 64000, 0.21),
    s('aspect-requirement-fit', 'aspect', 32000, 0.10),
    s('pre-context-scan', 'module', 21000, 0.011, true),
  ];
  return {
    project, days: [day],
    dayCosts: [{ day, totalTokens: 597000, costUsd: 1.761, unpricedRuns: 2, pricingGaps: [gap] }],
    windowDays: 90, kinds, steps,
    totalTokens: 597000, totalCostUsd: 1.761, anyModelUnknown: true,
    unpricedRuns: 2, pricingGaps: [gap],
    taskCount: 7, hasData: true, fetchedAt: '2026-06-10T00:00:00Z',
  };
}

let projectSlug = '';
let projectName = '';

test.beforeEach(() => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
});

test('pipeline page: reworked panel shows health, steps, models, prompt bindings, per-step tokens', async ({ page, devBackend }) => {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  expect(devBackend.port).toBe(5030);
  const preferred: WatchPath = { name: 'Agent Studio Worktree', path: '/fixtures/agent-studio-worktree' };
  projectName = preferred.name;
  projectSlug = slugFor(projectName);

  await page.route('**/api/**', r => r.fulfill(json({})));
  await page.route('**/api/auth/status', r => r.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/clients**', r => r.fulfill(json([])));
  await page.route('**/api/v1/management/remote-hosts', r => r.fulfill(json([])));
  await page.route('**/api/v1/management/remote-hosts/link-health', r => r.fulfill(json([])));
  await page.route('**/api/tags', r => r.fulfill(json([])));
  await page.route('**/api/orchestrator/sessions', r => r.fulfill(json({ sessions: [] })));
  await page.route('**/api/crash-recovery/pending', r => r.fulfill(json({ pending: [] })));
  await page.route('**/api/cli/*/models*', r => r.fulfill(json({ models: [], source: 'stubbed' })));
  await page.route('**/api/watch-paths', r => r.fulfill(json([preferred])));
  await page.route('**/api/projects', r => r.fulfill(json([])));
  await page.route('**/api/workspaces', r => r.fulfill(json([{
    id: 'WS-1', displayName: 'Workspace', sortOrder: 0, isDefault: true, color: null,
    createdAt: '2026-07-22T00:00:00Z',
    projects: [{
      id: 'PROJ-1', displayName: projectName, shortCode: 'PLH', workspaceId: 'WS-1',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
      storageLocation: preferred.path, archived: false, createdAt: '2026-07-22T00:00:00Z',
    }],
  }])));
  await page.route('**/api/environment', r => r.fulfill(json({
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  })));
  await page.route('**/api/cli/quota', r => r.fulfill(json({
    at: '2026-07-23T01:00:00Z', ttlSeconds: 600, snapshots: [],
  })));
  await page.route('**/api/cli/usage**', r => r.fulfill(json({
    at: '2026-07-23T01:00:00Z', sessions: [],
  })));
  await page.route('**/api/tasks/grouped', r => r.fulfill(json({
    archive: [], autoReview: [], backlog: [], codeNotComplete: [], completed: [],
    failedPickup: [], humanReview: [], orchestratorPrep: [], preparation: [],
    progress: [], ready: [], review: [],
  })));
  await page.route('**/api/tasks/archive**', r => r.fulfill(json({ items: [], total: 0 })));
  await page.route('**/api/runner/status', r => r.fulfill(json({ projects: {} })));
  await page.route('**/api/bus/*/messages**', r => r.fulfill(json([])));
  await page.route('**/api/projects/*/workbenches**', r => r.fulfill(json({
    projectName,
    includesHistory: true,
    count: 0,
    items: [],
  })));
  await page.route('**/api/projects/pipeline-catalogue**', r => r.fulfill(json(CATALOGUE)));
  await page.route('**/api/projects/settings', r => r.fulfill(json({ [projectName]: SETTINGS_PROJECTION })));
  await page.route('**/token-usage/pipeline-cost*', r => r.fulfill(json(fakeCost(projectName))));
  await page.route('**/token-usage/summary', r => r.fulfill(json({
    project: projectName, hasData: true,
    lifetimeTotalTokens: 597000, lifetimeJobTokens: 597000,
    lifetimeSupportingTokens: 0, lifetimeOrchestratorTokens: 0, lifetimeCalls: 7,
    last24hTotalTokens: 21000, last24hJobTokens: 21000,
    last24hSupportingTokens: 0, last24hOrchestratorTokens: 0, last24hCalls: 1,
    last7dTotalTokens: 597000, last7dJobTokens: 597000,
    last7dSupportingTokens: 0, last7dOrchestratorTokens: 0, last7dCalls: 7,
    firstActivity: '2026-08-01T00:00:00Z', lastActivity: '2026-08-09T00:00:00Z',
    fetchedAt: '2026-08-09T00:00:00Z', disclaimer: 'Fixture data.',
  })));
  await page.route('**/token-usage/heatmap*', r => r.fulfill(json({
    project: projectName, days: ['2026-08-09'], jobs: [], hasData: false,
    fetchedAt: '2026-08-09T00:00:00Z',
  })));
  await page.route('**/token-usage/expensive*', r => r.fulfill(json({ project: projectName, jobs: [] })));
  await page.route('**/api/projects/*/pipeline-health', r => r.fulfill(json({
    project: projectName,
    capturedAtUtc: '2026-07-23T01:00:00Z',
    status: 'alarm',
    activeGate: {
      gateRunId: 'gate-night-1', project: projectName, jobId: 'AGT-2183',
      acquiredAtUtc: '2026-07-22T22:30:00Z', elapsedMinutes: 150,
      budgetMinutes: 30, isHanging: true,
    },
    fingerprint: {
      fingerprint: 'lock:9c2f19e4a88c73ab', consecutiveFailures: 3, threshold: 3,
      projects: [projectName, 'Website'], isSystemic: true,
    },
    lanes: [
      { lane: '2-ready', queueCount: 2, completedPerHour: 1, isStalled: false },
      { lane: '3-progress', queueCount: 1, completedPerHour: 1, isStalled: false },
      { lane: '4-auto-review', queueCount: 4, completedPerHour: 0, isStalled: true },
      { lane: '5-human-review', queueCount: 3, completedPerHour: 2, isStalled: false },
    ],
    alerts: [],
  })));
  await page.route('**/api/projects/*/pipeline-steps/post-lint-scss/probe', r => r.fulfill(json({
    stepId: 'post-lint-scss', status: 'passed', applicable: true, exitCode: 0,
    durationMs: 81, output: 'stylelint passed', queueWaitMs: 4,
  })));

  // Tall viewport so the shell's inner scroll area shows the whole panel
  // (every phase group, each with its per-step token chips) in one shot.
  await page.setViewportSize({ width: 1440, height: 2400 });

  await page.goto(`/#/projects/${projectSlug}/pipeline`, { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();
  await expect(page.getByTestId('pipeline-type-select')).toHaveValue('task');
  await expect(page.getByTestId('pipeline-type-select').locator('option'))
    .toHaveText(['Task', 'Bug', 'Feature', 'Planning']);
  const health = page.getByTestId('pipeline-health');
  await expect(health).toHaveAttribute('data-status', 'alarm');
  await expect(page.getByTestId('pipeline-health-gate')).toContainText('Gate hanging since 150 min');
  await expect(page.getByTestId('pipeline-health-fingerprint')).toContainText('Systemic gate problem');
  await expect(page.getByTestId('pipeline-health-drain').locator('[data-lane="4-auto-review"]')).toContainText('0/h');

  // Phase groups: core renders "always on"; aspects expose a model picker; a
  // prompt cell deep-links to the Prompts registry rather than editing inline.
  await expect(page.getByTestId('pipeline-step-row-core-run')).toBeVisible();
  const codeQualityRow = page.getByTestId('pipeline-step-row-aspect-code-quality');
  await codeQualityRow.evaluate(el => { (el as HTMLDetailsElement).open = true; });
  await expect(codeQualityRow.getByTestId('pipeline-step-setting-run-aspect-code-quality')).toBeVisible();
  await expect(codeQualityRow.getByTestId('pipeline-step-setting-run-aspect-code-quality')).toContainText('sequential');
  await expect(codeQualityRow.getByTestId('pipeline-step-setting-model-aspect-code-quality')).toBeVisible();
  const migration = page.getByTestId('pipeline-step-model-migration-aspect-code-quality');
  await expect(migration).toContainText('Update available:');
  await expect(migration).toContainText('claude-sonnet-4-6');
  await expect(migration).toContainText('claude-sonnet-5');
  await expect(migration.getByTestId('model-migration-impact')).toContainText('standard to standard');
  await expect(page.getByTestId('pipeline-step-prompt-open-aspect-code-quality')).toBeVisible();
  await expect(page.getByTestId('pipeline-step-agent-aspect-code-quality')).toBeVisible();
  await page.getByTestId('pipeline-step-row-aspect-requirement-fit').evaluate(el => { (el as HTMLDetailsElement).open = true; });
  await expect(page.getByTestId('pipeline-step-prompt-manage-aspect-requirement-fit')).toBeVisible();
  // The inline-override step exposes its clear-to-registry escape hatch.
  await page.getByTestId('pipeline-step-row-aspect-security').evaluate(el => { (el as HTMLDetailsElement).open = true; });
  await expect(page.getByTestId('pipeline-step-prompt-clear-aspect-security')).toBeVisible();
  // Per-step token sum from the mocked window, on each row; no bottom total.
  await expect(page.getByTestId('pipeline-step-tokens-core-run')).toContainText('480.0k');
  await expect(page.getByTestId('pipeline-step-tokens-aspect-code-quality')).toContainText('64.0k');
  await expect(page.getByTestId('pipeline-cost')).toHaveCount(0);
  await expect(page.getByTestId('pipeline-cost-total')).toHaveCount(0);

  const stylelint = page.getByTestId('pipeline-step-row-post-lint-scss');
  await stylelint.evaluate(el => { (el as HTMLDetailsElement).open = true; });
  await expect(page.getByTestId('pipeline-step-framework-post-lint-scss')).toHaveText('angular');
  await expect(page.getByTestId('pipeline-step-commands-post-lint-scss'))
    .toContainText('cd frontend && npx stylelint "src/**/*.scss"');
  await page.getByTestId('pipeline-step-probe-post-lint-scss').click();
  await expect(page.getByTestId('pipeline-step-probe-output-post-lint-scss')).toContainText('stylelint passed');

  await setTheme(page, 'light');
  await migration.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-model-migration--light--mocked.png') });
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-page-full--mocked.png'), fullPage: true });
  await section.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-page-section--mocked.png') });
  await health.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-health-night-alarms--light--mocked.png') });

  await setTheme(page, 'dark');
  await migration.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-model-migration--dark--mocked.png') });
  await health.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-health-night-alarms--dark--mocked.png') });
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-page-full-dark--mocked.png'), fullPage: true });
  await setTheme(page, 'light');
  await section.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-page-section-light--mocked.png') });

  await page.goto(`/#/projects/${projectSlug}/token-usage`, { waitUntil: 'domcontentloaded' });
  const projectTotal = page.getByTestId('pipeline-cost-total');
  await expect(projectTotal).toContainText('$1.76');
  await expect(projectTotal).toContainText('incomplete (2 runs without price)');
  await projectTotal.hover();
  await expect(page.getByTestId('cac-tooltip')).toContainText('gpt-5.6-sol');
  await expect(page.getByTestId('cac-tooltip')).toContainText('NoPriceForDate');
  await setTheme(page, 'light');
  await page.getByTestId('token-usage-pipeline-cost').screenshot({
    path: path.join(SCREENSHOT_DIR, 'project-price-mixed-light--mocked.png'),
  });
  await setTheme(page, 'dark');
  await page.getByTestId('token-usage-pipeline-cost').screenshot({
    path: path.join(SCREENSHOT_DIR, 'project-price-mixed-dark--mocked.png'),
  });
});

test('pipeline page: pure dotnet project keeps Angular stylelint visible but inapplicable', async ({ page, devBackend }) => {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  const pathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  const paths = await pathsResponse.json() as WatchPath[];
  const preferred = paths.find(p => /playwright|worktree/i.test(p.name)) ?? paths[0];
  expect(preferred, 'needs at least one watched project').toBeTruthy();
  projectName = preferred.name;
  projectSlug = slugFor(projectName);
  const dotnetCatalogue = {
    ...CATALOGUE,
    detectedStacks: ['dotnet'],
    steps: CATALOGUE.steps.map(step => step.id === 'post-lint-scss'
      ? { ...step, applicable: false, effectiveExecution: { executionKind: 'shell', source: 'catalogue', commands: [] } }
      : step),
  };
  await page.route('**/api/**', r => r.fulfill(json({})));
  await page.route('**/api/auth/status', r => r.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/clients**', r => r.fulfill(json([])));
  await page.route('**/api/v1/management/remote-hosts', r => r.fulfill(json([])));
  await page.route('**/api/v1/management/remote-hosts/link-health', r => r.fulfill(json([])));
  await page.route('**/api/tags', r => r.fulfill(json([])));
  await page.route('**/api/orchestrator/sessions', r => r.fulfill(json({ sessions: [] })));
  await page.route('**/api/crash-recovery/pending', r => r.fulfill(json({ pending: [] })));
  await page.route('**/api/cli/*/models*', r => r.fulfill(json({ models: [], source: 'stubbed' })));
  await page.route('**/api/watch-paths', r => r.fulfill(json([preferred])));
  await page.route('**/api/projects', r => r.fulfill(json([])));
  await page.route('**/api/workspaces', r => r.fulfill(json([{
    id: 'WS-1', displayName: 'Workspace', sortOrder: 0, isDefault: true, color: null,
    createdAt: '2026-07-22T00:00:00Z',
    projects: [{
      id: 'PROJ-1', displayName: projectName, shortCode: 'PLH', workspaceId: 'WS-1',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
      storageLocation: preferred.path, archived: false, createdAt: '2026-07-22T00:00:00Z',
    }],
  }])));
  await page.route('**/api/environment', r => r.fulfill(json({
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  })));
  await page.route('**/api/cli/quota', r => r.fulfill(json({
    at: '2026-07-23T01:00:00Z', ttlSeconds: 600, snapshots: [],
  })));
  await page.route('**/api/cli/usage**', r => r.fulfill(json({
    at: '2026-07-23T01:00:00Z', sessions: [],
  })));
  await page.route('**/api/tasks/grouped', r => r.fulfill(json({
    archive: [], autoReview: [], backlog: [], codeNotComplete: [], completed: [],
    failedPickup: [], humanReview: [], orchestratorPrep: [], preparation: [],
    progress: [], ready: [], review: [],
  })));
  await page.route('**/api/tasks/archive**', r => r.fulfill(json({ items: [], total: 0 })));
  await page.route('**/api/runner/status', r => r.fulfill(json({ projects: {} })));
  await page.route('**/api/bus/*/messages**', r => r.fulfill(json([])));
  await page.route('**/api/projects/*/workbenches**', r => r.fulfill(json({
    projectName,
    includesHistory: true,
    count: 0,
    items: [],
  })));
  await page.route('**/api/projects/pipeline-catalogue**', r => r.fulfill(json(dotnetCatalogue)));
  await page.route('**/api/projects/settings', r => r.fulfill(json({ [projectName]: SETTINGS_PROJECTION })));
  await page.route('**/token-usage/pipeline-cost*', r => r.fulfill(json(fakeCost(projectName))));
  await page.route('**/api/projects/*/pipeline-health', r => r.fulfill(json({
    project: projectName,
    capturedAtUtc: '2026-07-23T01:00:00Z',
    status: 'healthy',
    activeGate: null,
    fingerprint: null,
    lanes: [],
    alerts: [],
  })));

  await page.goto(`/#/projects/${projectSlug}/pipeline`);
  const row = page.getByTestId('pipeline-step-row-post-lint-scss');
  await expect(row).toBeVisible();
  await expect(row).toHaveAttribute('data-applicable', 'false');
  await row.evaluate(el => { (el as HTMLDetailsElement).open = true; });
  await expect(page.getByTestId('pipeline-step-not-applicable-post-lint-scss'))
    .toContainText('Requires angular');
});
