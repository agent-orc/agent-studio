import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

async function stubStudioShell(page: import('@playwright/test').Page): Promise<void> {
  await page.route('**/favicon.ico', route => route.fulfill({ status: 204, body: '' }));
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'ok' }));
  await page.route('**/update/status**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ available: false }) }));
  await page.route('**/api/**', route => route.fulfill({ json: [] }));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ json: {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  } }));
  await page.route('**/api/auth/status', route => route.fulfill({ json: {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  } }));
  await page.route('**/api/v1/studio/board**', route => route.fulfill({ json: {
    backlog: [], preparation: [], orchestratorPrep: [], failedPickup: [], codeNotComplete: [],
    autoReview: [], review: [], completed: [], archive: [], ready: [], progress: [], humanReview: [], escalated: [],
  } }));
  await page.route('**/api/usage/cockpit**', route => route.fulfill({ json: { workspaceId: 'web', clis: [], cost: null } }));
  await page.route('**/api/cli/usage', route => route.fulfill({ json: { at: '2026-10-20T09:00:00Z', sessions: [] } }));
  await page.route('**/api/crash-recovery/pending', route => route.fulfill({ json: { pending: [] } }));
  await page.route('**/api/runner/queue-starvation', route => route.fulfill({ json: {
    active: false, waitingTaskCount: 0, availableSlots: 1, thresholdMinutes: 15,
    oldestEnteredLaneAt: null, observedAt: '2026-10-20T09:00:00Z', items: [],
  } }));
  await page.route('**/api/pipeline/accepted-integration-alert', route => route.fulfill({ json: {
    active: false, stalledTaskCount: 0, thresholdMinutes: 30, oldestAcceptedAt: null,
    observedAt: '2026-10-20T09:00:00Z', items: [],
  } }));
  await page.route('**/api/watch-paths', route => route.fulfill({ json: [] }));
  await page.route('**/api/v1/workspaces*', route => route.fulfill({ json: [] }));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill({ json: { projects: {} } }));
  await page.route('**/api/cli/quota', route => route.fulfill({ json: { snapshots: [] } }));
  await page.route('**/api/clients', route => route.fulfill({ json: [] }));
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({ json: [] }));
  await page.route('**/api/v1/management/credential-inventory**', route => route.fulfill({ json: { generatedAt: '2026-10-20T09:00:00Z', items: [] } }));
  await page.route('**/api/v1/management/links', route => route.fulfill({ json: [] }));
  await page.route('**/api/environment/health', route => route.fulfill({ json: {
    capturedAt: '2026-10-20T09:00:00Z', probeOrigin: 'control-plane', environments: [],
  } }));
  await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill({ json: {
    negotiateVersion: 1, connectionId: 'environment-flow-e2e', connectionToken: 'environment-flow-e2e',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  } }));
  await page.routeWebSocket('**/hubs/v1/studio*', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send(`{}\u001e`);
    });
  });
}

test('environment view refreshes and fits light and dark desktop widths', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-10-20T09:00:00Z') });
  await stubStudioShell(page);
  const consoleErrors: string[] = [];
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()); });
  page.on('pageerror', error => consoleErrors.push(error.message));
  page.on('requestfailed', request => consoleErrors.push(`request failed: ${request.url()}`));
  page.on('response', response => { if (response.status() >= 400) consoleErrors.push(`HTTP ${response.status()}: ${response.url()}`); });
  let reads = 0;
  await page.route('**/api/environment/flow', async route => {
    reads++;
    await route.fulfill({ json: {
      capturedAt: '2026-10-16T12:00:00Z',
      today: { created: reads, delivered: 4, reviewed: 3, integrated: 2,
        deployed: null, completed: 2, readyToIntegrationMedianMinutes: 150 },
      lanes: [{ lane: '2-ready', entered: 5, left: 4, medianMinutes: 45 }],
      queues: [{ queue: 'Coding', waiting: 3, oldestWaitMinutes: 90, throughputPerHour: 0.2 }],
      waitingReasons: [{ reason: 'Waiting for coding slot', count: 3 }],
    } });
  });
  await page.route('**/api/watcher/cases', route => route.fulfill({ json: { cases: [{
    id: 'case-1', fingerprint: 'queue:review', detectorClass: 'silence', project: 'Demo',
    affectedCards: ['t1'], firstSeenUtc: '2026-10-16T11:00:00Z', lastSeenUtc: '2026-10-16T12:00:00Z',
    occurrenceCount: 2, sweepCount: 2, state: 'open', lastSummary: 'Review queue has stopped moving',
    isCommentOnly: false,
  }] } }));
  await page.route('**/api/orchestration/decision-receipts?limit=100', route => route.fulfill({ json: [{
    receiptId: 'r1', taskId: 't1', chosenAction: 'observe', callStatus: 'succeeded',
    endedAt: '2026-10-16T12:00:00Z',
  }] }));
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'light');
  const openEnvironment = async () => {
    const inline = page.getByTestId('studio-titlebar-environment');
    if (await inline.isVisible()) await inline.click();
    else {
      await page.getByRole('button', { name: 'More navigation' }).click();
      await page.getByRole('menuitem', { name: 'Environment' }).click();
    }
  };
  await openEnvironment();
  const view = page.getByTestId('environment-flow');
  await expect(view).toBeVisible();
  await expect(view.getByText('Ready to integration median: 2.5 h.')).toBeVisible();
  await expect(view.getByText('AI Operator: observe · succeeded')).toBeVisible();
  await expect(view.getByRole('cell', { name: '3', exact: true })).toBeVisible();
  const output = process.env.JOB_RESULTS_DIR ?? join(process.cwd(), 'test-results');
  mkdirSync(output, { recursive: true });
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: join(output, `environment-${width}-light.png`), fullPage: true });
  }

  await view.getByRole('button', { name: 'Refresh' }).click();
  await expect(view.locator('.environment-flow__metrics > div').first().locator('b')).toHaveText('2');
  expect(reads).toBeGreaterThanOrEqual(2);
  await view.getByRole('button', { name: 'Close environment view' }).click();
  await page.setViewportSize({ width: 1920, height: 1080 });
  await page.getByTestId('studio-titlebar-theme').click();
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
  await openEnvironment();
  await expect(view).toBeVisible();
  await page.screenshot({ path: join(output, 'environment-1920-dark.png') });
  const box = await view.boundingBox();
  expect(box?.width).toBe(1920);
  expect(consoleErrors).toEqual([]);
});

test('environment view presents an unavailable flow state', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-10-20T09:00:00Z') });
  await stubStudioShell(page);
  await page.route('**/api/environment/flow', route => route.fulfill({ status: 503, body: 'unavailable' }));
  await page.route('**/api/watcher/cases', route => route.fulfill({ json: { cases: [] } }));
  await page.route('**/api/orchestration/decision-receipts?limit=100', route => route.fulfill({ json: [] }));
  await page.goto('/');
  await expect(page.getByTestId('studio-titlebar-theme')).toBeVisible();
  const inline = page.getByTestId('studio-titlebar-environment');
  if (await inline.isVisible()) await inline.click();
  else {
    await page.getByRole('button', { name: 'More navigation' }).click();
    await page.getByRole('menuitem', { name: 'Environment' }).click();
  }
  await expect(page.getByTestId('environment-flow').getByText('Flow data is temporarily unavailable.')).toBeVisible();
});
