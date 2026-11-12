import { test, expect } from '../fixtures/dev-backend';
import * as fs from 'fs';
import * as path from 'path';
import { setTheme } from '../helpers/theme';

const SCREENSHOT_DIR = process.env.PROJECT_SHELL_RESULTS_DIR?.trim()
  || path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-execution-assignment');

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test.beforeAll(() => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
});

test('fresh local installation shows no invented runner and explains unavailable coding locations', async ({ page, devBackend }) => {
  const projectName = 'Agent Studio Worktree';
  const shotDir = process.env.JOB_RESULTS_DIR ?? SCREENSHOT_DIR;
  fs.mkdirSync(shotDir, { recursive: true });
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
  await page.route('**/api/watch-paths', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify([{ name: projectName, path: devBackend.workspace, rootPath: devBackend.workspace }]),
  }));
  await page.route('**/api/projects/settings', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ [projectName]: {
      pickupMode: 'auto', executionLocation: 'local', localCodingAdmitted: false,
    } }),
  }));
  await page.route('**/api/clients', route => route.fulfill({
    status: 200, contentType: 'application/json', body: '[]',
  }));
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({
    status: 200, contentType: 'application/json', body: '[]',
  }));
  await page.goto(`/#/projects/${slugFor(projectName)}/settings`, { waitUntil: 'domcontentloaded' });
  const card = page.getByTestId('project-execution-card');
  await expect(card).toBeVisible({ timeout: 60_000 });
  const select = card.getByTestId('project-execution-host-select');
  await expect(select.locator('option')).toHaveCount(1, { timeout: 30_000 });
  await expect(select.locator('option')).toContainText(['Local (Local coding is disabled)']);
  await expect(select.locator('option')).toHaveJSProperty('disabled', true);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await card.screenshot({ path: path.join(shotDir, `fresh-execution--${theme}.png`) });
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await card.screenshot({ path: path.join(shotDir, 'fresh-execution--390px.png') });
});

test('keeps pickup mode and execution location as independent controls', async ({ page, devBackend }) => {
  test.setTimeout(90_000);
  expect(devBackend.workspace).toBeTruthy();
  const projectName = 'Agent Studio Worktree';
  await page.route('**/api/crash-recovery/pending', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: '[]',
    });
  });
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
  await page.route('**/api/watch-paths', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: projectName, path: devBackend.workspace, rootPath: devBackend.workspace }]),
    });
  });
  await page.route('**/api/projects/settings', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        [projectName]: {
          pickupMode: 'auto',
          executionLocation: 'local',
          localCodingAdmitted: false,
          integrationBranch: 'develop',
          maxParallelism: 1,
        },
      }),
    });
  });
  await page.route('**/api/clients', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 'agent-runner-01',
        displayName: 'agent-runner-01',
        kind: 'service',
        registeredAt: new Date().toISOString(),
        lastSeenAt: new Date().toISOString(),
        runnerGitStatus: 'ready',
        runnerDaemonState: 'running',
        runnerActiveSlots: 0,
        runnerAvailableSlots: 2,
      }]),
    });
  });
  await page.route('**/api/clients/agent-runner-01/telemetry?window=14d', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        clientId: 'agent-runner-01',
        window: '14d',
        points: [],
        findings: [],
      }),
    });
  });
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify([{
      runnerId: 'agent-runner-01', name: 'agent-runner-01', lastSeenAt: new Date().toISOString(),
      capabilities: [{ key: 'executor:coding', isFresh: true, healthState: 'healthy', advertisedStatus: 'ready' }],
      hostAdmission: { admissionState: 'open' },
    }, {
      runnerId: 'review-only', name: 'review-only', lastSeenAt: new Date().toISOString(),
      capabilities: [{ key: 'executor:review', isFresh: true, healthState: 'healthy', advertisedStatus: 'ready' }],
      hostAdmission: { admissionState: 'open' },
    }]),
  }));
  await page.route('**/api/projects/*/execution-runner', async (route) => {
    expect(route.request().method()).toBe('PUT');
    const body = route.request().postDataJSON();
    expect([
      { executionLocation: 'agent-runner-01' },
      { pickupMode: 'paused' },
    ]).toContainEqual(body);
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        pickupMode: body.pickupMode || 'manual',
        executionLocation: 'agent-runner-01',
      }),
    });
  });

  await page.goto(`/#/projects/${slugFor(projectName)}/settings`, { waitUntil: 'domcontentloaded' });
  const card = page.getByTestId('project-execution-card');
  await expect(card).toBeVisible({ timeout: 60_000 });
  await expect(card.getByTestId('project-pickup-mode-auto')).toHaveAttribute('aria-pressed', 'true');
  await expect(card.getByTestId('project-pickup-mode-auto')).toBeVisible();
  await expect(card.getByTestId('project-pickup-mode-paused')).toBeVisible();

  const hostSelect = card.getByTestId('project-execution-host-select');
  await expect(hostSelect).toHaveValue('local');
  await expect(hostSelect.locator('option[value="local"]')).toHaveJSProperty('disabled', true);
  await expect(hostSelect.locator('option[value="review-only"]')).toHaveJSProperty('disabled', true);
  await expect(hostSelect.locator('option[value="review-only"]')).toContainText('Review-only runner');
  await expect(card.getByTestId('local-coding-disabled-warning')).toContainText('Local coding is disabled');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    const resultDir = process.env.JOB_RESULTS_DIR ?? SCREENSHOT_DIR;
    fs.mkdirSync(resultDir, { recursive: true });
    await card.screenshot({ path: path.join(resultDir, `local-coding-disabled--${theme}--mocked.png`) });
  }
  await hostSelect.selectOption('agent-runner-01');
  await expect(hostSelect).toHaveValue('agent-runner-01');
  await expect(card.getByTestId('project-execution-save-confirmation')).toHaveText('Execution location saved.');
  await expect(card.getByTestId('local-coding-disabled-warning')).toHaveCount(0);
  await expect(card.getByTestId('project-execution-selected-host')).toContainText('agent-runner');
  const resultDir = process.env.JOB_RESULTS_DIR ?? SCREENSHOT_DIR;
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await card.screenshot({ path: path.join(resultDir, `execution-location-saved-${theme}.png`) });
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await card.screenshot({ path: path.join(resultDir, 'execution-location-saved-390.png') });

  await card.getByTestId('project-pickup-mode-paused').click();
  await expect(card.getByTestId('project-pickup-mode-paused')).toHaveAttribute('aria-pressed', 'true');
  await expect(hostSelect).toHaveValue('agent-runner-01');

  await card.screenshot({
    path: path.join(SCREENSHOT_DIR, 'project-execution-controls-separated--mocked.png'),
  });
  await page.evaluate(() => {
    document.documentElement.setAttribute('data-studio-theme', 'dark');
  });
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
  await card.screenshot({
    path: path.join(SCREENSHOT_DIR, 'project-execution-controls-separated--dark--mocked.png'),
  });
});

test('shows the assigned host project delivery failure beside provider refusals', async ({ page, devBackend }) => {
  const projectName = 'Agent Studio Worktree';
  await page.route('**/api/crash-recovery/pending', route => route.fulfill({
    status: 200, contentType: 'application/json', body: '[]',
  }));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
  await page.route('**/api/watch-paths', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify([{ name: projectName, path: devBackend.workspace, rootPath: devBackend.workspace }]),
  }));
  await page.route('**/api/projects/settings', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({
      [projectName]: { pickupMode: 'auto', executionLocation: 'agent-runner-01', integrationBranch: 'develop' },
    }),
  }));
  await page.route('**/api/clients', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify([{
      id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
      registeredAt: '2026-07-22T10:00:00Z', lastSeenAt: new Date().toISOString(),
      runnerGitStatus: 'ready',
      runnerProjectPreflights: [{
        projectId: 'PROJ-001', projectName, registrationFingerprint: 'b'.repeat(64),
        repositoryUrl: 'https://github.com/example/agent-studio.git',
        fetchUrl: 'https://github.com/example/agent-studio.git',
        pushUrl: 'https://github.com/example/agent-studio.git', targetBranch: 'develop', status: 'failed',
        detail: 'write probe failed (128): permission denied', checkedAt: '2026-07-22T10:01:00Z',
      }],
    }]),
  }));
  await page.route('**/api/v1/management/provider-refusals?days=14', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{
      day: '2026-09-18',
      model: 'gpt-6-astra',
      count: 1,
      refusals: ['unsupported_parameter access_programs.cyber'],
    }]),
  }));

  await page.goto(`/#/projects/${slugFor(projectName)}/settings`, { waitUntil: 'domcontentloaded' });
  const card = page.getByTestId('project-execution-card');
  const failure = card.getByTestId('project-delivery-preflight');
  const refusal = card.getByTestId('project-provider-refusal');
  await expect(failure).toContainText('blocked');
  await expect(failure).toContainText('Target develop');
  await expect(failure).toContainText('permission denied');
  await expect(refusal).toContainText('gpt-6-astra');
  await expect(refusal).toContainText('unsupported_parameter access_programs.cyber');
  await setTheme(page, 'light');
  await card.screenshot({ path: path.join(SCREENSHOT_DIR, 'project-delivery-preflight-failed--mocked.png') });
  await setTheme(page, 'dark');
  await expect(failure).toContainText('permission denied');
  await expect(refusal).toContainText('gpt-6-astra');
  await card.screenshot({ path: path.join(SCREENSHOT_DIR, 'project-delivery-preflight-failed-dark--mocked.png') });
});
