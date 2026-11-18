import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

/**
 * AGT-3875 - The owner answered every decision point of a Dossier without a
 * concept card and could neither save the answers nor create cards.
 *
 * 1. Save answers writes complete or partial answers (with notes) through the
 *    decisions prepare/confirm gate as `record-only`; the bar then says Saved in Dossier.
 * 2. Only one action panel is open at a time, and the open panel's confirm
 *    button is in view at 1280 px and 390 px.
 * 3. Create implementation cards is offered only when the server says the
 *    Dossier can promote; otherwise the control names the missing piece and
 *    offers the next step.
 * 4. Unsaved answers survive closing the browser tab, and leaving the Dossier warns once.
 *
 * Fully mocked via route interception.
 */

const PROJECT = 'Platform Evidence';
const WATCH_PATH = 'C:/evidence/platform';
const WORKBENCH_ID = 'integrierte-plattform';
const WORKBENCH_KEY = 'AGT-W81';
const CONCEPT_KEY = 'AGT-3600';
const ENTRY = 'docs/operations/integrierte-plattform/index.html';
const NOTE = 'Hier bin ich unsicher und haette gerne noch ein Dossier.';
const RESULTS = resolve(
  process.env['JOB_RESULTS_DIR'] ?? resolve(__dirname, '..', '..', 'test-results', 'dossier-save-answers'),
);

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
  escalated: [], review: [], completed: [], archive: [],
};

const DOSSIER_HTML = `<main>
  <h1>Integrierte Plattform</h1>
  <p>Fuenf Zonen, ein durchgehender Fluss, eigene Laufzeit plus Adapter.</p>
  <section data-decision-id="ip-d1" data-decision-kind="single">
    <h2>IP-D1 Scheduler fuer die Arbeitszone</h2>
    <ul>
      <li data-option-id="own">A · Eigener Scheduler (empfohlen)</li>
      <li data-option-id="k8s">B · Kubernetes</li>
    </ul>
  </section>
  <section data-decision-id="ip-d10" data-decision-kind="single">
    <h2>IP-D10 Secret-Provider</h2>
    <ul>
      <li data-option-id="builtin">A · Eingebauter Speicher plus Adapter</li>
      <li data-option-id="external">B · Nur externe Provider</li>
    </ul>
    <div data-comment="Notiz zu IP-D10"></div>
  </section>
</main>`;

const ITEMS = [
  { title: 'Zone boundaries', promptMarkdown: 'Zones', decisionId: 'ip-d1' },
  { title: 'Secret provider', promptMarkdown: 'Secrets', decisionId: 'ip-d10' },
];

type Promotion = 'no-concept-card' | 'available';

interface Captured {
  prepares: Record<string, unknown>[];
  confirms: Record<string, unknown>[];
}

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installMocks(page: Page, promotion: Promotion): Promise<Captured> {
  const captured: Captured = { prepares: [], confirms: [] };
  let recorded: unknown[] = [];

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
      id: 'project-platform', displayName: PROJECT, shortCode: 'PEV',
      workspaceId: 'workspace-evidence', storageLocation: WATCH_PATH,
      sortOrder: 0, archived: false, urls: [],
    }],
  }]));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-11-16T10:00:00Z', snapshots: [], ttlSeconds: 600,
  }));
  await page.route('**/api/usage/cockpit**', route => json(route, {
    snapshotVersion: 1, workspaceId: 'fixture', timeZone: 'UTC', weekStart: 1,
    generatedAt: '2026-11-16T10:00:00Z', calendar: { timeZone: 'UTC', weekStart: 1 }, clis: [],
    cost: { currency: 'USD', todayUsd: null, weekUsd: null, projects: [],
      coverage: { status: 'unavailable', observedAt: null, ttlSeconds: null } },
    runs: [], slots: [], sources: {},
  }));
  await page.route('**/api/workspaces/*/quota/admission**', route => json(route, {
    workspaceId: 'workspace-evidence',
    workspace: { codingAllowed: true, hostingAllowed: true, limited: false },
    projects: [],
  }));
  await page.route('**/api/cli/usage**', route => json(route, { at: '2026-11-16T10:00:00Z', sessions: [] }));
  await page.route('**/api/crash-recovery/pending**', route => json(route, { pending: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route('**/api/v1/studio/board**', route => json(route, EMPTY_GROUPED));

  const workbench = () => ({
    id: WORKBENCH_ID, key: WORKBENCH_KEY, title: 'Integrierte Plattform',
    summary: 'Eine voll integrierte Plattform von Planung bis Hosting.',
    status: 'decision-pending', phase: 'decision-ready', updatedAtUtc: '2026-10-16T20:30:00Z',
    entryPath: ENTRY, valid: true, error: null,
    sourceTaskKeys: promotion === 'available' ? [CONCEPT_KEY] : [], relatedTaskKeys: [],
    recordedResponses: recorded,
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
      projectName: PROJECT, dossierKey: WORKBENCH_KEY, dossierPath: ENTRY,
      implementationSlices: ITEMS.length, completedSlices: 0, cards: [],
    },
    promotion: promotion === 'available'
      ? { available: true, reason: null, message: null, sourceTaskKey: CONCEPT_KEY, sourceJobId: 'platform-concept',
        sourceTitle: 'Platform concept', sourceLane: '5-human-review', proposalCount: ITEMS.length }
      : { available: false, reason: 'no-concept-card',
        message: 'This Dossier has no source concept card. Implementation cards come from a concept card\'s reviewed Dossier.',
        sourceTaskKey: null, sourceJobId: null, sourceTitle: null, sourceLane: null, proposalCount: 0 },
  }));
  const decisions = `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}/decisions`;
  await page.route(`${decisions}/prepare`, route => {
    const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;
    captured.prepares.push(body);
    return json(route, {
      success: true, workbenchId: WORKBENCH_ID, operationId: body['operationId'], outcome: body['outcome'],
      decisionStage: 'prepared', revision: '0123456789abcdef', fingerprint: 'a'.repeat(64),
      responses: body['responses'], spawnedTaskKeys: [], idempotent: false,
    });
  });
  await page.route(`${decisions}/confirm`, route => {
    const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;
    captured.confirms.push(body);
    recorded = body['responses'] as unknown[];
    return json(route, {
      success: true, workbenchId: WORKBENCH_ID, operationId: body['operationId'], outcome: body['outcome'],
      decisionStage: 'succeeded', revision: 'fedcba9876543210', fingerprint: 'b'.repeat(64),
      responses: recorded, spawnedTaskKeys: [], idempotent: false,
    });
  });
  await page.route(/\/api\/tasks\/[^/]+\/promote-concept(\?|$)/, route => json(route, {
    source: { repoRelativePath: ENTRY, title: 'Integrierte Plattform', decisions: [] },
    items: ITEMS, mode: 'coding', targetState: '1-preparation', watchPath: WATCH_PATH,
    projectName: PROJECT, promoted: [],
  }));
  return captured;
}

async function seedTabs(page: Page, theme: 'light' | 'dark') {
  await page.addInitScript(({ project, workbenchId, theme }) => {
    sessionStorage.clear();
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [
        { kind: 'workbenches', projectName: project },
        { kind: 'workbench', projectName: project, workbenchId, title: 'Integrierte Plattform' },
      ],
      activeKey: `workbench:${project}:${workbenchId}`,
    }));
    localStorage.setItem('atp.studio.theme', theme);
  }, { project: PROJECT, workbenchId: WORKBENCH_ID, theme });
}

async function openDossier(page: Page) {
  await page.goto('/');
  await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('workbench-inline-decision-action')).toBeVisible();
}

async function answerAll(page: Page) {
  const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  await frame.locator('[data-option-id="own"] input').check();
  await frame.locator('[data-option-id="builtin"] input').check();
  await frame.locator('[data-studio-decision-comment]').fill(NOTE);
}

async function shot(page: Page, name: string) {
  mkdirSync(RESULTS, { recursive: true });
  await page.screenshot({ path: resolve(RESULTS, name), fullPage: true });
}

/** Whether the element itself receives a click at its centre, so no panel covers it. */
async function topmostAtCenter(locator: ReturnType<Page['getByTestId']>): Promise<boolean> {
  return locator.evaluate(element => {
    const box = element.getBoundingClientRect();
    const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
    return hit !== null && (hit === element || element.contains(hit));
  });
}

const VIEWPORTS = [
  { name: '1280', width: 1280, height: 900 },
  { name: '390', width: 390, height: 844 },
] as const;

test.describe('AGT-3875 Dossier answers and action panels', () => {
  test.beforeEach(() => test.setTimeout(120_000));

  for (const theme of ['light', 'dark'] as const) {
    for (const viewport of VIEWPORTS) {
      test(`saves a fully answered Dossier with its note and explains why no cards can be created (${theme}, ${viewport.name} px)`, async ({ page }) => {
        await page.setViewportSize(viewport);
        const captured = await installMocks(page, 'no-concept-card');
        await seedTabs(page, theme);
        await openDossier(page);

        // 3. A Dossier without a concept card says so and offers the next step.
        await expect(page.getByTestId('concept-promotion-open')).toHaveCount(0);
        await expect(page.getByTestId('dossier-promotion-reason')).toContainText('no source concept card');
        await expect(page.getByTestId('dossier-promotion-create-concept'))
          .toHaveText('Create a concept card for this Dossier');

        await answerAll(page);
        const inline = page.getByTestId('workbench-inline-decision-action');
        await expect(inline.getByTestId('workbench-decision-answer-count')).toContainText('2 of 2 answered');
        await expect(inline.getByTestId('workbench-decision-answer-count')).toContainText('1 note');
        await expect(inline.getByTestId('workbench-decision-save-state')).toContainText('Not saved yet');

        // 1. A fully answered Dossier is saved without starting a card.
        const save = inline.getByTestId('workbench-decision-record');
        await save.scrollIntoViewIfNeeded();
        await expect(save).toHaveText('Save answers');
        await save.click();
        await expect(inline.getByTestId('workbench-decision-save-state')).toContainText('Saved in Dossier');
        await expect(save).toBeDisabled();

        expect(captured.prepares).toHaveLength(1);
        expect(captured.confirms).toHaveLength(1);
        expect(captured.confirms[0]['outcome']).toBe('record-only');
        expect(captured.confirms[0]['task']).toBeNull();
        expect(captured.confirms[0]['responses']).toEqual([
          { decisionId: 'ip-d1', kind: 'single', selectedOptionIds: ['own'], comment: null },
          { decisionId: 'ip-d10', kind: 'single', selectedOptionIds: ['builtin'], comment: NOTE },
        ]);

        // 5. The saved note is shown in the Dossier's note field after the reload.
        const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
        await expect(frame.locator('[data-studio-decision-comment]')).toHaveValue(NOTE);
        await shot(page, `dossier-saved-answers-${theme}-${viewport.name}--mocked.png`);
      });
    }
  }

  for (const viewport of VIEWPORTS) {
    test(`opens one action panel at a time with its confirm button in view (${viewport.name} px)`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await installMocks(page, 'available');
      await seedTabs(page, viewport.name === '390' ? 'dark' : 'light');
      await openDossier(page);
      await answerAll(page);

      await page.getByTestId('concept-promotion-open').click();
      await expect(page.getByTestId('concept-promotion-panel')).toBeVisible();
      await expect(page.getByTestId('concept-promotion-confirm')).toBeInViewport();

      const inline = page.getByTestId('workbench-inline-decision-action');
      await inline.getByTestId('workbench-decision-start').scrollIntoViewIfNeeded();
      await inline.getByTestId('workbench-decision-start').click();
      await expect(page.getByTestId('concept-promotion-panel')).toHaveCount(0);
      await expect(inline.getByTestId('workbench-decision-feature-confirmation')).toBeVisible();
      await expect(inline.getByTestId('workbench-decision-confirm')).toBeInViewport();
      await expect(inline.getByTestId('workbench-decision-confirm')).toBeEnabled();
      expect(await topmostAtCenter(inline.getByTestId('workbench-decision-confirm')), 'nothing covers Confirm and start')
        .toBe(true);
      // The sticky confirm row paints an opaque surface over the scrolled form.
      expect(await inline.getByTestId('workbench-decision-confirm').evaluate(button =>
        getComputedStyle(button.parentElement!).backgroundColor)).not.toBe('rgba(0, 0, 0, 0)');
      await shot(page, `dossier-one-panel-combined-${viewport.name}--mocked.png`);

      // Opening the promotion panel again closes the combined-task form.
      await page.getByTestId('concept-promotion-open').click();
      await expect(inline.getByTestId('workbench-decision-feature-confirmation')).toHaveCount(0);
      await expect(page.getByTestId('concept-promotion-confirm')).toBeInViewport();
      expect(await topmostAtCenter(page.getByTestId('concept-promotion-confirm')), 'nothing covers Create cards')
        .toBe(true);
      await shot(page, `dossier-one-panel-promotion-${viewport.name}--mocked.png`);
    });
  }

  test('unsaved answers survive closing the tab, and leaving the Dossier warns once', async ({ context }) => {
    const first = await context.newPage();
    await first.setViewportSize({ width: 1280, height: 900 });
    await installMocks(first, 'no-concept-card');
    await seedTabs(first, 'light');
    await openDossier(first);
    await answerAll(first);
    await expect(first.getByTestId('workbench-inline-decision-action').getByTestId('workbench-decision-save-state'))
      .toContainText('Not saved yet');

    // Closing the browser tab with unsaved answers asks first.
    const dialog = new Promise<string>(done => first.once('dialog', async prompt => {
      done(prompt.type());
      await prompt.accept();
    }));
    await first.close({ runBeforeUnload: true });
    expect(await dialog).toBe('beforeunload');

    const second = await context.newPage();
    await second.setViewportSize({ width: 1280, height: 900 });
    await installMocks(second, 'no-concept-card');
    await openDossier(second);
    const frame = second.frameLocator('[data-testid="workbench-viewer-frame"]');
    await expect(frame.locator('[data-option-id="own"] input')).toBeChecked();
    await expect(frame.locator('[data-option-id="builtin"] input')).toBeChecked();
    await expect(frame.locator('[data-studio-decision-comment]')).toHaveValue(NOTE);
    const inline = second.getByTestId('workbench-inline-decision-action');
    await expect(inline.getByTestId('workbench-decision-save-state')).toContainText('Not saved yet');
    await expect(inline.getByTestId('workbench-decision-answer-count')).toContainText('2 of 2 answered');

    // Leaving the Dossier warns once; the draft stays in this browser.
    await second.locator('[data-tab-kind="workbenches"]').click();
    const warning = second.getByText('are not saved in the Dossier');
    await expect(warning).toBeVisible();
    await shot(second, 'dossier-leave-warning-light-1280--mocked.png');
    await second.locator('[data-tab-kind="workbench"]').click();
    await expect(second.getByTestId('workbench-viewer')).toBeVisible();
    await second.locator('[data-tab-kind="workbenches"]').click();
    await expect(warning).toHaveCount(1);
  });
});
