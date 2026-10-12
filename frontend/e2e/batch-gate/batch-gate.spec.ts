import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as nodePath from 'node:path';

test.use({ serviceWorkers: 'block' });

const project = 'batch-fixture';
const path = '/tmp/batch-fixture';
const key = `${path}::AGT-2`;
const settings = {
  enabled: true, scope: 'code', planner: 'look-ahead', minimumSize: 2, maximumSize: 4,
  deadlineMinutes: 30, lookAheadMinutes: 15, urgentTags: ['urgent'], riskPolicy: 'balanced',
  overlap: 'separate', reviewTestLevel: 'work-package', gateTimeoutMinutes: 90,
  advisor: { mode: 'enforce', cliType: 'codex', model: 'test-model', timeoutSeconds: 45 },
};
const state = {
  plan: { decision: 'wait', reason: 'Advisor veto', targetSize: 2, closeAtUtc: '2026-10-11T14:05:00Z',
    proposedMembers: [key], upcoming: [], advisor: { mode: 'enforce', verdict: 'veto', reasons: ['Risk too high'], model: 'test-model', atUtc: '2026-10-11T13:00:00Z' } },
  pending: [{ taskKey: key, reviewAttemptId: 'review-1', resultSha: 'a'.repeat(40), waitMinutes: 12, risk: 'high' }],
  batches: [{ batchId: 'batch-7', phase: 'red', baseSha: 'b'.repeat(40), candidateSha: 'c'.repeat(40),
    members: [{ taskKey: 'AGT-2', replay: 'applied', released: false }],
    runs: [{ batchRunId: 'run-7', host: 'agent-runner-01', outcome: 'failed', classification: 'test-failure', startedAtUtc: '2026-10-11T13:00:00Z', finishedAtUtc: '2026-10-11T13:05:00Z' }] },
  { batchId: 'batch-8', parentBatchId: 'batch-7', phase: 'running', baseSha: 'b'.repeat(40), candidateSha: 'd'.repeat(40),
    members: [{ taskKey: 'AGT-2', replay: 'applied', released: false }],
    runs: [{ batchRunId: 'run-8', host: 'agent-runner-02', startedAtUtc: '2026-10-11T13:06:00Z' }] }],
  members: { 'AGT-2': { status: 'in-batch', batchId: 'batch-8', phase: 'running', position: 1, size: 1 } },
};
const task = { id: 'AGT-2', key: 'AGT-2', taskKey: key, title: 'Batch fixture task', state: '4-auto-review', order: 1,
  agent: 'codex', cliType: 'codex', createdAt: '2026-10-11T12:00:00Z', watchPath: path, projectName: project,
  folderPath: `${path}/tasks/AGT-2`, lastActivity: '2026-10-11T13:00:00Z', commits: [], ownerClientId: 'local-default', tags: [] };
const grouped = { backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [], codeNotComplete: [], review: [], autoReview: [task], humanReview: [], escalated: [], completed: [], archive: [] };
const json = (route: import('@playwright/test').Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
async function captureThemes(page: import('@playwright/test').Page, evidence: string | undefined, name: string): Promise<void> {
  if (!evidence) return;
  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await page.screenshot({ path: nodePath.join(evidence, `${name}-${theme}.png`) });
  }
}

test('board badge, batch panel and project settings save use batch contract', async ({ page }) => {
  const evidence = process.env.JOB_RESULTS_DIR;
  if (evidence) fs.mkdirSync(evidence, { recursive: true });
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__' })));
  let saved = false;
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null }));
  await page.route('**/api/v1/studio/board**', route => json(route, grouped));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0 }));
  await page.route('**/api/watch-paths**', route => json(route, [{ name: project, path, rootPath: path, repositoryPath: path }]));
  await page.route('**/api/v1/workspaces**', route => json(route, [{ id: 'workspace-1', displayName: 'Fixture', projects: [{
    id: project, displayName: project, shortCode: 'BAT', color: '#587a9b', sourceType: 'local-folder',
    workspaceId: 'workspace-1', storageLocation: path, rootPath: path, repositoryPath: path,
    repositoryUrl: null, urls: [], archived: false, sortOrder: 0,
  }] }]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/usage/cockpit**', route => json(route, {
    snapshotVersion: 1, workspaceId: null, generatedAt: '2026-10-11T13:00:00Z', timeZone: 'Europe/Berlin', weekStart: 1,
    calendar: { timeZone: 'Europe/Berlin', weekStart: 1, dayStartUtc: '2026-10-11T00:00:00Z', dayEndUtc: '2026-10-12T00:00:00Z', weekStartUtc: '2026-10-05T00:00:00Z', weekEndUtc: '2026-10-12T00:00:00Z' },
    clis: [], cost: null, runs: [], slots: [], sources: {},
  }));
  await page.route('**/api/cli/quota**', route => json(route, { ttlSeconds: 600, snapshots: [] }));
  await page.route('**/api/cli/usage**', route => json(route, { at: '2026-10-11T13:00:00Z', sessions: [] }));
  await page.route('**/api/v1/management/retention/policy/**', route => json(route, { rules: [], revision: 1 }));
  await page.route('**/api/v1/management/retention/policy', route => json(route, { rules: [], revision: 1 }));
  await page.route('**/api/v1/studio/runner/status**', route => json(route, { projects: { [project]: { projectName: project, mode: 'manual', activeJobId: null, queuedJobIds: [] } } }));
  await page.route('**/api/projects/*/batch-gate/state?history=10', route => json(route, state));
  await page.route('**/api/projects/*/snapshot**', route => json(route, {
    settings: { autoCommit: false, crashRecoveryEnabled: false, autoPushStrategy: 'off', runnerMode: 'manual' },
    runnerStatus: null, orchestratorLogTail: [], orchestratorSession: null, paths: null,
    reviewDecisionsPending: [], runnerPendingDecisions: [], queueHealth: null,
  }));
  await page.route('**/api/projects/*/workbenches**', route => json(route, { items: [] }));
  await page.route('**/api/projects/*/execution', route => json(route, { valid: false, issues: [], source: 'repository' }));
  await page.route('**/api/projects/*/batch-gate', route => {
    if (route.request().method() === 'PUT') { saved = true; expect(route.request().headers()['x-client-id']).toBeTruthy(); return json(route, { settings: route.request().postDataJSON(), limits: { hardMaximumSize: 8 } }); }
    return json(route, { settings, limits: { hardMaximumSize: 8 } });
  });
  await page.goto('/?includeFixtures=true');
  // This route-only fixture has no SignalR server; keep its offline banner out of the target surface.
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  const card = page.getByTestId('task-card').filter({ hasText: 'Batch fixture task' });
  await expect(card.getByTestId('batch-member-badge')).toContainText('Batch batch-8');
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  await page.getByTestId('batch-lane-indicator').first().click();
  await expect.poll(async () => (await page.getByTestId('batch-panel').boundingBox())?.x ?? 0).toBeGreaterThan(700);
  await expect(page.getByTestId('batch-panel')).toContainText('Advisor veto');
  await expect(page.getByTestId('batch-panel')).toContainText('from batch-7');
  await expect(page.getByTestId('batch-panel')).toContainText('test-failure');
  await captureThemes(page, evidence, 'batch-panel');
  await page.locator('.batch-panel__body').evaluate(element => { element.scrollTop = element.scrollHeight; });
  await captureThemes(page, evidence, 'batch-panel-history');
  await page.getByRole('button', { name: 'Close batch panel' }).click();
  await expect(page.getByTestId('batch-panel')).toHaveCount(0);
  await expect(page.getByTestId('batch-lane-indicator').first()).toBeFocused();
  await page.goto('/#/projects/batch-fixture/settings');
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  await expect(page.getByTestId('batch-settings')).toBeVisible();
  await page.getByTestId('batch-settings-save').click();
  await expect.poll(() => saved).toBe(true);
  await expect(page.getByTestId('batch-settings')).toContainText('Saved');
  await page.locator('#batch-settings-title').scrollIntoViewIfNeeded();
  await captureThemes(page, evidence, 'batch-settings');
});
