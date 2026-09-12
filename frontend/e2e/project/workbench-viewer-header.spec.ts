import { expect, test } from '@playwright/test';
import type { Page, Route, TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Viewer Header Evidence';
const WORKBENCH_ID = 'compact-viewer-header';
const WORKBENCH_KEY = 'VHE-W4';
const WATCH_PATH = 'C:/evidence/viewer-header';

// Route mocks are the complete backend for this visual contract. A production
// build's service worker would bypass page.route after activation.
test.use({ serviceWorkers: 'block' });

const EMPTY_GROUPED = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [],
  failedPickup: [],
  codeNotComplete: [],
  autoReview: [],
  humanReview: [],
  escalated: [],
  review: [],
  completed: [],
  archive: [],
};

const primaryTask = {
  id: 'compact-header-task',
  key: 'VHE-11',
  displayKey: 'VHE-11',
  taskKey: `${PROJECT}::compact-header-task`,
  title: 'Implement compact viewer header without clipping the complete operational context',
  state: '3-progress',
  order: 1,
  agent: 'codex',
  createdAt: '2026-08-09T09:00:00Z',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/3-progress/compact-header-task`,
  lastActivity: '2026-08-09T10:00:00Z',
  sessionName: null,
  model: null,
  cliType: 'codex',
  useOwnSession: null,
  lastUsage: null,
  execution: null,
  commit: null,
  references: {
    dependsOn: [],
    relatedTo: [],
    blockedBy: [],
    supersedes: [],
    workbenches: [WORKBENCH_KEY],
  },
};

interface WorkbenchCapture {
  releaseWorkbench: () => void;
  workbenchReads: number;
}

interface DossierFixture {
  key: string;
  title: string;
  heading: string;
  summary: string;
  entryPath: string;
  html: string;
}

interface WorkbenchMockOptions {
  workbenchError?: {
    status: number;
    message: string;
  };
  dossier?: DossierFixture;
  deferWorkbench?: boolean;
}

const DEFAULT_DOSSIER: DossierFixture = {
  key: WORKBENCH_KEY,
  title: 'Compact viewer header keeps operational context in one quiet line',
  heading: 'Compact viewer header',
  summary: 'Source metadata, actions, and decision controls move into a detail popover.',
  entryPath: 'docs/operations/compact-viewer-header/index.html',
  html: `<!doctype html><html><body><main>
    <h1>Compact viewer header</h1>
    <p>The document remains the primary reading surface.</p>
    <section data-decision-id="route" data-decision-kind="single"><strong>Route</strong><span data-option-id="direct">Direct</span></section>
    <section data-decision-id="density" data-decision-kind="single"><strong>Density</strong><span data-option-id="compact">Compact</span></section>
    <section data-decision-id="proof" data-decision-kind="confirm"><strong>Proof</strong><span data-option-id="capture">Capture</span></section>
  </main></body></html>`,
};

function reviewMetadata() {
  return {
    verdict: 'partially-superseded',
    supersededBy: ['VHE-W8'],
    reviewedAt: '2026-09-09T10:00:00Z',
    reviewedBy: 'AGT-2784',
    note: 'The compact host remains current; the older action layout is superseded.',
  };
}

function scrollingDossierFixture(
  metadata: Omit<DossierFixture, 'html'>,
  sectionCount: number,
): DossierFixture {
  const sections = Array.from({ length: sectionCount }, (_, index) => `
    <section>
      <h2>${String(index + 1).padStart(2, '0')} · Evidence section</h2>
      <p>Repository evidence remains readable inside the document viewport after asynchronous loading.</p>
      <p>The viewer owns the available height while this document owns its vertical scroll position.</p>
    </section>`).join('');
  return {
    ...metadata,
    html: `<!doctype html><html><head><style>
      :root { color-scheme: light dark; --bg: #fcfcfb; --fg: #11110f; --muted: #5f5e59; --line: #d8d6cf; }
      @media (prefers-color-scheme: dark) { :root { --bg: #1a1a19; --fg: #f7f6f2; --muted: #b7b6ae; --line: #3d3d3a; } }
      * { box-sizing: border-box; }
      body { margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.55 system-ui, sans-serif; }
      main { width: min(100% - 48px, 960px); margin: 0 auto; padding: 36px 0 64px; }
      h1 { margin: 0 0 12px; font-size: 34px; }
      h2 { margin: 0 0 8px; font-size: 20px; }
      p { max-width: 76ch; color: var(--muted); }
      section { min-height: 150px; padding: 28px 0; border-top: 1px solid var(--line); }
      footer { padding-top: 28px; border-top: 1px solid var(--line); font-weight: 700; }
    </style></head><body><main>
      <h1 data-testid="dossier-start">${metadata.heading}</h1>
      <p>${metadata.summary}</p>
      ${sections}
      <footer data-testid="dossier-end">Complete dossier · ${metadata.key}</footer>
    </main></body></html>`,
  };
}

const HEIGHT_DOSSIERS = [
  {
    label: 'short',
    fixture: scrollingDossierFixture({
      key: 'QS-W5',
      title: 'Quality Studio Test Baseline',
      heading: 'A test baseline that stays green',
      summary: 'A compact quality dossier with enough evidence to require internal scrolling.',
      entryPath: 'docs/operations/test-baseline/index.html',
    }, 8),
  },
  {
    label: 'long',
    fixture: scrollingDossierFixture({
      key: 'AGT-W1',
      title: 'Admin Surface Design Guideline',
      heading: 'Admin Surface Design Guideline',
      summary: 'A long Agent Studio dossier that exercises the same viewer height contract.',
      entryPath: 'docs/operations/admin-design-guideline/index.html',
    }, 24),
  },
] as const;

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installMocks(
  page: Page,
  options: WorkbenchMockOptions = {},
): Promise<WorkbenchCapture> {
  const dossier = options.dossier ?? DEFAULT_DOSSIER;
  let releaseWorkbench = () => undefined;
  const workbenchReady = options.deferWorkbench
    ? new Promise<void>((resolveWorkbench) => {
        releaseWorkbench = resolveWorkbench;
      })
    : Promise.resolve();
  const capture: WorkbenchCapture = {
    releaseWorkbench,
    workbenchReads: 0,
  };
  await page.route('**/healthz', (route) => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route('**/api/**', (route) => json(route, []));
  await page.route('**/api/auth/status', (route) =>
    json(route, {
      profile: 'local',
      bootstrapRequired: false,
      authenticated: true,
      user: null,
    }),
  );
  await page.route('**/api/watch-paths', (route) =>
    json(route, [
      {
        name: PROJECT,
        path: WATCH_PATH,
        rootPath: WATCH_PATH,
        repositoryPath: WATCH_PATH,
      },
    ]),
  );
  await page.route('**/api/workspaces**', (route) =>
    json(route, [
      {
        id: 'ws-viewer-evidence',
        displayName: 'Evidence',
        sortOrder: 0,
        isDefault: true,
        projects: [
          {
            id: 'project-viewer-evidence',
            displayName: PROJECT,
            shortCode: 'VHE',
            workspaceId: 'ws-viewer-evidence',
            storageLocation: WATCH_PATH,
            sortOrder: 0,
            archived: false,
            urls: [],
          },
        ],
      },
    ]),
  );
  await page.route('**/api/environment**', (route) =>
    json(route, {
      isDev: false,
      devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
    }),
  );
  await page.route('**/api/runner/status**', (route) => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', (route) =>
    json(route, {
      at: '2026-08-09T10:00:00Z',
      snapshots: [],
      ttlSeconds: 600,
    }),
  );
  await page.route('**/api/cli/usage**', (route) =>
    json(route, {
      at: '2026-08-09T10:00:00Z',
      sessions: [],
    }),
  );
  await page.route(/\/api\/cli\/[^/]+\/models(?:\?.*)?$/, (route) =>
    json(route, {
      models: [],
      source: 'viewer-header-evidence',
    }),
  );
  await page.route('**/api/cli/maintenance-model', (route) =>
    json(route, {
      cliType: 'codex',
      model: 'gpt-5',
      thinkingLevel: null,
    }),
  );
  await page.route('**/api/crash-recovery/pending**', (route) => json(route, { pending: [] }));
  await page.route('**/api/tasks/archive**', (route) =>
    json(route, {
      items: [],
      total: 0,
      offset: 0,
      limit: 50,
    }),
  );
  await page.route('**/api/tasks/grouped**', (route) =>
    json(route, {
      ...EMPTY_GROUPED,
      progress: [primaryTask],
    }),
  );
  await page.route('**/api/tasks/reference-status', (route) => {
    const request = JSON.parse(route.request().postData() ?? '{"keys":[]}') as { keys?: string[] };
    const statuses = new Map([
      ['VHE-11', { title: primaryTask.title, taskKey: primaryTask.taskKey, lane: '3-progress' }],
      [
        'VHE-12',
        {
          title: 'Prepare header contract',
          taskKey: `${PROJECT}::prepare-header`,
          lane: '2-ready',
        },
      ],
      [
        'VHE-13',
        {
          title: 'Review compact interaction',
          taskKey: `${PROJECT}::review-header`,
          lane: '5-human-review',
        },
      ],
    ]);
    return json(route, {
      items: (request.keys ?? []).flatMap((key) => {
        const status = statuses.get(key);
        return status
          ? [
              {
                key,
                exists: true,
                ...status,
                projectId: 'project-viewer-evidence',
                projectName: PROJECT,
                projectColor: '#a78bfa',
                merge: key === 'VHE-11' ? {
                  inIntegration: true,
                  inRelease: false,
                  integrationBranch: 'develop',
                  releaseBranch: 'main',
                } : null,
                reviewGrade: null,
              },
            ]
          : [];
      }),
    });
  });
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/wiki/home`, (route) =>
    json(route, {
      sections: [],
    }),
  );
  await page.route(/\/api\/workbenches(?:\?.*)?$/, (route) =>
    json(route, {
      projectName: PROJECT,
      count: 1,
      currentCount: 1,
      historyCount: 0,
      items: [
        {
          projectName: PROJECT,
          workbench: {
            id: WORKBENCH_ID,
            key: dossier.key,
            title: dossier.title,
            summary: dossier.summary,
            status: 'decision-pending',
            phase: 'decision-ready',
            updatedAtUtc: '2026-08-09T10:00:00Z',
            entryPath: dossier.entryPath,
            valid: true,
            error: null,
            sourceTaskKeys: [],
            relatedTaskKeys: ['VHE-12', 'VHE-13'],
            openDecisionCount: 3,
            review: reviewMetadata(),
            reviewDue: false,
          },
        },
      ],
    }),
  );
  await page.route(
    new RegExp(`/api/projects/${encodeURIComponent(PROJECT)}/workbenches(?:\\?.*)?$`),
    (route) => json(route, {
        projectName: PROJECT,
        includesHistory: false,
        count: 1,
        items: [
          {
            id: WORKBENCH_ID,
            key: dossier.key,
            title: dossier.title,
            summary: dossier.summary,
            status: 'decision-pending',
            phase: 'decision-ready',
            updatedAtUtc: '2026-08-09T10:00:00Z',
            entryPath: dossier.entryPath,
            valid: true,
            error: null,
            sourceTaskKeys: [],
            relatedTaskKeys: ['VHE-12', 'VHE-13'],
            review: reviewMetadata(),
            reviewDue: false,
          },
        ],
      }),
  );
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}`,
    async (route) => {
      capture.workbenchReads += 1;
      await workbenchReady;
      if (options.workbenchError) {
        return route.fulfill({
          status: options.workbenchError.status,
          contentType: 'application/json',
          body: JSON.stringify({ error: options.workbenchError.message }),
        });
      }
      return json(route, {
        workbench: {
          id: WORKBENCH_ID,
          key: dossier.key,
          title: dossier.title,
          summary: dossier.summary,
          status: 'decision-pending',
          phase: 'decision-ready',
          updatedAtUtc: '2026-08-09T10:00:00Z',
          entryPath: dossier.entryPath,
          valid: true,
          error: null,
          sourceTaskKeys: [],
          relatedTaskKeys: ['VHE-12', 'VHE-13'],
          review: reviewMetadata(),
          reviewDue: false,
        },
        html: dossier.html,
        branch: 'task/compact-viewer-header',
        revision: '1234567890abcdef',
        workingTreeModified: false,
        fingerprint: 'a'.repeat(64),
      });
    },
  );
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${dossier.key}/references`,
    (route) =>
      json(route, {
        projectName: PROJECT,
        workbenchKey: dossier.key,
        workbenchId: WORKBENCH_ID,
        legacyTaskKeys: ['VHE-12', 'VHE-13'],
        items: [{
          sourceKey: 'VHE-11',
          sourceJobId: primaryTask.id,
          sourceTitle: primaryTask.title,
          sourceState: primaryTask.state,
          sourceWatchPath: WATCH_PATH,
          kind: 'workbenches',
        }],
      }),
  );
  return capture;
}

async function seedWorkbench(page: Page): Promise<void> {
  await page.addInitScript(
    ({ project, workbenchId }) => {
      const tab = {
        kind: 'workbench',
        projectName: project,
        workbenchId,
        title: 'Compact viewer header',
      };
      localStorage.setItem(
        'atp.studio.tabs.v1',
        JSON.stringify({
          v: 1,
          tabs: [tab],
          activeKey: `workbench:${project}:${workbenchId}`,
        }),
      );
      localStorage.setItem('atp.studio.theme', 'light');
    },
    { project: PROJECT, workbenchId: WORKBENCH_ID },
  );
}

async function seedWorkbenchOverview(page: Page): Promise<void> {
  await page.addInitScript(
    ({ project }) => {
      const tab = {
        kind: 'workbenches',
        projectName: project,
      };
      localStorage.setItem(
        'atp.studio.tabs.v1',
        JSON.stringify({
          v: 1,
          tabs: [tab],
          activeKey: `workbenches:${project}`,
        }),
      );
      localStorage.setItem('atp.studio.theme', 'light');
    },
    { project: PROJECT },
  );
}

function evidencePath(testInfo: TestInfo, fileName: string): string {
  const root = resolve(process.env['JOB_RESULTS_DIR'] ?? testInfo.outputDir);
  mkdirSync(root, { recursive: true });
  return resolve(root, fileName);
}

async function captureViewerTop(page: Page, testInfo: TestInfo, fileName: string): Promise<void> {
  const header = page.getByTestId('workbench-viewer-header');
  const box = await header.boundingBox();
  expect(box).not.toBeNull();
  await page.screenshot({
    path: evidencePath(testInfo, fileName),
    clip: box!,
  });
}

async function expectViewerSettled(page: Page): Promise<void> {
  await expect(page.getByTestId('workbench-viewer-frame')).toBeVisible({ timeout: 30_000 });
  await expect(page.frameLocator('[data-testid="workbench-viewer-frame"]')
    .getByRole('heading', { name: 'Compact viewer header' })).toBeVisible();
  await expect(page.getByTestId('workbench-viewer-loading')).toHaveCount(0);
  await expect(page.getByTestId('loading-surface-list')).toHaveCount(0);
}

async function expectViewerHeightAndInternalScroll(page: Page): Promise<void> {
  const viewer = page.getByTestId('workbench-viewer');
  const geometry = await viewer.evaluate((element) => {
    const header = element.querySelector<HTMLElement>('[data-testid="workbench-viewer-header"]');
    const shell = element.querySelector<HTMLElement>('[data-testid="workbench-viewer-frame-shell"]');
    const frame = element.querySelector<HTMLIFrameElement>('[data-testid="workbench-viewer-frame"]');
    if (!header || !shell || !frame) throw new Error('Viewer geometry nodes are missing.');
    return {
      viewerHeight: element.getBoundingClientRect().height,
      viewerClientHeight: element.clientHeight,
      viewerScrollHeight: element.scrollHeight,
      headerHeight: header.getBoundingClientRect().height,
      shellHeight: shell.getBoundingClientRect().height,
      frameHeight: frame.getBoundingClientRect().height,
    };
  });
  expect(geometry.viewerHeight).toBeGreaterThan(650);
  expect(geometry.shellHeight).toBeGreaterThan(
    geometry.viewerHeight - geometry.headerHeight - 2,
  );
  expect(Math.abs(geometry.frameHeight - geometry.shellHeight)).toBeLessThanOrEqual(1);
  expect(geometry.viewerScrollHeight).toBeLessThanOrEqual(geometry.viewerClientHeight + 1);

  const isolatedRoot = page.frameLocator('[data-testid="workbench-viewer-frame"]').locator('html');
  const scroll = await isolatedRoot.evaluate(() => ({
    clientHeight: document.documentElement.clientHeight,
    scrollHeight: document.documentElement.scrollHeight,
  }));
  expect(scroll.scrollHeight).toBeGreaterThan(scroll.clientHeight);
  await isolatedRoot.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  await expect(page.frameLocator('[data-testid="workbench-viewer-frame"]')
    .getByTestId('dossier-end')).toBeInViewport();
}

for (const dossier of HEIGHT_DOSSIERS) {
  test(`${dossier.fixture.key} ${dossier.label} dossier fills the viewer after late load and scrolls internally`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    const capture = await installMocks(page, {
      dossier: dossier.fixture,
      deferWorkbench: true,
    });
    await page.goto(
      `/#/projects/viewer-header-evidence/workbenches/${encodeURIComponent(WORKBENCH_ID)}`,
    );
    await page.addStyleTag({
      content: '[data-testid="offline-banner"] { display: none !important; }',
    });
    await expect(page.getByTestId('workbench-viewer-loading')).toBeVisible({ timeout: 30_000 });

    capture.releaseWorkbench();
    await expect(page.getByTestId('workbench-viewer-frame')).toBeVisible({ timeout: 30_000 });
    const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
    await expect(frame.getByTestId('dossier-start')).toContainText(dossier.fixture.heading);
    await expectViewerHeightAndInternalScroll(page);

    await frame.locator('html').evaluate(() => window.scrollTo(0, 0));
    for (const theme of ['light', 'dark'] as const) {
      await page.emulateMedia({ colorScheme: theme });
      await setTheme(page, theme);
      await page.screenshot({
        path: evidencePath(
          testInfo,
          `dossier-height-${dossier.label}-${theme}--mocked.png`,
        ),
        fullPage: true,
      });
    }
  });
}

test('overview entry settles without a persistent loading surface', async ({
  page,
}, testInfo) => {
  await installMocks(page);
  await seedWorkbenchOverview(page);
  await page.goto('/');
  await page.addStyleTag({
    content: '[data-testid="offline-banner"] { display: none !important; }',
  });

  await expect(page.getByTestId(`workbench-overview-item-${PROJECT}-${WORKBENCH_ID}`))
    .toBeVisible({ timeout: 30_000 });
  await page.getByTestId(`workbench-overview-full-${PROJECT}-${WORKBENCH_ID}`).click();
  await expectViewerSettled(page);
  await setTheme(page, 'light');
  await page.screenshot({
    path: evidencePath(testInfo, 'dossier-overview-entry-settled-light--mocked.png'),
    fullPage: true,
  });
});

test('direct dossier route settles without a persistent loading surface', async ({
  page,
}, testInfo) => {
  await installMocks(page);
  await page.goto(
    `/#/projects/viewer-header-evidence/workbenches/${encodeURIComponent(WORKBENCH_ID)}`,
  );
  await page.addStyleTag({
    content: '[data-testid="offline-banner"] { display: none !important; }',
  });
  await expect(page).toHaveURL(
    /#\/projects\/viewer-header-evidence\/workbenches\/compact-viewer-header(?:&|$)/,
  );
  await expectViewerSettled(page);
  await setTheme(page, 'dark');
  await page.screenshot({
    path: evidencePath(testInfo, 'dossier-direct-entry-settled-dark--mocked.png'),
    fullPage: true,
  });
});

test('unreadable dossier replaces loading feedback with the backend reason', async ({
  page,
}, testInfo) => {
  await installMocks(page, {
    workbenchError: {
      status: 404,
      message: 'Dossier not found, invalid, or path rejected',
    },
  });
  await page.goto(
    `/#/projects/viewer-header-evidence/workbenches/${encodeURIComponent(WORKBENCH_ID)}`,
  );
  await page.addStyleTag({
    content: '[data-testid="offline-banner"] { display: none !important; }',
  });

  const error = page.getByTestId('workbench-viewer-error');
  await expect(error).toBeVisible({ timeout: 30_000 });
  await expect(error).toContainText('Dossier not found, invalid, or path rejected');
  await expect(page.getByTestId('workbench-viewer-loading')).toHaveCount(0);
  await expect(page.getByTestId('loading-surface-list')).toHaveCount(0);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: evidencePath(testInfo, `dossier-load-error-${theme}--mocked.png`),
      fullPage: true,
    });
  }
});

test('compact viewer head centers controls and exposes honest live status with offline fallback', async ({
  page,
}, testInfo) => {
  const capture = await installMocks(page);
  await seedWorkbench(page);
  await page.goto('/');
  await page.addStyleTag({
    content: '[data-testid="offline-banner"] { display: none !important; }',
  });

  const header = page.getByTestId('workbench-viewer-header');
  await expect(header).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('workbench-viewer-key')).toContainText(WORKBENCH_KEY);
  await expect(page.getByTestId('workbench-viewer-open-decisions')).toContainText('3 open');
  await expect(page.getByTestId('workbench-viewer-implementation-status')).toHaveText(
    'In implementation',
  );
  await expect(page.getByTestId(/^workbench-viewer-task-VHE-(11|12|13)$/)).toHaveCount(3);
  await expect(page.getByTestId('workbench-viewer-refresh')).toHaveCount(0);
  await expect(page.getByTestId('workbench-viewer-stale-as-of')).toContainText(
    'Updates paused · as of',
  );
  await expect(page.getByTestId('workbench-review-tag')).toContainText('partially superseded');
  await page.getByTestId('workbench-review-tag').hover();
  await expect(page.getByTestId('workbench-review-tooltip')).toContainText('Reviewed by: AGT-2784');
  await page.mouse.move(2, 2);
  await page.getByTestId('workbench-record-review').click();
  await expect(page.getByTestId('workbench-review-form')).toBeVisible();
  await page.getByTestId('workbench-review-note').fill('The compact header remains current.');
  await page.getByTestId('workbench-viewer-task-VHE-11').hover();
  const laneTooltip = page.getByTestId('workbench-viewer-task-VHE-11-tooltip');
  await expect(laneTooltip).toContainText(primaryTask.title);
  await expect(laneTooltip).toContainText('In Progress · Viewer Header Evidence');
  await expect(laneTooltip).toContainText('develop: merged · main: open');
  const laneTooltipBox = await laneTooltip.boundingBox();
  const initialViewport = page.viewportSize();
  expect(laneTooltipBox).not.toBeNull();
  expect(laneTooltipBox!.x).toBeGreaterThanOrEqual(8);
  expect(laneTooltipBox!.x + laneTooltipBox!.width)
    .toBeLessThanOrEqual((initialViewport?.width ?? 0) - 8);
  expect(laneTooltipBox!.width).toBeLessThanOrEqual(448);
  await page.mouse.move(2, 2);
  await expect(laneTooltip).toHaveCount(0);

  for (const viewport of [
    { label: 'wide', width: 1700, height: 1000 },
    { label: 'narrow', width: 1180, height: 820 },
  ]) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await expect
      .poll(async () => (await header.boundingBox())?.height ?? 999)
      .toBeLessThanOrEqual(48);
    const geometry = await header.evaluate((element) => {
      const rect = (selector: string) => {
        const target = element.querySelector<HTMLElement>(selector);
        if (!target) return null;
        const { x, y, width, height } = target.getBoundingClientRect();
        return { x, y, width, height };
      };
      return {
        title: rect('[data-testid="workbench-viewer-title"]'),
        status: rect('[data-testid="workbench-viewer-status"]'),
        key: rect('[data-testid="workbench-viewer-key"]'),
        tasks: rect('[data-testid="workbench-viewer-tasks"]'),
        trigger: rect('[data-testid="workbench-viewer-details-trigger"]'),
        icon: rect('[data-testid="workbench-viewer-details-trigger"] svg'),
      };
    });
    const centerY = (box: NonNullable<typeof geometry.title>) => box.y + box.height / 2;
    const centerX = (box: NonNullable<typeof geometry.title>) => box.x + box.width / 2;
    expect(geometry.title).not.toBeNull();
    expect(geometry.status).not.toBeNull();
    expect(geometry.key).not.toBeNull();
    expect(geometry.tasks).not.toBeNull();
    expect(geometry.trigger).not.toBeNull();
    expect(geometry.icon).not.toBeNull();
    expect(Math.abs(centerY(geometry.title!) - centerY(geometry.status!))).toBeLessThan(1.5);
    expect(Math.abs(centerY(geometry.title!) - centerY(geometry.key!))).toBeLessThan(1.5);
    expect(geometry.trigger!.height).toBe(geometry.tasks!.height);
    expect(Math.abs(centerX(geometry.trigger!) - centerX(geometry.icon!))).toBeLessThan(0.5);
    expect(Math.abs(centerY(geometry.trigger!) - centerY(geometry.icon!))).toBeLessThan(0.5);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await captureViewerTop(
        page,
        testInfo,
        `workbench-viewer-header-${theme}-${viewport.label}--mocked.png`,
      );
    }
  }

  await page.setViewportSize({ width: 1700, height: 1000 });
  await page.getByRole('button', { name: 'Close review form' }).click();
  await page.getByTestId('workbench-viewer-details-trigger').click();
  const details = page.getByTestId('workbench-viewer-details-popover');
  await expect(details).toBeVisible();
  await expect(details).toContainText('Source metadata, actions, and decision controls');
  await expect(details).toContainText('docs/operations/compact-viewer-header/index.html');
  await expect(details.getByTestId('workbench-decision-panel')).toBeVisible();
  await expect(details.getByTestId('workbench-viewer-connection-state')).toContainText(
    'Disconnected since',
  );
  const fallback = details.getByTestId('workbench-viewer-manual-refresh');
  await expect(fallback).toBeVisible();

  await details.getByTestId('workbench-viewer-detail-task-VHE-11').hover();
  const chipTooltip = page.getByTestId('workbench-viewer-detail-task-VHE-11-tooltip');
  await expect(chipTooltip).toContainText(primaryTask.title);
  await expect(chipTooltip).toContainText('develop: merged · main: open');
  const chipTooltipBox = await chipTooltip.boundingBox();
  const viewport = page.viewportSize();
  expect(chipTooltipBox).not.toBeNull();
  expect(chipTooltipBox!.x).toBeGreaterThanOrEqual(8);
  expect(chipTooltipBox!.x + chipTooltipBox!.width).toBeLessThanOrEqual((viewport?.width ?? 0) - 8);

  await page.mouse.move(2, 2);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: evidencePath(testInfo, `dossier-live-status-menu-${theme}--mocked.png`),
      fullPage: true,
    });
  }

  const readsBeforeFallback = capture.workbenchReads;
  if (!await fallback.isVisible()) {
    await page.getByTestId('workbench-viewer-details-trigger').click();
  }
  await expect(fallback).toBeVisible();
  await fallback.click();
  await expect(header).toBeVisible();
  await expect.poll(() => capture.workbenchReads).toBeGreaterThan(readsBeforeFallback);
  await expect(page.getByTestId('workbench-viewer-stale-as-of')).toBeVisible();
});
