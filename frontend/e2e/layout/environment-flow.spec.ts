import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

test('environment view refreshes and fits light and dark desktop widths', async ({ page }) => {
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
  // AGT-3231: two work hosts report their zone policy, one verified, one failed.
  const seenAt = new Date().toISOString();
  const capability = (key: string, category: string, patch: Record<string, unknown> = {}) => ({
    key, category, advertisedStatus: 'ready', healthState: 'healthy', advertisedAt: seenAt, freshUntil: seenAt,
    isFresh: true, consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [], ...patch,
  });
  const host = (runnerId: string, zone: Record<string, unknown>) => ({
    runnerId, name: runnerId, hostId: runnerId, instanceId: `${runnerId}-1`, runnerVersion: '0.9.6', protocolVersion: 2,
    status: 'online', registeredAt: seenAt, lastSeenAt: seenAt, hostAdmission: { hostId: runnerId, admissionState: 'open' },
    capabilities: [capability('executor:review', 'executor'),
      capability('zone-policy', 'zone', { version: '2026-10-20.1', identity: 'work', ...zone })],
  });
  await page.route('**/api/clients', route => route.fulfill({ json: [] }));
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({ json: [
    host('kori-review-1', { detail: 'verifiedAt=2026-10-20T09:30:00Z; networks=agent-work; rules=sha256:0123456789ab' }),
    host('kori-gate', { advertisedStatus: 'unavailable', detail: 'verification failed at 2026-10-20T10:30:00Z: 1 of 9 checks; '
      + 'blocked 1.1.1.1:443 expected blocked, observed connected; networks=agent-work; rules=sha256:0123456789ab' }),
  ] }));
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
  const verified = view.locator('[data-host-id="kori-review-1"]').getByTestId('environment-host-zone-policy');
  await expect(verified).toHaveAttribute('data-zone-policy-state', 'verified');
  await expect(verified).toContainText('work · 2026-10-20.1');
  await expect(verified).toContainText('Verified 2026-10-20 09:30 UTC');
  const failed = view.locator('[data-host-id="kori-gate"]').getByTestId('environment-host-zone-policy');
  await expect(failed).toHaveAttribute('data-zone-policy-state', 'failed');
  await expect(failed).toContainText('Verification failed · 2026-10-20.1');
  await expect(failed).toContainText('observed connected');
  const output = process.env.JOB_RESULTS_DIR ?? join(process.cwd(), 'test-results');
  mkdirSync(output, { recursive: true });
  await page.screenshot({ path: join(output, 'environment-1280-light.png') });

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
});
