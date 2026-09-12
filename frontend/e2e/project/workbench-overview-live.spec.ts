import { expect, test } from '../fixtures/dev-backend';
import type { Page, TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const VISUAL_OVERVIEW = {
  projectName: null,
  count: 2,
  currentCount: 2,
  historyCount: 0,
  items: [
    {
      projectName: 'Agent Studio',
      workbench: {
        id: 'docker-ausfuehrungswelt-migration',
        key: 'AGT-W51',
        title: 'Isolated execution: preparation scripts, isolation, and a healing orchestrator',
        summary: 'The target architecture separates preparation, execution isolation, and orchestrator recovery into three layers. Product-owned stable checkouts remain current and provide leased worktrees for every run. Technology building blocks restore npm, NuGet, Node, SDK, and Playwright dependencies from content-addressed caches with explicit manifests and failure signatures. Each project composes those blocks through a repository-owned project definition and preparation script, while Agent Studio validates the definition and proposes repairs after preparation failures or manifest drift. Executor profiles choose process sandboxing by default, containers as an optional accelerator, and micro-VMs only for evidence that requires Docker-level isolation. The orchestrator diagnoses failures from evidence, enriches follow-up prompts, records operator questions on the card, and drives the pipeline toward a terminal outcome without hiding environment defects. The product experience includes onboarding, execution settings, test inventory, duration and cost evidence, pipeline stages, deployment stages, and project-level statistics. Migration is split into bounded delivery stages so preparation and cache correctness land before deeper isolation and automated healing.',
        status: 'decision-pending',
        phase: 'decision-ready',
        updatedAtUtc: '2026-09-12T13:40:00Z',
        entryPath: 'docs/operations/docker-ausfuehrungswelt-migration/index.html',
        valid: true,
        error: null,
        sourceTaskKeys: [],
        relatedTaskKeys: ['AGT-2780', 'AGT-2781', 'AGT-2782', 'AGT-2778'],
        openDecisionCount: 8,
        pattern: 'concept',
        documentation: {
          eligible: false,
          totalCount: 4,
          terminalCount: 0,
          openCount: 4,
          missingCount: 0,
          references: [
            { key: 'AGT-2780', exists: true, terminal: false, lane: '3-progress' },
            { key: 'AGT-2781', exists: true, terminal: false, lane: '2-ready' },
            { key: 'AGT-2782', exists: true, terminal: false, lane: '4-auto-review' },
            { key: 'AGT-2778', exists: false, terminal: false, lane: null },
          ],
        },
      },
    },
    {
      projectName: 'Agent Studio',
      workbench: {
        id: 'task-detail-usage-panel',
        key: 'AGT-W48',
        title: 'Task detail: usage block at pipeline width',
        summary: 'Align task totals, per-run tokens, cost, and agent activity with the pipeline metric columns while preserving the existing run details and cost breakdown controls.',
        status: 'decided',
        phase: 'decision-ready',
        updatedAtUtc: '2026-09-11T06:51:01Z',
        entryPath: 'docs/operations/task-detail-usage-panel/index.html',
        valid: true,
        error: null,
        sourceTaskKeys: [],
        relatedTaskKeys: ['AGT-2769'],
        openDecisionCount: 0,
        pattern: 'ui',
      },
    },
  ],
};

const VISUAL_REFERENCE_STATUSES = [
  {
    key: 'AGT-2780', exists: true, taskKey: 'Agent Studio::AGT-2780', title: 'Stable execution checkouts',
    lane: '3-progress', projectId: 'PROJ-002', projectName: 'Agent Studio', projectColor: null,
    merge: null, reviewGrade: null,
  },
  {
    key: 'AGT-2781', exists: true, taskKey: 'Agent Studio::AGT-2781', title: 'Preparation manifest',
    lane: '2-ready', projectId: 'PROJ-002', projectName: 'Agent Studio', projectColor: null,
    merge: null, reviewGrade: null,
  },
  {
    key: 'AGT-2782', exists: true, taskKey: 'Agent Studio::AGT-2782', title: 'Build and test gate cache',
    lane: '4-auto-review', projectId: 'PROJ-002', projectName: 'Agent Studio', projectColor: null,
    merge: null, reviewGrade: null,
  },
  {
    key: 'AGT-2769', exists: true, taskKey: 'Agent Studio::AGT-2769', title: 'Pipeline-width usage block',
    lane: '6-completed', projectId: 'PROJ-002', projectName: 'Agent Studio', projectColor: null,
    merge: null, reviewGrade: null,
  },
];

function evidencePath(testInfo: TestInfo, fileName: string): string {
  const resultRoot = process.env['JOB_RESULTS_DIR']?.trim();
  const directory = resultRoot ? path.resolve(resultRoot) : testInfo.outputDir;
  fs.mkdirSync(directory, { recursive: true });
  return path.join(directory, fileName);
}

test('captures the Dossier overview at 1536 and 900 px in both themes', async ({ page }, testInfo) => {
  const phase = process.env['DOSSIER_EVIDENCE_PHASE']?.trim() || 'after';
  const projects = [
    {
      sourceType: 'local-folder', id: 'PROJ-002', displayName: 'Agent Studio', shortCode: 'AGT',
      workspaceId: 'workspace', color: '#6f8fc9', cliDefault: null, modelDefault: null,
      sortOrder: 0, storageLocation: '/projects/agent-studio', repositoryPath: null,
      rootPath: '/projects/agent-studio', repositoryUrl: null, urls: [], archived: false,
      createdAt: '2026-01-01T00:00:00Z',
    },
    {
      sourceType: 'local-folder', id: 'PROJ-003', displayName: 'Coding Agent Chat', shortCode: 'CAC',
      workspaceId: 'workspace', color: '#67a783', cliDefault: null, modelDefault: null,
      sortOrder: 1, storageLocation: '/projects/coding-agent-chat', repositoryPath: null,
      rootPath: '/projects/coding-agent-chat', repositoryUrl: null, urls: [], archived: false,
      createdAt: '2026-01-01T00:00:00Z',
    },
  ];
  await page.route('**/api/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      profile: 'local',
      bootstrapRequired: false,
      authenticated: true,
      user: null,
    }),
  }));
  await page.route('**/api/runner/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ projects: {} }),
  }));
  await page.route('**/api/crash-recovery/pending', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ pending: [] }),
  }));
  await page.route('**/api/workbenches**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(VISUAL_OVERVIEW),
  }));
  await page.route('**/api/tasks/reference-status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ items: VISUAL_REFERENCE_STATUSES }),
  }));
  await page.route('**/api/workspaces', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{
      id: 'workspace', displayName: 'Workspace', sortOrder: 0, isDefault: true,
      color: null, createdAt: '2026-01-01T00:00:00Z', projects,
    }]),
  }));
  await page.route('**/api/projects', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(projects),
  }));
  await page.route('**/api/watch-paths', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([]),
  }));
  await page.route('**/api/tasks/grouped', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
      failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
      escalated: [], completed: [], archive: [],
    }),
  }));

  await page.goto('/#/workbenches');
  await expect(page.getByTestId('workbench-overview')).toBeVisible();
  await expect(page.getByTestId('workbench-overview-item-Agent Studio-docker-ausfuehrungswelt-migration'))
    .toBeVisible();
  const longToggle = page.getByTestId(
    'workbench-overview-excerpt-toggle-Agent Studio-docker-ausfuehrungswelt-migration',
  );
  if (phase === 'after') {
    await expect(page.getByTestId('workbench-overview-key-Agent Studio-docker-ausfuehrungswelt-migration'))
      .toContainText('AGT-W51');
    await expect(page.getByTestId('workbench-overview-key-Agent Studio-task-detail-usage-panel'))
      .toContainText('AGT-W48');
    await expect(page.getByTestId('workbench-overview-excerpt-toggle-Agent Studio-task-detail-usage-panel'))
      .toHaveCount(0);
    await expect(longToggle).toHaveText('Show more');
    await expect(longToggle).toHaveAttribute('aria-expanded', 'false');
    const progressDot = page.getByTestId(
      'workbench-overview-task-Agent Studio-docker-ausfuehrungswelt-migration-AGT-2780',
    );
    await expect(progressDot.locator('[data-lane-tone="progress"]')).toHaveCount(1);
    await progressDot.hover();
    await expect(page.getByTestId(
      'workbench-overview-task-Agent Studio-docker-ausfuehrungswelt-migration-AGT-2780-tooltip',
    )).toContainText('Key: AGT-2780');
    const unknownDot = page.getByTestId(
      'workbench-overview-task-Agent Studio-docker-ausfuehrungswelt-migration-AGT-2778',
    );
    await unknownDot.hover();
    await expect(page.getByTestId(
      'workbench-overview-task-Agent Studio-docker-ausfuehrungswelt-migration-AGT-2778-tooltip',
    )).toContainText('State: Unknown or deleted');
    await page.mouse.move(0, 0);
  }

  for (const [widthName, width] of [['1536', 1536], ['900', 900]] as const) {
    await page.setViewportSize({ width, height: 1400 });
    if (phase === 'after') {
      const excerptBox = await page.locator(`#${await longToggle.getAttribute('aria-controls')}`).boundingBox();
      const actionsBox = await page.getByTestId(
        'workbench-overview-actions-Agent Studio-docker-ausfuehrungswelt-migration',
      ).boundingBox();
      expect(excerptBox?.width).toBeGreaterThan(width === 1536 ? 600 : 400);
      expect(actionsBox!.y).toBeGreaterThan(excerptBox!.y + excerptBox!.height);
    }
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: evidencePath(testInfo, `workbench-overview-${phase}-${theme}-${widthName}--mocked.png`),
        fullPage: true,
      });
    }
  }
  if (phase === 'after') {
    await page.setViewportSize({ width: 740, height: 1000 });
    const rowBox = await page.getByTestId(
      'workbench-overview-item-Agent Studio-docker-ausfuehrungswelt-migration',
    ).boundingBox();
    const excerptBox = await page.locator(`#${await longToggle.getAttribute('aria-controls')}`).boundingBox();
    const actionsBox = await page.getByTestId(
      'workbench-overview-actions-Agent Studio-docker-ausfuehrungswelt-migration',
    ).boundingBox();
    expect(rowBox!.x + rowBox!.width).toBeLessThanOrEqual(740);
    expect(actionsBox!.y).toBeGreaterThan(excerptBox!.y + excerptBox!.height);
  }
});

async function proxyApi(page: Page, backendBaseUrl: string): Promise<void> {
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    const json = (body: unknown) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });
    if (/^\/api\/cli\/[^/]+\/models$/.test(url.pathname))
      return json({ models: [], source: 'workbench-live-e2e' });
    if (url.pathname === '/api/cli/quota')
      return json({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] });
    if (url.pathname === '/api/cli/usage')
      return json({ at: new Date().toISOString(), sessions: [] });
    if (url.pathname === '/api/crash-recovery/pending')
      return json({ pending: [] });
    const response = await route.fetch({
      url: `${backendBaseUrl}${url.pathname}${url.search}`,
      timeout: 30_000,
    });
    await route.fulfill({ response });
  });
}

test('project and central overviews receive a newly created item without reloading the Tree', async ({ page, devBackend }, testInfo) => {
  test.setTimeout(150_000);
  const watchPathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  expect(watchPathsResponse.ok).toBe(true);
  const watchPaths = await watchPathsResponse.json() as { name: string; rootPath?: string; repositoryPath?: string }[];
  let projectName: string | null = null;
  for (const candidate of watchPaths) {
    const response = await fetch(
      `${devBackend.baseUrl}/api/projects/${encodeURIComponent(candidate.name)}/workbenches?history=true`,
    );
    if (!response.ok) continue;
    const body = await response.json() as { items?: { id: string }[] };
    if (body.items?.some(item => item.id === 'workbench-konzept')) {
      projectName = candidate.name;
      break;
    }
  }
  expect(projectName, 'The dev backend must expose the task checkout Dossiers.').not.toBeNull();

  const id = `live-tree-proof-${Date.now().toString(36)}`;
  const probeDir = path.join(devBackend.workspace, 'docs', 'operations', id);
  try {
    await proxyApi(page, devBackend.baseUrl);
    const hubConnected = page.waitForEvent('websocket', {
      predicate: socket => socket.url().includes('/hubs/jobs'),
      timeout: 15_000,
    });
    await page.goto('/');
    await hubConnected;
    await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });

    const projectRow = page.getByTestId(`studio-explorer-project-${projectName}`);
    await expect(projectRow).toBeVisible();
    if (await projectRow.getAttribute('aria-expanded') === 'false') await projectRow.click();

    const sectionRow = page.getByTestId(`studio-explorer-project-workbenches-${projectName}`);
    await expect(sectionRow).toBeVisible();
    await sectionRow.click();
    await expect(page).toHaveURL(/\/workbenches(?:&|$)/);
    await expect(page.getByTestId('workbench-overview-scope')).toContainText(projectName!);

    fs.mkdirSync(probeDir, { recursive: true });
    fs.writeFileSync(path.join(probeDir, 'index.html'), `<!doctype html>
<html>
  <head><style>:root { color-scheme: light dark; } body { color: CanvasText; background: Canvas; }</style></head>
  <body>
    <h1>Live creation proof</h1>
    <section data-decision-id="delivery" data-decision-kind="single">
      <strong>Choose delivery</strong>
      <span data-option-id="direct">Direct</span>
      <span data-option-id="staged">Staged</span>
    </section>
  </body>
</html>`);
    fs.writeFileSync(path.join(probeDir, 'workbench.json'), JSON.stringify({
      schemaVersion: 1,
      id,
      title: 'Live creation proof',
      summary: 'Created while the project overview and Explorer Tree are already open.',
      entrypoint: 'index.html',
      status: 'decision-pending',
      phase: 'decision-ready',
      updatedAt: new Date().toISOString(),
      sourceTaskKeys: [],
      relatedTaskKeys: [],
    }, null, 2));

    const treeItem = page.getByTestId(`studio-explorer-workbench-${projectName}-${id}`);
    await expect(treeItem, 'SignalR created event must add the Tree child without page.reload().')
      .toBeVisible({ timeout: 15_000 });
    await expect(treeItem).toContainText('1 open');
    await expect(page.getByTestId(`workbench-overview-item-${projectName}-${id}`)).toBeVisible();

    const overviewUrl = page.url();
    await page.getByTestId(`workbench-overview-open-${projectName}-${id}`).click();
    await expect(page).toHaveURL(overviewUrl);
    const inlineViewer = page.getByTestId(`workbench-overview-inline-${projectName}-${id}`);
    await expect(inlineViewer).toBeVisible();
    await expect(inlineViewer.frameLocator('[data-testid="workbench-viewer-frame"]')
      .locator('[data-studio-decision-control]')).toHaveCount(2);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: evidencePath(testInfo, `workbench-project-overview-${theme}--real.png`),
        fullPage: true,
      });
    }

    await page.getByTestId('studio-ab-workbenches').click();
    await expect(page).toHaveURL(/#\/workbenches(?:&|$)/);
    await expect(page.getByTestId('workbench-overview-scope')).toHaveCount(0);
    await expect(page.getByTestId(`workbench-overview-item-${projectName}-${id}`)).toBeVisible();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: evidencePath(testInfo, `workbench-central-overview-${theme}--real.png`),
        fullPage: true,
      });
    }
  } finally {
    fs.rmSync(probeDir, { recursive: true, force: true });
  }
});
