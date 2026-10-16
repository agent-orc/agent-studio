import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';
import { api } from '../helpers/api';
import { createJob, getJobDetail } from '../helpers/jobs';
import { setTheme } from '../helpers/theme';

interface WatchPath { path: string }
interface Lease { taskKey: string; leaseId: string; fencingToken: number; attemptId: string; authorityEpoch: number }

test('Stop on a remote card reaches the held attempt through the board', async ({ page, devBackend }, testInfo) => {
  test.setTimeout(180_000);
  const watches = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  const watch = watches[0];
  expect(watch).toBeTruthy();
  const id = `e2e-remote-stop-${Date.now()}`;
  let lease: Lease | null = null;
  await createJob({ id, title: 'Remote stop button', watchPath: watch.path,
    targetState: '3-progress', fixture: false });
  try {
    const detail = await getJobDetail(id, watch.path);
    const info = detail.info as typeof detail.info & { key?: string; taskKey: string };
    const key = info.key ?? info.taskKey;
    const acquired = await api<{ granted: boolean; lease: Lease }>('/api/runner/lease/acquire', {
      method: 'POST',
      body: JSON.stringify({ taskKey: key, runnerId: 'e2e-stop-runner', runnerName: 'E2E Stop Runner',
        hostname: 'e2e-host', pid: 1, backendName: 'codex', idempotencyKey: `${id}:acquire` }),
    });
    expect(acquired.granted).toBe(true);
    lease = acquired.lease;

    await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }));
    await page.route('**/api/v1/studio/runner/status**', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }),
    }));
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watch.path)}`,
      { waitUntil: 'domcontentloaded', timeout: 45_000 });
    const stop = page.getByTestId('studio-triage-action-stop-run');
    await expect(stop).toBeVisible();
    await expect(page.getByText('Running remote on E2E Stop Runner')).toBeVisible();
    await expect(page.getByText('STATUS + AGENT')).toBeVisible();
    const results = resolve(process.env.JOB_RESULTS_DIR ?? testInfo.outputDir);
    mkdirSync(results, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: join(results, `remote-stop-button--${theme}.png`),
        fullPage: false, animations: 'disabled' });
    }

    const stopPath = `/tasks/${encodeURIComponent(id)}/stop`;
    const [, response] = await Promise.all([
      page.waitForRequest(req =>
        req.method() === 'POST' && req.url().includes(stopPath), { timeout: 30_000 }),
      page.waitForResponse(res =>
        res.request().method() === 'POST' && res.url().includes(stopPath), { timeout: 60_000 }),
      stop.click(),
    ]);
    expect(response.status()).toBe(202);
    const renewed = await api<{ stopRequest?: { taskKey: string; attemptId: string } }>(
      '/api/runner/lease/renew', {
        method: 'POST',
        body: JSON.stringify({ taskKey: key, leaseId: lease.leaseId,
          fencingToken: lease.fencingToken, attemptId: lease.attemptId,
          authorityEpoch: lease.authorityEpoch, runnerId: 'e2e-stop-runner',
          idempotencyKey: `${id}:renew` }),
      });
    expect(renewed.stopRequest?.taskKey).toBe(key);
    expect(renewed.stopRequest?.attemptId).toBe(lease.attemptId);
  } finally {
    if (lease) {
      try {
        await api('/api/runner/lease/release', {
          method: 'POST', body: JSON.stringify({ taskKey: lease.taskKey,
            leaseId: lease.leaseId, fencingToken: lease.fencingToken,
            attemptId: lease.attemptId, authorityEpoch: lease.authorityEpoch,
            runnerId: 'e2e-stop-runner', idempotencyKey: `${id}:release` }),
        });
      } catch (error) {
        console.warn('Remote lease cleanup failed:', error);
      }
    }
    try {
      await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watch.path)}`,
        { method: 'DELETE' });
    } catch (error) {
      console.warn('Remote stop test card cleanup failed:', error);
    }
    void devBackend;
  }
});
