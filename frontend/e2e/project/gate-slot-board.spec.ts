import * as fs from 'fs';
import * as path from 'path';
import { test, expect } from '@playwright/test';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3423 evidence: the project Pipeline page shows which remote gate slot
 * runs what, which slot serves short gates, and why a long gate waits.
 * Fully mocked, with the same stubs as `operator-sweeps-visual.spec.ts`.
 */
test('pipeline page shows which gate slot runs what and why a gate waits', async ({ page }) => {
  const project = 'Agent Studio Worktree';
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  const directory = path.join(process.env.JOB_RESULTS_DIR || 'playwright-screenshots', 'gate-slot-board');
  fs.mkdirSync(directory, { recursive: true });
  const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

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
  await page.route('**/api/cli/quota', route => route.fulfill(json({ at: '2026-11-02T08:00:00Z', ttlSeconds: 600, snapshots: [] })));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ at: '2026-11-02T08:00:00Z', sessions: [] })));
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
    project, capturedAtUtc: '2026-11-02T08:00:00Z', status: 'ok',
    activeGate: null, fingerprint: null, lanes: [], alerts: [],
  })));
  await page.route('**/api/usage/cockpit**', route => route.fulfill(json({
    snapshotVersion: 1, workspaceId: 'WS-1', timeZone: 'UTC', weekStart: 1,
    generatedAt: '2026-11-02T08:00:00Z',
    calendar: {
      timeZone: 'UTC', weekStart: 1, dayStartUtc: '2026-11-02T00:00:00Z',
      dayEndUtc: '2026-11-03T00:00:00Z', weekStartUtc: '2026-11-02T00:00:00Z',
      weekEndUtc: '2026-11-09T00:00:00Z',
    },
    clis: [], cost: null, runs: [], slots: [], sources: {},
  })));
  await page.route('**/api/workspaces/*/quota/admission', route => route.fulfill(json({
    workspaceId: 'WS-1',
    workspace: { projectId: null, project: null, status: 'ok', message: null, overrideActive: false,
      codingAllowed: true, hostingAllowed: true, limited: false },
    projects: [],
  })));
  // Pipeline-page reads this spec does not exercise: absent, so their panels stay empty.
  await page.route('**/api/projects/*/deploy/**', route => route.fulfill({ status: 404, body: '' }));
  await page.route('**/api/projects/*/environments**', route => route.fulfill({ status: 404, body: '' }));
  await page.route('**/api/projects/*/pipeline-definition', route => route.fulfill({ status: 404, body: '' }));
  await page.route('**/api/projects/*/token-usage/pipeline-cost**', route => route.fulfill({ status: 404, body: '' }));
  await page.route('**/api/gate-economics**', route => route.fulfill(json({ window: '7d', asOfUtc: '2026-11-02T08:00:00Z', rows: [] })));
  await page.route('**/api/system/gate-slots', route => route.fulfill(json({
    total: 3,
    shortSlot: 2,
    rule: 'Slot 2 serves short gates (median of recent gates at most 10 min); the other slots go in arrival order, '
      + 'a short gate may take any free slot, and a long gate may take slot 2 after waiting 45 min.',
    slots: [
      { slot: 0, shortSlot: false, project, gateId: 'build-test', durationClass: 'long', since: minutesAgo(38) },
      { slot: 1, shortSlot: false, project: 'Quality Studio', gateId: 'build-test', durationClass: 'long', since: minutesAgo(12) },
      { slot: 2, shortSlot: true, project: 'AI Patterns', gateId: 'build-test', durationClass: 'short', since: minutesAgo(1) },
    ],
    waiting: [{
      project, gateId: 'merge-into-main', durationClass: 'long', since: minutesAgo(9),
      reason: 'All 2 shared slots are busy; slot 2 is kept for short gates and opens to this long gate in 36 min.',
    }],
  })));

  await page.setViewportSize({ width: 1440, height: 1600 });
  await page.goto('/#/projects/agent-studio-worktree/pipeline', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  const board = page.getByTestId('gate-slot-board');
  await expect(board).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId('gate-slot-board-busy')).toHaveText('3/3 busy here');
  await expect(page.getByTestId('gate-slot-2')).toContainText('Short gates');
  await expect(page.getByTestId('gate-slot-2')).toContainText('AI Patterns');
  await expect(page.getByTestId('gate-slot-0')).toContainText(project);
  await expect(page.getByTestId('gate-slot-waiter')).toContainText('slot 2 is kept for short gates');
  // The page shell polls unrelated endpoints; any fixture gap must not cover the evidence.
  const dismissErrors = async () => {
    const dialog = page.getByTestId('error-dialog');
    for (let quietChecks = 0; quietChecks < 3;) {
      if (await dialog.isVisible()) {
        console.log(`Visual fixture error: ${await page.getByTestId('error-dialog-message').textContent()}`);
        await page.getByTestId('error-dialog-close').click();
        await expect(dialog).toBeHidden();
        quietChecks = 0;
      } else {
        quietChecks++;
        await page.waitForTimeout(300);
      }
    }
  };

  await setTheme(page, 'light');
  await dismissErrors();
  await board.screenshot({ path: path.join(directory, 'gate-slot-board--light--mocked.png') });
  await setTheme(page, 'dark');
  await dismissErrors();
  await board.screenshot({ path: path.join(directory, 'gate-slot-board--dark--mocked.png') });
});
