import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';
import { api } from '../helpers/api';
import { createJob, getJobDetail } from '../helpers/jobs';
import { setTheme } from '../helpers/theme';

interface WatchPath { name: string; path: string }
interface Lease {
  taskKey: string;
  leaseId: string;
  fencingToken: number;
  attemptId: string;
  authorityEpoch: number;
}

test('durable stop receipt stays correlated in task status, timeline and operator feed',
  async ({ page, devBackend }, testInfo) => {
    test.setTimeout(180_000);
    const watches = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
    const watch = watches[0];
    expect(watch).toBeTruthy();
    const id = `e2e-steering-feedback-${Date.now()}`;
    let lease: Lease | null = null;
    await createJob({ id, title: 'Correlated steering feedback', watchPath: watch.path,
      targetState: '2-ready', fixture: false });
    try {
      const detail = await getJobDetail(id, watch.path);
      const taskKey = (detail.info as typeof detail.info & { taskKey: string }).taskKey;
      expect(taskKey).toBeTruthy();
      const acquired = await api<{ granted: boolean; lease: Lease }>('/api/runner/lease/acquire', {
        method: 'POST', body: JSON.stringify({ taskKey, runnerId: 'e2e-runner', runnerName: 'E2E Runner',
          hostname: 'e2e-host', pid: 1, backendName: 'codex', idempotencyKey: `${id}:acquire` }),
      });
      expect(acquired.granted).toBe(true);
      lease = acquired.lease;
      const commandId = `${id}:stop`;
      await api(`/api/tasks/${encodeURIComponent(id)}/stop?watchPath=${encodeURIComponent(watch.path)}&commandId=${encodeURIComponent(commandId)}`, {
        method: 'POST',
      });
      const duplicate = await api<{ commandId: string }>(
        `/api/tasks/${encodeURIComponent(id)}/stop?watchPath=${encodeURIComponent(watch.path)}&commandId=${encodeURIComponent(commandId)}`,
        { method: 'POST' },
      );
      expect(duplicate.commandId).toBe(commandId);

      const feedback = await api<{ current: { commandId: string | null } | null;
        history: { commandId: string | null; attemptId: string | null; state: string }[] }>(
        `/api/tasks/${encodeURIComponent(id)}/steering-feedback?watchPath=${encodeURIComponent(watch.path)}`);
      expect(feedback.history.filter(fact => fact.commandId === commandId && fact.state === 'requested')).toHaveLength(1);
      expect(feedback.history.find(fact => fact.commandId === commandId)?.attemptId).toBe(lease.attemptId);
      expect(feedback.current?.commandId).toBe(commandId);
      const timeline = await api<{ kind: string; details?: { commandId?: string; attemptId?: string } }[]>(
        `/api/tasks/${encodeURIComponent(id)}/timeline?watchPath=${encodeURIComponent(watch.path)}`);
      expect(timeline.filter(event => event.kind === 'steering_feedback' && event.details?.commandId === commandId)).toHaveLength(1);
      const feed = await api<{ entries: { commandId?: string; attemptId?: string; kind: string }[] }>(
        '/api/runner/orchestrator-feed');
      expect(feed.entries.filter(entry => entry.commandId === commandId && entry.attemptId === lease?.attemptId)).toHaveLength(1);

      // The isolated backend serves real task and feed data. The local profile
      // needs only its browser session gate bypassed for this visual proof.
      await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ profile: 'local', bootstrapRequired: false,
          authenticated: true, user: null }),
      }));
      await page.route('**/api/v1/studio/runner/status**', route => route.fulfill({
        status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }),
      }));
      await page.setViewportSize({ width: 1440, height: 900 });
      await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watch.path)}`,
        { waitUntil: 'domcontentloaded', timeout: 45_000 });
      await expect(page.getByTestId('overview-steering-receipt')).toContainText(commandId);
      const results = resolve(process.env.JOB_RESULTS_DIR ?? testInfo.outputDir);
      mkdirSync(results, { recursive: true });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
        await page.screenshot({ path: join(results, `steering-task-status--${theme}--real.png`), fullPage: true });
        await page.getByTestId('prompt-tab-timeline').click();
        await expect(page.getByTestId('timeline-list')).toContainText('Stop requested');
        await expect(page.getByTestId('timeline-list')).not.toContainText('Remote stop requested');
        await page.screenshot({ path: join(results, `steering-timeline--${theme}--real.png`), fullPage: true });
        await page.getByTestId('prompt-tab-overview').click();
      }
      await page.goto('/#/feed');
      const stopRow = page.getByTestId('orchestrator-feed-entry').filter({ hasText: 'stop: requested' }).first();
      await expect(stopRow).toBeVisible();
      await stopRow.click();
      await expect(page.getByTestId('orchestrator-feed-detail')).toContainText(commandId);
      await expect(page.getByTestId('orchestrator-feed-detail')).toContainText(lease.attemptId);
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await page.screenshot({ path: join(results, `steering-operator-feed--${theme}--real.png`), fullPage: true });
      }
    } finally {
      if (lease) await api('/api/runner/lease/release', {
        method: 'POST', body: JSON.stringify({ taskKey: lease.taskKey,
          leaseId: lease.leaseId, fencingToken: lease.fencingToken,
          attemptId: lease.attemptId, authorityEpoch: lease.authorityEpoch,
          runnerId: 'e2e-runner', idempotencyKey: `${id}:release` }),
      }).catch(() => undefined);
      await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watch.path)}`,
        { method: 'DELETE' }).catch(() => undefined);
      void devBackend;
    }
  });
