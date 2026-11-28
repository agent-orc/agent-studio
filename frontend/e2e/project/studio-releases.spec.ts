import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Studio release fixture';
const WATCH = '/fixtures/studio-releases';
const SHA = 'a'.repeat(40);
const task = {
  id: 'fix-layout', key: 'DEMO-1', taskKey: `${WATCH}::fix-layout`, title: 'Keep card details visible on narrow screens',
  state: '6-completed', agent: 'codex', cliType: 'codex', projectName: PROJECT, watchPath: WATCH,
  folderPath: `${WATCH}/6-completed/fix-layout`, createdAt: '2026-11-26T01:00:00Z', taskType: 'bug',
  tags: [], commits: [], integration: { status: 'integrated', integrationBranch: 'develop' },
};
const card = { key: 'DEMO-1', title: task.title, type: 'bug', href: '#/tasks/DEMO-1', integratedAtUtc: '2026-11-26T03:00:00Z' };

test('Releases shows waiting deliveries, history, and the card live state in both themes and widths', async ({ page }) => {
  test.setTimeout(120000);
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  page.on('console', message => { if (message.text().includes('NG0955')) errors.push(message.text()); });
  let live = false;
  await page.clock.setFixedTime(new Date('2026-11-26T12:00:00Z'));
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    const path = decodeURIComponent(url.pathname);
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    if (path.endsWith('/auth/status')) return json({ profile: 'local', authenticated: true, bootstrapRequired: false, user: null });
    if (path === '/api/usage/cockpit') return json({ snapshotVersion: 1, workspaceId: 'WS-1', timeZone: 'UTC', weekStart: 1, generatedAt: '2026-11-26T12:00:00Z', calendar: { timeZone: 'UTC', weekStart: 1, dayStartUtc: '2026-11-26T00:00:00Z', dayEndUtc: '2026-11-27T00:00:00Z', weekStartUtc: '2026-11-23T00:00:00Z', weekEndUtc: '2026-11-30T00:00:00Z' }, clis: [], cost: null, runs: [], slots: [], sources: {} });
    if (path === '/api/v1/operations/structure') return json({ nodes: [], links: [], revision: 1 });
    if (path === '/api/v1/operations/structure/library') return json({ registries: [], consumers: [] });
    if (path.endsWith('/environments')) return json({ project: PROJECT, environments: [], promotions: [] });
    if (path.endsWith('/environments/health')) return json({ environments: [], generatedAt: '2026-11-26T12:00:00Z' });
    if (path.endsWith('/deploy/operations')) return json({ project: PROJECT, since: '2026-10-27T12:00:00Z', until: '2026-11-26T12:00:00Z', environments: [], dora: { successfulProductionDeploys: 0, deploysPerWeek: null, medianCardToProductionHours: null, cardsMeasured: 0, changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 } });
    if (path === '/api/diagnostics/api-stalls') return json({ status: 'ok', recent: [], checkedAtUtc: '2026-11-26T12:00:00Z', intervalMs: 1000, warningThresholdMs: 1000, alarmThresholdMs: 5000, alarmWindowMinutes: 60, maxDelayMs: 0 });
    if (path.startsWith('/api/runtime/') && path.endsWith('/events')) return json({ project: PROJECT, events: [], warnings: [] });
    if (path === '/api/runtime/task-detail-latency') return json({ count: 0, p50Ms: null, p95Ms: null, maxMs: null });
    if (path === '/api/environment') return json({ isDev: false, devTools: {} });
    if (path === '/api/watch-paths') return json([{ name: PROJECT, path: WATCH, rootPath: WATCH, repositoryPath: WATCH }]);
    if (path.endsWith('/quota/admission')) return json({ workspaceId: 'WS-1', workspace: { projectId: null, project: null, status: 'ok', message: null, overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false }, projects: [], cardRefusals: {} });
    if (path.endsWith('/workspaces')) return json([{ id: 'WS-1', displayName: 'Workspace', projects: [{ id: 'PROJ-1', displayName: PROJECT, storageLocation: WATCH, repositoryPath: WATCH, rootPath: WATCH, repositoryUrl: null, urls: [], shortCode: 'DEMO', workspaceId: 'WS-1', archived: false }] }]);
    if (path.endsWith('/board')) return json({ backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [], escalated: [], completed: [task], archive: [] });
    if (path.endsWith('/studio-releases/cards/DEMO-1')) return json({ state: live ? 'live' : 'integrated-not-live', revision: live ? SHA : null, sinceUtc: live ? '2026-11-26T11:00:00Z' : null });
    if (path.endsWith('/studio-releases')) return json({ project: PROJECT, runningRevision: live ? SHA : 'b'.repeat(40),
      computedAtUtc: '2026-11-26T12:00:00Z', detail: null, pending: live ? [] : [card],
      releases: [{ occurrenceId: 'a-first', revision: 'b'.repeat(40), atUtc: '2026-11-26T01:00:00Z', observed: false, running: false, cards: [] },
      { occurrenceId: 'b-first', revision: SHA, atUtc: '2026-11-26T02:00:00Z', observed: false, running: false,
        cards: [{ ...card, key: 'DEMO-2', title: 'Show the delivery revision', type: 'feature', href: '#/tasks/DEMO-2' }] },
      { occurrenceId: 'a-rollback', revision: 'b'.repeat(40), atUtc: '2026-11-26T03:00:00Z', observed: true, running: !live, cards: [] },
      ...(live ? [{ occurrenceId: 'b-return', revision: SHA, atUtc: '2026-11-26T11:00:00Z', observed: false, running: true, cards: [card] }] : [])] });
    if (/\/api\/tasks\/[^/]+\/runs$/.test(path)) return json({ runCount: 0, firstStartedAt: null, lastActivityAt: null, hasActiveRun: false, runs: [], promptEntries: [], refinements: [], runnerEvents: [] });
    if (path.endsWith('/session-events')) return json({ events: [], sessionChain: [] });
    if (path.endsWith('/plan')) return json({ hasPlan: false, source: null, snapshotCount: 0, activeItemId: null, items: [], unassignedSubActions: [] });
    if (path.endsWith('/pipeline')) return json({ pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] }, execution: null, executions: [], config: {}, cost: null });
    if (path.endsWith('/delivery-claim')) return json({ studioLive: { state: live ? 'live' : 'integrated-not-live', revision: live ? SHA : null, sinceUtc: live ? '2026-11-26T11:00:00Z' : null }, taskKey: 'DEMO-1', jobId: task.id, lane: task.state, deliveryRef: 'task/DEMO-1', integrated: true, integrationBranch: 'develop', containmentStatus: 'integrated', released: true, releaseBranch: 'main', commits: [], findings: [] });
    if (path.endsWith('/workbenches')) return json({ projectName: PROJECT, includesHistory: false, count: 0, items: [] });
    if (path.endsWith('/reference-status')) return json({ items: [] });
    if (/\/api\/tasks\/(fix-layout|DEMO-1)\/core$/.test(path)) return json({
      ...task, state: 'ready', projectId: 'PROJ-1', lane: task.state, kind: 'task', mode: 'coding',
      enteredLaneAt: task.createdAt, order: 1, released: true, pendingIntent: false, coreVersion: '1', runtimeVersion: '1',
      pins: { modelExplicit: false, thinkingLevelExplicit: false, cliType: 'codex', allowWebAccess: false, noBranchExpected: false },
      actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
      blocking: { dependencyBlocked: false, dependencyState: 'ready', dependencies: [], dependsOn: [], blockedBy: [] },
      runtime: { location: 'none', leaseState: 'none' },
      statusSummary: { state: 'ready', text: 'Delivered.', originalBytes: 10 },
      prompt: { state: 'ready', text: 'Fix the layout.', originalBytes: 15 },
      timeline: { state: 'ready', events: [], originalBytes: 0 },
    });
    const resource = path.match(/\/api\/tasks\/(fix-layout|DEMO-1)\/details\/(\w+)$/)?.[2];
    if (resource) {
      const data = resource === 'documents' ? { name: url.searchParams.get('name'), markdown: 'Delivered.', summaryState: null }
        : resource === 'usage' ? { tokenSummary: null, lastUsage: null, contextUsage: null }
        : resource === 'review' ? { reviewProjection: null, evidence: [] }
        : resource === 'overview' ? { relatedWikiPages: [], references: [], decision: null, planningSpawn: null, conceptDossier: null, olderBriefDelivery: null, completionClaim: null, parkedBlocker: null, needsInput: null, waitsOn: null }
        : {};
      return json({ id: task.id, taskKey: task.taskKey, projectId: 'PROJ-1', attemptId: null, coreVersion: '1',
        resource, version: '1', computedAt: '2026-11-26T12:00:00Z', state: 'ready', data, reason: null });
    }
    if (path === '/api/tasks' || path === '/api/jobs') return json([task]);
    if (/\/api\/(tasks|jobs)\/(fix-layout|DEMO-1)$/.test(path)) return json(task);
    if (path.includes('/resolve')) return json(task);
    if (path.endsWith('/runner/status')) return json({ projects: {} });
    if (path.startsWith('/api/clients/') && path.endsWith('/telemetry')) return json({ points: [], findings: [], window: '14d' });
    if (path === '/api/clients') return json([{ id: 'local-default', displayName: 'Local', kind: 'agent-instance' }]);
    if (path.includes('/cli/')) return json({ snapshots: [], sessions: [] });
    if (path.includes('/archive')) return json({ items: [], total: 0 });
    return json([]);
  });
  await page.addInitScript(({ project }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1,
      tabs: [{ kind: 'hub', projectName: project, section: 'studio-releases' }], activeKey: `hub:${project}` }));
  }, { project: PROJECT });
  await page.goto('/#/projects/PROJ-1/studio-releases');
  const releases = page.getByTestId('studio-releases');
  await expect(releases).toBeVisible({ timeout: 30000 });
  await expect(releases.getByTestId('studio-release-pending')).toContainText(task.title);
  await expect(releases.getByTestId('studio-release-pending')).toContainText('Waiting 9 h 0 min');
  await expect(releases.getByTestId('studio-release-entry')).toHaveCount(3);
  await expect(releases.getByText('Running', { exact: true })).toHaveCount(1);
  await expect(releases.getByTestId('studio-release-entry').nth(2)).toContainText('Running');
  await expect(releases.getByTestId('studio-release-entry').nth(2).locator('time')).toHaveAttribute('datetime', '2026-11-26T03:00:00Z');
  await expect(releases.getByTestId('studio-release-card')).toContainText('feature');
  const results = process.env.JOB_RESULTS_DIR;
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      expect(await releases.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
      if (results) {
        mkdirSync(results, { recursive: true });
        await releases.screenshot({ path: join(results, `releases-${process.env.RELEASE_CAPTURE_ROUND ?? 'round1'}-${width}-${theme}--mocked.png`) });
      }
    }
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await releases.getByRole('link', { name: 'DEMO-1', exact: true }).click();
  await expect(page.getByTestId('studio-card-live-state')).toHaveText('Integrated, not live');
  live = true;
  await expect(page.getByTestId('studio-card-live-state')).toContainText('Live since aaaaaaaaaa', { timeout: 45000 });
  if (results) {
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: 900 });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await page.getByTestId('delivery-claim-panel').screenshot({ path: join(results, `card-live-${width}-${theme}--mocked.png`) });
      }
    }
  }
  await page.goto('/#/projects/PROJ-1/operations');
  const entry = page.getByTestId('operations-releases');
  await expect(entry).toBeVisible();
  expect((await entry.boundingBox())!.height, 'Operations release link has a phone touch target').toBeGreaterThanOrEqual(44);
  await entry.click();
  await expect(page.getByTestId('studio-releases')).toBeVisible();
  await expect(releases.getByTestId('studio-release-entry')).toHaveCount(4);
  await expect(releases.getByText('Running', { exact: true })).toHaveCount(1);
  await expect(releases.getByTestId('studio-release-entry').nth(3)).toContainText('Running');
  await expect(releases.getByTestId('studio-release-entry').nth(3).locator('time')).toHaveAttribute('datetime', '2026-11-26T11:00:00Z');
  expect(await releases.getByTestId('studio-release-entry').evaluateAll(rows => rows.map(row => row.getAttribute('data-occurrence-id'))))
    .toEqual(['a-first', 'b-first', 'a-rollback', 'b-return']);
  if (results) await releases.screenshot({ path: join(results, 'releases-b-return--mocked.png') });
  expect(errors).toEqual([]);
});
