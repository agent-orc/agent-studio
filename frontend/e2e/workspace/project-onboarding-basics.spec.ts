import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

const results = process.env.JOB_RESULTS_DIR
  ? join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : join(__dirname, '..', '..', 'test-results', 'project-onboarding-basics-screenshots');

interface ProjectFixture {
  sourceType: 'local-folder';
  id: string;
  displayName: string;
  shortCode: string;
  workspaceId: string;
  color: string;
  cliDefault: string | null;
  modelDefault: string | null;
  sortOrder: number;
  storageLocation: string;
  repositoryPath: string | null;
  rootPath: string | null;
  repositoryUrl: string | null;
  urls: {
    id: string;
    label: string;
    url: string;
    sortOrder: number;
    startRule: null;
  }[];
  archived: boolean;
  createdAt: string;
}

interface RegistryFixture {
  id: string;
  displayName: string;
  sortOrder: number;
  isDefault: boolean;
  color: string | null;
  createdAt: string;
  projects: ProjectFixture[];
}

const existingProject = (): ProjectFixture => ({
  sourceType: 'local-folder',
  id: 'PROJ-023',
  displayName: 'Existing Project',
  shortCode: 'EX',
  workspaceId: 'ws-default',
  color: '#569cd6',
  cliDefault: 'claude',
  modelDefault: null,
  sortOrder: 0,
  // Task storage belongs to the Agent Studio store, not to the checkout below.
  storageLocation: 'C:/AgentStudio/store/projects/PROJ-023/tasks',
  repositoryPath: 'C:/Projects/existing-project',
  rootPath: 'C:/Projects/existing-project/frontend',
  repositoryUrl: 'https://github.com/example/existing-project',
  urls: [{
    id: 'repo',
    label: 'Repository',
    url: 'https://github.com/example/existing-project',
    sortOrder: 0,
    startRule: null,
  }],
  archived: false,
  createdAt: '2026-07-01T00:00:00Z',
});

function registryWith(project: ProjectFixture): RegistryFixture[] {
  return [{
    id: 'ws-default',
    displayName: 'Default',
    sortOrder: 0,
    isDefault: true,
    color: null,
    createdAt: '2026-01-01T00:00:00Z',
    projects: [project],
  }];
}

async function fulfillJson(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function mockBootApis(page: Page, currentProject: () => ProjectFixture): Promise<void> {
  await page.route('**/api/v1/studio/auth/status', (route) => fulfillJson(route, {
    profile: 'local',
    bootstrapRequired: false,
    authenticated: true,
    user: null,
  }));
  await page.route('**/api/crash-recovery/pending', (route) => fulfillJson(route, { pending: [] }));
  await page.route('**/api/watch-paths**', (route) => fulfillJson(route, [{
    name: currentProject().displayName,
    path: currentProject().repositoryPath,
    rootPath: currentProject().rootPath,
  }]));
  await page.route('**/api/v1/workspaces**', (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    return fulfillJson(route, registryWith(currentProject()));
  });
  await page.route('**/api/v1/projects', (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    return fulfillJson(route, [currentProject()]);
  });
  await page.route('**/api/projects/settings', (route) => fulfillJson(route, {
    [currentProject().displayName]: {
      maxParallelism: 1,
      pickupMode: 'auto',
      executionLocation: 'agent-runner-01',
    },
  }));
  await page.route('**/api/projects/*/snapshot', (route) => fulfillJson(route, {
    settings: {
      autoCommit: false,
      crashRecoveryEnabled: false,
      autoPushStrategy: 'never',
      runnerMode: null,
      orchestratorModel: null,
    },
    runnerStatus: null,
    orchestratorLogTail: [],
    orchestratorSession: null,
    paths: null,
    reviewDecisionsPending: [],
    runnerPendingDecisions: [],
    queueHealth: null,
  }));
  await page.route('**/api/projects/*/cli-modes', (route) => fulfillJson(route, {
    resolved: {}, overrides: {}, available: ['yolo', 'workspace-write'],
  }));
  await page.route('**/api/projects/*/cli-context-modes', (route) => fulfillJson(route, {
    resolved: {}, overrides: {}, available: ['clean', 'shared'],
  }));
  await page.route('**/api/v1/studio/board**', (route) => fulfillJson(route, {
    preparation: [], ready: [], progress: [], review: [], completed: [], archive: [],
  }));
  await page.route('**/api/tasks', (route) => fulfillJson(route, []));
  await page.route('**/api/v1/studio/runner/status**', (route) => fulfillJson(route, { projects: {} }));
  await page.route('**/api/runner/queue-starvation', (route) => fulfillJson(route, {
    active: false,
    waitingTaskCount: 0,
    availableSlots: 0,
    thresholdMinutes: 30,
    observedAt: new Date().toISOString(),
    oldestEnteredLaneAt: null,
    items: [],
  }));
  await page.route('**/api/clients', (route) => fulfillJson(route, [{
    id: 'agent-runner-01',
    displayName: 'agent-runner-01',
    emoji: null,
    colour: null,
    kind: 'agent-instance',
    registeredAt: new Date(Date.now() - 10_000).toISOString(),
    lastSeenAt: new Date().toISOString(),
    tokenBudgetMonthly: null,
    notes: null,
    runnerGitStatus: 'ok',
    runnerGitDetail: null,
    runnerActiveSlots: 0,
    runnerAvailableSlots: 1,
  }]));
  await page.route('**/api/clients/agent-runner-01/telemetry**', (route) => fulfillJson(route, {
    clientId: 'agent-runner-01',
    window: '14d',
    points: [],
    findings: [],
  }));
  await page.route('**/api/clients/*/defaults', (route) => fulfillJson(route, {
    defaultCliType: 'claude',
    defaultModel: null,
    defaultThinkingLevel: null,
  }));
  await page.route('**/api/cli/quota/caps', (route) => fulfillJson(route, {
    defaultCapPct: 95,
    caps: {},
  }));
  await page.route('**/api/workspaces/ws-default/settings', (route) => fulfillJson(route, {
    orchestratorModel: null,
    orchestratorThinkingLevel: null,
    autonomyLevel: null,
    defaultOrchestratorModel: 'claude-sonnet-4-6',
    defaultAutonomyLevel: 2,
  }));
  await page.route('**/api/cli/*/models*', (route) => fulfillJson(route, {
    models: [], source: 'project-onboarding-basics-e2e',
  }));
  await page.route('**/api/dev-tools/flags', (route) => fulfillJson(route, {
    updateStableEnabled: false,
    deleteE2EJobsEnabled: false,
  }));
}

async function openOnboarding(page: Page) {
  await page.goto('/');
  await dismissDevErrorDialog(page);

  const addProject = page.getByTestId('studio-workspace-ws-default-add-project');
  await expect(addProject).toBeVisible();
  await addProject.click();

  const dialog = page.getByTestId('onboard-project-dialog');
  await expect(dialog).toBeVisible();
  return dialog;
}

test.describe('project onboarding and editable project basics', () => {
  let project: ProjectFixture;

  test.beforeEach(async ({ page }) => {
    mkdirSync(results, { recursive: true });
    project = existingProject();
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.addInitScript(() => {
      try { localStorage.setItem('atp.flag.vsCodeLayout', '1'); } catch { /* ignore */ }
    });
    await mockBootApis(page, () => project);
  });

  test('retired Project Sources deep-link lands on Settings Overview', async ({ page }) => {
    await page.goto('/#/workspace/settings/project-sources');
    await dismissDevErrorDialog(page);

    await expect(page.getByTestId('workspace-settings-overview')).toBeVisible();
    await expect.poll(() => new URL(page.url()).hash).toBe('#/workspace/settings');
    await expect(page.getByTestId('workspace-settings-rail-overview')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('workspace-settings-rail-project-sources')).toHaveCount(0);
    await expect(page.getByTestId('workspace-project-sources')).toHaveCount(0);
    await expect(page.getByTestId('project-source-local-folder')).toHaveCount(0);
  });

  test('large onboarding dialog groups the project basics without a source picker', async ({ page }) => {
    const dialog = await openOnboarding(page);

    const box = await dialog.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.width).toBeGreaterThanOrEqual(800);
    expect(box!.height).toBeGreaterThanOrEqual(650);

    await expect(dialog.getByTestId('onboard-project-form')).toBeVisible();
    for (const heading of ['Identity', 'Code location', 'Default coding agent', 'Execution location']) {
      await expect(dialog.getByRole('heading', { name: heading, exact: true })).toBeVisible();
    }
    for (const field of [
      'workspace',
      'display-name',
      'short-code',
      'color',
      'repository-path',
      'root-path',
      'repository-url',
      'agent',
      'runner',
    ]) {
      await expect(dialog.getByTestId(`onboard-project-${field}`)).toBeAttached();
    }

    await expect(dialog.getByTestId('onboard-project-source')).toHaveCount(0);
    await expect(dialog.getByText('Project source', { exact: true })).toHaveCount(0);
    await expect(dialog.getByTestId('onboard-project-preview')).toContainText(
      'Task storage stays in the Agent Studio store, never in the product repository.',
    );

    const controlsFitWidth = await dialog.evaluate((element) => {
      const dialogBox = element.getBoundingClientRect();
      return [...element.querySelectorAll('input, select')].every((control) => {
        const controlBox = control.getBoundingClientRect();
        return controlBox.left >= dialogBox.left && controlBox.right <= dialogBox.right;
      });
    });
    expect(controlsFitWidth).toBe(true);

    for (const theme of ['dark', 'light'] satisfies Theme[]) {
      await setTheme(page, theme);
      await dialog.screenshot({
        path: join(results, `onboarding-large-grouped-basics-${theme}--mocked.png`),
      });
    }
  });

  test('invalid short code, local paths, and repository URL show field errors', async ({ page }) => {
    const dialog = await openOnboarding(page);
    await dialog.getByTestId('onboard-project-display-name').fill('Quality Studio');

    const shortCode = dialog.getByTestId('onboard-project-short-code');
    const repositoryPath = dialog.getByTestId('onboard-project-repository-path');
    const rootPath = dialog.getByTestId('onboard-project-root-path');
    const repositoryUrl = dialog.getByTestId('onboard-project-repository-url');

    await shortCode.fill('1');
    await shortCode.blur();
    await repositoryPath.fill('../quality-studio');
    await repositoryPath.blur();
    await rootPath.fill('frontend');
    await rootPath.blur();
    await repositoryUrl.fill('ssh://git@example.com/quality-studio.git');
    await repositoryUrl.blur();

    for (const field of [shortCode, repositoryPath, rootPath, repositoryUrl]) {
      await expect(field).toHaveAttribute('aria-invalid', 'true');
    }
    await expect(dialog.getByRole('alert').filter({ hasText: 'Use 2-6 characters' })).toBeVisible();
    await expect(dialog.getByRole('alert').filter({ hasText: 'absolute local Windows or POSIX path' })).toHaveCount(2);
    await expect(dialog.getByRole('alert').filter({ hasText: 'absolute http or https URL' })).toBeVisible();
    await expect(dialog.getByTestId('onboard-project-submit')).toBeDisabled();

    await shortCode.fill('QS');
    await repositoryPath.fill('C:/Projects/quality-studio');
    await rootPath.fill('C:/Projects/quality-studio/frontend');
    await repositoryUrl.fill('https://github.com/example/quality-studio');

    for (const field of [shortCode, repositoryPath, rootPath, repositoryUrl]) {
      await expect(field).not.toHaveAttribute('aria-invalid', 'true');
    }
    await expect(dialog.getByTestId('onboard-project-submit')).toBeEnabled();
  });

  test('POST sends project basics and runner but never chooses task-store placement', async ({ page }) => {
    let submitted: Record<string, unknown> | null = null;
    await page.route('**/api/v1/projects', async (route) => {
      if (route.request().method() !== 'POST') return route.continue();
      submitted = route.request().postDataJSON() as Record<string, unknown>;
      await fulfillJson(route, {
        ...existingProject(),
        id: 'PROJ-024',
        displayName: submitted.displayName,
        shortCode: submitted.shortCode,
        workspaceId: submitted.workspaceId,
        color: submitted.color,
        cliDefault: submitted.cliDefault,
        modelDefault: submitted.modelDefault,
        storageLocation: 'C:/AgentStudio/store/projects/PROJ-024/tasks',
        repositoryPath: submitted.repositoryPath,
        rootPath: submitted.rootPath,
        repositoryUrl: submitted.repositoryUrl,
        urls: [{
          id: 'repo', label: 'Repository', url: String(submitted.repositoryUrl), sortOrder: 0, startRule: null,
        }],
      }, 201);
    });

    const dialog = await openOnboarding(page);
    await dialog.getByTestId('onboard-project-display-name').fill('Quality Studio');
    await dialog.getByTestId('onboard-project-short-code').fill('QS');
    await dialog.getByTestId('onboard-project-repository-path').fill('C:/Projects/quality-studio');
    await dialog.getByTestId('onboard-project-root-path').fill('C:/Projects/quality-studio/frontend');
    await dialog.getByTestId('onboard-project-repository-url').fill('https://github.com/example/quality-studio');
    await dialog.getByTestId('onboard-project-runner').selectOption('agent-runner-01');

    await expect(dialog.getByTestId('onboard-project-preview')).toContainText(
      'Task storage stays in the Agent Studio store, never in the product repository.',
    );
    await dialog.screenshot({ path: join(results, 'onboarding-api-payload--mocked.png') });
    await dialog.getByTestId('onboard-project-submit').click();

    await expect(dialog).not.toBeVisible();
    expect(submitted).toMatchObject({
      workspaceId: 'ws-default',
      displayName: 'Quality Studio',
      shortCode: 'QS',
      color: '#569cd6',
      cliDefault: 'claude',
      modelDefault: null,
      repositoryPath: 'C:/Projects/quality-studio',
      rootPath: 'C:/Projects/quality-studio/frontend',
      repositoryUrl: 'https://github.com/example/quality-studio',
      executionRunner: 'agent-runner-01',
    });
    expect(submitted).not.toHaveProperty('sourceType');
    expect(submitted).not.toHaveProperty('storageLocation');
    expect(submitted).not.toHaveProperty('taskPath');
    expect(submitted).not.toHaveProperty('jobsPath');
  });

  test('Project Settings edits the same basics and sends a PUT payload', async ({ page }) => {
    let submitted: Record<string, unknown> | null = null;
    await page.route('**/api/projects/PROJ-023', async (route) => {
      if (route.request().method() !== 'PUT') return route.continue();
      submitted = route.request().postDataJSON() as Record<string, unknown>;
      project = {
        ...project,
        workspaceId: String(submitted.workspaceId),
        displayName: String(submitted.displayName),
        shortCode: String(submitted.shortCode),
        color: String(submitted.color),
        repositoryPath: String(submitted.repositoryPath),
        rootPath: String(submitted.rootPath),
        repositoryUrl: String(submitted.repositoryUrl),
        urls: [{
          id: 'repo', label: 'Repository', url: String(submitted.repositoryUrl), sortOrder: 0, startRule: null,
        }],
      };
      await fulfillJson(route, project);
    });

    await page.goto('/#/projects/existing-project/settings');
    await dismissDevErrorDialog(page);

    const card = page.getByTestId('project-basics-card');
    await expect(card).toBeVisible();
    await expect(card.getByTestId('project-basics-display-name')).toHaveValue('Existing Project');
    await expect(card.getByTestId('project-basics-repository-path')).toHaveValue('C:/Projects/existing-project');
    await expect(card.getByTestId('project-basics-root-path')).toHaveValue('C:/Projects/existing-project/frontend');
    await expect(card.getByTestId('project-basics-repository-url')).toHaveValue(
      'https://github.com/example/existing-project',
    );
    await expect(card.getByTestId('project-basics-display-name')).toBeEnabled();

    await card.getByTestId('project-basics-short-code').fill('EX2');
    await card.getByTestId('project-basics-repository-path').fill('D:/Work/quality-studio-next');
    await card.getByTestId('project-basics-root-path').fill('D:/Work/quality-studio-next/apps/web');
    await card.getByTestId('project-basics-repository-url').fill('https://github.com/example/quality-studio-next');

    const save = card.getByTestId('project-basics-save');
    await expect(save).toBeEnabled();
    await save.click();

    await expect.poll(() => submitted).not.toBeNull();
    await expect(card.getByTestId('project-basics-save-success')).toContainText('Saved');
    expect(submitted).toMatchObject({
      workspaceId: 'ws-default',
      displayName: 'Existing Project',
      shortCode: 'EX2',
      color: '#569cd6',
      repositoryPath: 'D:/Work/quality-studio-next',
      clearRepositoryPath: false,
      rootPath: 'D:/Work/quality-studio-next/apps/web',
      clearRootPath: false,
      repositoryUrl: 'https://github.com/example/quality-studio-next',
      clearRepositoryUrl: false,
      cliDefault: 'claude',
    });
    expect(submitted).not.toHaveProperty('storageLocation');

    await expect(card.getByTestId('project-basics-short-code')).toHaveValue('EX2');
    await expect(card.getByTestId('project-basics-repository-path')).toHaveValue('D:/Work/quality-studio-next');
    for (const theme of ['dark', 'light'] satisfies Theme[]) {
      await setTheme(page, theme);
      await card.screenshot({
        path: join(results, `project-basics-settings-saved-${theme}--mocked.png`),
      });
    }
  });

  test('Project Settings warns when a remotely routed project has no repository URL', async ({ page }) => {
    project = {
      ...project,
      repositoryUrl: null,
      urls: [],
    };

    await page.goto('/#/projects/existing-project/settings');
    await dismissDevErrorDialog(page);

    const card = page.getByTestId('project-basics-card');
    const warning = card.getByTestId('project-remote-repository-warning');
    await expect(warning).toContainText('Remote execution is not claimable.');
    await expect(warning).toContainText('repositoryUrl is missing');
    await expect(warning).toContainText('agent-runner-01');

    for (const theme of ['dark', 'light'] satisfies Theme[]) {
      await setTheme(page, theme);
      await card.screenshot({
        path: join(results, `project-remote-repository-warning-${theme}--mocked.png`),
      });
    }
  });
});
