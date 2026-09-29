import { test, expect } from '../fixtures/dev-backend';
import { writeFileSync } from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { setTheme } from '../helpers/theme';

interface WatchPath {
  name: string;
  path: string;
}

interface ArchivedTasksResponse {
  items: { id: string }[];
  total: number;
}

interface BatchMoveJobResponse {
  id: string;
  status: 'queued' | 'running' | 'completed' | 'failed';
  total: number;
  completed: number;
  succeeded: number;
  failed: number;
  results: {
    index: number;
    jobId: string;
    status: string;
    message: string | null;
    durationMs: number;
  }[];
  metrics: {
    totalDurationMs: number;
    itemMoveDurationMs: number;
    laneLockAcquisitions: number;
    laneLockWaitMs: number;
    laneLockHeldMs: number;
    scannerInvalidations: number;
    scannerRefreshes: number;
    scannerRefreshMs: number;
    gitProcesses: number;
    gitProcessMs: number;
  };
}

const CARD_COUNT = 55;

test.describe('Async archive all', () => {
  test.setTimeout(180_000);

  test('archives 55 cards while review reads stay responsive and progress remains visible', async ({ page, devBackend }) => {
    const [watchPath] = await api<WatchPath[]>('/api/watch-paths');
    if (!watchPath) throw new Error('No watch path configured for archive-all regression');

    const prefix = `e2e-async-archive-${Date.now()}`;
    const ids = Array.from({ length: CARD_COUNT }, (_, index) => `${prefix}-${index + 1}`);
    const createInBatches = async () => {
      for (const id of ids) {
        await api('/api/tasks', {
          method: 'POST',
          body: JSON.stringify({
            id,
            title: `Async archive card ${id}`,
            watchPath: watchPath.path,
            targetState: '2-ready',
            mode: 'concept',
            requiresIntegration: false,
            fixture: false,
            agent: 'codex',
            cliType: 'codex',
            model: 'gpt-5.3-codex',
            thinkingLevel: 'high',
            modelExplicit: true,
            thinkingLevelExplicit: true,
          }),
        });
        await api(`/api/tasks/${encodeURIComponent(id)}/concept-dossier?watchPath=${encodeURIComponent(watchPath.path)}`, {
          method: 'POST',
          body: JSON.stringify({ noDossierNeeded: true, reason: 'Code-free archive sweep fixture.' }),
        });
        await api(`/api/tasks/${encodeURIComponent(id)}/move?watchPath=${encodeURIComponent(watchPath.path)}`, {
          method: 'POST', body: JSON.stringify({ targetState: '6-completed' }),
        });
      }
    };

    await createInBatches();

    let legacyMoveRequestCount = 0;
    let batchRequestCount = 0;
    page.on('request', (request) => {
      if (request.method() === 'POST' && /\/api\/tasks\/[^/]+\/move/.test(request.url())) {
        legacyMoveRequestCount += 1;
      }
      if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/tasks/batch-move') {
        batchRequestCount += 1;
      }
    });

    const reviewLatenciesMs: number[] = [];
    let reviewProbeFailures = 0;

    try {
      await page.goto('/');
      const recoveryOverlay = page.getByTestId('crash-recovery-prompt-overlay');
      if (await recoveryOverlay.isVisible({ timeout: 2_000 }).catch(() => false)) {
        // Keep operator recovery data untouched; only remove the browser-side
        // blocker from this isolated acceptance run.
        await recoveryOverlay.evaluate((element) => {
          (element as HTMLElement).style.display = 'none';
          (element as HTMLElement).style.pointerEvents = 'none';
        });
      }
      await setTheme(page, 'light');
      const completedCards = page.locator('[data-testid="lane-6-completed"] app-job-card');
      await expect.poll(() => completedCards.count(), { timeout: 30_000, intervals: [200, 500, 1000] })
        .toBeGreaterThanOrEqual(CARD_COUNT);

      const archiveButton = page.getByTestId('archive-all-btn');
      const startedAt = Date.now();
      const acceptedResponse = page.waitForResponse((response) =>
        response.request().method() === 'POST'
        && new URL(response.url()).pathname === '/api/tasks/batch-move'
        && response.status() === 202);
      await archiveButton.click();
      const accepted = await (await acceptedResponse).json() as BatchMoveJobResponse;
      const acceptedLatencyMs = Date.now() - startedAt;
      await expect(archiveButton).toHaveAttribute('aria-busy', 'true');
      const progressMessage = page.getByTestId('notification-message').filter({
        hasText: /^Archiving \d+ of 55 tasks\.\.\.$/,
      });
      await expect(progressMessage).toBeVisible();
      await expect.poll(() => progressMessage.textContent(), {
        timeout: 10_000,
        intervals: [50, 100],
      }).toMatch(/^Archiving ([1-9]|[1-4]\d|5[0-4]) of 55 tasks\.\.\.$/);
      if (process.env.JOB_RESULTS_DIR) {
        await page.screenshot({
          path: path.join(process.env.JOB_RESULTS_DIR, 'bulk-archive-progress-light--real.png'),
          fullPage: true,
        });
      }

      while (await archiveButton.getAttribute('aria-busy') === 'true') {
        const probeStartedAt = Date.now();
        try {
          const response = await fetch(
            `${devBackend.baseUrl}/api/projects/${encodeURIComponent(watchPath.name)}/review-decisions-pending`,
            { headers: { 'x-client-id': 'local-default' }, signal: AbortSignal.timeout(10_000) },
          );
          if (!response.ok) reviewProbeFailures += 1;
          await response.text();
        } catch {
          reviewProbeFailures += 1;
        }
        reviewLatenciesMs.push(Date.now() - probeStartedAt);
        await page.waitForTimeout(100);
      }
      const totalDurationMs = Date.now() - startedAt;

      await expect.poll(async () => {
        const job = await api<BatchMoveJobResponse>(`/api/tasks/batch-move/${accepted.id}`);
        return job.status;
      }, { timeout: 30_000, intervals: [100, 250, 500] }).toBe('completed');
      const completedBatch = await api<BatchMoveJobResponse>(`/api/tasks/batch-move/${accepted.id}`);
      await expect(page.getByTestId('notification-message').filter({
        hasText: `Archived ${CARD_COUNT} tasks.`,
      })).toBeVisible();

      await expect.poll(async () => {
        const archived = await api<ArchivedTasksResponse>(
          `/api/tasks/archive?watchPath=${encodeURIComponent(watchPath.path)}&search=${encodeURIComponent(prefix)}&limit=200&includeFixtures=true`,
        );
        return archived.items.length;
      }, { timeout: 30_000, intervals: [200, 500, 1000] }).toBe(CARD_COUNT);

      const evidence = {
        capturedAt: new Date().toISOString(),
        cardCount: CARD_COUNT,
        totalDurationMs,
        acceptedLatencyMs,
        batchRequestCount,
        legacyMoveRequestCount,
        batchId: completedBatch.id,
        batchStatus: completedBatch.status,
        succeeded: completedBatch.succeeded,
        failed: completedBatch.failed,
        perCardMoveMs: completedBatch.results.map((result) => result.durationMs),
        serverMetrics: completedBatch.metrics,
        reviewProbeCount: reviewLatenciesMs.length,
        reviewProbeFailures,
        reviewLatenciesMs,
        reviewLatencyMaxMs: Math.max(0, ...reviewLatenciesMs),
      };
      const resultDir = process.env.JOB_RESULTS_DIR;
      if (resultDir) {
        writeFileSync(path.join(resultDir, 'bulk-archive-after.json'), `${JSON.stringify(evidence, null, 2)}\n`);
        await page.screenshot({
          path: path.join(resultDir, 'bulk-archive-after-light--real.png'),
          fullPage: true,
        });
        await setTheme(page, 'dark');
        await page.screenshot({
          path: path.join(resultDir, 'bulk-archive-after-dark--real.png'),
          fullPage: true,
        });
      }

      expect(batchRequestCount).toBe(1);
      expect(legacyMoveRequestCount).toBe(0);
      expect(completedBatch.succeeded).toBe(CARD_COUNT);
      expect(completedBatch.failed).toBe(0);
      expect(reviewProbeFailures).toBe(0);
    } finally {
      for (const id of ids) {
        await api<void>(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath.path)}`, {
          method: 'DELETE',
        }).catch(() => undefined);
      }
    }
  });
});
