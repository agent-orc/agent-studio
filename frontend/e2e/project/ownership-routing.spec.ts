import { expect, test, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

const PROJECT = 'Coding Agent Chat';
const STORAGE = 'C:/tasks/cac';

const mapping = {
  id: 'cac-agent-studio-chat', observedSurfaces: ['Agent Studio chat message'],
  component: 'Coding Agent Chat rendering footer message components', packageOrModule: 'coding-agent-chat',
  primaryProjectId: 'PROJ-003', repository: 'coding-agent-chat', consumerProjectIds: ['PROJ-002'],
  integrationHosts: ['Agent Studio'], releaseArtifact: 'coding-agent-chat npm package',
  versioningMechanism: 'npm package version', deploymentSteps: ['Publish package', 'Deploy Agent Studio'],
  environments: ['development', 'stable'], allowedTicketPrefix: 'CAC', evidence: ['frontend/AGENTS.md'],
  confidence: 1, unresolvedAlternatives: [], version: 1, updatedAt: '2026-07-12T10:00:00Z', updatedBy: 'owner',
};

async function mockApp(page: Page): Promise<void> {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/**', route => {
    if (route.request().method() === 'PUT' && route.request().url().includes('/ownership-mappings/')) {
      return route.fulfill(json({ ...mapping, releaseArtifact: 'coding-agent-chat npm package vNext', unresolvedAlternatives: ['Legacy host ownership'], version: 2, updatedBy: 'local-default' }));
    }
    return route.fulfill(json([])).catch(() => undefined);
  });
  await page.route('**/api/watch-paths**', route => route.fulfill(json([{ name: PROJECT, path: STORAGE, rootPath: 'C:/repo/cac' }])));
  await page.route('**/api/v1/studio/board**', route => route.fulfill(json({
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    review: [], autoReview: [], humanReview: [], completed: [], archive: [],
  })));
  await page.route('**/api/environment**', route => route.fulfill(json({ isDev: false, devTools: {} })));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill(json({ projects: {} })));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({ ttlSeconds: 600, snapshots: [] })));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ sessions: [] })));
  await page.route('**/api/projects/*/snapshot**', route => route.fulfill(json({
    project: PROJECT, capturedAt: '2026-07-12T10:00:00Z',
    paths: { path: STORAGE, rootPath: 'C:/repo/cac', repositoryPath: 'C:/repo/cac' },
    settings: { runnerMode: 'manual', laneSortStrategies: {} },
    runnerStatus: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
    orchestratorLogTail: [], orchestratorSession: null, reviewDecisionsPending: [], runnerPendingDecisions: [],
    publishTargets: [], queueHealth: { severity: 'ok', issueCount: 0, missingJobJson: [], duplicates: [], stateMismatches: [] },
  })));
  await page.route('**/api/projects/*/visual-evidence', route => route.fulfill(json({
    project: PROJECT, capturedAt: '2026-07-12T10:00:00Z', unseenCount: 0, items: [],
  })));
  await page.route('**/api/projects/*/wiki/pulse**', route => route.fulfill(json({
    projectName: PROJECT, baseDir: 'docs', exists: true, generatedAtUtc: '2026-07-12T10:00:00Z',
    feed: { available: true, reason: null, items: [] },
    inbox: { available: true, reason: null, count: 0, items: [] },
    drift: { available: true, reason: null, overallGrade: 'Fresh', areas: [], counts: { fresh: 0, aging: 0, stale: 0, graded: 0 } },
    critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
  })));
  await page.route('**/api/v1/workspaces**', route => route.fulfill(json([{
    id: 'ws', displayName: 'Workspace', projects: [{
      sourceType: 'local-folder', id: 'PROJ-003', displayName: PROJECT, shortCode: 'CAC', workspaceId: 'ws',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: STORAGE,
      repositoryPath: 'C:/repo/cac', rootPath: 'C:/repo/cac', urls: [], ownershipMappings: [mapping],
      archived: false, createdAt: '2026-07-01T00:00:00Z',
    }],
  }])));
}

test.describe('Project Hub ownership routing', () => {
  test('edits and versions a component ownership mapping', async ({ page }, testInfo) => {
    await page.addInitScript(({ project }) => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName: project, section: 'overview' }],
      activeKey: `hub:${project}`,
    })), { project: PROJECT });
    await mockApp(page);
    await page.goto('/');

    await page.evaluate(() => document.querySelectorAll('[data-testid="error-dialog-overlay"], vite-error-overlay')
      .forEach(element => ((element as HTMLElement).style.display = 'none')));
    await page.getByRole('button', { name: 'Ownership Routing' }).click();
    const panel = page.getByTestId('ownership-mapping-panel');
    await expect(panel).toBeVisible({ timeout: 60_000 });
    await expect(page.getByTestId('ownership-mapping-cac-agent-studio-chat')).toContainText('Version 1');
    await panel.getByLabel('Release artifact').fill('coding-agent-chat npm package vNext');
    await panel.getByLabel('Unresolved alternatives').fill('Legacy host ownership');
    await page.route('**/api/projects/**/ownership-mappings/**', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({
        ...mapping, releaseArtifact: 'coding-agent-chat npm package vNext', unresolvedAlternatives: ['Legacy host ownership'], version: 2, updatedBy: 'local-default',
      }),
    }));
    const saveRequest = page.waitForRequest(request => request.method() === 'PUT' && request.url().includes('/ownership-mappings/'));
    const saveResponse = page.waitForResponse(response => response.request().method() === 'PUT' && response.url().includes('/ownership-mappings/'));
    await page.getByTestId('save-ownership-mapping').click();
    expect((await saveRequest).postDataJSON().releaseArtifact).toBe('coding-agent-chat npm package vNext');
    expect((await saveRequest).postDataJSON().unresolvedAlternatives).toEqual(['Legacy host ownership']);
    const response = await saveResponse;
    expect(response.url()).toContain('/api/');
    expect(response.status()).toBe(200);
    expect(await response.json()).toMatchObject({ id: 'cac-agent-studio-chat', version: 2 });
    await expect(panel).toContainText('Saved cac-agent-studio-chat as version 2');

    const output = path.join(process.env.JOB_RESULTS_DIR || testInfo.outputDir, 'ownership-routing--mocked.png');
    fs.mkdirSync(path.dirname(output), { recursive: true });
    await panel.screenshot({ path: output });
    await testInfo.attach('ownership-routing--mocked.png', { path: output, contentType: 'image/png' });
  });
});
