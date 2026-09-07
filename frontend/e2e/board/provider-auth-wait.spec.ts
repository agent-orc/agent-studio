import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const WATCH_PATH = 'C:/fixtures/provider-auth-wait';
const PROJECT = 'Provider auth wait';

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  const now = new Date();
  const task = {
    id: 'AGT-AUTH-WAIT', key: 'AGT-AUTH-WAIT', displayKey: 'AGT-AUTH-WAIT',
    taskKey: `${WATCH_PATH}::AGT-AUTH-WAIT`, title: 'Claude task waiting for host authentication',
    state: '2-ready', order: 1, agent: 'claude', cliType: 'claude',
    createdAt: now.toISOString(), watchPath: WATCH_PATH, projectName: PROJECT,
    folderPath: `${WATCH_PATH}/2-ready/AGT-AUTH-WAIT`, lastActivity: now.toISOString(),
    sessionName: null, model: 'claude-sonnet-5', useOwnSession: null,
    lastUsage: null, execution: null, commit: null, ownerClientId: null, tags: [],
    executionLocation: {
      state: 'queued-remote', executionKind: 'remote', runnerId: 'agent-runner-01',
      configuredRunnerId: 'agent-runner-01', hostDisplayName: 'runner-berlin',
      connectionState: 'connected', leaseState: 'queued',
      trustReason: 'Project execution assignment targets agent-runner-01.',
    },
  };
  const grouped = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [task], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
    escalated: [], completed: [], archive: [],
  };
  const capability = (key: string, status: string, detail?: string) => ({
    key, category: key.split(':')[0], advertisedStatus: status, healthState: 'healthy',
    reason: null, advertisedAt: now.toISOString(),
    freshUntil: new Date(now.getTime() + 120_000).toISOString(), isFresh: true,
    firstFailureAt: null, lastFailureAt: null, cooldownUntil: null, canaryClaimId: null,
    consecutiveFailures: 0, version: null, identity: key.split(':')[1], detail,
    affectedClaims: [], recoveryHistory: [],
  });

  await page.route('**/api/**', route => {
    const url = route.request().url();
    if (url.includes('/api/auth/status')) return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0, offset: 0, limit: 50 });
    if (url.includes('/api/tasks/grouped')) return json(route, grouped);
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, [task]);
    if (url.includes('/api/watch-paths')) return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    if (url.includes('/api/v1/management/remote-hosts/link-health')) return json(route, []);
    if (url.includes('/api/v1/management/remote-hosts')) return json(route, [{
      runnerId: 'agent-runner-01', name: 'runner-berlin', hostId: 'host-berlin',
      instanceId: 'coding', runnerVersion: '1.2.0', protocolVersion: 2, status: 'active',
      registeredAt: now.toISOString(), lastSeenAt: now.toISOString(),
      hostAdmission: { hostId: 'host-berlin', admissionState: 'open' },
      capabilities: [
        capability('cli-execution:claude', 'ready'),
        { ...capability('provider-auth:claude', 'unavailable', 'Not logged in'), signal: 'signed-out', consecutiveFailures: 2 },
      ], telemetry: null,
    }]);
    if (url.includes('/api/clients')) return json(route, [{
      id: 'agent-runner-01', displayName: 'runner-berlin', kind: 'service',
      registeredAt: now.toISOString(), lastSeenAt: now.toISOString(),
    }]);
    if (url.includes('/api/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/cli/quota')) return json(route, { at: now.toISOString(), ttlSeconds: 600, snapshots: [] });
    if (url.includes('/api/cli/usage')) return json(route, { at: now.toISOString(), sessions: [] });
    return json(route, []);
  });
}

test('Ready card shows the provider sign-in wait reason in both themes', async ({ page }) => {
  const resultsDir = resolve(process.env.JOB_RESULTS_DIR ?? '../results', 'provider-auth');
  mkdirSync(resultsDir, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await installRoutes(page);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await dismissDevErrorDialog(page);
  await page.addStyleTag({
    content: 'app-error-dialog, app-offline-banner, [data-testid="error-dialog-overlay"] { display: none !important; }',
  });

  const wait = page.getByTestId('task-card-provider-auth-wait');
  await expect(wait).toContainText('Waiting for Claude sign-in on runner-berlin');
  await wait.hover();
  await expect(page.getByRole('tooltip')).toContainText('Not logged in');

  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await page.getByTestId('lane-2-ready').screenshot({
      path: join(resultsDir, `ready-card-provider-auth-wait-${theme}--mocked.png`),
    });
  }
});
