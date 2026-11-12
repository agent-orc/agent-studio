import { expect, test } from '@playwright/test';
import { createHash } from 'node:crypto';
import { mkdirSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

// The batch state endpoint is served by the isolated backend. Only its
// instance-local batch ledger is seeded; no browser API response is mocked.
test.use({ serviceWorkers: 'block', reducedMotion: 'reduce', trace: 'off', video: 'off',
  actionTimeout: 60_000, navigationTimeout: 60_000 });

const results = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve('test-results', 'batch-interrupted-real-backend');

function hash(value: string): string {
  return createHash('sha256').update(value).digest('hex');
}

function batchStoreRoot(): string {
  if (process.env.PW_PREBUILT !== '1' || !process.env.PW_E2E_WORKSPACE)
    throw new Error('This spec needs the isolated prebuilt backend.');
  const contentRoot = path.resolve(__dirname, '..', '..', '..', 'backend');
  const taskRepository = path.resolve(process.env.PW_E2E_WORKSPACE);
  const identity = `${contentRoot}\n${taskRepository}`;
  const localData = path.join(taskRepository, 'data');
  return path.join(localData, 'agentstudio', 'instances', hash(identity).slice(0, 20), 'batch-gates');
}

function seedBatch(project: string, repository: string, taskKey: string, batchId: string,
  phase: 7 | 8, reason: string, recordedAtUtc: string): void {
  const folder = path.join(batchStoreRoot(), hash(batchId));
  const stateFolder = path.join(folder, 'state');
  mkdirSync(stateFolder, { recursive: true });
  const scope = { project, repository, integrationBranch: 'develop', gateProfile: 'full',
    gateProfileDigest: 'e2e-profile', platformVersion: 'v1' };
  const member = { taskKey, ...scope, resultRef: `refs/heads/agent-studio/results/${taskKey}`,
    resultSha: 'a'.repeat(40), runAttempt: `run-${taskKey}`, deliveryEpoch: 1, fencingToken: 1,
    reviewSubjectSha: 'a'.repeat(40), settledEnvelope: true, modelReviewPassed: true,
    buildTestDeferredToBatch: true, currentGeneration: true, activePerTaskGate: false,
    owningBatchId: batchId, documentationOnly: true, reviewCompletedAtUtc: recordedAtUtc,
    enqueueSequence: 1 };
  const membershipDigest = hash(batchId);
  writeFileSync(path.join(stateFolder, '00000001.json'), JSON.stringify({ batchId,
    membershipDigest, phase: 0, candidateSha: null, coordinatorFence: 0,
    recordedAtUtc }));
  writeFileSync(path.join(stateFolder, '00000002.json'), JSON.stringify({ batchId,
    membershipDigest, phase, candidateSha: 'c'.repeat(40), coordinatorFence: 1,
    recordedAtUtc, reason }));
  const manifestTemp = path.join(folder, 'manifest.json.tmp');
  writeFileSync(manifestTemp, JSON.stringify({ batchId, scope,
    baseSha: 'b'.repeat(40), membershipDigest, closedAtUtc: recordedAtUtc,
    members: [member], exclusions: [], eligibleKeys: [taskKey] }));
  renameSync(manifestTemp, path.join(folder, 'manifest.json'));
}

test.afterEach(() => rmSync(batchStoreRoot(), { recursive: true, force: true }));

test('card and batch panel explain interrupted isolation from the real backend', async ({ page }) => {
  test.setTimeout(240_000);
  mkdirSync(results, { recursive: true });
  const backend = process.env.PW_BACKEND_URL!;
  const paths = await (await page.request.get(`${backend}/api/watch-paths`, { timeout: 60_000 })).json() as
    { name: string; path: string; repositoryPath: string }[];
  const project = paths[0].name;
  const watchPath = paths[0].path;
  const created: string[] = [];
  for (const title of ['Interrupted isolation member']) {
    const response = await page.request.post(`${backend}/api/tasks`, {
      headers: { 'X-Client-Id': 'local-default' },
      data: { title, watchPath, agent: 'codex', cliType: 'codex', targetState: '1-preparation', fixture: false },
      timeout: 60_000,
    });
    expect(response.ok(), await response.text()).toBe(true);
    created.push(((await response.json()) as { id: string }).id);
  }
  const enabled = await page.request.put(`${backend}/api/projects/${encodeURIComponent(project)}/batch-gate`, {
    headers: { 'X-Client-Id': 'local-default' }, data: { enabled: true },
    timeout: 60_000,
  });
  expect(enabled.ok(), await enabled.text()).toBe(true);
  seedBatch(project, paths[0].repositoryPath, created[0], 'interrupted-e2e', 8,
    'isolation interrupted', '2026-11-10T03:12:00Z');

  const state = await (await page.request.get(`${backend}/api/projects/${encodeURIComponent(project)}/batch-gate/state?history=10`,
    { timeout: 60_000 })).json();
  mkdirSync(path.join(results, 'evidence'), { recursive: true });
  writeFileSync(path.join(results, 'evidence', 'interrupted-batch-state.json'),
    JSON.stringify(state, null, 2) + '\n');
  expect(state.batches.find((batch: { batchId: string }) => batch.batchId === 'interrupted-e2e')?.phaseReason)
    .toBe('isolation interrupted');

  await page.goto(`/?job=${encodeURIComponent(created[0])}&watchPath=${encodeURIComponent(watchPath)}`);
  const card = page.getByTestId('task-batch-verification');
  await expect(card.getByTestId('task-batch-isolation-state')).toContainText(
    'Ended without a verdict at 2026-11-10T03:12:00Z', { timeout: 60_000 });
  await expect(card.getByTestId('task-batch-isolation-state')).toContainText(
    'Waiting for: Its own gate after interrupted isolation', { timeout: 60_000 });
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await card.screenshot({ path: path.join(results, `interrupted-card-${width}-${theme}--real.png`) });
    }
  }

  await card.getByRole('button', { name: 'Open batch panel' }).click();
  const panel = page.getByTestId('batch-panel');
  await expect(panel.locator('#batch-interrupted-e2e').getByTestId('batch-isolation-state'))
    .toContainText('Ended without a verdict at 2026-11-10T03:12:00Z', { timeout: 60_000 });
  await expect(panel.locator('#batch-interrupted-e2e').getByTestId('batch-isolation-member'))
    .toContainText('Waiting for: Its own gate after interrupted isolation', { timeout: 60_000 });
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await panel.locator('#batch-interrupted-e2e').scrollIntoViewIfNeeded();
      await page.screenshot({ path: path.join(results, `interrupted-panel-${width}-${theme}--real.png`) });
    }
  }
});
