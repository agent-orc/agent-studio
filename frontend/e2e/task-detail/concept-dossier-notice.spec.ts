import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'fs';
import * as path from 'path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'Concept dossier fixture';
const WATCH_PATH = 'C:/fixtures/concept-dossier';
const JOB_ID = 'concept-dossier-card';
const DOSSIER_PATH = 'docs/coding-agent-sidesheet/index.html';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve('test-results', 'concept-dossier-notice');

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

function taskInfo(conceptDossier: Record<string, unknown>) {
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
    conceptDossier,
  };
}

function detail(conceptDossier: Record<string, unknown>) {
  return {
    info: taskInfo(conceptDossier),
    promptMarkdown: '# Concept\n\nPrepare the dossier.',
    statusMarkdown: '# Status\n\nConcept complete.',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: null,
  };
}

async function installRoutes(page: Page): Promise<{ mutationBodies: unknown[] }> {
  const emptyDossier = { noDossierNeeded: false, contractSatisfied: false };
  let current = emptyDossier;
  const mutationBodies: unknown[] = [];
  const escapedId = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: false, user: null,
  }));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    codeNotComplete: [], review: [], autoReview: [], humanReview: [taskInfo(current)],
    escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/projects/settings**', route => json(route, {}));
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, route => json(route, []));
  await page.route('**/api/clients', route => json(route, [
    { id: 'local-default', displayName: 'Local', kind: 'agent-instance' },
  ]));
  await page.route('**/api/clients/local-default/defaults**', route => json(route, {}));
  await page.route('**/api/clients/local-default/telemetry**', route => json(route, {
    points: [], findings: [], window: '14d',
  }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-08-09T18:30:00.000Z', ttlSeconds: 600, snapshots: [],
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
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${escapedId}(\\?|$)`), route => json(route, detail(current)));
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

test('concept completion shows one compact dossier sensor and links the recorded path', async ({ page }) => {
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
  const link = page.getByTestId('concept-dossier-link');
  await expect(link).toHaveText(DOSSIER_PATH);
  await expect(link).toHaveAttribute(
    'href',
    '#/projects/concept-dossier-fixture/wiki?page=coding-agent-sidesheet%2Findex.html',
  );

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await notice.screenshot({ path: path.join(RESULTS_DIR, `concept-dossier-linked-${theme}--mocked.png`) });
  }
});
