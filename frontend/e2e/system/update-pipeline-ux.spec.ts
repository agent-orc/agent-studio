import { test, expect, Page, Route } from '@playwright/test';

/**
 * ADR-0031 reissue-2026-05-11: lock the FE-visible contract of the 9-phase
 * pipeline so a future tweak to the block-modal, banner, or run-id picker
 * cannot silently regress. All Update Service traffic is mocked at the
 * browser via `page.route` — this spec does NOT require the standalone
 * UpdateService on :5039 to be reachable.
 *
 * Scenarios:
 *   1. Phase-label transitions in the block-modal across a forward run.
 *   2. Green "done" toast with a Reload button when HEAD changed.
 *   3. Red "failed" banner with the verification-failure list and the
 *      "Other runs…" picker that hits /update/history.
 *   4. The failed-update toast's rollback carries the trigger token the
 *      Studio backend hands out.
 */

interface MockStatus {
  phase: string;
  phaseLabel: string | null;
  message: string | null;
  currentRunId: string | null;
  isRunning: boolean;
  backendReachable: boolean;
  headLocal: string;
  headOrigin: string | null;
  behindBy: number;
  pendingCommits: unknown[];
  startedAt: string | null;
  finishedAt: string | null;
  lastFetchAt: string | null;
  lastUpdateAt: string | null;
  lastSuccessAt: string | null;
  lastRunFinishedAt: string | null;
  lastRunHeadBefore: string | null;
  lastRunHeadAfter: string | null;
  serviceVersion: string;
  productVersion: string;
  mode: 'manual' | 'scheduled';
  verificationFailures: Array<{ step: string; observed: string; expected: string }> | null;
  autoRollbackEnabled: boolean;
}

const baseStatus: MockStatus = {
  phase: 'idle',
  phaseLabel: null,
  message: null,
  currentRunId: null,
  isRunning: false,
  backendReachable: true,
  headLocal: 'aaaaaaa',
  headOrigin: 'aaaaaaa',
  behindBy: 0,
  pendingCommits: [],
  startedAt: null,
  finishedAt: null,
  lastFetchAt: null,
  lastUpdateAt: null,
  lastSuccessAt: null,
  lastRunFinishedAt: null,
  lastRunHeadBefore: null,
  lastRunHeadAfter: null,
  serviceVersion: '0.0.0',
  productVersion: '0.1.0',
  mode: 'manual',
  verificationFailures: null,
  autoRollbackEnabled: false,
};

// A generated fixture value, never a real token.
const FIXTURE_UPDATE_TOKEN = `fixture-${Date.now().toString(36)}`;

async function installUpdateMock(page: Page, getStatus: () => MockStatus, opts?: {
  history?: () => Array<Record<string, unknown>>;
  onRollback?: (runId: string, token: string | null) => void;
}) {
  await page.route('**/api/update-service/token', (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ token: FIXTURE_UPDATE_TOKEN }) }));
  await page.route('**://*:5039/**', async (route: Route) => {
    const url = route.request().url();
    if (url.endsWith('/healthz') || url.endsWith('/update/health')) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '"ok"' });
      return;
    }
    if (url.includes('/update/status')) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(getStatus()) });
      return;
    }
    if (url.includes('/update/history')) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(opts?.history?.() ?? []) });
      return;
    }
    if (url.includes('/update/rollback')) {
      const body = route.request().postDataJSON() as { runId?: string } | null;
      opts?.onRollback?.(body?.runId ?? '', route.request().headers()['x-update-token'] ?? null);
      await route.fulfill({
        status: 202,
        contentType: 'application/json',
        body: JSON.stringify({ runId: body?.runId ?? 'rb', phase: 'rolling-back', message: 'accepted' }),
      });
      return;
    }
    if (url.includes('/update/trigger')) {
      await route.fulfill({
        status: 202,
        contentType: 'application/json',
        body: JSON.stringify({ runId: 'mockrun1', phase: 'preparing', message: 'accepted' }),
      });
      return;
    }
    // Anything else: 404 so a regression in URL building is visible.
    await route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
  });
}

// Per-spec evidence dir: passing tests by default leave nothing on disk
// (only failure attachments survive), but ADR-0031 reissue-2026-05-11
// asks for visible screenshots of the three modes. Each test writes one
// PNG into this folder; the job-run copier can then move them under
// results/.
const EVIDENCE_DIR = 'test-results/_evidence/update-pipeline-ux';

test.describe('Update Service pipeline UX (mocked)', () => {
  test('block-modal renders the phase label across a forward run', async ({ page }) => {
    let status: MockStatus = { ...baseStatus };
    await installUpdateMock(page, () => status);
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const block = page.locator('[data-testid="update-block-modal"]');
    const phase = page.locator('[data-testid="update-block-phase"]');

    // Phase 1
    status = {
      ...baseStatus,
      phase: 'preparing',
      phaseLabel: 'Preparing snapshot',
      message: 'snapshotting pre-state',
      currentRunId: 'r1',
      isRunning: true,
      startedAt: new Date().toISOString(),
    };
    await expect(block).toBeVisible({ timeout: 5_000 });
    await expect(phase).toContainText('Preparing snapshot');

    // Phase 3
    status = { ...status, phase: 'pulling', phaseLabel: 'Pulling and rebuilding', message: 'git fetch + pull --ff-only' };
    await expect(phase).toContainText('Pulling and rebuilding');

    // Phase 6
    status = { ...status, phase: 'verifying-after-restart', phaseLabel: 'Verifying restart', message: 'running 6-check matrix' };
    await expect(phase).toContainText('Verifying restart');
    await page.screenshot({ path: `${EVIDENCE_DIR}/01-block-modal-verifying.png`, fullPage: false });

    // Phase 7
    status = { ...status, phase: 'resuming', phaseLabel: 'Resuming runners', message: null };
    await expect(phase).toContainText('Resuming runners');

    // Done — block modal must clear, isRunning becomes false.
    status = {
      ...baseStatus,
      phase: 'done',
      phaseLabel: 'Update verified',
      currentRunId: 'r1',
      isRunning: false,
      lastRunFinishedAt: new Date().toISOString(),
      lastRunHeadBefore: 'aaaaaaa',
      lastRunHeadAfter: 'bbbbbbb',
      headLocal: 'bbbbbbb',
    };
    await expect(block).not.toBeVisible({ timeout: 5_000 });
  });

  test('done toast shows headBefore -> headAfter and a reload button', async ({ page }) => {
    let status: MockStatus = {
      ...baseStatus,
      phase: 'done',
      phaseLabel: 'Update verified',
      currentRunId: 'r2',
      isRunning: false,
      lastRunFinishedAt: new Date().toISOString(),
      lastRunHeadBefore: 'aaaaaaa',
      lastRunHeadAfter: 'bbbbbbb',
      headLocal: 'bbbbbbb',
    };
    await installUpdateMock(page, () => status);
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const toast = page.locator('[data-testid="update-banner-done"]');
    await expect(toast, 'done toast should appear with reload button').toBeVisible({ timeout: 5_000 });
    await expect(toast).toContainText('aaaaaaa');
    await expect(toast).toContainText('bbbbbbb');

    const reload = page.locator('[data-testid="update-banner-reload"]');
    await expect(reload).toBeVisible();
    await page.screenshot({ path: `${EVIDENCE_DIR}/02-done-toast-with-reload.png`, fullPage: false });
  });

  test('failed banner lists verification failures + run-id picker reads history', async ({ page }) => {
    let status: MockStatus = {
      ...baseStatus,
      phase: 'failed',
      phaseLabel: 'Update failed',
      message: 'verification failed: db-touch',
      currentRunId: 'r3',
      isRunning: false,
      lastRunFinishedAt: new Date().toISOString(),
      lastRunHeadBefore: 'aaaaaaa',
      lastRunHeadAfter: 'bbbbbbb',
      headLocal: 'bbbbbbb',
      verificationFailures: [{ step: 'db-touch', observed: 'http=503', expected: 'http=200' }],
    };

    let rollbackTarget: string | null = null;
    await installUpdateMock(page, () => status, {
      history: () => [
        {
          runId: 'older1', startedAt: '2026-05-08T08:00:00Z', finishedAt: '2026-05-08T08:02:30Z',
          status: 'failed', headBefore: '9999999', headAfter: 'aaaaaaa', durationSeconds: 150,
          error: 'verification failed: clients', trigger: 'manual',
        },
        {
          runId: 'r3', startedAt: '2026-05-11T12:00:00Z', finishedAt: '2026-05-11T12:02:30Z',
          status: 'failed', headBefore: 'aaaaaaa', headAfter: 'bbbbbbb', durationSeconds: 150,
          error: 'verification failed: db-touch', trigger: 'manual',
        },
      ],
      onRollback: (runId) => { rollbackTarget = runId; },
    });

    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const failed = page.locator('[data-testid="update-banner-failed"]');
    await expect(failed).toBeVisible({ timeout: 5_000 });
    await expect(failed).toContainText('verification failed');

    const failures = page.locator('[data-testid="update-banner-verification-failures"]');
    await expect(failures).toContainText('db-touch');
    await expect(failures).toContainText('http=503');

    await page.screenshot({ path: `${EVIDENCE_DIR}/03-failed-banner-collapsed.png`, fullPage: false });

    // Open the picker.
    await page.locator('[data-testid="update-banner-picker-open"]').click();
    const list = page.locator('[data-testid="update-banner-picker-list"]');
    await expect(list).toBeVisible();
    await expect(list).toContainText('older1');
    await expect(list).toContainText('r3');
    await page.screenshot({ path: `${EVIDENCE_DIR}/04-failed-banner-picker-open.png`, fullPage: false });

    // Roll back the older run, not the currentRunId — this is the load-bearing
    // delayed-rollback path that the picker exists to enable.
    await page.locator('[data-testid="update-banner-picker-rollback-older1"]').click();
    await expect.poll(() => rollbackTarget).toBe('older1');
  });

  test('failed-update toast rolls back with the trigger token from the Studio backend', async ({ page }) => {
    const status: MockStatus = {
      ...baseStatus,
      phase: 'failed',
      phaseLabel: 'Failed',
      message: 'verification failed: db-touch',
      currentRunId: 'r4',
      finishedAt: new Date().toISOString(),
      lastRunFinishedAt: new Date().toISOString(),
      lastRunHeadBefore: 'aaaaaaa',
      lastRunHeadAfter: 'bbbbbbb',
      headLocal: 'bbbbbbb',
      verificationFailures: [{ step: 'db-touch', observed: 'http=503', expected: 'http=200' }],
    };
    const rollbacks: Array<{ runId: string; token: string | null }> = [];
    await installUpdateMock(page, () => status, { onRollback: (runId, token) => rollbacks.push({ runId, token }) });

    await page.goto('/', { waitUntil: 'domcontentloaded' });
    const rollback = page.locator('[data-testid="toast-update-rollback"]');
    await expect(rollback).toBeVisible({ timeout: 10_000 });
    await page.screenshot({ path: `${EVIDENCE_DIR}/05-failed-toast-rollback.png`, fullPage: false });
    await rollback.click();

    await expect.poll(() => rollbacks.length).toBe(1);
    expect(rollbacks[0]).toEqual({ runId: 'r4', token: FIXTURE_UPDATE_TOKEN });
  });
});
