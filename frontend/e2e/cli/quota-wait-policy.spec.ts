import { test, expect } from '../fixtures/dev-backend';
import type { Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const CLIENT_ID = 'local-default';
const SCREENSHOT_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : path.resolve(__dirname, '..', '..', '..', 'results', 'AGT-2098');

interface WaitPolicy {
  enabled: boolean;
  thresholdMinutes: number;
  source?: 'global' | 'project';
  projectEnabled?: boolean | null;
  projectThresholdMinutes?: number | null;
}

interface WatchPath { name: string }

async function api<T>(baseUrl: string, route: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(`${baseUrl}${route}`, {
    ...init,
    headers: {
      'content-type': 'application/json',
      'x-client-id': CLIENT_ID,
      ...(init.headers ?? {}),
    },
  });
  const body = await response.text();
  if (!response.ok) throw new Error(`${init.method ?? 'GET'} ${route} -> ${response.status}: ${body}`);
  return JSON.parse(body) as T;
}

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

async function hideRecoveryOverlay(page: Page): Promise<void> {
  const overlay = page.getByTestId('crash-recovery-prompt-overlay');
  await overlay.waitFor({ state: 'attached', timeout: 2_000 }).catch(() => undefined);
  if (await overlay.count()) {
    await overlay.evaluate(element => {
      (element as HTMLElement).style.display = 'none';
      (element as HTMLElement).style.pointerEvents = 'none';
    });
  }
}

async function installQuotaWaitCardRoutes(page: Page): Promise<void> {
  const observedAt = new Date().toISOString();
  const resetAt = new Date(Date.now() + 12 * 60_000).toISOString();
  const task = {
    id: 'quota-wait-visible',
    taskKey: 'C:/fixtures/quota-wait::quota-wait-visible',
    key: 'QUOTA-WAIT',
    title: 'Keep the requested model after quota reset',
    state: '2-ready',
    phase: 'quota-waiting',
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    model: 'gpt-5.4',
    watchPath: 'C:/fixtures/quota-wait',
    projectName: 'Quota wait fixture',
    folderPath: 'C:/fixtures/quota-wait/.orchestrator/tasks/2-ready/quota-wait-visible',
    createdAt: new Date(Date.now() - 30 * 60_000).toISOString(),
    lastActivity: new Date(Date.now() - 60_000).toISOString(),
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: CLIENT_ID,
    tags: [],
    quotaWait: {
      cliType: 'codex',
      startedAt: new Date(Date.now() - 60_000).toISOString(),
      resetAt,
      thresholdMinutes: 30,
      reason: 'confirmed nearby reset',
    },
  };
  const grouped = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [task],
    progress: [], failedPickup: [], codeNotComplete: [], review: [],
    autoReview: [], humanReview: [], completed: [], archive: [],
  };

  await page.route('**/api/tasks/grouped**', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) }));
  await page.route('**/api/watch-paths**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{
      name: task.projectName,
      path: task.watchPath,
      rootPath: task.watchPath,
      repositoryPath: task.watchPath,
    }]),
  }));
  await page.route('**/api/git/summary**', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{
      runnerId: 'quota-fixture-runner', name: 'quota-fixture-host', hostId: 'quota-fixture-host',
      instanceId: 'quota-fixture-instance', runnerVersion: '1.0.0', protocolVersion: 2,
      status: 'active', registeredAt: observedAt, lastSeenAt: observedAt,
      hostAdmission: { hostId: 'quota-fixture-host', admissionState: 'open' },
      capabilities: [{
        key: 'provider-auth:codex', category: 'provider-auth', advertisedStatus: 'ready',
        healthState: 'healthy', advertisedAt: observedAt,
        freshUntil: new Date(Date.now() + 120_000).toISOString(), isFresh: true,
        consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [],
      }],
    }]),
  }));
  await page.route('**/api/v1/management/remote-hosts/link-health', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: '[]',
  }));
  await page.route(/\/api\/runner\/status(\?|$)/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      projects: {
        [task.projectName]: {
          projectName: task.projectName,
          mode: 'manual',
          activeJobId: null,
          activeExecution: null,
          queuedJobIds: [],
        },
      },
    }),
  }));
}

test.describe('wait on nearby quota reset', () => {
  test('global and project policies round-trip through the real backend', async ({ devBackend }) => {
    const global = await api<WaitPolicy>(devBackend.baseUrl, '/api/cli/quota/wait-policy', {
      method: 'PUT',
      body: JSON.stringify({ enabled: true, thresholdMinutes: 28 }),
    });
    expect(global).toEqual({ enabled: true, thresholdMinutes: 28 });

    const [project] = await api<WatchPath[]>(devBackend.baseUrl, '/api/watch-paths');
    expect(project?.name).toBeTruthy();
    const projectRoute = `/api/projects/${encodeURIComponent(project.name)}/quota-wait-policy`;
    const override = await api<WaitPolicy>(devBackend.baseUrl, projectRoute, {
      method: 'PUT',
      body: JSON.stringify({ enabled: false, thresholdMinutes: 17 }),
    });

    expect(override.enabled).toBe(false);
    expect(override.thresholdMinutes).toBe(17);
    expect(override.source).toBe('project');
    expect(override.projectEnabled).toBe(false);
  });

  test('renders the global policy in both themes and edits the project override', async ({ page, devBackend }) => {
    fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
    await api<WaitPolicy>(devBackend.baseUrl, '/api/cli/quota/wait-policy', {
      method: 'PUT',
      body: JSON.stringify({ enabled: true, thresholdMinutes: 30 }),
    });

    await page.goto('/');
    await hideRecoveryOverlay(page);
    await page.getByTestId('status-bar-settings').click();
    await page.getByTestId('workspace-settings-rail-caps').click();

    const panel = page.getByTestId('cli-admin-panel');
    await expect(panel.getByTestId('quota-wait-policy')).toBeVisible();
    await expect(panel.getByTestId('quota-wait-enabled')).toBeChecked();
    await expect(panel.getByTestId('quota-wait-threshold')).toHaveValue('30');
    await expect(panel.getByText('before model switch and throttling')).toBeVisible();
    const globalCard = panel.getByTestId('quota-wait-policy');

    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await globalCard.screenshot({
        path: path.join(SCREENSHOT_DIR, `quota-wait-global-${theme}.png`),
      });
    }

    const settingsTab = page.getByRole('tab', { name: /Workspace settings/ });
    await settingsTab.getByRole('button', { name: 'Close tab' }).click();
    await expect(settingsTab).not.toBeVisible();

    const [project] = await api<WatchPath[]>(devBackend.baseUrl, '/api/watch-paths');
    await page.goto(`/#/projects/${slugFor(project.name)}/settings`);
    await page.reload();
    await hideRecoveryOverlay(page);

    const card = page.getByTestId('project-settings-quota-wait');
    await expect(card).toBeVisible();
    const mode = page.getByTestId('project-settings-quota-wait-mode');
    await mode.selectOption('disabled');
    await expect(mode).toHaveValue('disabled');
    const threshold = page.getByTestId('project-settings-quota-wait-threshold');
    await threshold.fill('17');
    await threshold.press('Tab');

    await expect.poll(async () => {
      const policy = await api<WaitPolicy>(
        devBackend.baseUrl,
        `/api/projects/${encodeURIComponent(project.name)}/quota-wait-policy`,
      );
      return `${policy.projectEnabled}:${policy.projectThresholdMinutes}`;
    }).toBe('false:17');

    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await card.screenshot({
        path: path.join(SCREENSHOT_DIR, `quota-wait-project-${theme}.png`),
      });
    }
  });

  test('shows the remote-admission quota wait on a Ready card in both themes', async ({ page, devBackend }) => {
    fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
    await api<WaitPolicy>(devBackend.baseUrl, '/api/cli/quota/wait-policy');
    await page.addInitScript(() => {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1,
        tabs: [{ kind: 'board', projectName: '__all__' }],
        activeKey: 'board:__all__',
      }));
    });
    await installQuotaWaitCardRoutes(page);
    await page.goto('/?includeFixtures=true');
    await hideRecoveryOverlay(page);

    const card = page.getByTestId('task-card').filter({ hasText: 'Keep the requested model after quota reset' });
    await expect(card).toBeVisible();
    const wait = card.getByTestId('task-card-quota-wait');
    await expect(wait).toBeVisible();
    await expect(wait).toContainText(/Waiting for quota reset .* 12 min remaining/);

    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await card.screenshot({
        path: path.join(SCREENSHOT_DIR, `quota-wait-ready-card-${theme}.png`),
      });
    }
  });
});
