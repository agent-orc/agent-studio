import { expect, test } from '../fixtures/dev-backend';
import type { Page, TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const VISUAL_OVERVIEW = {
  projectName: null,
  count: 5,
  currentCount: 3,
  historyCount: 2,
  items: [
    {
      projectName: 'Agent Studio',
      workbench: {
        id: 'admin-design-language',
        key: 'AGT-W12',
        title: 'Admin surface design language',
        summary: 'Define a calm, consistent visual grammar for dense operator surfaces and their decision queues.',
        status: 'decision-pending',
        taggingStatus: 'tagged',
        phase: 'decision-ready',
        updatedAtUtc: '2026-08-11T12:25:00Z',
        entryPath: 'docs/operations/admin-design-guideline/index.html',
        valid: true,
        error: null,
        sourceTaskKeys: ['AGT-2606'],
        relatedTaskKeys: ['AGT-2611'],
        openDecisionCount: 1,
        pattern: 'ui',
        documentation: {
          eligible: false,
          totalCount: 2,
          terminalCount: 0,
          openCount: 2,
          missingCount: 0,
          references: [
            { key: 'AGT-2606', exists: true, terminal: false, lane: '3-progress' },
            { key: 'AGT-2611', exists: true, terminal: false, lane: '2-ready' },
          ],
        },
      },
    },
    {
      projectName: 'Coding Agent Chat',
      workbench: {
        id: 'conversation-recovery',
        key: 'CAC-W4',
        title: 'Conversation recovery contract',
        summary: 'Keep interrupted operator conversations resumable without duplicating settled work.',
        status: 'active',
        taggingStatus: 'tags-proposed',
        phase: 'testing',
        updatedAtUtc: '2026-08-10T16:40:00Z',
        entryPath: 'docs/operations/conversation-recovery/index.html',
        valid: true,
        error: null,
        sourceTaskKeys: [],
        relatedTaskKeys: ['CAC-418'],
        openDecisionCount: 0,
        pattern: 'concept',
      },
    },
    {
      projectName: 'Agent Studio',
      workbench: {
        id: 'runner-host-hardening',
        key: 'AGT-W9',
        title: 'Runner host hardening',
        summary: 'The direction is accepted while the linked implementation cards move through delivery.',
        status: 'decided',
        taggingStatus: 'future-status',
        phase: 'testing',
        updatedAtUtc: '2026-08-10T09:15:00Z',
        entryPath: 'docs/operations/runner-host-hardening/index.html',
        valid: true,
        error: null,
        sourceTaskKeys: [],
        relatedTaskKeys: ['AGT-2590'],
        openDecisionCount: 0,
        pattern: 'concept',
      },
    },
    {
      projectName: 'Agent Studio',
      workbench: {
        id: 'old-navigation-study',
        key: 'AGT-W3',
        title: 'Old navigation study',
        summary: 'Superseded direction retained for traceability.',
        status: 'archived',
        phase: null,
        updatedAtUtc: '2026-08-04T11:00:00Z',
        entryPath: 'docs/archive/old-navigation-study/index.html',
        valid: true,
        error: null,
        sourceTaskKeys: [],
        relatedTaskKeys: [],
        openDecisionCount: 0,
        pattern: 'ui',
      },
    },
    {
      projectName: 'Agent Studio',
      workbench: {
        id: 'task-reference-contract',
        key: 'AGT-W2',
        title: 'Task reference contract',
        summary: 'Settled contract recorded in the product documentation.',
        status: 'documented',
        phase: null,
        updatedAtUtc: '2026-08-02T08:30:00Z',
        entryPath: 'docs/system/contracts/task-reference.html',
        valid: true,
        error: null,
        sourceTaskKeys: [],
        relatedTaskKeys: [],
        openDecisionCount: 0,
        pattern: 'concept',
      },
    },
  ],
};

const VISUAL_REFERENCE_STATUSES = [
  {
    key: 'AGT-2606', exists: true, taskKey: 'Agent Studio::AGT-2606', title: 'Calm Dossier list',
    lane: '3-progress', projectId: 'PROJ-002', projectName: 'Agent Studio', projectColor: null,
    merge: null, reviewGrade: null,
  },
  {
    key: 'AGT-2611', exists: true, taskKey: 'Agent Studio::AGT-2611', title: 'Dossier task references',
    lane: '2-ready', projectId: 'PROJ-002', projectName: 'Agent Studio', projectColor: null,
    merge: null, reviewGrade: null,
  },
  {
    key: 'CAC-418', exists: true, taskKey: 'Coding Agent Chat::CAC-418', title: 'Conversation recovery',
    lane: '5-human-review', projectId: 'PROJ-003', projectName: 'Coding Agent Chat', projectColor: null,
    merge: null, reviewGrade: null,
  },
  {
    key: 'AGT-2590', exists: true, taskKey: 'Agent Studio::AGT-2590', title: 'Runner host hardening',
    lane: '4-auto-review', projectId: 'PROJ-002', projectName: 'Agent Studio', projectColor: null,
    merge: null, reviewGrade: null,
  },
];

function evidencePath(testInfo: TestInfo, fileName: string): string {
  const resultRoot = process.env['JOB_RESULTS_DIR']?.trim();
  const directory = resultRoot ? path.resolve(resultRoot) : testInfo.outputDir;
  fs.mkdirSync(directory, { recursive: true });
  return path.join(directory, fileName);
}

test('captures the Dossier overview at wide and narrow widths in both themes', async ({ page }, testInfo) => {
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
  await page.route('**/api/tasks/grouped**', route => route.fulfill({
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
  await expect(page.getByTestId('workbench-overview-item-Agent Studio-admin-design-language'))
    .toBeVisible();
  await expect(page.getByTestId('workbench-overview-item-Agent Studio-admin-design-language'))
    .toContainText('Auto-tagged');
  await expect(page.getByTestId('workbench-overview-item-Coding Agent Chat-conversation-recovery'))
    .toContainText('Tags proposed');
  await expect(page.getByTestId('workbench-overview-item-Agent Studio-runner-host-hardening')
    .getByTestId('workbench-tagging-status')).toHaveCount(0);

  for (const [widthName, width] of [['wide', 1440], ['narrow', 760]] as const) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: evidencePath(testInfo, `workbench-overview-${phase}-${theme}-${widthName}--mocked.png`),
        fullPage: true,
      });
    }
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
      // A cold dev-server bundle can take longer than the default navigation window.
      timeout: 45_000,
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
