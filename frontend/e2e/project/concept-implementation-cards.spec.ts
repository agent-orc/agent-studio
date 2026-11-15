import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

/**
 * AGT-3701 - Create implementation cards from a concept Dossier.
 *
 * The Dossier view and the concept card both offer one action that lists
 * every implementation slice with its order, title and working decision, and
 * lets the owner create all or some of them. Promotion never moves the
 * concept card, so the POST answers with the created cards and nothing else.
 * "Start task" says that it creates one combined card.
 *
 * Fully mocked via route interception.
 */

const PROJECT = 'Concept Evidence';
const WATCH_PATH = 'C:/evidence/concept';
const WORKBENCH_ID = 'delivery-flow';
const WORKBENCH_KEY = 'AGT-W40';
const CONCEPT_KEY = 'AGT-3600';
const RESULTS = resolve(
  process.env['JOB_RESULTS_DIR'] ?? resolve(__dirname, '..', '..', 'test-results', 'concept-implementation-cards'),
);

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
  escalated: [], review: [], completed: [], archive: [],
};

const DOSSIER_HTML = `<main>
  <h1>Delivery flow</h1>
  <p>Ship the approved delivery design in three reviewable slices.</p>
  <section data-decision-id="delivery-mode" data-decision-kind="single">
    <h2>Choose the delivery mode</h2>
    <ul>
      <li data-option-id="queued">A · Queued (recommended)</li>
      <li data-option-id="direct">B · Direct</li>
    </ul>
  </section>
</main>`;

const ITEMS = [
  { title: 'Add the queued delivery endpoint', promptMarkdown: 'API', section: '§4', decisionId: 'delivery-mode' },
  { title: 'Show delivery state on the board card', promptMarkdown: 'UI', section: '§5' },
  { title: 'Document the delivery runbook', promptMarkdown: 'Docs', decisionId: 'delivery-mode' },
];

const PLAN = {
  source: {
    repoRelativePath: 'docs/delivery-flow/index.html',
    title: 'Delivery flow',
    decisions: [
      { id: 'delivery-mode', label: 'Delivery mode', optionId: 'queued', optionLabel: 'A · Queued',
        operatorSelected: false, isWorkingAssumption: true },
      { id: 'delivery-mode', label: 'Delivery mode', optionId: 'direct', optionLabel: 'B · Direct',
        operatorSelected: false, isWorkingAssumption: false },
    ],
  },
  items: ITEMS,
  mode: 'coding',
  targetState: '1-preparation',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
};

interface Captured {
  promotionRequests: { url: string; body: Record<string, unknown> }[];
  moves: string[];
}

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function conceptInfo() {
  return {
    id: 'delivery-flow-concept', key: CONCEPT_KEY, displayKey: CONCEPT_KEY,
    taskKey: `${PROJECT}::${CONCEPT_KEY}`, title: 'Delivery flow concept',
    state: '5-human-review', order: 1, agent: 'claude', cliType: 'claude', model: null,
    mode: 'concept', kind: 'task', watchPath: WATCH_PATH, projectName: PROJECT,
    folderPath: `${WATCH_PATH}/5-human-review/delivery-flow-concept`,
    createdAt: '2026-11-09T09:00:00Z', lastActivity: '2026-11-09T10:00:00Z',
    sessionName: null, useOwnSession: null, lastUsage: null, execution: null, commit: null,
    commits: [], tags: [],
    conceptDossier: {
      repoRelativePath: 'docs/delivery-flow/index.html',
      contractSatisfied: true, noDossierNeeded: false,
    },
  };
}

async function installMocks(page: Page): Promise<Captured> {
  const captured: Captured = { promotionRequests: [], moves: [] };
  const created = new Map<number, { jobId: string; taskKey: string; title: string; itemIndex: number; state: string }>();

  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/watch-paths**', route => json(route, [{
    name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH,
  }]));
  await page.route('**/api/v1/workspaces**', route => json(route, [{
    id: 'workspace-evidence', displayName: 'Evidence', sortOrder: 0, isDefault: true,
    projects: [{
      id: 'project-concept', displayName: PROJECT, shortCode: 'CEV',
      workspaceId: 'workspace-evidence', storageLocation: WATCH_PATH,
      sortOrder: 0, archived: false, urls: [],
    }],
  }]));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-11-10T10:00:00Z', snapshots: [], ttlSeconds: 600,
  }));
  await page.route('**/api/usage/cockpit**', route => json(route, {
    snapshotVersion: 1, workspaceId: 'fixture', timeZone: 'UTC', weekStart: 1,
    generatedAt: '2026-11-10T10:00:00Z', calendar: { timeZone: 'UTC', weekStart: 1 }, clis: [],
    cost: { currency: 'USD', todayUsd: null, weekUsd: null, projects: [],
      coverage: { status: 'unavailable', observedAt: null, ttlSeconds: null } },
    runs: [], slots: [], sources: {},
  }));
  await page.route('**/api/workspaces/*/quota/admission**', route => json(route, {
    workspaceId: 'workspace-evidence',
    workspace: { codingAllowed: true, hostingAllowed: true, limited: false },
    projects: [],
  }));
  await page.route('**/api/cli/usage**', route => json(route, { at: '2026-11-10T10:00:00Z', sessions: [] }));
  await page.route('**/api/crash-recovery/pending**', route => json(route, { pending: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    ...EMPTY_GROUPED, humanReview: [conceptInfo()],
  }));
  await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/delivery-flow-concept(\?|$)/, route => json(route, {
    info: conceptInfo(), promptMarkdown: '# Concept', promptHistory: [], titleHistory: [],
    statusMarkdown: '# Status\n\n- Result: Done', statusGeneration: null, contextUsage: null, log: [],
    summaryState: { status: 'ready', startedAt: null, finishedAt: null, errorMessage: null, bytesWritten: 10 },
    reviewEvidence: [],
  }));
  await page.route(/\/api\/tasks\/[^/]+\/pipeline(\?|$)/, route => json(route, null));
  await page.route(/\/api\/tasks\/[^/]+\/runs(\?|$)/, route => json(route, { runs: [] }));
  await page.route(/\/api\/tasks\/[^/]+\/session-events(\?|$)/, route => json(route, { events: [], sessionChain: [] }));
  await page.route(/\/api\/tasks\/[^/]+\/(agent-work|claude-session)(\?|$)/, route => json(route, null));
  await page.route(/\/api\/tasks\/[^/]+\/move(\?|$)/, route => {
    captured.moves.push(route.request().url());
    return json(route, { success: true });
  });

  const workbench = () => ({
    id: WORKBENCH_ID, key: WORKBENCH_KEY, title: 'Delivery flow',
    summary: 'Ship the approved delivery design in three reviewable slices.',
    status: 'active', phase: 'decision-ready', updatedAtUtc: '2026-11-09T10:00:00Z',
    entryPath: 'docs/delivery-flow/index.html', valid: true, error: null,
    sourceTaskKeys: [CONCEPT_KEY], relatedTaskKeys: [],
  });
  await page.route('**/api/workbenches**', route => json(route, {
    includesHistory: true, count: 1, items: [{ projectName: PROJECT, workbench: workbench() }],
  }));
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/workbenches**`, route => json(route, {
    projectName: PROJECT, includesHistory: false, count: 1, items: [workbench()],
  }));
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_KEY}/references`,
    route => json(route, {
      projectName: PROJECT, workbenchKey: WORKBENCH_KEY, workbenchId: WORKBENCH_ID, legacyTaskKeys: [], items: [],
    }),
  );
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}`, route => json(route, {
    workbench: workbench(),
    html: DOSSIER_HTML, branch: 'develop', revision: '0123456789abcdef',
    workingTreeModified: false, fingerprint: 'a'.repeat(64), entryFingerprint: 'entry-a',
    cards: {
      projectName: PROJECT, dossierKey: WORKBENCH_KEY, dossierPath: 'docs/delivery-flow/index.html',
      implementationSlices: ITEMS.length, completedSlices: 0,
      cards: [...created.values()].map(card => ({
        key: card.taskKey, id: card.jobId, title: card.title, lane: card.state, state: card.state,
        environments: [], section: ITEMS[card.itemIndex].section ?? null,
        decision: ITEMS[card.itemIndex].decisionId ?? null, implementationIndex: card.itemIndex,
      })),
    },
  }));

  await page.route(/\/api\/tasks\/[^/]+\/promote-concept(\?|$)/, route => {
    const request = route.request();
    if (request.method() === 'GET') return json(route, { ...PLAN, promoted: [...created.values()] });
    const body = JSON.parse(request.postData() ?? '{}') as Record<string, unknown>;
    captured.promotionRequests.push({ url: request.url(), body });
    const indexes = (body['itemIndexes'] as number[] | undefined) ?? [];
    const result = indexes.map(index => {
      const card = created.get(index) ?? {
        jobId: `delivery-slice-${index + 1}`, taskKey: `AGT-${3610 + index}`,
        title: ITEMS[index].title, itemIndex: index, state: '1-preparation',
      };
      created.set(index, card);
      return card;
    });
    return json(route, { source: PLAN.source, created: result });
  });

  return captured;
}

async function seedDossierTab(page: Page, theme: 'light' | 'dark') {
  await page.addInitScript(({ project, workbenchId, theme }) => {
    sessionStorage.clear();
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'workbench', projectName: project, workbenchId, title: 'Delivery flow' }],
      activeKey: `workbench:${project}:${workbenchId}`,
    }));
    localStorage.setItem('atp.studio.theme', theme);
  }, { project: PROJECT, workbenchId: WORKBENCH_ID, theme });
}

async function shot(page: Page, name: string) {
  mkdirSync(RESULTS, { recursive: true });
  await page.screenshot({ path: resolve(RESULTS, name), fullPage: true });
}

test.describe('AGT-3701 create implementation cards', () => {
  test.beforeEach(() => test.setTimeout(120_000));

  test('the Dossier view lists every slice and creates some, then the rest, without moving the concept', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1000 });
    const captured = await installMocks(page);
    await seedDossierTab(page, 'light');
    await page.goto('/');
    await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });

    const promotion = page.getByTestId('concept-promotion');
    await expect(promotion).toContainText('One card per Dossier slice');
    await promotion.getByTestId('concept-promotion-open').click();

    for (const [index, item] of ITEMS.entries()) {
      const row = promotion.getByTestId(`concept-promotion-slice-${index + 1}`);
      await expect(row).toContainText(String(index + 1));
      await expect(row).toContainText(item.title);
    }
    await expect(promotion.getByTestId('concept-promotion-slice-decision-1'))
      .toContainText('Delivery mode: A · Queued (recommended)');
    await expect(promotion.getByTestId('concept-promotion-slice-decision-2'))
      .toContainText('No linked decision');
    await expect(promotion.getByTestId('concept-promotion-confirm')).toHaveText('Create all 3 cards');
    await shot(page, 'dossier-create-implementation-cards-light--mocked.png');

    // Create only the first two slices.
    await promotion.getByTestId('concept-promotion-slice-check-3').uncheck();
    await expect(promotion.getByTestId('concept-promotion-confirm')).toHaveText('Create 2 cards');
    await promotion.getByTestId('concept-promotion-confirm').click();
    await expect(promotion.getByTestId('concept-promotion-created')).toContainText('AGT-3610');
    await expect(promotion.getByTestId('concept-promotion-created')).toContainText('AGT-3611');
    await expect(promotion.getByTestId('concept-promotion-slice-1')).toContainText('Created AGT-3610');
    await expect(promotion.getByTestId('concept-promotion-slice-check-1')).toBeDisabled();
    await expect(promotion.getByTestId('concept-promotion-confirm')).toHaveText('Create cards');
    await expect(promotion.getByTestId('concept-promotion-confirm')).toBeDisabled();
    await expect(page.getByTestId('dossier-card-list')).toContainText('0 / 3 implementation slices complete');

    // Then the remaining slice.
    await promotion.getByTestId('concept-promotion-slice-check-3').check();
    await promotion.getByTestId('concept-promotion-confirm').click();
    await expect(promotion.getByTestId('concept-promotion-slice-3')).toContainText('Created AGT-3612');
    await shot(page, 'dossier-implementation-cards-created-light--mocked.png');

    expect(captured.promotionRequests.map(request => request.body['itemIndexes'])).toEqual([[0, 1], [2]]);
    expect(captured.promotionRequests[0].url).toContain(`/api/tasks/${CONCEPT_KEY}/promote-concept`);
    expect(new URL(captured.promotionRequests[0].url).searchParams.get('project')).toBe(PROJECT);
    expect(captured.moves).toEqual([]);
  });

  test('the Dossier view in dark and at 390 px, and Start task names the combined card', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 1000 });
    await installMocks(page);
    await seedDossierTab(page, 'dark');
    await page.goto('/');
    await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });
    await page.getByTestId('concept-promotion-open').click();
    await expect(page.getByTestId('concept-promotion-slice-3')).toBeVisible();
    await shot(page, 'dossier-create-implementation-cards-dark--mocked.png');

    await page.setViewportSize({ width: 390, height: 900 });
    await expect(page.getByTestId('concept-promotion-confirm')).toBeVisible();
    const overflow = await page.getByTestId('concept-promotion-panel').evaluate(element =>
      element.scrollWidth - element.clientWidth);
    expect(overflow, 'panel fits 390 px without horizontal overflow').toBeLessThanOrEqual(1);
    await shot(page, 'dossier-create-implementation-cards-390--mocked.png');

    await page.setViewportSize({ width: 1440, height: 1000 });
    const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
    await frame.locator('[data-option-id="queued"] input').check();
    const inline = page.getByTestId('workbench-inline-decision-action');
    await expect(inline.getByTestId('workbench-decision-start')).toHaveText('Start one combined task');
    await inline.getByTestId('workbench-decision-start').click();
    await expect(inline.getByTestId('workbench-decision-combined-note')).toContainText('one combined card');
    const preview = await inline.getByTestId('workbench-decision-prompt-preview').innerText();
    expect(preview.split('A · Queued').length - 1, 'decisions are listed once').toBe(1);
  });

  test('the concept card offers the same action and keeps its lane', async ({ page }) => {
    const captured = await installMocks(page);
    await page.addInitScript(() => {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
      }));
    });
    for (const theme of ['light', 'dark'] as const) {
      await page.setViewportSize({ width: 1440, height: 1000 });
      await page.addInitScript(value => localStorage.setItem('atp.studio.theme', value), theme);
      await page.goto(`/?job=delivery-flow-concept&watchPath=${encodeURIComponent(WATCH_PATH)}`);
      const promotion = page.getByTestId('concept-promotion');
      await expect(promotion).toBeVisible({ timeout: 30_000 });
      await promotion.getByTestId('concept-promotion-open').click();
      await expect(promotion.getByTestId('concept-promotion-slice-2')).toContainText(ITEMS[1].title);
      await promotion.scrollIntoViewIfNeeded();
      await shot(page, `concept-card-create-implementation-cards-${theme}--mocked.png`);
    }

    const promotion = page.getByTestId('concept-promotion');
    await promotion.getByTestId('concept-promotion-select-all').uncheck();
    await expect(promotion.getByTestId('concept-promotion-slice-check-2')).not.toBeChecked();
    await promotion.getByTestId('concept-promotion-slice-check-2').check();
    await promotion.getByTestId('concept-promotion-confirm').click();
    await expect(promotion.getByTestId('concept-promotion-created')).toContainText('AGT-3611');

    expect(captured.promotionRequests).toHaveLength(1);
    expect(captured.promotionRequests[0].body['itemIndexes']).toEqual([1]);
    expect(captured.promotionRequests[0].url).toContain('/api/tasks/delivery-flow-concept/promote-concept');
    expect(captured.moves).toEqual([]);
  });
});
