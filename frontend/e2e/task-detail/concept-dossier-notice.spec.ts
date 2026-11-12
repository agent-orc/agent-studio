import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'fs';
import * as path from 'path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';
import { SNAPSHOT } from '../../src/mockups/usage-chips/app/usage-chips.fixtures';

const PROJECT = 'Concept dossier fixture';
const WATCH_PATH = 'C:/fixtures/concept-dossier';
const JOB_ID = 'concept-dossier-card';
const DOSSIER_PATH = 'docs/coding-agent-sidesheet/index.html';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve('test-results', 'concept-dossier-notice');
const REGISTERED_PROJECT = {
  id: PROJECT, displayName: PROJECT, shortCode: 'MKT', workspaceId: 'ws-concept',
  storageLocation: WATCH_PATH, sortOrder: 0, archived: false, createdAt: '2026-08-09T18:00:00Z',
};

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

function taskInfo(conceptDossier: Record<string, unknown>, published = false, deliveryPending = false) {
  return {
    id: JOB_ID,
    taskKey: `${WATCH_PATH}::${JOB_ID}`,
    key: 'MKT-21',
    title: 'Coding agent side sheet concept',
    state: '5-human-review',
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    model: 'gpt-5.6-sol',
    createdAt: '2026-08-09T18:00:00.000Z',
    lastActivity: '2026-08-09T18:30:00.000Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/5-human-review/${JOB_ID}`,
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    mode: 'concept',
    requiresIntegration: false,
    integration: {
      status: published ? 'integrated' : 'pending',
      deliveryRef: 'refs/results/concept-dossier-card',
      sha: published ? 'abc123' : null,
      integrationBranch: 'develop',
      detail: deliveryPending ? 'Delivery pending.' : null,
    },
    dossier: conceptDossier['repoRelativePath'] ? { path: conceptDossier['repoRelativePath'] } : null,
    conceptDossier,
    ...(deliveryPending ? { testEvidence: {
      evidenceState: 'unassigned', summary: 'Review build-tests Not proven',
      sources: [{ kind: 'review-build-tests', id: 'review-1', summary: 'Review build-tests Not proven' }],
    } } : {}),
  };
}

function detail(conceptDossier: Record<string, unknown>, published = false, deliveryPending = false) {
  return {
    info: taskInfo(conceptDossier, published, deliveryPending),
    promptMarkdown: '# Concept\n\nPrepare the dossier.',
    statusMarkdown: '# Status\n\nConcept complete.',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: null,
  };
}

async function installRoutes(page: Page, published = false, deliveryPending = false): Promise<{ mutationBodies: unknown[] }> {
  const emptyDossier = { noDossierNeeded: false, contractSatisfied: false };
  let current: Record<string, unknown> = published
    ? { repoRelativePath: DOSSIER_PATH, noDossierNeeded: false, contractSatisfied: true }
    : emptyDossier;
  const mutationBodies: unknown[] = [];
  const escapedId = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/usage/cockpit**', route => json(route, {
    ...SNAPSHOT, workspaceId: 'ws-concept',
  }));
  await page.route('**/api/v1/management/credential-inventory**', route => json(route, {
    generatedAt: '2026-08-09T18:00:00Z', items: [],
  }));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    codeNotComplete: [], review: [], autoReview: [], humanReview: [taskInfo(current, published, deliveryPending)],
    escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/projects/settings**', route => json(route, {}));
  await page.route(/\/api\/projects\/[^/]+\/workbenches(?:\?|$)/, route => json(route, { items: [] }));
  await page.route(/\/api\/v1\/workspaces(?:\?|$)/, route => json(route, [{
    id: 'ws-concept', displayName: 'Concept fixtures', sortOrder: 0, isDefault: true,
    createdAt: '2026-08-09T18:00:00Z', projects: [REGISTERED_PROJECT],
  }]));
  await page.route(/\/api\/v1\/projects(?:\?|$)/, route => json(route, [REGISTERED_PROJECT]));
  await page.route(/\/api\/workspaces\/[^/]+\/quota\/admission(?:\?|$)/, route => json(route, {
    workspaceId: 'ws-concept',
    workspace: { projectId: null, project: null, status: 'ok', message: null,
      overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false },
    projects: [],
  }));
  await page.route('**/api/clients', route => json(route, [
    { id: 'local-default', displayName: 'Local', kind: 'agent-instance' },
  ]));
  await page.route(/\/api\/workbenches\?project=/, route => json(route, {
    projectName: PROJECT,
    count: published && current.repoRelativePath ? 1 : 0,
    currentCount: published && current.repoRelativePath ? 1 : 0,
    historyCount: 0,
    items: published && current.repoRelativePath ? [{ projectName: PROJECT, workbench: {
      id: 'DEMO-W2', entryPath: DOSSIER_PATH, title: 'Export retention', summary: '',
      status: 'decided', phase: 'decision-ready', updatedAtUtc: '2026-08-09T18:30:00Z',
      valid: true, error: null, sourceTaskKeys: ['MKT-21'], linkedTaskKeys: ['MKT-21', 'DA-1'],
    } }] : [],
  }));
  await page.route(new RegExp(`/api/projects/${encodeURIComponent(PROJECT)}/workbenches(?:\\?|$)`), route => json(route, {
    projectName: PROJECT,
    count: published && current.repoRelativePath ? 1 : 0,
    items: published && current.repoRelativePath ? [{
      id: 'DEMO-W2', key: 'DEMO-W2', entryPath: DOSSIER_PATH, valid: true,
      sourceTaskKeys: ['MKT-21'], linkedTaskKeys: ['MKT-21', 'DA-1'],
    }] : [],
  }));
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/DEMO-W2`, route => json(route, {
    workbench: { decision: {
      responses: [{ decisionId: 'retention', selectedOptionIds: ['keep'] }],
      spawnedTaskKeys: ['DA-1'],
    } },
    html: '<section data-decision-id="retention" data-decision-kind="single"><span data-option-id="keep">Keep</span></section>',
  }));
  await page.route('**/api/clients/local-default/defaults**', route => json(route, {}));
  await page.route('**/api/clients/local-default/telemetry**', route => json(route, {
    points: [], findings: [], window: '14d',
  }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-08-09T18:30:00.000Z', ttlSeconds: 600, snapshots: [],
  }));
  await page.route('**/api/usage/cockpit**', route => json(route, {
    workspaceId: null, clis: [], cost: null,
  }));
  await page.route('**/api/runner/orchestrator-feed**', route => json(route, {
    entries: [], generatedAtUtc: '2026-08-09T18:30:00.000Z',
  }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route(new RegExp(`/api/tasks/${escapedId}/output(\\?|$)`), route => json(route, []));
  await page.route(new RegExp(`/api/tasks/${escapedId}/runs(\\?|$)`), route => json(route, { runs: [] }));
  await page.route(new RegExp(`/api/tasks/${escapedId}/session-events(\\?|$)`), route => json(route, {
    events: [], sessionChain: [],
  }));
  await page.route(new RegExp(`/api/tasks/${escapedId}/pipeline(\\?|$)`), route => json(route, {
    pipeline: {
      id: 'concept-task-pipeline', displayName: 'Concept task pipeline', version: 1,
      pre: [], core: [], post: [], allSteps: [],
    },
    execution: null,
    cost: null,
    config: {},
  }));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${escapedId}(\\?|$)`), route => json(route, detail(current, published, deliveryPending)));
  await page.route(new RegExp(`/api/tasks/${escapedId}/concept-dossier(\\?|$)`), async route => {
    mutationBodies.push(await route.request().postDataJSON());
    current = {
      repoRelativePath: DOSSIER_PATH,
      referenceSource: 'results/deliverables.md',
      noDossierNeeded: false,
      contractSatisfied: true,
    };
    await json(route, current);
  });
  return { mutationBodies };
}

test('concept Dossier shows its stage before publication', async ({ page }) => {
  mkdirSync(RESULTS_DIR, { recursive: true });
  const { mutationBodies } = await installRoutes(page);
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await dismissDevErrorDialog(page);

  const notice = page.getByTestId('concept-dossier-notice');
  await expect(notice).toHaveCount(1);
  await expect(page.getByTestId('concept-dossier-missing')).toHaveText('No dossier linked');
  await expect(page.getByTestId('concept-dossier-add-path')).toBeVisible();
  await expect(page.getByTestId('concept-dossier-no-need')).toBeVisible();
  await setTheme(page, 'light');
  await notice.screenshot({ path: path.join(RESULTS_DIR, 'concept-dossier-missing-light--mocked.png') });

  await page.getByTestId('concept-dossier-add-path').click();
  await page.getByTestId('concept-dossier-path-input').fill(DOSSIER_PATH);
  await page.getByTestId('concept-dossier-path-form').getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => mutationBodies).toEqual([{ path: DOSSIER_PATH, noDossierNeeded: false }]);
  await expect(page.getByTestId('concept-dossier-link')).toHaveCount(0);
  await expect(page.getByTestId('concept-dossier-path')).toHaveText(DOSSIER_PATH);
  await expect(page.getByTestId('concept-dossier-publication'))
    .toHaveText('Not published yet · Awaiting integration');
  await expect(page.getByTestId('studio-triage-action-mark-done')).toContainText('Await integration');

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await notice.screenshot({ path: path.join(RESULTS_DIR, `concept-dossier-pending-${theme}--mocked.png`) });
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await setTheme(page, 'light');
  await notice.screenshot({ path: path.join(RESULTS_DIR, 'concept-dossier-pending-390--mocked.png') });
});

test('published concept Dossier opens its integration Workbench', async ({ page }) => {
  await installRoutes(page, true);
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await dismissDevErrorDialog(page);
  const link = page.getByTestId('concept-dossier-link');
  await expect(link).toHaveText(DOSSIER_PATH);
  await expect(link).toHaveAttribute(
    'href', '#/projects/concept-dossier-fixture/workbenches/DEMO-W2',
  );
  await expect(page.getByTestId('concept-dossier-decision-receipt'))
    .toContainText('Decisions recorded on DEMO-W2: 1 of 1; spawned DA-1.');
  await expect(page.getByTestId('concept-dossier-publication')).toHaveCount(0);
});

test('concept detail omits delivery context and the Git pane in both themes and on a phone', async ({ page }) => {
  mkdirSync(RESULTS_DIR, { recursive: true });
  await installRoutes(page, false, true);
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await dismissDevErrorDialog(page);

  await expect(page.getByTestId('concept-dossier-notice')).toBeVisible();
  await expect(page.getByTestId('error-dialog')).toHaveCount(0);
  await expect(page.getByTestId('delivery-claim-panel')).toHaveCount(0);
  await expect(page.getByTestId('integration-dead-end')).toHaveCount(0);
  await expect(page.getByTestId('pane-toggle-git')).toHaveCount(0);
  await expect(page.locator('app-git-pane')).toHaveCount(0);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: path.join(RESULTS_DIR, `concept-review-detail-${theme}.png`) });
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: path.join(RESULTS_DIR, 'concept-review-detail-390px.png') });
});
