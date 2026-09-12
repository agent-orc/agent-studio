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
        id: 'docker-ausfuehrungswelt-migration',
        key: 'AGT-W51',
        title: 'Isolierte Ausführung: Vorbereitungsskripte, Isolation, heilender Orchestrator',
        summary: 'Operator-Entscheidungen 12.09.2026 (Vormittag: Windows keine Ausführungswelt, Docker für alles; Nachmittag präzisiert: Onboarding darf nicht komplizierter werden, Worktree-Vorbereitung als Skript, das sich um alles kümmert, Docker-Image nur optionale Beschleunigung, Kernproblem sind Versionen, dazu ein Orchestrator, der Probleme gründlich bewertet, Folgeprompts ergänzt und die Pipeline heilt; für die Produktversion reichen Coding-Agents in isolierten Umgebungen, Kommunikation und Heilung laufen durch Agent Studio). Zielbild in drei Schichten: Vorbereitung als Basis vom Produkt (stabile, laufend aktualisierte Checkouts, Worktrees mit Lease), Technologie-Bausteinen mit Caches (npm, NuGet, Node, SDK, Playwright) und projekteigenem Skript (project.yml + prepare), das der Orchestrator mit dem Nutzer entwickelt und auf eigene Initiative pflegt, Isolation je Lauf nach Executor-Profil (Sandbox-Prozess Standard, Container optional, Micro-VM für Docker-Beweise), Orchestrator mit Diagnose, Heilung, Folgeprompt und Frage in der Karte. Produkterfahrung für Installierende, Auswirkungen je Studio-Komponente und je Projekt (17), Migration M1–M7 (9–12 Wochen), Risiken, Tests und CI/CD als Produktfeature (Inventar, Testlauf-Historie mit Laufzeiten und Läufen, Stabilisierung durch den Orchestrator, Pipeline-Engine mit Stufen aus project.yml, Deploy als Stufe, Dauer und Kosten je Schritt im Lauf und als Statistikseite je Projekt (Dauer, CPU, IO, Tokens, Fehlerhäufigkeit je Schritt); AGT: 7.296 Backend-Tests in 24 min), Entscheidungen D1–D8, Karten MK1–MK14. Vormittagsfassung „Docker als einzige Ausführungswelt\" in der Git-Historie.',
        status: 'decision-pending',
        phase: 'decision-ready',
        updatedAtUtc: '2026-08-11T12:25:00Z',
        entryPath: 'docs/operations/docker-ausfuehrungswelt-migration/index.html',
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
      projectName: 'Agent Studio',
      workbench: {
        id: 'task-detail-usage-panel',
        key: 'AGT-W48',
        title: 'Task-Detail: Verbrauchsblock in Pipeline-Breite',
        summary: 'Operator-Befund 11.09.2026 am Task-Detail (Overview-Tab): Task-Summe, Tokens je Lauf und Agent-Arbeit stehen unter den Pipeline-Schritten in einem eigenen, schmalen Raster; die Zahlen sitzen auf anderen Spalten als die Schrittmetriken. Drei Alternativen mit Mockups: A dieselben Spalten (ein Raster, Anteilsbalken je Lauf), B Tabelle mit eigener Kopfzeile auf denselben Tracks, C Kennzahlen-Kacheln plus Läufe im Raster. Empfehlung A. Entscheidung per Auswahl im Dossier; die Umsetzungskarte entsteht aus der Auswahl.',
        status: 'decided',
        phase: 'decision-ready',
        updatedAtUtc: '2026-09-11T06:51:01Z',
        entryPath: 'docs/operations/task-detail-usage-panel/index.html',
        valid: true,
        error: null,
        sourceTaskKeys: [],
        relatedTaskKeys: ['AGT-2769'],
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

test('captures long and short Dossier summaries at 1536 and 900 px in both themes', async ({ page }, testInfo) => {
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
  await expect(page.getByTestId('workbench-overview-item-Agent Studio-task-detail-usage-panel'))
    .toBeVisible();

  const longToggle = page.getByTestId(
    'workbench-overview-summary-toggle-Agent Studio-docker-ausfuehrungswelt-migration',
  );
  const longExcerpt = page.locator('#workbench-overview-summary-Agent-Studio-docker-ausfuehrungswelt-migration');
  const longActions = page.getByTestId(
    'workbench-overview-actions-Agent Studio-docker-ausfuehrungswelt-migration',
  );
  await expect(longToggle).toHaveText('Show more');
  await expect(page.getByTestId(
    'workbench-overview-summary-toggle-Agent Studio-task-detail-usage-panel',
  )).toHaveCount(0);
  expect(await longActions.evaluate((actions, excerptId) => {
    const excerpt = document.getElementById(excerptId);
    return excerpt !== null && actions.getBoundingClientRect().top >= excerpt.getBoundingClientRect().bottom;
  }, 'workbench-overview-summary-Agent-Studio-docker-ausfuehrungswelt-migration')).toBe(true);
  await longToggle.click();
  await expect(longToggle).toHaveText('Show less');
  await expect(longExcerpt).toHaveClass(/workbench-overview__excerpt--expanded/);
  await longToggle.click();
  await expect(longToggle).toHaveText('Show more');

  for (const [widthName, width] of [['1536', 1536], ['900', 900]] as const) {
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
