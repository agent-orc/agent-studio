import { expect, test, type Locator, type Page, type Route } from '@playwright/test';

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
  escalated: [], completed: [], archive: [],
};

interface PutObservation {
  projectId: string;
  workspaceId: string;
}

function project(workspaceId: string) {
  return {
    id: 'PROJ-POINTER', displayName: 'Pointer Project', shortCode: 'PTR', workspaceId,
    color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
    storageLocation: 'C:/fixtures/Pointer Project', repositoryPath: null, rootPath: null,
    repositoryUrl: null, urls: [], archived: false, createdAt: '2026-07-23T00:00:00Z',
  };
}

function unassignedProject() {
  return {
    ...project(''),
    id: 'PROJ-UNASSIGNED',
    displayName: 'Unassigned Project',
    shortCode: 'UNA',
    storageLocation: 'C:/fixtures/Unassigned Project',
  };
}

async function installRoutes(
  page: Page,
  initialWorkspaceId = 'WS-A',
  includeUnassignedProject = false,
): Promise<PutObservation[]> {
  const observations: PutObservation[] = [];
  const pointerProject = project(initialWorkspaceId);
  const registryProjects = [
    pointerProject,
    ...(includeUnassignedProject ? [unassignedProject()] : []),
  ];
  const workspaces = [
    {
      id: 'WS-A',
      displayName: 'Alpha Workspace',
      sortOrder: 0,
      isDefault: true,
      color: null,
      createdAt: '2026-07-23T00:00:00Z',
      projects: initialWorkspaceId === 'WS-A' ? [pointerProject] : [],
    },
    { id: 'WS-B', displayName: 'Beta Workspace', sortOrder: 1, isDefault: false, color: null, createdAt: '2026-07-23T00:00:00Z', projects: [] as ReturnType<typeof project>[] },
  ];
  const json = (route: Route, body: unknown) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/v1/studio/board**', route => json(route, EMPTY_GROUPED));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0 }));
  await page.route('**/api/watch-paths**', route => json(route, [{
    name: 'Pointer Project', path: 'C:/fixtures/Pointer Project',
    rootPath: 'C:/fixtures/Pointer Project', repositoryPath: 'C:/fixtures/Pointer Project',
  }]));
  await page.route('**/api/v1/workspaces**', route => json(route, workspaces));
  await page.route('**/api/v1/projects', route => json(route, registryProjects));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/cli/usage**', route => json(route, { at: '2026-07-23T00:00:00Z', sessions: [] }));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-07-23T00:00:00Z', ttlSeconds: 600, snapshots: [] }));
  await page.route('**/hubs/v1/studio/negotiate**', route => json(route, {
    negotiateVersion: 1,
    connectionId: 'project-move-inputs',
    connectionToken: 'project-move-inputs',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  }));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol"')) {
        socket.send('{}\u001e');
      }
    });
  });
  await page.route('**/api/projects/*', async (route, request) => {
    if (request.method() !== 'PUT') return route.continue();
    const projectId = decodeURIComponent(new URL(request.url()).pathname.split('/').pop() ?? '');
    const workspaceId = String((request.postDataJSON() as { workspaceId?: string }).workspaceId ?? '');
    observations.push({ projectId, workspaceId });
    const source = workspaces.find(workspace => workspace.projects.some(item => item.id === projectId));
    const target = workspaces.find(workspace => workspace.id === workspaceId);
    const moved = registryProjects.find(item => item.id === projectId);
    if (target && moved) {
      if (source) source.projects = source.projects.filter(item => item.id !== projectId);
      moved.workspaceId = workspaceId;
      target.projects = [...target.projects, moved];
    }
    await json(route, moved ?? {});
  });
  return observations;
}

async function openApp(page: Page): Promise<void> {
  await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  await expect(page.getByTestId('studio-explorer-workspace-head')).toBeVisible();
}

async function openWorkspaces(page: Page): Promise<void> {
  await page.getByTestId('studio-ab-settings').click();
  await page.getByTestId('workspace-settings-rail-workspaces').click();
  await expect(page.getByTestId('settings-workspaces')).toBeVisible();
}

function settingsWorkspace(page: Page, name: string): Locator {
  return page.getByTestId('settings-workspace-row').filter({ hasText: name });
}

async function mouseDrag(page: Page, source: Locator, target: Locator): Promise<void> {
  const from = await source.boundingBox();
  const to = await target.boundingBox();
  expect(from).not.toBeNull();
  expect(to).not.toBeNull();
  await page.mouse.move(from!.x + from!.width / 2, from!.y + from!.height / 2);
  await page.mouse.down();
  await page.mouse.move(from!.x + from!.width / 2 + 12, from!.y + from!.height / 2 + 12, { steps: 3 });
  await page.mouse.move(to!.x + to!.width / 2, to!.y + to!.height / 2, { steps: 12 });
}

async function touchDrag(page: Page, source: Locator, target: Locator): Promise<void> {
  await expect(source).toBeVisible();
  await expect(target).toBeVisible();
  const from = await source.boundingBox();
  const to = await target.boundingBox();
  expect(from).not.toBeNull();
  expect(to).not.toBeNull();
  const session = await page.context().newCDPSession(page);
  const start = { x: from!.x + from!.width / 2, y: from!.y + from!.height / 2 };
  const end = { x: to!.x + to!.width / 2, y: to!.y + to!.height / 2 };
  await session.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [start] });
  for (let step = 1; step <= 12; step += 1) {
    await session.send('Input.dispatchTouchEvent', {
      type: 'touchMove',
      touchPoints: [{
        x: start.x + ((end.x - start.x) * step) / 12,
        y: start.y + ((end.y - start.y) * step) / 12,
      }],
    });
  }
  await expect(target).toHaveClass(/drop-target/);
  await session.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await session.detach();
}

test.describe('project move input paths', () => {
  test('a registered project with no workspace moves out of Unassigned', async ({ page }) => {
    const observations = await installRoutes(page, '');
    await openApp(page);

    const unassigned = page.getByTestId('studio-explorer-ws-drop-__unassigned__');
    const projectRow = page.getByTestId('studio-explorer-project-row-Pointer Project');
    const target = page.getByTestId('studio-explorer-ws-drop-WS-A');
    await expect(unassigned.getByTestId('studio-explorer-project-Pointer Project')).toBeVisible();
    await expect(projectRow).not.toHaveAttribute('aria-disabled', 'true');

    await mouseDrag(page, projectRow, target);
    await expect(target).toHaveClass(/drop-target/);
    await page.mouse.up();

    await expect.poll(() => observations).toEqual([
      { projectId: 'PROJ-POINTER', workspaceId: 'WS-A' },
    ]);
    await expect(target.getByTestId('studio-explorer-project-row-Pointer Project')).toBeVisible();
    await expect(page.getByTestId('studio-explorer-ws-drop-__unassigned__')).toHaveCount(0);
  });

  test('Settings can move a registered project out of Unassigned without making the bucket a target', async ({ page }) => {
    const observations = await installRoutes(page, '');
    await openApp(page);
    await openWorkspaces(page);

    const unassigned = settingsWorkspace(page, 'Unassigned');
    const projectRow = unassigned.getByTestId('settings-project-row');
    const alpha = settingsWorkspace(page, 'Alpha Workspace');
    await expect(projectRow).toBeVisible();

    await mouseDrag(page, projectRow, alpha);
    await expect(alpha).toHaveClass(/drop-target/);
    await page.mouse.up();

    await expect.poll(() => observations).toEqual([
      { projectId: 'PROJ-POINTER', workspaceId: 'WS-A' },
    ]);
    await expect(alpha.getByTestId('settings-project-row')).toBeVisible();
    await expect(settingsWorkspace(page, 'Unassigned')).toHaveCount(0);
  });

  test('Settings offers the move menu for an Unassigned project', async ({ page }) => {
    const observations = await installRoutes(page, '');
    await openApp(page);
    await openWorkspaces(page);

    const unassigned = settingsWorkspace(page, 'Unassigned');
    await unassigned.getByTestId('settings-project-workspace').click();
    await expect(page.getByTestId('settings-project-move-panel')).toBeVisible();
    await expect(page.getByTestId('settings-project-move-item-WS-A')).toBeVisible();
    await expect(page.getByTestId('settings-project-move-item-WS-B')).toBeVisible();
    await page.getByTestId('settings-project-move-item-WS-A').click();

    await expect.poll(() => observations).toEqual([
      { projectId: 'PROJ-POINTER', workspaceId: 'WS-A' },
    ]);
    await expect(settingsWorkspace(page, 'Alpha Workspace').getByTestId('settings-project-row')).toBeVisible();
    await expect(settingsWorkspace(page, 'Unassigned')).toHaveCount(0);
  });

  test('Settings never accepts a project on the synthetic Unassigned bucket', async ({ page }) => {
    const observations = await installRoutes(page, 'WS-A', true);
    await openApp(page);
    await openWorkspaces(page);

    const alpha = settingsWorkspace(page, 'Alpha Workspace');
    const unassigned = settingsWorkspace(page, 'Unassigned');
    await mouseDrag(page, alpha.getByTestId('settings-project-row'), unassigned);
    await expect(unassigned).not.toHaveClass(/drop-target/);
    await page.mouse.up();

    expect(observations).toHaveLength(0);
    await expect(alpha.getByTestId('settings-project-row')).toBeVisible();
    await expect(unassigned.getByTestId('settings-project-row')).toBeVisible();
  });

  test('mouse pointer moves in Explorer and Settings, and the menu path remains available', async ({ page }, testInfo) => {
    const observations = await installRoutes(page);
    await openApp(page);
    const explorerSource = page.getByTestId('studio-explorer-project-row-Pointer Project');
    const explorerSourceWorkspace = page.getByTestId('studio-explorer-ws-drop-WS-A');
    const explorerTarget = page.getByTestId('studio-explorer-ws-drop-WS-B');
    await expect(explorerSource).toBeVisible();
    await mouseDrag(page, explorerSource, explorerSourceWorkspace);
    await expect(explorerSourceWorkspace).not.toHaveClass(/drop-target/);
    await page.mouse.up();
    expect(observations).toHaveLength(0);
    await expect(explorerSourceWorkspace.getByTestId('studio-explorer-project-row-Pointer Project')).toBeVisible();

    await mouseDrag(page, explorerSource, explorerTarget);
    await expect(explorerTarget).toHaveClass(/drop-target/);
    await page.mouse.up();
    await expect.poll(() => observations.length).toBe(1);
    await expect(explorerTarget.getByTestId('studio-explorer-project-row-Pointer Project')).toBeVisible();

    await openWorkspaces(page);
    const beta = settingsWorkspace(page, 'Beta Workspace');
    const alpha = settingsWorkspace(page, 'Alpha Workspace');
    await mouseDrag(page, beta.getByTestId('settings-project-row'), alpha);
    await expect(alpha).toHaveClass(/drop-target/);
    await page.mouse.up();
    await expect.poll(() => observations.length).toBe(2);
    await expect(alpha.getByTestId('settings-project-row')).toBeVisible();

    await alpha.getByTestId('settings-project-workspace').click();
    await expect(page.getByTestId('settings-project-move-panel')).toBeVisible();
    await expect(page.getByTestId('settings-project-move-item-WS-A')).toHaveCount(0);
    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
      await testInfo.attach(`project-move-menu-${theme}.png`, {
        body: await page.screenshot(),
        contentType: 'image/png',
      });
    }
    await page.getByTestId('settings-project-move-item-WS-B').click();
    await expect.poll(() => observations.length).toBe(3);
    expect(observations).toEqual([
      { projectId: 'PROJ-POINTER', workspaceId: 'WS-B' },
      { projectId: 'PROJ-POINTER', workspaceId: 'WS-A' },
      { projectId: 'PROJ-POINTER', workspaceId: 'WS-B' },
    ]);
    await expect(beta.getByTestId('settings-project-row')).toBeVisible();
  });

  test('the menu move shows a busy row and an inline error when persistence fails', async ({ page }) => {
    await installRoutes(page);
    let releasePut: (() => void) | undefined;
    const putGate = new Promise<void>(resolve => { releasePut = resolve; });
    await page.route('**/api/projects/*', async (route, request) => {
      if (request.method() !== 'PUT') return route.continue();
      await putGate;
      await route.fulfill({
        status: 500,
        contentType: 'application/json',
        body: JSON.stringify({ error: 'Registry unavailable' }),
      });
    });
    await openApp(page);
    await openWorkspaces(page);
    const alpha = settingsWorkspace(page, 'Alpha Workspace');
    await alpha.getByTestId('settings-project-workspace').click();
    await page.getByTestId('settings-project-move-item-WS-B').click();
    await expect(alpha.getByTestId('settings-project-row')).toHaveClass(/--busy/);
    releasePut?.();
    await expect(page.getByTestId('settings-project-move-error')).toContainText('Registry unavailable');
    await expect(alpha.getByTestId('settings-project-row')).not.toHaveClass(/--busy/);
  });
});

test.describe('project move touch emulation', () => {
  test.use({ hasTouch: true, viewport: { width: 1440, height: 900 } });

  test('touch moves a project in both Explorer and Settings', async ({ page }) => {
    const observations = await installRoutes(page);
    await openApp(page);
    const explorerTarget = page.getByTestId('studio-explorer-ws-drop-WS-B');
    await touchDrag(page, page.getByTestId('studio-explorer-project-row-Pointer Project'), explorerTarget);
    await expect.poll(() => observations.length).toBe(1);
    await expect(explorerTarget.getByTestId('studio-explorer-project-row-Pointer Project')).toBeVisible();

    await openWorkspaces(page);
    const beta = settingsWorkspace(page, 'Beta Workspace');
    const alpha = settingsWorkspace(page, 'Alpha Workspace');
    await touchDrag(page, beta.getByTestId('settings-project-row'), alpha);
    await expect.poll(() => observations.length).toBe(2);
    expect(observations.map(item => item.workspaceId)).toEqual(['WS-B', 'WS-A']);
    await expect(alpha.getByTestId('settings-project-row')).toBeVisible();
  });
});
