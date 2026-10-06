import * as fs from 'fs';
import * as path from 'path';
import { test, expect } from '@playwright/test';
import { setTheme } from '../helpers/theme';

/** Browser evidence for the real operator-sweeps component with fixed API data. */
test('operator sweeps render beside pipeline health in both themes', async ({ page }) => {
  const project = 'Agent Studio Worktree';
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  const directory = path.join(process.env.JOB_RESULTS_DIR || 'playwright-screenshots', 'operator-sweeps');
  fs.mkdirSync(directory, { recursive: true });

  await page.route('**/api/**', route => route.fulfill(json({})));
  await page.route('**/api/auth/status', route => route.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/clients**', route => route.fulfill(json([])));
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill(json([])));
  await page.route('**/api/v1/management/links', route => route.fulfill(json([])));
  await page.route('**/api/tags', route => route.fulfill(json([])));
  await page.route('**/api/v1/studio/orchestrator/sessions', route => route.fulfill(json({ sessions: [] })));
  await page.route('**/api/crash-recovery/pending', route => route.fulfill(json({ pending: [] })));
  await page.route('**/api/cli/*/models*', route => route.fulfill(json({ models: [], source: 'fixture' })));
  await page.route('**/api/environment', route => route.fulfill(json({
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  })));
  await page.route('**/api/cli/quota', route => route.fulfill(json({ at: '2026-10-05T01:00:00Z', ttlSeconds: 600, snapshots: [] })));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ at: '2026-10-05T01:00:00Z', sessions: [] })));
  await page.route('**/api/watch-paths', route => route.fulfill(json([
    { name: project, path: '/fixtures/agent-studio-worktree' },
  ])));
  await page.route('**/api/v1/workspaces', route => route.fulfill(json([{
    id: 'WS-1', displayName: 'Workspace', sortOrder: 0, isDefault: true,
    projects: [{
      id: 'PROJ-1', displayName: project, shortCode: 'AST', workspaceId: 'WS-1',
      sortOrder: 0, storageLocation: '/fixtures/agent-studio-worktree', archived: false,
    }],
  }])));
  await page.route('**/api/v1/projects', route => route.fulfill(json([])));
  await page.route('**/api/projects/*/workbenches**', route => route.fulfill(json({
    projectName: project, includesHistory: false, count: 0, items: [],
  })));
  await page.route('**/api/v1/management/provider-refusals**', route => route.fulfill(json([])));
  await page.route('**/api/v1/studio/board**', route => route.fulfill(json({
    archive: [], autoReview: [], backlog: [], codeNotComplete: [], completed: [],
    failedPickup: [], humanReview: [], orchestratorPrep: [], preparation: [],
    progress: [], ready: [], review: [],
  })));
  await page.route('**/api/tasks/archive**', route => route.fulfill(json({ items: [], total: 0 })));
  await page.route('**/api/v1/studio/runner/status', route => route.fulfill(json({ projects: {} })));
  await page.route('**/api/bus/*/messages**', route => route.fulfill(json([])));
  await page.route('**/api/projects/settings', route => route.fulfill(json({ [project]: { pipelineSteps: {}, pipelineStepOrder: [] } })));
  await page.route('**/api/projects/pipeline-catalogue**', route => route.fulfill(json({ pipelineId: 'default', steps: [] })));
  await page.route('**/api/projects/*/pipeline-health', route => route.fulfill(json({
    project, capturedAtUtc: '2026-10-05T01:00:00Z', status: 'alarm',
    activeGate: null, fingerprint: null, lanes: [], alerts: [],
  })));
  await page.route('**/api/projects/*/operator-sweeps', route => route.fulfill(json({
    project, capturedAtUtc: '2026-10-05T01:00:00Z', status: 'alarm', enabled: true,
    tickIntervalSeconds: 600, maxRoundsPerCard: 4, lastTickAtUtc: '2026-10-05T00:59:00Z',
    sweeps: [
      { sweep: 'fix-rounds', paused: false, isOverdue: false, lastActed: 1, lastHeld: 2,
        lastWaitingForPerson: 1, lastRunFinishedAtUtc: '2026-10-05T00:59:00Z', recentActions: [] },
      { sweep: 'gate-triage', paused: true, pausedBy: 'human:ops', pauseReason: 'gate host repair',
        isOverdue: false, lastActed: 0, lastHeld: 0, lastWaitingForPerson: 0, recentActions: [] },
      { sweep: 'salvage', paused: false, isOverdue: true, lastActed: 0, lastHeld: 0,
        lastWaitingForPerson: 0, recentActions: [] },
    ],
    cards: [
      { taskKey: 'AGT-2955', jobId: 'AGT-2955', title: 'Budget card', lane: '5-human-review',
        roundsUsed: 4, roundsAllowed: 4, roundsRemaining: 0,
        decisions: [{ sweep: 'fix-rounds', action: 'WaitForPerson', reason: 'round-budget-exhausted',
          detail: 'The card has used its whole round budget; a person decides the next step.' }] },
      { taskKey: 'AGT-3001', jobId: 'AGT-3001', title: 'Fresh failure', lane: '5-human-review',
        roundsUsed: 1, roundsAllowed: 4, roundsRemaining: 3,
        decisions: [{ sweep: 'fix-rounds', action: 'Act', reason: 'fresh-product-failure', detail: 'Fix round opened.' }] },
    ],
    waitingForPerson: [{ taskKey: 'AGT-2955', title: 'Budget card', lane: '5-human-review',
      sweep: 'fix-rounds', reason: 'round-budget-exhausted',
      detail: 'The card has used its whole round budget; a person decides the next step.' }],
  })));

  await page.setViewportSize({ width: 1440, height: 1600 });
  await page.goto('/#/projects/agent-studio-worktree/pipeline', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  const block = page.getByTestId('operator-sweeps');
  await expect(block).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId('pipeline-health')).toBeVisible();
  await expect(page.getByTestId('operator-sweeps-waiting')).toContainText('AGT-2955');
  await expect(page.getByTestId('operator-sweeps-budget')).toContainText('3 of 4 left');
  // The page shell polls unrelated endpoints; any fixture gap must not cover the evidence.
  const dismissErrors = async () => {
    const dialog = page.getByTestId('error-dialog');
    if (await dialog.isVisible()) {
      console.log(`Visual fixture error: ${await page.getByTestId('error-dialog-message').textContent()}`);
      await page.getByTestId('error-dialog-close').click();
      await expect(dialog).toBeHidden();
    }
  };

  await setTheme(page, 'light');
  await dismissErrors();
  await block.screenshot({ path: path.join(directory, 'operator-sweeps--light--mocked.png') });
  await setTheme(page, 'dark');
  await dismissErrors();
  await block.screenshot({ path: path.join(directory, 'operator-sweeps--dark--mocked.png') });
});
